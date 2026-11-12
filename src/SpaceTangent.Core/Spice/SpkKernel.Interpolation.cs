// SPDX-License-Identifier: GPL-2.0-or-later
using SpaceTangent.Contracts;

namespace SpaceTangent.Core.Spice;

public sealed partial class SpkKernel
{
    private const int MaximumWindow = 64;

    private Segment PrepareSlidingWindow(DafSegment descriptor, JulianInstant start, JulianInstant end)
    {
        bool uniform = descriptor.FourthId is 8 or 12;
        int words = descriptor.LastWord - descriptor.FirstWord + 1;
        int count = DafFile.ExactInteger(_file.Word(descriptor.LastWord), 1, words / 6, "SPK sampled state count");
        int window = 1 + DafFile.ExactInteger(_file.Word(descriptor.LastWord - 1), 0,
            Math.Min(MaximumWindow, count) - 1, "SPK interpolation window");
        long required = uniform ? 6L * count + 4 : 7L * count + (count - 1) / 100 + 2;
        if (required != words)
            throw new InvalidDataException("The SPK interpolation table does not fit its segment.");

        if (uniform)
        {
            double initial = _file.Word(descriptor.LastWord - 3), interval = _file.Word(descriptor.LastWord - 2);
            double last = initial + (count - 1) * interval;
            if (!double.IsFinite(initial) || !double.IsFinite(interval) || interval <= 0 || !double.IsFinite(last) ||
                (count > 1 && initial + interval <= initial))
                throw new InvalidDataException("The SPK interpolation interval is invalid.");
            return new(descriptor, start, end, RecordCount: count, RecordWords: window, InitialEpoch: initial, Interval: interval);
        }

        int epochs = descriptor.FirstWord + 6 * count;
        double previous = double.NegativeInfinity;
        for (int index = 0; index < count; index++)
        {
            double epoch = _file.Word(epochs + index);
            if (!double.IsFinite(epoch) || epoch <= previous)
                throw new InvalidDataException("SPK interpolation epochs must increase.");
            previous = epoch;
        }
        return new(descriptor, start, end, epochs, count, window);
    }

    private CartesianState SampleSlidingWindow(Segment segment, double et)
    {
        int window = segment.RecordWords;
        int count = segment.RecordCount;
        int left;
        bool odd = (window & 1) != 0;
        if (segment.EpochAddress == 0)
        {
            double coordinate = (et - segment.InitialEpoch) / segment.Interval;
            left = (int)Math.Clamp(Math.Floor(coordinate + (odd ? .5 : 0)), 0, count - 1);
        }
        else
        {
            int low = 0, high = count;
            while (low < high)
            {
                int middle = low + (high - low) / 2;
                if (Epoch(middle) <= et)
                    low = middle + 1;
                else
                    high = middle;
            }
            left = Math.Max(0, low - 1);
            if (odd && low < count && et - Epoch(left) >= Epoch(low) - et)
                left = low;
        }
        int first = Math.Clamp(left - (window - 1) / 2, 0, count - window);
        Span<double> times = stackalloc double[window];
        Span<double> values = stackalloc double[window * 6];
        for (int index = 0; index < window; index++)
        {
            int record = first + index;
            times[index] = Epoch(record) - et;
            for (int component = 0; component < 6; component++)
                values[6 * index + component] = _file.Word(segment.Descriptor.FirstWord + 6 * record + component);
        }

        bool hermite = segment.Descriptor.FourthId is 12 or 13;
        Span<double> result = stackalloc double[6];
        result.Clear();
        for (int index = 0; index < window; index++)
        {
            double basis = 1, derivative = 0, slope = 0;
            for (int node = 0; node < window; node++)
            {
                if (node == index)
                    continue;
                double inverse = 1 / (times[index] - times[node]);
                double factor = -times[node] * inverse;
                derivative = derivative * factor + basis * inverse;
                basis *= factor;
                slope += inverse;
            }
            if (hermite)
                AddHermite(values.Slice(index * 6, 6), -times[index], basis, derivative, slope, result);
            else
                for (int component = 0; component < 6; component++)
                    result[component] += basis * values[index * 6 + component];
        }
        return CheckedState(new(new(result[0], result[1], result[2]), new(result[3], result[4], result[5])));

        double Epoch(int index) => segment.EpochAddress != 0 ? _file.Word(segment.EpochAddress + index) :
            segment.InitialEpoch + index * segment.Interval;
    }

    private static void AddHermite(ReadOnlySpan<double> state, double elapsed, double basis, double derivative,
        double slope, Span<double> result)
    {
        double square = basis * basis, squareRate = 2 * basis * derivative;
        double adjustment = 1 - 2 * slope * elapsed;
        double positionWeight = adjustment * square, velocityWeight = elapsed * square;
        double positionRate = adjustment * squareRate - 2 * slope * square;
        double velocityRate = square + elapsed * squareRate;
        for (int axis = 0; axis < 3; axis++)
        {
            result[axis] += positionWeight * state[axis] + velocityWeight * state[3 + axis];
            result[3 + axis] += positionRate * state[axis] + velocityRate * state[3 + axis];
        }
    }

    private static CartesianState CheckedState(CartesianState state) =>
        state.PositionKilometers.IsFinite && state.VelocityKilometersPerSecond.IsFinite ? state :
            throw new InvalidDataException("The SPK interpolation produced a nonfinite state.");
}
