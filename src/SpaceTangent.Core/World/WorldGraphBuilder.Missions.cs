// SPDX-License-Identifier: GPL-2.0-or-later
using SpaceTangent.Contracts;
using SpaceTangent.Core.Addons;
using SpaceTangent.Core.Orbits;
using SpaceTangent.Core.Ephemerides;
using SpaceTangent.Core.Observing;
using SpaceTangent.Core.Time;

namespace SpaceTangent.Core.World;

public sealed partial class WorldGraphBuilder
{
    private readonly Dictionary<ObjectId, MissionArc[]> _missionArcs = new();

    public void AddMeanEarthRotation()
    {
        AddOrientation(SolarSystemSample.EarthId, epoch =>
        {
            try
            {
                var (sin, cos) = Math.SinCos(EarthSurfaceLighting.GreenwichSiderealAngle(epoch, DeltaTModel.MorrisonStephenson2004));
                return new(new(new(cos, sin, 0), new(-sin, cos, 0), new(0, 0, 1)),
                    new Vector3d(0, 0, Math.Tau / 86164.0905));
            }
            catch (ArgumentOutOfRangeException) { return null; }
        });
    }

    public void AddSampledMission(SampledMission mission)
    {
        ArgumentNullException.ThrowIfNull(mission);
        if (mission.Arcs.Count == 0)
            throw new ArgumentException("A sampled mission requires at least one arc.", nameof(mission));
        MissionArc[] arcs = mission.Arcs.ToArray();
        for (int index = 0; index < arcs.Length; index++)
        {
            var arc = arcs[index];
            if (arc.Start.Scale != TimeScale.Tdb || arc.End.Scale != TimeScale.Tdb ||
                arc.End.CompareTo(arc.Start) <= 0 || (index > 0 && arc.Start.CompareTo(arcs[index - 1].End) < 0))
                throw new ArgumentException("Sampled mission arcs must be ordered, non-overlapping TDB intervals.", nameof(mission));
        }
        WorldKey[] trajectoryKeys = arcs.SelectMany(arc => FrameKeys(arc.TrajectoryFrame).Append(StateKey(arc.Center)))
            .Distinct().ToArray();
        var stateKey = StateKey(mission.Id);
        Add(new(stateKey, trajectoryKeys, input =>
        {
            var arc = FindMissionArc(arcs, input.Epoch);
            if (arc?.LocalState(input.Epoch) is not { } local)
                return default;
            var parent = input.Read(StateKey(arc.Center));
            if (!parent.Available || !TryMissionFrame(input, arc.TrajectoryFrame, out var frame))
                return default;
            CartesianState relative;
            if (arc.TrajectoryFrame is MissionFrame.BodyFixed rotating)
            {
                if (frame.AngularVelocityRadiansPerSecond is { } angularVelocity)
                {
                    Vector3d position = frame.ToWorld.Transform(local.PositionKilometers);
                    // Native entity-state trajectories cross omega with the unrotated position in source axes.
                    Vector3d offset = arc.UsePhysicalBodyFixedVelocity ? position : arc.NativeVelocitySource is { } source
                        ? source.Transform(local.PositionKilometers) : SscSampledSpacecraft.ToEquatorial(local.PositionKilometers);
                    relative = new(position, frame.ToWorld.Transform(local.VelocityKilometersPerSecond) + Vector3d.Cross(angularVelocity, offset));
                }
                else
                {
                    if (rotating.LocalAxes is { } axes)
                        local = new(axes.Transform(local.PositionKilometers), axes.Transform(local.VelocityKilometersPerSecond));
                    if (!TransformLocal(input, rotating.Body, local, out relative))
                        return default;
                }
            }
            else
                relative = new(frame.ToWorld.Transform(local.PositionKilometers), frame.ToWorld.Transform(local.VelocityKilometersPerSecond));
            CartesianState state = parent.State + relative;
            return state.PositionKilometers.IsFinite && state.VelocityKilometersPerSecond.IsFinite
                ? WorldValue.FromState(state, arc.Center) : default;
        }, RequireAll: false));

        WorldKey[] orientationKeys = arcs.SelectMany(arc => FrameKeys(arc.BodyFrame)).Append(stateKey).Distinct().ToArray();
        Add(new(new(mission.Id, WorldChannel.Orientation), orientationKeys, input =>
        {
            var arc = FindMissionArc(arcs, input.Epoch);
            return arc is not null && input.Read(stateKey).Available && TryMissionFrame(input, arc.BodyFrame, out var frame)
                ? WorldValue.FromOrientation(frame) : default;
        }, RequireAll: false));

        _missionArcs.Add(mission.Id, arcs);
    }

    internal static MissionArc? FindMissionArc(MissionArc[] arcs, JulianInstant epoch)
    {
        int low = 0, high = arcs.Length;
        while (low < high)
        {
            int middle = low + (high - low) / 2;
            if (epoch.CompareTo(arcs[middle].End) < 0)
                high = middle;
            else
                low = middle + 1;
        }
        return low < arcs.Length && epoch.CompareTo(arcs[low].Start) >= 0 ? arcs[low] : null;
    }

    private static WorldKey StateKey(ObjectId id) => new(id, WorldChannel.State);

    private static IEnumerable<WorldKey> FrameKeys(MissionFrame frame) => frame switch
    {
        MissionFrame.BodyFixed fixedFrame => [StateKey(fixedFrame.Body), new(fixedFrame.Body, WorldChannel.Orientation)],
        MissionFrame.Rotating rotating => FrameKeys(rotating.Parent),
        MissionFrame.RelativeAttitude attitude => FrameKeys(attitude.Parent),
        MissionFrame.TwoVector two => DirectionKeys(two.Primary).Concat(DirectionKeys(two.Secondary)),
        _ => []
    };

    internal static bool TryMissionFrame(EvaluationInput input, MissionFrame definition, out FrameKinematics frame)
    {
        switch (definition)
        {
            case MissionFrame.Spice spice when spice.Sample(input.Epoch) is { } pointing:
                frame = pointing;
                return true;
            case MissionFrame.Inertial inertial:
                frame = new(inertial.ToWorld, default(Vector3d));
                return true;
            case MissionFrame.BodyFixed fixedFrame:
                var value = input.ReadIfPresent(new(fixedFrame.Body, WorldChannel.Orientation));
                frame = fixedFrame.LocalAxes is { } axes && value.Available
                    ? new(SampledMissionCatalog.Multiply(value.Orientation.ToWorld, axes), value.Orientation.AngularVelocityRadiansPerSecond)
                    : value.Orientation;
                return value.Available;
            case MissionFrame.Rotating rotating when TryMissionFrame(input, rotating.Parent, out var parent):
                frame = ComposeAttitudes(parent, rotating.Local(input.Epoch));
                return true;
            case MissionFrame.RelativeAttitude attitude when TryMissionFrame(input, attitude.Parent, out var reference) &&
                attitude.Local(input.Epoch) is { } local:
                frame = ComposeAttitudes(reference, local);
                return true;
            case MissionFrame.TwoVector two when TryMissionDirection(input, two.Primary, out var primary) &&
                TryMissionDirection(input, two.Secondary, out var secondary) &&
                TwoVectorBasis.TryCreate(ResolveAxis(two.PrimaryAxis), primary, ResolveAxis(two.SecondaryAxis), secondary, out var basis):
                frame = new(basis, null);
                return true;
            default:
                frame = default;
                return false;
        }
    }

    private static FrameKinematics ComposeAttitudes(FrameKinematics parent, FrameKinematics local)
    {
        Vector3d? angularVelocity = parent.AngularVelocityRadiansPerSecond is { } parentRate &&
            local.AngularVelocityRadiansPerSecond is { } localRate
                ? parentRate + parent.ToWorld.Transform(localRate) : null;
        return new(SampledMissionCatalog.Multiply(parent.ToWorld, local.ToWorld), angularVelocity);
    }

    private static IEnumerable<WorldKey> DirectionKeys(MissionDirection direction) => direction switch
    {
        MissionDirection.Relative relative => [StateKey(relative.Direction.Observer), StateKey(relative.Direction.Target)],
        MissionDirection.Constant constant => FrameKeys(constant.Frame),
        _ => []
    };

    private static bool TryMissionDirection(EvaluationInput input, MissionDirection direction, out Vector3d vector)
    {
        switch (direction)
        {
            case MissionDirection.Relative { Direction: var relative } when
                input.ReadIfPresent(StateKey(relative.Observer)).Available && input.ReadIfPresent(StateKey(relative.Target)).Available:
                vector = ReadDirection(input, relative);
                return true;
            case MissionDirection.Constant constant when TryMissionFrame(input, constant.Frame, out var frame):
                vector = frame.ToWorld.Transform(constant.Vector);
                return true;
            default:
                vector = default;
                return false;
        }
    }
}
