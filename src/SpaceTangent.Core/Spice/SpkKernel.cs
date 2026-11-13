// SPDX-License-Identifier: GPL-2.0-or-later
using SpaceTangent.Contracts;
using SpaceTangent.Core.Ephemerides;

namespace SpaceTangent.Core.Spice;

/// <summary>Reads primitive J2000 states from supported SPK segments without a native runtime.</summary>
public sealed partial class SpkKernel
{
    private readonly record struct Segment(DafSegment Descriptor, JulianInstant Start, JulianInstant End,
        int EpochAddress = 0, int RecordCount = 0, int RecordWords = 0, double InitialEpoch = 0, double Interval = 0);
    private const int TypeOneWords = 71;
    private readonly DafFile _file;
    private readonly Segment[] _segments;

    public SpkKernel(string path)
    {
        _file = new DafFile(path, "SPK");
        _segments = _file.Segments().Select(Prepare).ToArray();
    }

    internal IEnumerable<(JulianInstant Start, JulianInstant End)> TargetCoverage(int target) =>
        _segments.Where(segment => segment.Descriptor.FirstId == target).Select(segment => (segment.Start, segment.End));
    /// <summary>Returns the union of coverage intervals for the specified primitive target and centre.</summary>
    public IReadOnlyList<(JulianInstant Start, JulianInstant End)> GetCoverage(int target, int center)
    {
        var ranges = new List<(JulianInstant Start, JulianInstant End)>();
        foreach (Segment segment in _segments.Where(segment => segment.Descriptor.FirstId == target &&
                     segment.Descriptor.SecondId == center).OrderBy(segment => segment.Start))
        {
            if (ranges.Count == 0 || segment.Start.CompareTo(ranges[^1].End) > 0)
                ranges.Add((segment.Start, segment.End));
            else if (segment.End.CompareTo(ranges[^1].End) > 0)
                ranges[^1] = (ranges[^1].Start, segment.End);
        }
        return ranges.AsReadOnly();
    }

    /// <summary>Returns a target relative to the segment's centre. Later segments take precedence.</summary>
    public bool TryState(int target, int center, JulianInstant tdb, out CartesianState state)
    {
        if (tdb.Scale != TimeScale.Tdb)
            throw new ArgumentException("SPK requires a TDB epoch.", nameof(tdb));
        if (!TrySelect(target, center, tdb, out var segment))
        {
            state = default;
            return false;
        }
        if (segment.Frame != 1)
            throw new NotSupportedException("The selected SPK segment requires the J2000 frame.");
        state = segment.Sample(tdb);
        return true;
    }

    internal readonly record struct SelectedSegment(SpkKernel Kernel, int Index)
    {
        internal int Center => Kernel._segments[Index].Descriptor.SecondId;
        internal int Frame => Kernel._segments[Index].Descriptor.ThirdId;
        internal CartesianState Sample(JulianInstant tdb) => Kernel.Evaluate(Kernel._segments[Index], tdb);
    }

    internal bool TrySelect(int target, int? center, JulianInstant tdb, out SelectedSegment selected)
    {
        for (int index = _segments.Length - 1; index >= 0; index--)
        {
            var segment = _segments[index];
            if (segment.Descriptor.FirstId == target && (!center.HasValue || segment.Descriptor.SecondId == center) &&
                tdb.CompareTo(segment.Start) >= 0 && tdb.CompareTo(segment.End) <= 0)
            {
                selected = new(this, index);
                return true;
            }
        }
        selected = default;
        return false;
    }

    private CartesianState Evaluate(Segment segment, JulianInstant tdb)
    {
        var descriptor = segment.Descriptor;
        double et = (tdb.Day - JulianInstant.J2000Day) * (double)JulianInstant.SecondsPerDay +
            tdb.NanosecondsOfDay / (double)JulianInstant.NanosecondsPerSecond;
        double evaluationTime = Math.Clamp(et, descriptor.Begin, descriptor.End);
        return descriptor.FourthId switch
        {
            1 or 21 => SampleDifferences(segment, evaluationTime),
            2 or 3 => SampleChebyshev(segment, evaluationTime),
            8 or 9 or 12 or 13 => SampleSlidingWindow(segment, evaluationTime),
            17 => SampleEquinoctial(segment, evaluationTime),
            _ => throw new NotSupportedException($"Unsupported SPK record type {descriptor.FourthId}.")
        };
    }

    private Segment Prepare(DafSegment descriptor)
    {
        JulianInstant start = JulianInstant.FromTdbSeconds(descriptor.Begin);
        JulianInstant end = JulianInstant.FromTdbSeconds(descriptor.End);
        if (descriptor.FourthId is 2 or 3)
            return PrepareChebyshev(descriptor, start, end);
        if (descriptor.FourthId is 8 or 9 or 12 or 13)
            return PrepareSlidingWindow(descriptor, start, end);
        if (descriptor.FourthId == 17)
            return PrepareEquinoctial(descriptor, start, end);
        if (descriptor.FourthId is not (1 or 21))
            return new(descriptor, start, end);

        int words = descriptor.LastWord - descriptor.FirstWord + 1;
        bool extended = descriptor.FourthId == 21;
        int recordWords = extended ? 4 * DafFile.ExactInteger(_file.Word(descriptor.LastWord - 1),
            15, 64, "SPK difference line dimension") + 11 : TypeOneWords;
        int count = DafFile.ExactInteger(_file.Word(descriptor.LastWord), 1, words / recordWords, "SPK record count");
        int trailer = extended ? 2 : 1;
        if ((long)(recordWords + 1) * count + count / 100 + trailer != words)
            throw new InvalidDataException("SPK difference lines do not fit the segment.");
        int epochs = descriptor.FirstWord + count * recordWords;
        double previous = double.NegativeInfinity;
        for (int index = 0; index < count; index++)
        {
            double epoch = _file.Word(epochs + index);
            if (!double.IsFinite(epoch) || epoch <= previous)
                throw new InvalidDataException("SPK difference line epochs must increase.");
            previous = epoch;
        }
        if (previous < descriptor.End)
            throw new InvalidDataException("SPK difference lines do not reach segment end.");
        return new(descriptor, start, end, epochs, count, recordWords);
    }

    private Segment PrepareChebyshev(DafSegment descriptor, JulianInstant start, JulianInstant end)
    {
        int words = descriptor.LastWord - descriptor.FirstWord + 1;
        const int DirectoryWords = 4;
        int components = descriptor.FourthId == 2 ? 3 : 6;
        int minimumRecordWords = components + 2;
        if (words < DirectoryWords + minimumRecordWords)
            throw new InvalidDataException("SPK Chebyshev directory or record is incomplete.");
        int last = descriptor.LastWord;
        double initial = _file.Word(last - 3), interval = _file.Word(last - 2);
        int recordWords = DafFile.ExactInteger(_file.Word(last - 1), minimumRecordWords, words - DirectoryWords,
            "SPK Chebyshev record size");
        int count = DafFile.ExactInteger(_file.Word(last), 1, (words - DirectoryWords) / recordWords,
            "SPK Chebyshev record count");
        double final = initial + interval * count;
        // Some kernels accumulate record times separately from the rounded directory interval.
        double timeTolerance = interval * 5e-9;
        if ((recordWords - 2) % components != 0 || (long)recordWords * count + DirectoryWords != words ||
            !double.IsFinite(initial) || !double.IsFinite(interval) || !double.IsFinite(final) || interval <= 0 ||
            descriptor.Begin < Math.BitDecrement(initial - timeTolerance) ||
            descriptor.End > Math.BitIncrement(final + timeTolerance))
            throw new InvalidDataException("Invalid SPK Chebyshev directory.");
        return new(descriptor, start, end, RecordCount: count, RecordWords: recordWords, InitialEpoch: initial, Interval: interval);
    }

    private CartesianState SampleDifferences(Segment segment, double et)
    {
        int lower = 0, upper = segment.RecordCount - 1;
        while (lower < upper)
        {
            int middle = lower + (upper - lower) / 2;
            if (_file.Word(segment.EpochAddress + middle) < et)
                lower = middle + 1;
            else
                upper = middle;
        }
        return EvaluateDifferences(segment.Descriptor.FirstWord + lower * segment.RecordWords, (segment.RecordWords - 11) / 4, et);
    }

    private CartesianState SampleChebyshev(Segment segment, double et)
    {
        int index = (int)Math.Clamp(Math.Floor((et - segment.InitialEpoch) / segment.Interval), 0, segment.RecordCount - 1);
        int record = segment.Descriptor.FirstWord + index * segment.RecordWords;
        double midpoint = _file.Word(record), radius = _file.Word(record + 1);
        double u = (et - midpoint) / radius;
        if (!double.IsFinite(radius) || radius <= 0 || !double.IsFinite(u) || Math.Abs(u) > 1 + 1e-8)
            throw new InvalidDataException("Invalid SPK Chebyshev time interval.");

        bool storedVelocity = segment.Descriptor.FourthId == 3;
        int count = (segment.RecordWords - 2) / (storedVelocity ? 6 : 3);
        var (x, vx) = Axis(0);
        var (y, vy) = Axis(1);
        var (z, vz) = Axis(2);
        Vector3d velocity = storedVelocity ? new(Axis(3).Value, Axis(4).Value, Axis(5).Value)
            : new(vx / radius, vy / radius, vz / radius);
        var state = new CartesianState(new(x, y, z), velocity);
        if (!state.PositionKilometers.IsFinite || !state.VelocityKilometersPerSecond.IsFinite)
            throw new InvalidDataException("SPK Chebyshev record produced a nonfinite state.");
        return state;

        (double Value, double Derivative) Axis(int axis) =>
            Chebyshev.Evaluate(_file.WordBytes(record + 2 + axis * count, count), u, _file.LittleEndian);
    }

    // Modified divided differences. Each recurrence uses 1-based local indices.
    private CartesianState EvaluateDifferences(int record, int dimension, double et)
    {
        int reference = record + 1 + dimension;
        int differences = reference + 6;
        int maximumOrder = DafFile.ExactInteger(_file.Word(differences + 3 * dimension), 2, dimension + 2,
            "SPK difference line maximum order");
        Span<int> order = stackalloc int[3];
        for (int axis = 0; axis < 3; axis++)
            order[axis] = DafFile.ExactInteger(_file.Word(differences + 3 * dimension + 1 + axis), 1,
                Math.Min(maximumOrder - 1, dimension), "SPK difference line axis order");

        double delta = et - _file.Word(record);
        double elapsed = delta;
        int basisIndex = maximumOrder - 1;
        Span<double> fractions = stackalloc double[dimension + 2];
        Span<double> ratios = stackalloc double[dimension + 2];
        Span<double> weights = stackalloc double[dimension + 3];
        for (int j = 1; j <= maximumOrder - 2; j++)
        {
            double step = _file.Word(record + j);
            if (!double.IsFinite(step) || step == 0)
                throw new InvalidDataException("SPK difference line has an invalid time step.");
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
            double acceleration = SumDifferences(weights, basisIndex, differences + axis * dimension, order[axis]);
            position[axis] = _file.Word(reference + axis * 2) +
                delta * (_file.Word(reference + axis * 2 + 1) + delta * acceleration);
        }
        UpdateWeights(weights, fractions, ratios, basisIndex, width);
        Span<double> velocity = stackalloc double[3];
        for (int axis = 0; axis < 3; axis++)
        {
            double acceleration = SumDifferences(weights, basisIndex - 1, differences + axis * dimension, order[axis]);
            velocity[axis] = _file.Word(reference + axis * 2 + 1) + delta * acceleration;
        }
        var result = new CartesianState(new(position[0], position[1], position[2]), new(velocity[0], velocity[1], velocity[2]));
        if (!result.PositionKilometers.IsFinite || !result.VelocityKilometersPerSecond.IsFinite)
            throw new InvalidDataException("SPK difference line produced a nonfinite state.");
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
