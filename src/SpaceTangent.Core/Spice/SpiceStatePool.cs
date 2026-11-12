// SPDX-License-Identifier: GPL-2.0-or-later
using System.Collections.Concurrent;
using System.Globalization;
using SpaceTangent.Contracts;
using SpaceTangent.Core.World;

namespace SpaceTangent.Core.Spice;

/// <summary>Evaluates geometric states through the latest active target segments in the kernel pool.</summary>
internal sealed class SpiceStatePool(SpkKernel[] kernels, SpiceTextKernel definitions)
{
    private const int MaximumChainLength = 64;
    private const int MaximumFrameDepth = 12;
    private readonly ConcurrentDictionary<int, FrameModel> _frames = new();

    private abstract record FrameModel
    {
        internal sealed record Missing : FrameModel;
        internal sealed record Inertial(Basis3d Basis) : FrameModel;
        internal sealed record Fixed(int Parent, Basis3d ToParent) : FrameModel;
        internal sealed record Body(SpicePolynomialBodyFrame Rotation) : FrameModel;
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
        if (!TryFrame(edge.Frame, epoch, 0, out var frame))
        {
            state = default;
            return false;
        }
        CartesianState local = edge.Sample(epoch);
        Vector3d position = frame.ToWorld.Transform(local.PositionKilometers);
        Vector3d velocity = frame.ToWorld.Transform(local.VelocityKilometersPerSecond) +
            Vector3d.Cross(frame.AngularVelocityRadiansPerSecond ?? default, position);
        state = new(position, velocity);
        return true;
    }

    private bool TryFrame(int id, JulianInstant epoch, int depth, out FrameKinematics frame)
    {
        if (depth < MaximumFrameDepth)
            switch (_frames.GetOrAdd(id, CompileFrame))
            {
                case FrameModel.Inertial inertial:
                    frame = new(inertial.Basis, default(Vector3d));
                    return true;
                case FrameModel.Body body:
                    frame = body.Rotation.Sample(epoch);
                    return true;
                case FrameModel.Fixed fixedFrame when TryFrame(fixedFrame.Parent, epoch, depth + 1, out var parent):
                    frame = new(SpiceFixedFrames.Compose(parent.ToWorld, fixedFrame.ToParent),
                        parent.AngularVelocityRadiansPerSecond);
                    return true;
            }
        frame = default;
        return false;
    }

    private FrameModel CompileFrame(int id)
    {
        if (SpiceReferenceFrames.InertialBasis(id) is { } basis)
            return new FrameModel.Inertial(basis);
        if (SpiceReferenceFrames.Body(id).Body is > 0 and var body)
            return Pck(body);
        if (id == 10081)
            return new FrameModel.Fixed(10013, Basis3d.Identity); // EARTH_FIXED follows the standard IAU Earth frame.
        string key = "FRAME_" + id.ToString(CultureInfo.InvariantCulture);
        if (!definitions.Contains(key + "_CLASS") || !definitions.Contains(key + "_CLASS_ID"))
            return new FrameModel.Missing();
        int kind = definitions.GetInteger(key + "_CLASS"), classId = definitions.GetInteger(key + "_CLASS_ID");
        if (kind == 2)
            return Pck(classId);
        if (kind == 4)
        {
            string tk = "TKFRAME_" + classId.ToString(CultureInfo.InvariantCulture);
            string[] parents = definitions.Get(tk + "_RELATIVE");
            if (parents.Length != 1)
                throw new InvalidDataException("A fixed SPICE frame requires a parent frame.");
            string name = parents[0];
            int parent = SpiceReferenceFrames.Id(name);
            if (parent == 0 && name.Equals("EARTH_FIXED", StringComparison.OrdinalIgnoreCase))
                parent = 10081;
            if (parent == 0 && definitions.Contains("FRAME_" + name))
                parent = definitions.GetInteger("FRAME_" + name);
            return parent == 0 ? new FrameModel.Missing() : new FrameModel.Fixed(parent, SpiceFixedFrames.Transform(definitions, tk));
        }
        return new FrameModel.Missing();
    }

    private FrameModel Pck(int body)
    {
        string key = "BODY" + body.ToString(CultureInfo.InvariantCulture);
        if (!definitions.Contains(key + "_POLE_RA") || !definitions.Contains(key + "_POLE_DEC") || !definitions.Contains(key + "_PM"))
            return new FrameModel.Missing();
        return new FrameModel.Body(new(definitions, body, requireRadii: false));
    }
}
