// SPDX-License-Identifier: GPL-2.0-or-later
using SpaceTangent.Contracts;

namespace SpaceTangent.Core.Spice;

/// <summary>Evaluates geometric states through the latest active target segments in the kernel pool.</summary>
internal sealed class SpiceStatePool(SpkKernel[] kernels, SpiceFramePool frames)
{
    private const int MaximumChainLength = 64;

    internal JulianInstant? FirstEpoch(int target)
    {
        JulianInstant? start = null;
        foreach (var kernel in kernels)
        foreach (var coverage in kernel.TargetCoverage(target))
            if (start is null || coverage.Start.CompareTo(start.Value) < 0)
                start = coverage.Start;
        return start;
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
