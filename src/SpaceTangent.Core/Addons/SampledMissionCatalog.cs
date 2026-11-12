// SPDX-License-Identifier: GPL-2.0-or-later
using System.Globalization;
using System.Text.Json;
using SpaceTangent.Contracts;
using SpaceTangent.Core.Orbits;
using SpaceTangent.Core.Time;
using SpaceTangent.Core.World;

namespace SpaceTangent.Core.Addons;

public sealed record SampledMission(ObjectId Id, string Name, string? MeshPath, double RadiusKilometers,
    IReadOnlyList<MissionArc> Arcs)
{
    public JulianInstant Start => Arcs[0].Start;
    public JulianInstant End => Arcs[^1].End;
}

public sealed record MissionArc(JulianInstant Start, JulianInstant End, ObjectId Center,
    Func<JulianInstant, CartesianState?> LocalState, MissionFrame TrajectoryFrame, MissionFrame BodyFrame);

public abstract record MissionFrame
{
    private MissionFrame() { }

    public sealed record Inertial(Basis3d ToWorld) : MissionFrame;
    public sealed record BodyFixed(ObjectId Body) : MissionFrame;
    public sealed record TwoVector(FrameAxis PrimaryAxis, RelativeDirection Primary,
        FrameAxis SecondaryAxis, RelativeDirection Secondary) : MissionFrame;
}

/// <summary>Reads local sampled mission arcs in the version 1.0 JSON format.</summary>
public static class SampledMissionCatalog
{
    private static readonly string[] CalendarFormats = [
        "yyyy-MM-dd", "yyyy-MM-dd HH:mm:ss.FFFFFFF", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF"
    ];
    private static readonly MissionFrame.Inertial Equatorial = new(Basis3d.Identity);
    private static readonly MissionFrame.Inertial Ecliptic = new(new(new(1, 0, 0),
        SscSampledSpacecraft.ToEquatorial(new(0, 1, 0)), SscSampledSpacecraft.ToEquatorial(new(0, 0, 1))));
    private static readonly JulianInstant Minimum = new(0, 0, TimeScale.Tdb);
    private static readonly JulianInstant Maximum = new(JulianInstant.MaximumDay, JulianInstant.NanosecondsPerDay - 1, TimeScale.Tdb);

    public static IReadOnlyList<SampledMission> Load(string catalogPath, string assetRoot,
        Func<string, ObjectId?> resolveBody, Action<string>? warning = null, CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(resolveBody);
        string root = Path.GetFullPath(assetRoot);
        string path = Path.GetFullPath(catalogPath, root);
        return AddonDiagnostics.Read(AddonStage.Catalog, path, () => Read(root, path, resolveBody, warning, cancellation));
    }

    private static IReadOnlyList<SampledMission> Read(string root, string path, Func<string, ObjectId?> resolveBody,
        Action<string>? warning, CancellationToken cancellation)
    {
        path = LocalAddonFiles.Resolve(root, root, path);
        string directory = Path.GetDirectoryName(path)!;
        const int MaximumBytes = 4 * 1024 * 1024;
        using var stream = AddonDependencies.OpenRead(path, MaximumBytes);
        if (stream.Length > MaximumBytes)
            throw new InvalidDataException("Sampled mission catalogue exceeds 4 MiB.");
        using var json = JsonDocument.Parse(stream);
        JsonElement catalogue = json.RootElement;
        if (Text(catalogue, "version") != "1.0")
            throw new InvalidDataException("Sampled mission catalogues require version 1.0.");
        var items = catalogue.GetProperty("items");
        if (items.GetArrayLength() > 512)
            throw new InvalidDataException("A sampled mission catalogue may contain at most 512 items.");

        var missions = new Dictionary<string, ObjectId>(StringComparer.OrdinalIgnoreCase);
        var definitions = new List<JsonElement>();
        foreach (var item in items.EnumerateArray())
        {
            string name = Text(item, "name");
            if (item.TryGetProperty("geometry", out var geometry) && Text(geometry, "type") != "Mesh")
            {
                warning?.Invoke($"{name}: {Text(geometry, "type")} geometry is not supported by the sampled mission reader.");
                continue;
            }
            string ascii = new(name.ToLowerInvariant().Select(character => char.IsAsciiLetterOrDigit(character) ? character : '-').ToArray());
            string slug = string.Join('-', ascii.Split('-', StringSplitOptions.RemoveEmptyEntries));
            if (slug.Length is < 1 or > 80)
                throw new InvalidDataException($"Invalid mission identifier for {name}.");
            if (missions.Values.Contains(new ObjectId("mission/" + slug)) ||
                !missions.TryAdd(name, new("mission/" + slug)))
                throw new InvalidDataException($"Duplicate sampled mission identifier for {name}.");
            definitions.Add(item);
        }
        return Array.AsReadOnly(definitions.Select(item => ReadItem(item, Resolve, root, directory, warning, cancellation)).ToArray());

        ObjectId Resolve(string name) => missions.TryGetValue(name, out var id) ? id :
            resolveBody(name) ?? throw new InvalidDataException($"Unknown mission reference {name}.");
    }

    private static SampledMission ReadItem(JsonElement item, Func<string, ObjectId> resolve, string root, string directory,
        Action<string>? warning, CancellationToken cancellation)
    {
        string name = Text(item, "name");
        string? mesh = null;
        double radius = .005;
        if (item.TryGetProperty("geometry", out var geometry))
        {
            radius = geometry.GetProperty("size").GetDouble();
            if (!double.IsFinite(radius) || radius <= 0)
                throw new InvalidDataException($"{name}: the mesh size must be finite and positive.");
            string source = Text(geometry, "source");
            try
            {
                mesh = LocalAddonFiles.ResolveRelative(root, directory, source);
            }
            catch (FileNotFoundException)
            {
                warning?.Invoke($"{name}: mesh {source} is missing; its trajectory is available.");
            }
        }

        JulianInstant start = Date(item, "startTime", Minimum);
        JulianInstant finish = Date(item, "endTime", Maximum);
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
            if (arc.TryGetProperty("rotationModel", out _))
                throw new NotSupportedException($"{name}: a separate mission rotation model is not supported.");
            var center = resolve(Text(arc, "center"));
            var trajectoryFrame = Frame(arc, "trajectoryFrame", resolve, name, center);
            var bodyFrame = Frame(arc, "bodyFrame", resolve, name, center);
            var trajectory = arc.GetProperty("trajectory");
            result.Add(new(begins, end, center, ReadTrajectory(trajectory, root, directory), trajectoryFrame, bodyFrame));
            start = end;
        }
        return new(resolve(name), name, mesh, radius, result.AsReadOnly());
    }

    private static Func<JulianInstant, CartesianState?> ReadTrajectory(JsonElement value, string root, string directory)
    {
        string type = Text(value, "type");
        if (type == "FixedPoint")
        {
            double[] components = value.GetProperty("position").EnumerateArray().Select(component => component.GetDouble()).ToArray();
            if (components.Length != 3 || components.Any(component => !double.IsFinite(component)))
                throw new InvalidDataException("A fixed mission point requires three finite components.");
            var state = new CartesianState(new(components[0], components[1], components[2]), default);
            return _ => state;
        }
        if (type == "InterpolatedStates")
        {
            string path = LocalAddonFiles.ResolveRelative(root, directory, Text(value, "source"));
            var frame = new FrameId("mission-source");
            var curve = Path.GetExtension(path).ToLowerInvariant() switch
            {
                ".xyzv" => SampledStateTrajectory.ReadXyzv(path, frame),
                ".xyz" => SampledStateTrajectory.ReadXyz(path, frame),
                _ => throw new InvalidDataException("A sampled mission trajectory requires a local XYZ or XYZV file.")
            };
            return epoch => curve.TrySample(epoch, out var state) ? state : null;
        }
        throw new NotSupportedException($"Unsupported mission trajectory {type}.");
    }

    private static MissionFrame Frame(JsonElement arc, string property, Func<string, ObjectId> resolve,
        string self, ObjectId center)
    {
        if (!arc.TryGetProperty(property, out var value))
            return Equatorial;
        string type = value.ValueKind == JsonValueKind.String ? value.GetString()! : Text(value, "type");
        return type switch
        {
            "EclipticJ2000" => Ecliptic,
            "ICRF" or "EquatorJ2000" => Equatorial,
            "BodyFixed" => new MissionFrame.BodyFixed(resolve(Text(value, "body"))),
            "TwoVector" => new MissionFrame.TwoVector(Axis(Text(value, "primaryAxis")), Direction(value.GetProperty("primary")),
                Axis(Text(value, "secondaryAxis")), Direction(value.GetProperty("secondary"))),
            _ => throw new NotSupportedException($"Unsupported mission frame {type}.")
        };

        RelativeDirection Direction(JsonElement direction)
        {
            ObjectId observer = direction.TryGetProperty("observer", out var from) ? resolve(from.GetString()!) : resolve(self);
            ObjectId target = direction.TryGetProperty("target", out var to) ? resolve(to.GetString()!) : center;
            return new(observer, target, Text(direction, "type") switch
            {
                "RelativePosition" => DirectionQuantity.Position,
                "RelativeVelocity" => DirectionQuantity.Velocity,
                var kind => throw new NotSupportedException($"Unsupported mission frame vector {kind}.")
            });
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
