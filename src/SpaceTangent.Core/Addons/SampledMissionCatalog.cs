// SPDX-License-Identifier: GPL-2.0-or-later
using System.Globalization;
using System.Text.Json;
using SpaceTangent.Contracts;
using SpaceTangent.Core.Time;
using SpaceTangent.Core.Spice;
using SpaceTangent.Core.World;

namespace SpaceTangent.Core.Addons;

public sealed record SampledMission(ObjectId Id, string Name, string? MeshPath, double RadiusKilometers,
    IReadOnlyList<MissionArc> Arcs)
{
    public MissionObservation? Observation { get; init; }
    public MissionDisplay? Display { get; init; }
    public JulianInstant Start => Arcs[0].Start;
    public JulianInstant End => Arcs[^1].End;
    public IReadOnlyList<MissionGeometrySpan>? Geometry { get; init; }
    public MissionGeometryBounds? GeometryBounds { get; init; }
    public double? NavigationRadiusKilometers { get; init; }
}

public sealed record MissionArc(JulianInstant Start, JulianInstant End, ObjectId Center,
    Func<JulianInstant, CartesianState?> LocalState, MissionFrame TrajectoryFrame, MissionFrame BodyFrame)
{
    /// <summary>The frame on which the model's local attitude is based.</summary>
    public MissionFrame? BodyReferenceFrame { get; init; }
    internal Basis3d? NativeVelocitySource { get; init; }
    internal bool UsePhysicalBodyFixedVelocity { get; init; }
}

public abstract record MissionFrame
{
    private MissionFrame() { }

    public sealed record Inertial(Basis3d ToWorld) : MissionFrame;
    public sealed record Spice(Func<JulianInstant, FrameKinematics?> Sample) : MissionFrame;
    public sealed record BodyFixed(ObjectId Body) : MissionFrame
    {
        internal Basis3d? LocalAxes { get; init; }
    }
    public sealed record Rotating(MissionFrame Parent, Func<JulianInstant, FrameKinematics> Local) : MissionFrame;
    internal sealed record RelativeAttitude(MissionFrame Parent, Func<JulianInstant, FrameKinematics?> Local) : MissionFrame
    {
        internal bool LocalRateOnly { get; init; }
    }
    public sealed record TwoVector(FrameAxis PrimaryAxis, MissionDirection Primary,
        FrameAxis SecondaryAxis, MissionDirection Secondary) : MissionFrame
    {
        public TwoVector(FrameAxis primaryAxis, RelativeDirection primary, FrameAxis secondaryAxis, RelativeDirection secondary)
            : this(primaryAxis, new MissionDirection.Relative(primary), secondaryAxis, new MissionDirection.Relative(secondary)) { }
    }
}

public abstract record MissionDirection
{
    private MissionDirection() { }
    public sealed record Relative(RelativeDirection Direction) : MissionDirection;
    public sealed record Constant(Vector3d Vector, MissionFrame Frame) : MissionDirection;
}

/// <summary>Reads local sampled mission arcs in the version 1.0 JSON format.</summary>
public static partial class SampledMissionCatalog
{
    private static readonly string[] CalendarFormats = [
        "yyyy-MM-dd", "yyyy-MM-dd HH:mm", "yyyy-MM-dd'T'HH:mm", "yyyy-MM-dd HH:mm:ss.FFFFFFF", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF"
    ];
    private static readonly MissionFrame.Inertial Equatorial = new(Basis3d.Identity);
    private static readonly MissionFrame.Inertial Ecliptic = new(new(new(1, 0, 0),
        SscSampledSpacecraft.ToEquatorial(new(0, 1, 0)), SscSampledSpacecraft.ToEquatorial(new(0, 0, 1))));
    private static readonly JulianInstant Minimum = new(0, 0, TimeScale.Tdb);
    private static readonly JulianInstant Maximum = new(JulianInstant.MaximumDay, JulianInstant.NanosecondsPerDay - 1, TimeScale.Tdb);

    public static IReadOnlyList<SampledMission> Load(string catalogPath, string assetRoot,
        Func<string, ObjectId?> resolveBody, Action<string>? warning = null, CancellationToken cancellation = default) =>
        Load([catalogPath], assetRoot, resolveBody, warning, cancellation);

    public static IReadOnlyList<SampledMission> Load(IEnumerable<string> catalogPaths, string assetRoot,
        Func<string, ObjectId?> resolveBody, Action<string>? warning = null, CancellationToken cancellation = default) =>
        LoadContent(catalogPaths, assetRoot, resolveBody, warning, cancellation).Missions;

    public static SampledMissionContent LoadContent(IEnumerable<string> catalogPaths, string assetRoot,
        Func<string, ObjectId?> resolveBody, Action<string>? warning = null, CancellationToken cancellation = default) =>
        LoadContent(catalogPaths, assetRoot, resolveBody, warning, cancellation, null);

    internal static SampledMissionContent LoadContent(IEnumerable<string> catalogPaths, string assetRoot,
        Func<string, ObjectId?> resolveBody, Action<string>? warning, CancellationToken cancellation,
        Func<IReadOnlyList<SpiceResources.KernelGroup>, SpiceResources>? bindPool)
    {
        ArgumentNullException.ThrowIfNull(catalogPaths);
        ArgumentNullException.ThrowIfNull(resolveBody);
        using var swarmReads = new SwarmReadScope();
        using var documents = MissionDocumentSet.Open(catalogPaths, assetRoot, resolveBody, warning, cancellation);
        return new MissionRuntimeBinding(documents).Bind(bindPool);
    }

    private static bool HasLegacySampledTrajectory(JsonElement body) =>
        body.TryGetProperty("orbit", out var orbit) && UsesLegacySamples(orbit) ||
        body.TryGetProperty("timeline", out var phases) &&
        phases.EnumerateArray().Any(phase => UsesLegacySamples(phase.GetProperty("orbit")));

    private static bool UsesLegacySamples(JsonElement orbit) =>
        Text(orbit, "kind") == "sampled" && orbit.TryGetProperty("sampleConvention", out var convention) &&
        convention.ValueEquals(LegacySampleConvention);

    private static bool HasCompoundTrajectory(JsonElement body) =>
        body.TryGetProperty("orbit", out var orbit) && IsCompound(orbit) ||
        body.TryGetProperty("timeline", out var phases) &&
        phases.EnumerateArray().Any(phase => IsCompound(phase.GetProperty("orbit")));

    private static bool IsCompound(JsonElement orbit) =>
        Text(orbit, "kind") is "chebyshev" or "combination";

    private static bool IsArchivedFixedModel(JsonElement body) =>
        body.TryGetProperty("appearance", out var appearance) && appearance.TryGetProperty("model", out _) &&
        body.TryGetProperty("timeline", out var phases) && phases.GetArrayLength() == 1 &&
        phases[0].TryGetProperty("orbit", out var orbit) && IsFixedEarthMeanEquator(orbit);

    private static bool IsFixedEarthMeanEquator(JsonElement orbit) =>
        orbit.TryGetProperty("kind", out var kind) && kind.ValueEquals("fixed") &&
        orbit.TryGetProperty("frame", out var frame) && frame.TryGetProperty("kind", out var frameKind) &&
        frameKind.ValueEquals("meanEquator") && frame.TryGetProperty("body", out var body) && body.ValueEquals("Sol/Earth");

    private static bool IsTimedComponent(JsonElement item) =>
        item.TryGetProperty("class", out var kind) && kind.ValueEquals("component") && item.TryGetProperty("shown", out _) &&
        item.TryGetProperty("displayName", out var display) &&
        Text(item, "path").StartsWith(Text(item, "parent") + "/" + display.GetString() + " (", StringComparison.Ordinal);

    private static SampledMission ReadItem(JsonElement item, Func<string, ObjectId> resolve, string root, string directory,
        SpiceResources resources, Func<string, string?> instrumentFrame, Action<string>? warning, CancellationToken cancellation)
    {
        if (IsVisualizer(item))
            return ReadVisualizer(item, resolve, resources);
        string name = Text(item, "name");
        JulianInstant start = Date(item, "startTime", Minimum);
        JulianInstant finish = Date(item, "endTime", Maximum);
        if (item.TryGetProperty("geometry", out var special) && Text(special, "type") == "Spice" &&
            special.TryGetProperty("instrName", out _))
            return ReadSpiceInstrument(item, special, start, finish, name, resolve, resources, instrumentFrame);
        if (special.ValueKind == JsonValueKind.Object && Text(special, "type") == "Observations")
        {
            if (finish.CompareTo(start) <= 0)
                throw new InvalidDataException($"{name}: observation availability must end after it starts.");
            var target = resolve(Text(item, "center"));
            var observation = ReadObservation(special, target, resolve);
            return new(resolve(name), name, null, .001,
                [new(start, finish, target, _ => new CartesianState(default, default), Equatorial, Equatorial)])
                { Geometry = [], Observation = observation };
        }
        var bounds = special.ValueKind == JsonValueKind.Object && Text(special, "type") == "TimeSwitched"
            ? new GeometryBoundsBuilder() : null;
        IReadOnlyList<MissionGeometrySpan>? geometry = special.ValueKind == JsonValueKind.Object
            ? ReadGeometry(special, root, directory, name, start, finish, resolve, warning, cancellation, bounds) : null;
        if (geometry is not null && item.TryGetProperty("visible", out var visible) && !visible.GetBoolean())
            geometry = Array.AsReadOnly(geometry.Select(span => span with { Shape = span.Shape switch
            {
                { Axes: { } axes } shape => shape with { Axes = axes with { Visible = false } },
                { Swarm: { } swarm } shape => shape with { Swarm = swarm with { Visible = false } },
                _ => span.Shape
            } }).ToArray());
        var arcs = item.TryGetProperty("arcs", out var sequence) ? sequence.EnumerateArray().ToArray() : [item];
        if (arcs.Length is < 1 or > 128)
            throw new InvalidDataException($"{name}: a mission must contain one to 128 arcs.");
        var result = new List<MissionArc>();
        foreach (JsonElement arc in arcs)
        {
            cancellation.ThrowIfCancellationRequested();
            var begins = Date(arc, "startTime", start);
            var end = Date(arc, "endTime", finish);
            if (begins.CompareTo(start) < 0 || end.CompareTo(begins) <= 0 || end.CompareTo(finish) > 0)
                throw new InvalidDataException($"{name}: mission arcs must be ordered and non-overlapping within the mission window.");
            string centerName = Text(arc, "center");
            var center = resolve(centerName);
            var trajectoryFrame = Frame(arc, "trajectoryFrame", resolve, name, center, resources);
            var bodyReference = Frame(arc, "bodyFrame", resolve, name, center, resources);
            var bodyFrame = Rotation(bodyReference, arc,
                resources, root, directory, cancellation);
            var sample = arc.TryGetProperty("trajectory", out var trajectory)
                ? ReadTrajectory(trajectory, root, directory, begins, end, trajectoryFrame, resources, centerName)
                : geometry?.Any(span => span.Shape is { ParticleEmitters: not null } or { Swarm: not null }) == true
                    ? Constant(default)
                    : throw new InvalidDataException($"{name}: a mission arc requires a trajectory.");
            result.Add(new(begins, end, center, sample, trajectoryFrame, bodyFrame)
                { BodyReferenceFrame = bodyReference is MissionFrame.Spice ? Equatorial : bodyReference });
            start = end;
        }
        double radius = geometry is { Count: > 0 } ? geometry.Max(span => span.Shape.RadiusKilometers) : .005;
        return new(resolve(name), name, geometry?.FirstOrDefault()?.Shape.MeshPath, radius, result.AsReadOnly())
            { Geometry = geometry, GeometryBounds = bounds?.Build() };
    }

    private static MissionFrame Frame(JsonElement arc, string property, Func<string, ObjectId> resolve,
        string self, ObjectId center, SpiceResources resources)
    {
        if (!arc.TryGetProperty(property, out var value))
            return Equatorial;
        return ReadFrame(value, 0);

        MissionFrame ReadFrame(JsonElement frame, int depth)
        {
            if (depth >= 16)
                throw new InvalidDataException("Mission frame definitions exceed 16 nested levels.");
            string type = frame.ValueKind == JsonValueKind.String ? frame.GetString()! : Text(frame, "type");
            return type switch
            {
                "EclipticJ2000" => Ecliptic,
                "ICRF" or "EquatorJ2000" => Equatorial,
                "Spice" when property == "bodyFrame" => SpiceOrientation(Text(frame, "name"), resources),
                "BodyFixed" => new MissionFrame.BodyFixed(resolve(Text(frame, "body"))),
                "TwoVector" => new MissionFrame.TwoVector(Axis(Text(frame, "primaryAxis")), Direction(frame.GetProperty("primary"), depth),
                    Axis(Text(frame, "secondaryAxis")), Direction(frame.GetProperty("secondary"), depth)),
                _ => throw new NotSupportedException($"Unsupported mission frame {type}.")
            };
        }

        MissionDirection Direction(JsonElement direction, int depth)
        {
            string kind = Text(direction, "type");
            if (kind == "ConstantVector")
            {
                var vector = Vector(direction.GetProperty("direction"));
                if (!(vector.Length > 0) || !double.IsFinite(vector.Length))
                    throw new InvalidDataException("A constant mission frame vector must be finite and nonzero.");
                return new MissionDirection.Constant(vector,
                    direction.TryGetProperty("frame", out var frame) ? ReadFrame(frame, depth + 1) : Equatorial);
            }
            ObjectId observer = direction.TryGetProperty("observer", out var from) ? resolve(from.GetString()!) : resolve(self);
            ObjectId target = direction.TryGetProperty("target", out var to) ? resolve(to.GetString()!) : center;
            return new MissionDirection.Relative(new(observer, target, kind switch
            {
                "RelativePosition" => DirectionQuantity.Position,
                "RelativeVelocity" => DirectionQuantity.Velocity,
                _ => throw new NotSupportedException($"Unsupported mission frame vector {kind}.")
            }));
        }
    }

    private static FrameAxis Axis(string name) => name.ToLowerInvariant() switch
    {
        "x" or "+x" => FrameAxis.X,
        "-x" => FrameAxis.NegativeX,
        "y" or "+y" => FrameAxis.Y,
        "-y" => FrameAxis.NegativeY,
        "z" or "+z" => FrameAxis.Z,
        "-z" => FrameAxis.NegativeZ,
        _ => throw new InvalidDataException($"Invalid mission frame axis {name}.")
    };

    private static JulianInstant Date(JsonElement item, string property, JulianInstant fallback)
    {
        if (!item.TryGetProperty(property, out var value))
            return fallback;
        if (value.ValueKind == JsonValueKind.Number)
            return JulianInstant.FromJulianDate(value.GetDouble(), TimeScale.Tdb);
        string text = value.GetString()?.Trim() ?? "";
        bool utc = text.EndsWith("UTC", StringComparison.OrdinalIgnoreCase);
        if (utc || text.EndsWith("TDB", StringComparison.OrdinalIgnoreCase))
            text = text[..^3].TrimEnd();
        if (!DateTime.TryParseExact(text, CalendarFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            throw new InvalidDataException($"Invalid mission date {text}.");
        var day = DateOnly.FromDateTime(date);
        if (utc)
            return TimeConversion.FromUtc(new(day, date.Hour, date.Minute, date.Second,
                (int)(date.Ticks % TimeSpan.TicksPerSecond * 100)), TimeScale.Tdb);
        return new JulianInstant(1_721_425 + day.DayNumber, JulianInstant.NanosecondsPerDay / 2, TimeScale.Tdb)
            .AddNanoseconds(date.TimeOfDay.Ticks * 100);
    }

    private static string Text(JsonElement value, string property) =>
        value.GetProperty(property).GetString() is { Length: > 0 } text ? text :
            throw new InvalidDataException($"Missing mission property {property}.");
}
