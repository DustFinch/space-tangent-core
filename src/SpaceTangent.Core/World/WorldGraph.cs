// SPDX-License-Identifier: GPL-2.0-or-later
using SpaceTangent.Contracts;
using SpaceTangent.Core.Ephemerides;

namespace SpaceTangent.Core.World;

internal enum WorldChannel { State, Orientation }
internal readonly record struct WorldKey(ObjectId Id, WorldChannel Channel);

internal readonly record struct WorldValue(bool Available, CartesianState State, FrameKinematics Orientation)
{
    internal static WorldValue FromState(CartesianState state) => new(true, state, default);
    internal static WorldValue FromOrientation(FrameKinematics frame) => new(true, default, frame);
}

internal sealed record WorldRule(WorldKey Key, WorldKey[] Dependencies, Func<EvaluationInput, WorldValue> Read);

internal readonly record struct EvaluationInput(JulianInstant Epoch, SolarSystemSample? Solar,
    IReadOnlyDictionary<WorldKey, int> Indices, WorldValue[] Values)
{
    internal CartesianState State(ObjectId id) => Values[Indices[new(id, WorldChannel.State)]].State;
    internal FrameKinematics Orientation(ObjectId id) => Values[Indices[new(id, WorldChannel.Orientation)]].Orientation;
}

public sealed class WorldSnapshot
{
    private readonly IReadOnlyDictionary<WorldKey, int> _indices;
    private readonly WorldValue[] _values;

    internal WorldSnapshot(JulianInstant epoch, IReadOnlyDictionary<WorldKey, int> indices, WorldValue[] values)
    {
        Epoch = epoch;
        _indices = indices;
        _values = values;
    }

    public JulianInstant Epoch { get; }

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

    private bool TryValue(WorldKey key, out WorldValue value)
    {
        value = _indices.TryGetValue(key, out int index) ? _values[index] : default;
        return value.Available;
    }
}

/// <summary>Evaluates translations and orientations once in dependency order at a common TDB epoch.</summary>
public sealed class WorldGraph
{
    private readonly record struct Node(WorldRule Rule, int[] Dependencies);
    private readonly Dictionary<WorldKey, int> _indices = new();
    private readonly Node[] _nodes;
    private readonly De440Ephemeris? _solar;

    internal WorldGraph(WorldRule[] orderedRules, De440Ephemeris? solar)
    {
        _solar = solar;
        _nodes = new Node[orderedRules.Length];
        for (int index = 0; index < orderedRules.Length; index++)
        {
            WorldRule rule = orderedRules[index];
            _nodes[index] = new(rule, rule.Dependencies.Select(key => _indices[key]).ToArray());
            _indices.Add(rule.Key, index);
        }
    }

    public WorldSnapshot Evaluate(JulianInstant tdb)
    {
        if (tdb.Scale != TimeScale.Tdb)
            throw new ArgumentException("World graph evaluation requires TDB.", nameof(tdb));
        SolarSystemSample? solar = _solar is { } ephemeris && tdb.CompareTo(ephemeris.Start) >= 0 &&
            tdb.CompareTo(ephemeris.End) <= 0 ? ephemeris.Sample(tdb) : null;

        var values = new WorldValue[_nodes.Length];
        var input = new EvaluationInput(tdb, solar, _indices, values);
        for (int index = 0; index < _nodes.Length; index++)
        {
            Node node = _nodes[index];
            bool ready = true;
            foreach (int dependency in node.Dependencies)
                ready &= values[dependency].Available;
            if (ready)
                values[index] = node.Rule.Read(input);
        }
        return new(tdb, _indices, values);
    }
}
