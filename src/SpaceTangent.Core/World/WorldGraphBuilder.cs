// SPDX-License-Identifier: GPL-2.0-or-later
using System.Globalization;
using SpaceTangent.Contracts;
using SpaceTangent.Core.Addons;
using SpaceTangent.Core.Ephemerides;
using SpaceTangent.Core.Orbits;
using SpaceTangent.Core.Spice;

namespace SpaceTangent.Core.World;

public sealed class WorldGraphBuilder
{
    private readonly Dictionary<WorldKey, WorldRule> _rules = new();
    private De440Ephemeris? _solar;

    public void AddSolarSystem(De440Ephemeris ephemeris)
    {
        ArgumentNullException.ThrowIfNull(ephemeris);
        if (_solar is not null)
            throw new InvalidOperationException("A solar-system ephemeris was already added.");

        ObjectId[] ids = [SolarSystemSample.BarycenterId, SolarSystemSample.SunId, SolarSystemSample.EarthId,
            SolarSystemSample.MoonId, SolarSystemSample.SaturnBarycenterId];
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
        ObjectId? center = null, ObjectId? orientation = null)
    {
        ArgumentNullException.ThrowIfNull(sample);
        var dependencies = new List<WorldKey>(2);
        if (center is { } centerId)
            dependencies.Add(new(centerId, WorldChannel.State));
        if (orientation is { } orientationId)
            dependencies.Add(new(orientationId, WorldChannel.Orientation));

        Add(new(new(id, WorldChannel.State), [.. dependencies], input =>
        {
            if (sample(input.Epoch) is not { } state)
                return default;
            if (orientation is { } frame && !input.Orientation(frame).TryTransform(state, out state))
                return default;
            if (center is { } parent)
                state += input.State(parent);
            return state.PositionKilometers.IsFinite && state.VelocityKilometersPerSecond.IsFinite
                ? WorldValue.FromState(state, center)
                : default;
        }));
    }

    public void AddOrientation(ObjectId id, Func<JulianInstant, FrameKinematics?> sample)
    {
        ArgumentNullException.ThrowIfNull(sample);
        Add(new(new(id, WorldChannel.Orientation), [], input => sample(input.Epoch) is { } rotation
            ? WorldValue.FromOrientation(rotation)
            : default));
    }

    public void AddTwoVectorOrientation(ObjectId id, FrameAxis primaryAxis, RelativeDirection primary,
        FrameAxis secondaryAxis, RelativeDirection secondary)
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

    public void AddScienceSatellite(ScienceSatellite satellite)
    {
        ArgumentNullException.ThrowIfNull(satellite);
        AddTrajectory(satellite.Id, satellite.TryOrbit, SolarSystemSample.EarthId);
        AddTwoVectorOrientation(satellite.Id, ToAxis(satellite.NadirAxis),
            new(satellite.Id, SolarSystemSample.EarthId, DirectionQuantity.Position),
            ToAxis(satellite.RelativeVelocityAxis),
            new(satellite.Id, SolarSystemSample.EarthId, DirectionQuantity.Velocity));
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
