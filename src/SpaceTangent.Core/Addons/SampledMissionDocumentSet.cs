// SPDX-License-Identifier: GPL-2.0-or-later
using System.Text.Json;
using SpaceTangent.Contracts;
using SpaceTangent.Core.Spice;

namespace SpaceTangent.Core.Addons;

public static partial class SampledMissionCatalog
{
    // Owns source documents until runtime binding finishes. This is a source
    // traversal/symbol stage; it never creates the installation's SPICE pool.
    internal sealed class MissionDocumentSet : IDisposable
    {
        private readonly string root;
        private readonly Func<string, ObjectId?> resolveBody;
        private readonly Action<string>? warning;
        private readonly CancellationToken cancellation;
        private readonly List<MissionAnnotation> annotations = [];
        private readonly List<(JsonElement Item, bool Packed)> viewpointItems = [];
        private readonly Dictionary<string, ObjectId> names = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<ObjectId> ids = [];
        private readonly List<SpiceResources.KernelGroup> kernelLoads = [];
        private readonly Dictionary<string, string> instrumentFrames = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<(string Source, string Owner, JsonElement Item, bool Packed)> definitions = [];
        private readonly Dictionary<string, JsonDocument> documents = new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        private readonly HashSet<string> active;
        private readonly Dictionary<(string Source, string Path), List<JsonElement>> timedChildren = [];
        private const int MaximumCatalogueBytes = 4 * 1024 * 1024, MaximumTotalBytes = 16 * 1024 * 1024;
        private long bytes;
        private bool disposed;

        internal Action<string>? Warning => warning;
        internal CancellationToken Cancellation => cancellation;
        internal List<MissionAnnotation> Annotations => annotations;
        internal IReadOnlyList<(JsonElement Item, bool Packed)> Viewpoints => viewpointItems;
        internal IReadOnlyList<SpiceResources.KernelGroup> KernelLoads => kernelLoads;
        internal IReadOnlyDictionary<string, string> InstrumentFrames => instrumentFrames;
        internal IReadOnlyList<(string Source, string Owner, JsonElement Item, bool Packed)> Definitions => definitions;
        internal IReadOnlyDictionary<(string Source, string Path), List<JsonElement>> TimedChildren => timedChildren;

        private MissionDocumentSet(string assetRoot, Func<string, ObjectId?> resolveBody,
            Action<string>? warning, CancellationToken cancellation)
        {
            root = Path.GetFullPath(assetRoot);
            this.resolveBody = resolveBody; this.warning = warning; this.cancellation = cancellation;
            active = new(documents.Comparer);
        }

        internal static MissionDocumentSet Open(IEnumerable<string> paths, string assetRoot,
            Func<string, ObjectId?> resolveBody, Action<string>? warning, CancellationToken cancellation)
        {
            var result = new MissionDocumentSet(assetRoot, resolveBody, warning, cancellation);
            try
            {
                foreach (string entry in paths)
                    result.Visit(LocalAddonFiles.Resolve(result.root, result.root, entry), result.root, 0);
                return result;
            }
            catch { result.Dispose(); throw; }
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            foreach (var document in documents.Values) document.Dispose();
        }

        private void Visit(string path, string owner, int depth, bool sourceOnly = false)
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
                if (MissionCataloguePack.IsManifest(catalogue))
                {
                    if (sourceOnly)
                        throw new InvalidDataException("A mission pack must contain loose source catalogues.");
                    active.Add(path);
                    string contentRoot = MissionCataloguePack.ContentRoot(path);
                    foreach (string entry in MissionCataloguePack.ReadEntries(catalogue, contentRoot))
                        Visit(entry, contentRoot, depth + 1, sourceOnly: true);
                    active.Remove(path);
                    return true;
                }
                var loads = new List<(string Directory, string Name)>();
                if (catalogue.TryGetProperty("formatVersion", out var packedVersion))
                {
                    int version = packedVersion.GetInt32();
                    if (version is < 1 or > 19)
                        throw new NotSupportedException($"Packed mission format version {version} is outside 1–19.");
                    if (sourceOnly)
                        throw new InvalidDataException("A mission pack must contain loose source catalogues.");
                    ReadPackedKernelLoads(catalogue, owner, Path.GetDirectoryName(path)!, loads);
                    AddKernelLoads(owner, loads);
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
                    var groups = bodies.EnumerateArray().Where(IsTimedComponent)
                        .ToLookup(body => Text(body, "parent"), StringComparer.Ordinal);
                    var timedParents = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var body in bodies.EnumerateArray())
                    {
                        string key = Text(body, "path");
                        // Early exports used timed components before they carried explicit geometry bounds.
                        if (body.TryGetProperty("geometryBounds", out _) || body.TryGetProperty("shown", out _) && groups.Contains(key))
                        {
                            timedParents.Add(key);
                            timedChildren.TryAdd((path, key), groups[key].ToList());
                        }
                    }
                    bool hasEmbeddedSsc = catalogue.TryGetProperty("sscCatalogs", out var embeddedSsc) &&
                        embeddedSsc.GetArrayLength() > 0;
                    foreach (JsonElement body in bodies.EnumerateArray())
                        if (timedParents.Contains(Text(body, "path")) ||
                            (IsTimedComponent(body) && timedParents.Contains(Text(body, "parent"))) ||
                            body.TryGetProperty("sensor", out _) || body.TryGetProperty("observation", out _) ||
                            body.TryGetProperty("display", out _) && !body.TryGetProperty("replaces", out _) ||
                            body.TryGetProperty("particles", out _) || body.TryGetProperty("cosmographiaNaturalAxes", out _) ||
                            body.TryGetProperty("plane", out _) || body.TryGetProperty("direction", out _) || body.TryGetProperty("swarm", out _) ||
                            HasPackedSpice(body) || HasCompoundTrajectory(body) || IsArchivedFixedModel(body) ||
                            !hasEmbeddedSsc && HasLegacySampledTrajectory(body))
                            Register(body, path, owner, packed: true);
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
                        try { dependency = LocalAddonFiles.ResolveRelative(owner, Path.GetDirectoryName(path)!, resource); }
                        catch (FileNotFoundException)
                        {
                            warning?.Invoke($"{Path.GetFileName(path)} requires {resource}; resolving references against installed objects.");
                            continue;
                        }
                        Visit(dependency, owner, depth + 1, sourceOnly);
                    }
                }
                ReadSpiceLoads(catalogue, Path.GetDirectoryName(path)!, loads);
                AddKernelLoads(owner, loads);
                if (catalogue.TryGetProperty("items", out var items))
                    foreach (JsonElement item in items.EnumerateArray())
                        Register(item, path, owner);
                active.Remove(path);
                return true;
            });
        }

        private void AddKernelLoads(string owner, List<(string Directory, string Name)> loads)
        {
            if (loads.Count > 256 - kernelLoads.Count)
                throw new InvalidDataException("The sampled mission catalogues declare too many SPICE kernels.");
            foreach (var load in loads)
                kernelLoads.Add(new(owner, [load]));
        }

        private void Register(JsonElement item, string path, string owner, bool packed = false)
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
            // Keep installed science satellites and built-in ephemerides when this reader
            // cannot replace them. Other packed tracks can still refer to the installed body.
            if (packed && resolveBody(name) is not null && item.TryGetProperty("timeline", out var phases) &&
                phases.GetArrayLength() == 1 && phases[0].TryGetProperty("orbit", out var packedOrbit) &&
                (Text(packedOrbit, "kind") == "tle" ||
                 Text(packedOrbit, "kind") == PackedBuiltinKind &&
                 (!TrySatelliteTheory(Text(packedOrbit, "name"), out _) ||
                  Text(packedOrbit.GetProperty("frame"), "kind") != "equatorJ2000")))
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
            definitions.Add((path, owner, item, packed));
        }

        private void AddAnnotation(JsonElement value, bool packed)
        {
            if (annotations.Count >= 128)
                throw new InvalidDataException("A mission installation supports at most 128 annotation tracks.");
            annotations.Add(ReadAnnotation(value, packed, cancellation));
        }

        private void AddViewpoint(JsonElement value, bool packed)
        {
            if (viewpointItems.Count >= 512)
                throw new InvalidDataException("A mission installation supports at most 512 viewpoints.");
            viewpointItems.Add((value, packed));
        }

        internal ObjectId Resolve(string name) => names.TryGetValue(name, out var id) ? id :
            resolveBody(name) ?? throw new InvalidDataException($"Unknown mission reference {name}.");

        internal ObjectId ResolvePacked(string path)
        {
            if (names.TryGetValue(path, out var id))
                return id;
            if (resolveBody(path) is { } resolved)
                return resolved;
            if (path.StartsWith("Sol/", StringComparison.OrdinalIgnoreCase))
            {
                string name = path[(path.LastIndexOf('/') + 1)..];
                if (resolveBody(name) is { } leaf)
                    return leaf;
                int separator = name.IndexOf(' ');
                if (separator > 0 && name[..separator].All(char.IsAsciiDigit) &&
                    resolveBody(name[(separator + 1)..]) is { } numbered)
                    return numbered;
            }
            throw new InvalidDataException($"Unknown packed mission reference {path}.");
        }
    }
}
