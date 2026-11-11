// SPDX-License-Identifier: GPL-2.0-or-later
using SpaceTangent.Contracts;

namespace SpaceTangent.Core.Spice;

/// <summary>Reads primitive J2000 states in SPK type 1 without a native runtime.</summary>
public sealed class SpkKernel
{
    private readonly record struct Segment(DafSegment Descriptor, JulianInstant Start, JulianInstant End,
        int EpochAddress, int RecordCount);
    private const int TypeOneWords = 71;
    private readonly DafFile _file;
    private readonly Segment[] _segments;

    public SpkKernel(string path)
    {
        _file = new DafFile(path, "SPK");
        _segments = _file.Segments().Select(Prepare).ToArray();
    }

    /// <summary>Returns a target relative to the segment's centre. Later segments take precedence.</summary>
    public bool TryState(int target, int center, JulianInstant tdb, out CartesianState state)
    {
        if (tdb.Scale != TimeScale.Tdb)
            throw new ArgumentException("SPK requires a TDB epoch.", nameof(tdb));
        double et = (tdb.Day - JulianInstant.J2000Day) * (double)JulianInstant.SecondsPerDay +
                    tdb.NanosecondsOfDay / (double)JulianInstant.NanosecondsPerSecond;
        for (int index = _segments.Length - 1; index >= 0; index--)
        {
            Segment segment = _segments[index];
            DafSegment descriptor = segment.Descriptor;
            if (descriptor.FirstId != target || descriptor.SecondId != center ||
                tdb.CompareTo(segment.Start) < 0 || tdb.CompareTo(segment.End) > 0)
                continue;
            if (descriptor.ThirdId != 1 || descriptor.FourthId != 1)
                throw new NotSupportedException("The selected SPK segment requires J2000 frame and type 1 records.");

            double evaluationTime = Math.Clamp(et, descriptor.Begin, descriptor.End);
            int lower = 0, upper = segment.RecordCount - 1;
            while (lower < upper)
            {
                int middle = lower + (upper - lower) / 2;
                if (_file.Word(segment.EpochAddress + middle) < evaluationTime)
                    lower = middle + 1;
                else
                    upper = middle;
            }
            state = EvaluateTypeOne(descriptor.FirstWord + lower * TypeOneWords, evaluationTime);
            return true;
        }
        state = default;
        return false;
    }

    private Segment Prepare(DafSegment descriptor)
    {
        JulianInstant start = JulianInstant.FromTdbSeconds(descriptor.Begin);
        JulianInstant end = JulianInstant.FromTdbSeconds(descriptor.End);
        if (descriptor.FourthId != 1)
            return new(descriptor, start, end, 0, 0);

        int words = descriptor.LastWord - descriptor.FirstWord + 1;
        int count = DafFile.ExactInteger(_file.Word(descriptor.LastWord), 1, words / TypeOneWords, "SPK record count");
        if (72L * count + count / 100 + 1 != words)
            throw new InvalidDataException("SPK type 1 records do not fit the segment.");
        int epochs = descriptor.FirstWord + count * TypeOneWords;
        double previous = double.NegativeInfinity;
        for (int index = 0; index < count; index++)
        {
            double epoch = _file.Word(epochs + index);
            if (!double.IsFinite(epoch) || epoch <= previous)
                throw new InvalidDataException("SPK type 1 record epochs must increase.");
            previous = epoch;
        }
        if (previous < descriptor.End)
            throw new InvalidDataException("SPK type 1 records do not reach segment end.");
        return new(descriptor, start, end, epochs, count);
    }

    // NAIF SPKE01 modified divided differences. Each recurrence uses 1-based local indices.
    private CartesianState EvaluateTypeOne(int record, double et)
    {
        const int Dimension = 15;
        int reference = record + 1 + Dimension;
        int differences = reference + 6;
        int maximumOrder = DafFile.ExactInteger(_file.Word(differences + 3 * Dimension), 2, Dimension + 2,
            "SPK type 1 maximum order");
        Span<int> order = stackalloc int[3];
        for (int axis = 0; axis < 3; axis++)
            order[axis] = DafFile.ExactInteger(_file.Word(differences + 3 * Dimension + 1 + axis), 1,
                Math.Min(maximumOrder - 1, Dimension), "SPK type 1 axis order");

        double delta = et - _file.Word(record);
        double elapsed = delta;
        int basisIndex = maximumOrder - 1;
        Span<double> fractions = stackalloc double[Dimension + 2];
        Span<double> ratios = stackalloc double[Dimension + 2];
        Span<double> weights = stackalloc double[Dimension + 3];
        for (int j = 1; j <= maximumOrder - 2; j++)
        {
            double step = _file.Word(record + j);
            if (!double.IsFinite(step) || step == 0)
                throw new InvalidDataException("SPK type 1 record has an invalid time step.");
            fractions[j + 1] = elapsed / step;
            ratios[j] = delta / step;
            elapsed = delta + step;
        }
        for (int j = 1; j <= maximumOrder; j++)
            weights[j] = 1d / j;
        int width = 0;
        while (basisIndex >= 2)
        {
            width++;
            UpdateWeights(weights, fractions, ratios, basisIndex, width);
            basisIndex--;
        }

        Span<double> position = stackalloc double[3];
        for (int axis = 0; axis < 3; axis++)
        {
            double acceleration = SumDifferences(weights, basisIndex, differences + axis * Dimension, order[axis]);
            position[axis] = _file.Word(reference + axis * 2) +
                delta * (_file.Word(reference + axis * 2 + 1) + delta * acceleration);
        }
        UpdateWeights(weights, fractions, ratios, basisIndex, width);
        Span<double> velocity = stackalloc double[3];
        for (int axis = 0; axis < 3; axis++)
        {
            double acceleration = SumDifferences(weights, basisIndex - 1, differences + axis * Dimension, order[axis]);
            velocity[axis] = _file.Word(reference + axis * 2 + 1) + delta * acceleration;
        }
        var result = new CartesianState(new(position[0], position[1], position[2]), new(velocity[0], velocity[1], velocity[2]));
        if (!result.PositionKilometers.IsFinite || !result.VelocityKilometersPerSecond.IsFinite)
            throw new InvalidDataException("SPK type 1 record produced a nonfinite state.");
        return result;
    }

    private static void UpdateWeights(Span<double> weights, ReadOnlySpan<double> fractions, ReadOnlySpan<double> ratios,
        int offset, int width)
    {
        for (int j = 1; j <= width; j++)
            weights[j + offset] = fractions[j + 1] * weights[j + offset - 1] - ratios[j] * weights[j + offset];
    }

    private double SumDifferences(ReadOnlySpan<double> weights, int offset, int start, int order)
    {
        double sum = 0;
        for (int j = order; j >= 1; j--)
            sum += _file.Word(start + j - 1) * weights[j + offset];
        return sum;
    }
}
