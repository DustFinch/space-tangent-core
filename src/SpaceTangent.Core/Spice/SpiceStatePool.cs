// SPDX-License-Identifier: GPL-2.0-or-later
using SpaceTangent.Contracts;

namespace SpaceTangent.Core.Spice;

/// <summary>Evaluates geometric states through the latest active target segments in the kernel pool.</summary>
internal sealed class SpiceStatePool(SpkKernel[] kernels, SpiceFramePool frames)
{
    private const int MaximumChainLength = 64;

    private readonly Lazy<(JulianInstant Epoch, int Order)[]> _boundaries = new(() =>
    {
        var ordered = new Dictionary<JulianInstant, int>();
        foreach (var kernel in kernels)
        foreach (var (start, end) in kernel.Coverage)
        {
            ordered.TryAdd(start, ordered.Count);
            ordered.TryAdd(end, ordered.Count);
        }
        return ordered.OrderBy(entry => entry.Key).Select(entry => (entry.Key, entry.Value)).ToArray();
    });

    internal (JulianInstant Start, JulianInstant End)? FirstTargetInterval(int target)
    {
        (JulianInstant Start, JulianInstant End)? first = null;
        foreach (var range in kernels.SelectMany(kernel => kernel.TargetCoverage(target)).OrderBy(range => range.Start))
        {
            if (first is not { } span)
                first = range;
            else if (range.Start.CompareTo(span.End) > 0)
                break;
            else if (range.End.CompareTo(span.End) > 0)
                first = (span.Start, range.End);
        }
        return first;
    }

    internal bool TryNearestState(int target, int center, JulianInstant epoch, out CartesianState state, out Exception? error)
    {
        if (TrySample(epoch, out state, out error))
            return true;

        var boundaries = _boundaries.Value;
        int left = 0, right = boundaries.Length;
        while (left < right)
        {
            int middle = left + (right - left) / 2;
            if (boundaries[middle].Epoch.CompareTo(epoch) < 0)
                left = middle + 1;
            else
                right = middle;
        }

        left = right - 1;
        while (left >= 0 || right < boundaries.Length)
        {
            bool useLeft = right == boundaries.Length;
            if (!useLeft && left >= 0)
            {
                var leftDistance = epoch.DifferenceNanoseconds(boundaries[left].Epoch);
                var rightDistance = boundaries[right].Epoch.DifferenceNanoseconds(epoch);
                useLeft = leftDistance < rightDistance ||
                    leftDistance == rightDistance && boundaries[left].Order < boundaries[right].Order;
            }
            var boundary = useLeft ? boundaries[left--].Epoch : boundaries[right++].Epoch;
            if (boundary == epoch)
                continue;
            if (TrySample(boundary, out state, out var boundaryError))
                return true;
            error ??= boundaryError;
        }
        return false;

        bool TrySample(JulianInstant time, out CartesianState sample, out Exception? failure)
        {
            failure = null;
            try
            {
                return TryState(target, center, time, out sample);
            }
            catch (Exception caught) when (caught is InvalidDataException or NotSupportedException)
            {
                failure = caught;
                sample = default;
                return false;
            }
        }
    }

    internal bool CoversTarget(int target, JulianInstant beginning, JulianInstant ending)
    {
        var coveredThrough = beginning;
        foreach (var range in kernels.SelectMany(kernel => kernel.TargetCoverage(target)).OrderBy(range => range.Start))
        {
            if (range.Start.CompareTo(coveredThrough) > 0)
                break;
            if (range.End.CompareTo(ending) >= 0)
                return true;
            if (range.End.CompareTo(coveredThrough) > 0)
                coveredThrough = range.End;
        }
        return false;
    }

    internal bool TryState(int target, int center, JulianInstant epoch, out CartesianState state)
    {
        if (epoch.Scale != TimeScale.Tdb)
            throw new ArgumentException("SPICE states require a TDB epoch.", nameof(epoch));
        if (target == center)
        {
            state = default;
            return true;
        }
        if (TrySelect(target, epoch, out var direct) && direct.Center == center)
            return Transform(direct, epoch, out state);

        var targetNodes = new Dictionary<int, int> { [target] = 0 };
        var targetEdges = new List<SpkKernel.SelectedSegment>();
        int cursor = target;
        while (targetEdges.Count < MaximumChainLength && TrySelect(cursor, epoch, out var edge) &&
            targetNodes.TryAdd(edge.Center, targetEdges.Count + 1))
        {
            targetEdges.Add(edge);
            cursor = edge.Center;
            if (cursor == center)
                return Accumulate(targetEdges, targetEdges.Count, epoch, out state);
        }

        var centerEdges = new List<SpkKernel.SelectedSegment>();
        var centerNodes = new HashSet<int> { center };
        cursor = center;
        for (;;)
        {
            if (targetNodes.TryGetValue(cursor, out int throughTarget))
            {
                if (Accumulate(targetEdges, throughTarget, epoch, out var targetState) &&
                    Accumulate(centerEdges, centerEdges.Count, epoch, out var centerState))
                {
                    state = targetState - centerState;
                    return true;
                }
                break;
            }
            if (centerEdges.Count == MaximumChainLength || !TrySelect(cursor, epoch, out var edge) || !centerNodes.Add(edge.Center))
                break;
            centerEdges.Add(edge);
            cursor = edge.Center;
        }
        state = default;
        return false;
    }

    private bool TrySelect(int target, JulianInstant epoch, out SpkKernel.SelectedSegment edge)
    {
        for (int index = kernels.Length - 1; index >= 0; index--)
            if (kernels[index].TrySelect(target, null, epoch, out edge))
                return true;
        edge = default;
        return false;
    }

    private bool Accumulate(List<SpkKernel.SelectedSegment> edges, int count, JulianInstant epoch, out CartesianState state)
    {
        state = default;
        for (int index = count - 1; index >= 0; index--)
        {
            if (!Transform(edges[index], epoch, out var part))
            {
                state = default;
                return false;
            }
            state += part;
        }
        return true;
    }

    private bool Transform(SpkKernel.SelectedSegment edge, JulianInstant epoch, out CartesianState state)
    {
        if (edge.Frame == 1)
        {
            state = edge.Sample(epoch);
            return true;
        }
        if (frames.TrySample(edge.Frame, epoch, needRates: true, out var frame))
            return frame.TryTransform(edge.Sample(epoch), out state);
        state = default;
        return false;
    }
}
