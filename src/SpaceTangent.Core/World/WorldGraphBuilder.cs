// SPDX-License-Identifier: GPL-2.0-or-later
using System.Globalization;
using SpaceTangent.Contracts;
using SpaceTangent.Core.Addons;
using SpaceTangent.Core.Ephemerides;
using SpaceTangent.Core.Orbits;
using SpaceTangent.Core.Spice;

namespace SpaceTangent.Core.World;

public sealed partial class WorldGraphBuilder
{
    private readonly Dictionary<WorldKey, WorldRule> _rules = new();
    private ISolarSystemEphemeris? _solar;

    public void AddSolarSystem(De440Ephemeris ephemeris) => AddSolarSystem((ISolarSystemEphemeris)ephemeris);

    public void AddSolarSystem(ISolarSystemEphemeris ephemeris)
    {
        ArgumentNullException.ThrowIfNull(ephemeris);
        if (_solar is not null)
            throw new InvalidOperationException("A solar-system ephemeris was already added.");

        var ids = SolarSystemSample.StateIds;
        if (ids.Any(id => _rules.ContainsKey(new(id, WorldChannel.State))))
            throw new InvalidOperationException("A solar-system object already has a trajectory.");

        _solar = ephemeris;
        foreach (ObjectId id in ids)
        {
            Add(new(new(id, WorldChannel.State), [], input => input.Solar is { } sample
                ? WorldValue.FromState(sample.RelativeState(id, SolarSystemSample.BarycenterId))
                : default));
        }
    }

    public void AddTrajectory(ObjectId id, Func<JulianInstant, CartesianState?> sample,
        ObjectId? center = null, ObjectId? orientation = null) =>
        AddTrajectoryCore(id, sample, center, orientation is { } body ? new(body, WorldChannel.Orientation) : null);

    public void AddTrajectoryInFrame(ObjectId id, Func<JulianInstant, CartesianState?> sample,
        FrameId frame, ObjectId? center = null) =>
        AddTrajectoryCore(id, sample, center, WorldKey.Frame(frame));

    private void AddTrajectoryCore(ObjectId id, Func<JulianInstant, CartesianState?> sample,
        ObjectId? center, WorldKey? orientation)
    {
        ArgumentNullException.ThrowIfNull(sample);
        var dependencies = new List<WorldKey>(2);
        if (center is { } centerId)
            dependencies.Add(new(centerId, WorldChannel.State));
        if (orientation is { } orientationKey)
            dependencies.Add(orientationKey);

        Add(new(new(id, WorldChannel.State), [.. dependencies], input =>
        {
            if (sample(input.Epoch) is not { } state)
                return default;
            if (orientation is { } frame && !input.Read(frame).Orientation.TryTransform(state, out state))
                return default;
            if (center is { } parent)
                state += input.State(parent);
            return state.PositionKilometers.IsFinite && state.VelocityKilometersPerSecond.IsFinite
                ? WorldValue.FromState(state, center)
                : default;
        }));
    }

    public void AddOrientation(ObjectId id, Func<JulianInstant, FrameKinematics?> sample) =>
        AddOrientation(new WorldKey(id, WorldChannel.Orientation), sample);

    private void AddOrientation(WorldKey key, Func<JulianInstant, FrameKinematics?> sample)
    {
        ArgumentNullException.ThrowIfNull(sample);
        Add(new(key, [], input => sample(input.Epoch) is { } rotation
            ? WorldValue.FromOrientation(rotation)
            : default));
    }

    public void AddMeanEquator(FrameId id, SpicePolynomialBodyFrame model, JulianInstant? freeze = null)
    {
        ArgumentNullException.ThrowIfNull(model);
        if (freeze is { } epoch)
        {
            Basis3d fixedBasis = model.SampleMeanEquator(epoch).ToWorld;
            AddOrientation(WorldKey.Frame(id), _ => new(fixedBasis, default(Vector3d)));
        }
        else
            AddOrientation(WorldKey.Frame(id), epoch => model.SampleMeanEquator(epoch));
    }

    public void AddTwoVectorOrientation(ObjectId id, FrameAxis primaryAxis, RelativeDirection primary,
        FrameAxis secondaryAxis, RelativeDirection secondary) =>
        AddTwoVectorOrientation(id, primaryAxis, primary, secondaryAxis, secondary, null);

    private void AddTwoVectorOrientation(ObjectId id, FrameAxis primaryAxis, RelativeDirection primary,
        FrameAxis secondaryAxis, RelativeDirection secondary, Basis3d? bodyToFrame)
    {
        SignedAxis first = ResolveAxis(primaryAxis), second = ResolveAxis(secondaryAxis);
        if (first.Index == second.Index)
            throw new ArgumentException("Two-vector orientation axes must differ.", nameof(secondaryAxis));
        if (primary.Quantity is < DirectionQuantity.Position or > DirectionQuantity.Velocity ||
            secondary.Quantity is < DirectionQuantity.Position or > DirectionQuantity.Velocity)
            throw new ArgumentException("Unsupported two-vector direction.");

        var dependencies = new[] { primary.Observer, primary.Target, secondary.Observer, secondary.Target }
            .Distinct().Select(body => new WorldKey(body, WorldChannel.State)).ToArray();
        Add(new(new(id, WorldChannel.Orientation), dependencies, input =>
        {
            if (!TwoVectorBasis.TryCreate(first, ReadDirection(input, primary), second, ReadDirection(input, secondary),
                    out var basis))
                return default;
            if (bodyToFrame is { } local)
                basis = SampledMissionCatalog.Multiply(basis, local);
            return WorldValue.FromOrientation(new(basis, null));
        }));
    }

    public ObjectId AddSpiceSpacecraft(SpiceSpacecraft spacecraft)
    {
        ArgumentNullException.ThrowIfNull(spacecraft);
        ObjectId id = NaifObject(spacecraft.Target);
        AddTrajectory(id, epoch => spacecraft.TrySample(epoch, out var state) ? state : null,
            NaifObject(spacecraft.Center));
        if (spacecraft.AttitudeFrame is not null)
        {
            AddOrientation(id, epoch => spacecraft.TryAttitude(epoch, out var attitude)
                ? new FrameKinematics(attitude, null)
                : null);
        }
        return id;
    }

    public void AddLocalTleSatellite(LocalTleSatellite satellite)
    {
        ArgumentNullException.ThrowIfNull(satellite);
        AddTrajectory(satellite.Id, epoch => satellite.SampleInWorld(epoch), SolarSystemSample.EarthId);
    }

    public void AddScienceSatellite(ScienceSatellite satellite) => AddScienceSatellite(satellite, null);

    public void AddScienceSatellite(ScienceSatellite satellite, LocalTleSatellite? updatedElements)
    {
        ArgumentNullException.ThrowIfNull(satellite);
        if (updatedElements is not null && updatedElements.Id != satellite.Id)
            throw new ArgumentException("Updated elements must use the same satellite ID.", nameof(updatedElements));
        bool preferUpdate = updatedElements?.Epoch.CompareTo(satellite.Epoch) > 0;
        AddTrajectory(satellite.Id, epoch => preferUpdate
            ? updatedElements!.SampleInWorld(epoch) ?? satellite.TryOrbit(epoch)
            : satellite.TryOrbit(epoch) ?? updatedElements?.SampleInWorld(epoch), SolarSystemSample.EarthId);
        AddTwoVectorOrientation(satellite.Id, ToAxis(satellite.NadirAxis),
            new(satellite.Id, SolarSystemSample.EarthId, DirectionQuantity.Position),
            ToAxis(satellite.RelativeVelocityAxis),
            new(satellite.Id, SolarSystemSample.EarthId, DirectionQuantity.Velocity), satellite.InstrumentToFrame);
    }

    public void AddSampledSpacecraft(SscSampledSpacecraft spacecraft)
    {
        ArgumentNullException.ThrowIfNull(spacecraft);
        WorldKey[] centers = spacecraft.Centers.Select(id => new WorldKey(id, WorldChannel.State)).ToArray();
        Add(new(new(spacecraft.Id, WorldChannel.State), centers, input =>
        {
            if (!spacecraft.TrySampleRelative(input.Epoch, out ObjectId center, out CartesianState local))
                return default;
            CartesianState state = input.State(center) + local;
            return state.PositionKilometers.IsFinite && state.VelocityKilometersPerSecond.IsFinite
                ? WorldValue.FromState(state, center)
                : default;
        }));
        AddTwoVectorOrientation(spacecraft.Id, spacecraft.PrimaryAxis, spacecraft.Primary,
            spacecraft.SecondaryAxis, spacecraft.Secondary);
    }

    public ObjectId AddSpkBody(SpkKernel kernel, int target, int center, SpicePolynomialBodyFrame? bodyFrame = null)
    {
        ArgumentNullException.ThrowIfNull(kernel);
        if (bodyFrame is not null && bodyFrame.BodyId != target)
            throw new ArgumentException("The PCK body must match the SPK target.", nameof(bodyFrame));
        ObjectId id = NaifObject(target);
        AddTrajectory(id, epoch => kernel.TryState(target, center, epoch, out var state) ? state : null, NaifObject(center));
        if (bodyFrame is not null)
            AddOrientation(id, epoch => bodyFrame.Sample(epoch));
        return id;
    }

    public void AddHuygens(SscHuygensTrack track)
    {
        ArgumentNullException.ThrowIfNull(track);
        WorldKey[] dependencies = [new(track.Carrier, WorldChannel.State), new(track.Carrier, WorldChannel.Orientation),
            new(SscHuygensTrack.TitanId, WorldChannel.State), new(SscHuygensTrack.TitanId, WorldChannel.Orientation)];
        Add(new(new(track.Id, WorldChannel.State), dependencies, input =>
        {
            if (!track.TryLocalState(input.Epoch, out ObjectId center, out ObjectId? rotating, out var local))
                return default;
            WorldValue parent = input.Read(new(center, WorldChannel.State));
            if (!parent.Available || (rotating is { } frame && !TransformLocal(input, frame, local, out local)))
                return default;
            CartesianState state = parent.State + local;
            return state.PositionKilometers.IsFinite && state.VelocityKilometersPerSecond.IsFinite
                ? WorldValue.FromState(state, center)
                : default;
        }, RequireAll: false));

        var stateKey = new WorldKey(track.Id, WorldChannel.State);
        var carrierFrameKey = new WorldKey(track.Carrier, WorldChannel.Orientation);
        var saturnKey = new WorldKey(NaifObject(699), WorldChannel.State);
        var titanKey = new WorldKey(SscHuygensTrack.TitanId, WorldChannel.State);
        Add(new(new(track.Id, WorldChannel.Orientation), [stateKey, carrierFrameKey, saturnKey, titanKey], input =>
        {
            var probe = input.Read(stateKey);
            if (!probe.Available)
                return default;

            Basis3d frame;
            if (probe.Center == track.Carrier)
            {
                var carrier = input.Read(carrierFrameKey);
                if (!carrier.Available)
                    return default;
                frame = carrier.Orientation.ToWorld;
            }
            else
            {
                var target = input.Read(input.Epoch.CompareTo(track.Landing) < 0 ? saturnKey : titanKey);
                if (!target.Available)
                    return default;
                var relative = target.State - probe.State;
                if (!TwoVectorBasis.TryCreate(new(2, 1), relative.PositionKilometers,
                    new(1, 1), relative.VelocityKilometersPerSecond, out frame))
                    return default;
            }

            var rotation = track.BodyToFrame(input.Epoch);
            return WorldValue.FromOrientation(new(new(frame.Transform(rotation.X),
                frame.Transform(rotation.Y), frame.Transform(rotation.Z)), null));
        }, RequireAll: false));
    }

    private static bool TransformLocal(EvaluationInput input, ObjectId id, CartesianState local, out CartesianState relative)
    {
        var frameKey = new WorldKey(id, WorldChannel.Orientation);
        WorldValue frame = input.Read(frameKey);
        relative = default;
        if (!frame.Available)
            return false;
        if (frame.Orientation.AngularVelocityRadiansPerSecond is not null)
            return frame.Orientation.TryTransform(local, out relative);

        // The sampled carrier supplies an orientation without rates. Differentiate the mount offset only,
        // keeping the carrier's ephemeris velocity and never crossing a carrier phase center transition.
        const double StepSeconds = .25;
        Vector3d offset = frame.Orientation.ToWorld.Transform(local.PositionKilometers);
        var centerKey = new WorldKey(id, WorldChannel.State);
        ObjectId? center = input.Read(centerKey).Center;
        bool earlier = OffsetAt(-StepSeconds, out var before);
        bool later = OffsetAt(StepSeconds, out var after);
        if (!earlier && !later)
            return false;
        if (!earlier)
            before = offset;
        if (!later)
            after = offset;
        relative = new(offset, frame.Orientation.ToWorld.Transform(local.VelocityKilometersPerSecond) +
            (after - before) / (earlier && later ? 2 * StepSeconds : StepSeconds));
        return true;

        bool OffsetAt(double seconds, out Vector3d value)
        {
            EvaluationInput other = input.At(input.Epoch.AddSeconds(seconds));
            WorldValue otherCenter = other.Read(centerKey);
            WorldValue otherFrame = other.Read(frameKey);
            value = otherFrame.Orientation.ToWorld.Transform(local.PositionKilometers);
            return otherCenter.Available && otherCenter.Center == center && otherFrame.Available;
        }
    }

    public WorldGraph Build()
    {
        var dependencies = new Dictionary<WorldKey, int>();
        var consumers = new Dictionary<WorldKey, List<WorldKey>>();
        foreach (WorldRule rule in _rules.Values)
        {
            dependencies.Add(rule.Key, rule.Dependencies.Length);
            foreach (WorldKey dependency in rule.Dependencies)
            {
                if (!_rules.ContainsKey(dependency))
                    throw new InvalidOperationException($"Missing world dependency {Describe(dependency)} for {Describe(rule.Key)}.");
                if (!consumers.TryGetValue(dependency, out var list))
                    consumers.Add(dependency, list = []);
                list.Add(rule.Key);
            }
        }

        var queue = new Queue<WorldKey>(dependencies.Where(pair => pair.Value == 0).Select(pair => pair.Key));
        var sorted = new List<WorldRule>(_rules.Count);
        while (queue.TryDequeue(out var key))
        {
            sorted.Add(_rules[key]);
            if (!consumers.TryGetValue(key, out var list))
                continue;
            foreach (WorldKey consumer in list)
            {
                if (--dependencies[consumer] == 0)
                    queue.Enqueue(consumer);
            }
        }

        if (sorted.Count != _rules.Count)
            throw new InvalidOperationException("World dependency cycle among " +
                string.Join(", ", dependencies.Where(pair => pair.Value > 0).Select(pair => Describe(pair.Key))) + ".");
        return new([.. sorted], _solar);
    }

    private void Add(WorldRule rule)
    {
        if (rule.Key.Id == default)
            throw new ArgumentException("World objects require a valid ID.", nameof(rule));
        if (!_rules.TryAdd(rule.Key, rule))
            throw new InvalidOperationException($"World node {Describe(rule.Key)} already exists.");
    }

    private static Vector3d ReadDirection(EvaluationInput input, RelativeDirection direction)
    {
        CartesianState relative = input.State(direction.Target) - input.State(direction.Observer);
        return direction.Quantity == DirectionQuantity.Position ? relative.PositionKilometers : relative.VelocityKilometersPerSecond;
    }

    private static SignedAxis ResolveAxis(FrameAxis axis)
    {
        if (axis is < FrameAxis.X or > FrameAxis.NegativeZ)
            throw new ArgumentOutOfRangeException(nameof(axis));
        int index = (int)axis;
        return new(index / 2, index % 2 == 0 ? 1 : -1);
    }

    private static FrameAxis ToAxis(SignedAxis axis) => (FrameAxis)(axis.Index * 2 + (axis.Sign < 0 ? 1 : 0));

    private static ObjectId NaifObject(int id) => id switch
    {
        0 => SolarSystemSample.BarycenterId,
        6 => SolarSystemSample.SaturnBarycenterId,
        10 => SolarSystemSample.SunId,
        301 => SolarSystemSample.MoonId,
        399 => SolarSystemSample.EarthId,
        _ => new("naif/" + id.ToString(CultureInfo.InvariantCulture))
    };

    private static string Describe(WorldKey key) => $"{key.Id.Value} {key.Channel}";
}
