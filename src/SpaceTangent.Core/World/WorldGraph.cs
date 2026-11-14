// SPDX-License-Identifier: GPL-2.0-or-later
using SpaceTangent.Contracts;
using SpaceTangent.Core.Addons;
using SpaceTangent.Core.Ephemerides;
using SpaceTangent.Core.Orbits;
using System.Runtime.CompilerServices;

namespace SpaceTangent.Core.World;

internal enum WorldChannel { State, Orientation, ReferenceFrame }
internal readonly record struct WorldKey(ObjectId Id, WorldChannel Channel)
{
    internal static WorldKey Frame(FrameId id) => new(new(id.Value), WorldChannel.ReferenceFrame);
}

internal readonly record struct WorldValue(bool Available, CartesianState State, FrameKinematics Orientation, ObjectId? Center)
{
    internal static WorldValue FromState(CartesianState state, ObjectId? center = null) => new(true, state, default, center);
    internal static WorldValue FromOrientation(FrameKinematics frame) => new(true, default, frame, null);
}

internal sealed record WorldRule(WorldKey Key, WorldKey[] Dependencies, Func<EvaluationInput, WorldValue> Read,
    bool RequireAll = true);

internal readonly record struct EvaluationInput(JulianInstant Epoch, SolarSystemSample? Solar,
    IReadOnlyDictionary<WorldKey, int> Indices, WorldValue[] Values, WorldGraph Graph,
    WorldEvaluation? History, int RuleIndex = 0)
{
    internal CartesianState State(ObjectId id) => Values[Indices[new(id, WorldChannel.State)]].State;
    internal WorldValue Read(WorldKey key) => Values[Indices[key]];
    internal WorldValue ReadIfPresent(WorldKey key) => Indices.TryGetValue(key, out int index) ? Values[index] : default;
    internal EvaluationInput At(JulianInstant epoch) => Graph.EvaluatePrefix(epoch, RuleIndex);
}

public sealed class WorldSnapshot
{
    private readonly EvaluationInput _input;
    internal WorldSnapshot(EvaluationInput input) => _input = input;

    public JulianInstant Epoch => _input.Epoch;
    public SolarSystemSample? SolarSystem => _input.Solar;

    /// <summary>Evaluates the centre and axes of the mission arc selected at a possibly different TDB epoch.</summary>
    public bool TryMissionReferenceFrame(ObjectId owner, bool orbit, JulianInstant selection,
        out CartesianState origin, out FrameKinematics axes)
    {
        origin = default;
        axes = default;
        if (selection.Scale != TimeScale.Tdb || !_input.Graph.TryMissionArc(owner, selection, out var arc))
            return false;
        return TryState(orbit ? arc.Center : owner, out origin) &&
            WorldGraphBuilder.TryMissionFrame(_input,
                orbit ? arc.TrajectoryFrame : arc.BodyReferenceFrame ?? arc.BodyFrame, out axes);
    }

    public bool TryState(ObjectId id, out CartesianState state)
    {
        bool available = TryValue(new(id, WorldChannel.State), out var value);
        state = value.State;
        return available;
    }

    public bool TryOrientation(ObjectId id, out FrameKinematics orientation)
    {
        bool available = TryValue(new(id, WorldChannel.Orientation), out var value);
        orientation = value.Orientation;
        return available;
    }

    public bool TryFrame(FrameId id, out FrameKinematics orientation)
    {
        bool available = TryValue(WorldKey.Frame(id), out var value);
        orientation = value.Orientation;
        return available;
    }

    /// <summary>Returns the origin used to evaluate an available body's local trajectory.</summary>
    public bool TryStateCenter(ObjectId id, out ObjectId center)
    {
        if (TryValue(new(id, WorldChannel.State), out var value) && value.Center is { } origin)
        {
            center = origin;
            return true;
        }
        center = default;
        return false;
    }

    private bool TryValue(WorldKey key, out WorldValue value)
    {
        value = _input.Indices.TryGetValue(key, out int index) ? _input.Values[index] : default;
        return value.Available;
    }
}

/// <summary>Owns the sequential orbital state of one observer. Use a separate instance for each view.</summary>
public sealed class WorldEvaluation
{
    private readonly ConditionalWeakTable<ScienceSatellite, OrbitHistory> _orbits = new();

    private sealed record OrbitHistory(Sgp4Orbit.Evaluation? Stream);

    internal CartesianState? Sample(ScienceSatellite satellite, JulianInstant epoch) =>
        satellite.TryOrbit(epoch, _orbits.GetValue(satellite, static item => new(item.CreateOrbitalHistory())).Stream);
}

/// <summary>Evaluates translations and orientations once in dependency order at a common TDB epoch.</summary>
public sealed class WorldGraph
{
    private readonly record struct Node(WorldRule Rule, int[] Dependencies);
    private readonly Dictionary<WorldKey, int> _indices = new();
    private readonly Node[] _nodes;
    private readonly ISolarSystemEphemeris? _solar;
    private readonly IReadOnlyDictionary<ObjectId, MissionArc[]> _missions;

    internal WorldGraph(WorldRule[] orderedRules, ISolarSystemEphemeris? solar,
        IReadOnlyDictionary<ObjectId, MissionArc[]> missions)
    {
        _solar = solar;
        _missions = missions;
        _nodes = new Node[orderedRules.Length];
        for (int index = 0; index < orderedRules.Length; index++)
        {
            WorldRule rule = orderedRules[index];
            _nodes[index] = new(rule, rule.Dependencies.Select(key => _indices[key]).ToArray());
            _indices.Add(rule.Key, index);
        }
    }

    public WorldSnapshot Evaluate(JulianInstant tdb) => Evaluate(tdb, null);

    /// <summary>Evaluates a view while retaining the query history of compatible archived trajectories.</summary>
    public WorldSnapshot Evaluate(JulianInstant tdb, WorldEvaluation? history)
    {
        if (tdb.Scale != TimeScale.Tdb)
            throw new ArgumentException("World graph evaluation requires TDB.", nameof(tdb));
        return new(EvaluatePrefix(tdb, _nodes.Length, history));
    }

    internal bool TryMissionArc(ObjectId owner, JulianInstant selection,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out MissionArc? arc)
    {
        arc = _missions.TryGetValue(owner, out var arcs) ? WorldGraphBuilder.FindMissionArc(arcs, selection) : null;
        return arc is not null;
    }

    internal EvaluationInput EvaluatePrefix(JulianInstant tdb, int count, WorldEvaluation? history = null)
    {
        SolarSystemSample? solar = _solar is { } ephemeris && tdb.CompareTo(ephemeris.Start) >= 0 &&
            tdb.CompareTo(ephemeris.End) <= 0 ? ephemeris.Sample(tdb) : null;

        var values = new WorldValue[count];
        var input = new EvaluationInput(tdb, solar, _indices, values, this, history);
        for (int index = 0; index < count; index++)
        {
            Node node = _nodes[index];
            bool ready = true;
            if (node.Rule.RequireAll)
            {
                foreach (int dependency in node.Dependencies)
                    ready &= values[dependency].Available;
            }
            if (ready)
                values[index] = node.Rule.Read(input with { RuleIndex = index });
        }
        return input;
    }
}
