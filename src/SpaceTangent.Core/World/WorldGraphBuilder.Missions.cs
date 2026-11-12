// SPDX-License-Identifier: GPL-2.0-or-later
using SpaceTangent.Contracts;
using SpaceTangent.Core.Addons;
using SpaceTangent.Core.Orbits;

namespace SpaceTangent.Core.World;

public sealed partial class WorldGraphBuilder
{
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
            var arc = ActiveArc(input.Epoch);
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
                    // The original entity-state API applies its angular term to the unrotated source position.
                    relative = new(frame.ToWorld.Transform(local.PositionKilometers),
                        frame.ToWorld.Transform(local.VelocityKilometersPerSecond) + Vector3d.Cross(angularVelocity,
                            SscSampledSpacecraft.ToEquatorial(local.PositionKilometers)));
                }
                else if (!TransformLocal(input, rotating.Body, local, out relative))
                    return default;
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
            var arc = ActiveArc(input.Epoch);
            return arc is not null && input.Read(stateKey).Available && TryMissionFrame(input, arc.BodyFrame, out var frame)
                ? WorldValue.FromOrientation(frame) : default;
        }, RequireAll: false));

        MissionArc? ActiveArc(JulianInstant epoch)
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
    }

    private static WorldKey StateKey(ObjectId id) => new(id, WorldChannel.State);

    private static IEnumerable<WorldKey> FrameKeys(MissionFrame frame) => frame switch
    {
        MissionFrame.BodyFixed fixedFrame => [StateKey(fixedFrame.Body), new(fixedFrame.Body, WorldChannel.Orientation)],
        MissionFrame.TwoVector two => [StateKey(two.Primary.Observer), StateKey(two.Primary.Target),
            StateKey(two.Secondary.Observer), StateKey(two.Secondary.Target)],
        _ => []
    };

    private static bool TryMissionFrame(EvaluationInput input, MissionFrame definition, out FrameKinematics frame)
    {
        switch (definition)
        {
            case MissionFrame.Inertial inertial:
                frame = new(inertial.ToWorld, default(Vector3d));
                return true;
            case MissionFrame.BodyFixed fixedFrame:
                var value = input.Read(new(fixedFrame.Body, WorldChannel.Orientation));
                frame = value.Orientation;
                return value.Available;
            case MissionFrame.TwoVector two when DirectionAvailable(input, two.Primary) && DirectionAvailable(input, two.Secondary) &&
                TwoVectorBasis.TryCreate(ResolveAxis(two.PrimaryAxis), ReadDirection(input, two.Primary),
                    ResolveAxis(two.SecondaryAxis), ReadDirection(input, two.Secondary), out var basis):
                frame = new(basis, null);
                return true;
            default:
                frame = default;
                return false;
        }
    }

    private static bool DirectionAvailable(EvaluationInput input, RelativeDirection direction) =>
        input.Read(StateKey(direction.Observer)).Available && input.Read(StateKey(direction.Target)).Available;
}
