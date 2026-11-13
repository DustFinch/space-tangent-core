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
    internal sealed record RelativeAttitude(MissionFrame Parent, Func<JulianInstant, FrameKinematics?> Local) : MissionFrame;
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
        string root = Path.GetFullPath(assetRoot);
        var annotations = new List<MissionAnnotation>();
        var viewpointItems = new List<(JsonElement Item, bool Packed)>();
        var names = new Dictionary<string, ObjectId>(StringComparer.OrdinalIgnoreCase);
        var ids = new HashSet<ObjectId>();
        var kernelLoads = new List<(string Directory, string Name)>();
        var instrumentFrames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var definitions = new List<(string Source, JsonElement Item, bool Packed)>();
        var documents = new Dictionary<string, JsonDocument>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        var active = new HashSet<string>(documents.Comparer);
        const int MaximumCatalogueBytes = 4 * 1024 * 1024;
        const int MaximumTotalBytes = 16 * 1024 * 1024;
        long bytes = 0;
        try
        {
            foreach (string entry in catalogPaths)
                Visit(LocalAddonFiles.Resolve(root, root, entry), 0);
            var resources = bindPool is null ? new SpiceResources(root, kernelLoads) :
                bindPool(kernelLoads.Select(load => new SpiceResources.KernelGroup(root, [load])).ToArray());
            var rejected = new HashSet<ObjectId>();
            var loaded = new List<(SampledMission Mission, HashSet<ObjectId> References)>();
            foreach (var definition in definitions)
            {
                cancellation.ThrowIfCancellationRequested();
                string directory = Path.GetDirectoryName(definition.Source)!;
                var references = new HashSet<ObjectId>();
                try
                {
                    var mission = AddonDiagnostics.Read(AddonStage.Catalog, definition.Source, () =>
                        definition.Packed ? definition.Item.TryGetProperty("particles", out _)
                            ? ReadPackedParticleMission(definition.Item, TrackReference, directory, warning)
                            : ReadPackedBody(definition.Item, TrackReference, directory, resources, warning, cancellation)
                        : ReadItem(definition.Item, Resolve, root, directory, resources,
                            name => instrumentFrames.GetValueOrDefault(name), warning, cancellation));
                    loaded.Add((mission, references));
                }
                catch (Exception error) when (definition.Packed && !definition.Item.TryGetProperty("particles", out _) &&
                    (error is NotSupportedException or FileNotFoundException ||
                    AddonDiagnostics.Get(error) is { Stage: AddonStage.Trajectory, Code: AddonIssue.InvalidInput }))
                {
                    string name = Text(definition.Item, "name");
                    rejected.Add(ResolvePacked(Text(definition.Item, "path")));
                    warning?.Invoke($"{name}: {error.Message}");
                }

                ObjectId TrackReference(string path)
                {
                    ObjectId id = ResolvePacked(path);
                    references.Add(id);
                    return id;
                }
            }

            bool removed;
            do
            {
                removed = false;
                foreach (var (mission, references) in loaded)
                {
                    if (references.Overlaps(rejected) && rejected.Add(mission.Id))
                    {
                        warning?.Invoke($"{mission.Name}: depends on an unavailable packed mission body.");
                        removed = true;
                    }
                }
            } while (removed);
            var missions = loaded.Where(item => !rejected.Contains(item.Mission.Id)).Select(item => item.Mission).ToArray();
            var sensors = missions.Where(mission => mission.Geometry?.Any(span => span.Shape.Sensor is not null) == true)
                .Select(mission => mission.Id).ToHashSet();
            foreach (var mission in missions)
                if (mission.Observation is { } observation && !sensors.Contains(observation.Sensor))
                    throw new InvalidDataException($"{mission.Name}: observation requires an installed sensor field.");
            var viewpoints = new List<MissionViewpoint>();
            foreach (var (item, packed) in viewpointItems)
            {
                cancellation.ThrowIfCancellationRequested();
                try
                {
                    viewpoints.Add(ReadViewpoint(item, packed, ResolveViewpoint));
                }
                catch (Exception error) when (error is InvalidDataException or KeyNotFoundException or FormatException or InvalidOperationException)
                {
                    warning?.Invoke($"Mission viewpoint: {error.Message}");
                }

                ObjectId ResolveViewpoint(string body)
                {
                    ObjectId id = packed ? ResolvePacked(body) : Resolve(body);
                    return rejected.Contains(id) ? throw new InvalidDataException($"Unavailable viewpoint body {body}.") : id;
                }
            }
            return new(Array.AsReadOnly(missions), annotations.AsReadOnly(), viewpoints.AsReadOnly());
        }
        finally
        {
            foreach (var document in documents.Values)
                document.Dispose();
        }

        void Visit(string path, int depth)
        {
            cancellation.ThrowIfCancellationRequested();
            if (active.Contains(path))
                throw new InvalidDataException($"Mission catalogue dependency cycle at {Path.GetFileName(path)}.");
            if (documents.ContainsKey(path))
                return;
            if (depth > 16 || documents.Count == 64)
                throw new InvalidDataException("Mission catalogue dependencies exceed their depth or file limit.");
            AddonDiagnostics.Read(AddonStage.Catalog, path, () =>
            {
                using var stream = AddonDependencies.OpenRead(path, MaximumCatalogueBytes);
                bytes += stream.Length;
                if (stream.Length > MaximumCatalogueBytes || bytes > MaximumTotalBytes)
                    throw new InvalidDataException("Sampled mission catalogues exceed their size limit.");
                using var reader = new StreamReader(stream);
                var json = JsonDocument.Parse(reader.ReadToEnd(),
                    new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
                documents.Add(path, json);
                JsonElement catalogue = json.RootElement;
                if (catalogue.TryGetProperty("formatVersion", out var packedVersion))
                {
                    int version = packedVersion.GetInt32();
                    if (version is < 3 or > 19)
                        throw new NotSupportedException($"Packed mission format version {version} is outside 3–19.");
                    ReadPackedKernelLoads(catalogue, root, Path.GetDirectoryName(path)!, kernelLoads);
                    if (catalogue.TryGetProperty("annotations", out var packedAnnotations))
                        foreach (var annotation in packedAnnotations.EnumerateArray())
                            AddAnnotation(annotation, packed: true);
                    if (catalogue.TryGetProperty("viewpoints", out var packedViewpoints))
                        foreach (var viewpoint in packedViewpoints.EnumerateArray())
                            AddViewpoint(viewpoint, packed: true);
                    bool metadata = packedAnnotations.ValueKind == JsonValueKind.Array || packedViewpoints.ValueKind == JsonValueKind.Array;
                    if (!catalogue.TryGetProperty("bodies", out var bodies))
                    {
                        if (metadata || catalogue.TryGetProperty("spiceLoads", out _))
                            return true;
                        throw new InvalidDataException("A packed mission catalogue requires bodies, annotations or viewpoints.");
                    }
                    if (bodies.GetArrayLength() > 512 || bodies.GetArrayLength() == 0 && !metadata)
                        throw new InvalidDataException("A packed mission catalogue requires one to 512 bodies.");
                    foreach (JsonElement body in bodies.EnumerateArray())
                        if (body.TryGetProperty("particles", out _) || body.TryGetProperty("cosmographiaNaturalAxes", out _) ||
                            body.TryGetProperty("plane", out _) || body.TryGetProperty("direction", out _) || body.TryGetProperty("swarm", out _) ||
                            HasPackedSpice(body))
                            Register(body, path, packed: true);
                    return true;
                }
                if (Text(catalogue, "version") != "1.0")
                    throw new InvalidDataException("Sampled mission catalogues require version 1.0.");
                active.Add(path);
                if (catalogue.TryGetProperty("require", out var requirements))
                {
                    if (requirements.ValueKind != JsonValueKind.Array || requirements.GetArrayLength() > 64)
                        throw new InvalidDataException("Mission require must be an array of at most 64 local catalogues.");
                    foreach (var requirement in requirements.EnumerateArray())
                    {
                        if (requirement.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(requirement.GetString()))
                            throw new InvalidDataException("A mission dependency requires a nonempty local path.");
                        string resource = requirement.GetString()!;
                        string dependency;
                        try { dependency = LocalAddonFiles.ResolveRelative(root, Path.GetDirectoryName(path)!, resource); }
                        catch (FileNotFoundException)
                        {
                            warning?.Invoke($"{Path.GetFileName(path)} requires {resource}; resolving references against installed objects.");
                            continue;
                        }
                        Visit(dependency, depth + 1);
                    }
                }
                ReadSpiceLoads(catalogue, Path.GetDirectoryName(path)!, kernelLoads);
                if (catalogue.TryGetProperty("items", out var items))
                    foreach (JsonElement item in items.EnumerateArray())
                        Register(item, path);
                active.Remove(path);
                return true;
            });
        }

        void Register(JsonElement item, string path, bool packed = false)
        {
            if (!packed && item.TryGetProperty("type", out var type) && type.GetString() == "AnnotationScript")
            {
                AddAnnotation(item, packed: false);
                return;
            }
            if (!packed && item.TryGetProperty("type", out type) && type.GetString() == "Viewpoint")
            {
                AddViewpoint(item, packed: false);
                return;
            }
            bool visualizer = !packed && IsVisualizer(item);
            if (visualizer && Text(item.GetProperty("style"), "type") is not ("BodyAxes" or "FrameAxes" or "Plane" or "BodyDirection"))
            {
                warning?.Invoke($"Mission visualizer {Text(item, "tag")} will be unavailable.");
                return;
            }
            string name = Text(item, visualizer ? "tag" : "name");
            // A mixed pack can refer to an already installed science satellite. Let the science
            // loader own its TLE and attitude so mission dependants resolve the live object.
            if (packed && resolveBody(name) is not null && item.TryGetProperty("timeline", out var phases) &&
                phases.GetArrayLength() == 1 && phases[0].TryGetProperty("orbit", out var packedOrbit) &&
                Text(packedOrbit, "kind") == "tle")
                return;
            if (!packed && item.TryGetProperty("geometry", out var geometry) &&
                Text(geometry, "type") is not ("Mesh" or "Globe" or "TimeSwitched" or "Sensor" or "Observations" or "ParticleSystem" or "Axes" or "PlaneVisualizer" or "DirectionVisualizer" or "KeplerianSwarm") &&
                !(Text(geometry, "type") == "Spice" && geometry.TryGetProperty("instrName", out _)))
            {
                warning?.Invoke($"{name}: {Text(geometry, "type")} geometry is not supported by the sampled mission reader.");
                return;
            }
            if (item.TryGetProperty("trajectory", out var trajectory) && Text(trajectory, "type") == "TLE" &&
                resolveBody(name) is not null)
                return;
            string ascii = new(name.ToLowerInvariant().Select(character => char.IsAsciiLetterOrDigit(character) ? character : '-').ToArray());
            string slug = string.Join('-', ascii.Split('-', StringSplitOptions.RemoveEmptyEntries));
            if (slug.Length is < 1 or > 80)
                throw new InvalidDataException($"Invalid mission identifier for {name}.");
            var id = new ObjectId("mission/" + slug);
            if (!ids.Add(id) || !names.TryAdd(name, id))
                throw new InvalidDataException($"Duplicate sampled mission identifier for {name}.");
            if (packed && !names.TryAdd(Text(item, "path"), id))
                throw new InvalidDataException($"Duplicate packed mission path for {name}.");
            if (definitions.Count == 512)
                throw new InvalidDataException("The enabled catalogues may provide at most 512 sampled missions.");
            if (!packed && item.TryGetProperty("bodyFrame", out var attitude) && attitude.ValueKind == JsonValueKind.Object &&
                Text(attitude, "type") == "Spice")
                instrumentFrames.Add(name, Text(attitude, "name"));
            definitions.Add((path, item, packed));
        }

        void AddAnnotation(JsonElement value, bool packed)
        {
            if (annotations.Count >= 128)
                throw new InvalidDataException("A mission installation supports at most 128 annotation tracks.");
            annotations.Add(ReadAnnotation(value, packed, cancellation));
        }

        void AddViewpoint(JsonElement value, bool packed)
        {
            if (viewpointItems.Count >= 512)
                throw new InvalidDataException("A mission installation supports at most 512 viewpoints.");
            viewpointItems.Add((value, packed));
        }

        ObjectId Resolve(string name) => names.TryGetValue(name, out var id) ? id :
            resolveBody(name) ?? throw new InvalidDataException($"Unknown mission reference {name}.");

        ObjectId ResolvePacked(string path) => names.TryGetValue(path, out var id) ? id :
            resolveBody(path) ?? (path.StartsWith("Sol/", StringComparison.OrdinalIgnoreCase)
                ? resolveBody(path[(path.LastIndexOf('/') + 1)..]) : null) ??
            throw new InvalidDataException($"Unknown packed mission reference {path}.");
    }

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
