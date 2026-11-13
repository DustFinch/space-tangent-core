// SPDX-License-Identifier: GPL-2.0-or-later
using System.Text;
using System.Text.Json;

namespace SpaceTangent.Core.Addons;

public static partial class SscBodyCatalogue
{
    private delegate string SscResource(SscDefinition definition, string field, AddonStage stage,
        string name, params string[] fallbackDirectories);

    private static IReadOnlyList<SscBodyDefinition> ReadPackedSsc(string root, string file, ISscScriptModels? scripts,
        Action<string>? warning, CancellationToken cancellation)
    {
        var documents = ReadPackedDocuments(root, file, cancellation);
        var definitions = SscDefinitions.Load(documents.Documents, cancellation);
        return ReadBodyDefinitions(definitions, _ => root, scripts, warning, documents.Resolve, cancellation, [root], documents.Kernels);
    }

    private sealed record SscSource(IReadOnlyList<SscDocument> Documents, SscResource Resolve,
        IReadOnlyList<(string Root, string Directory, string Name)> Kernels);

    private static SscSource ReadPackedDocuments(string root, string file, CancellationToken cancellation)
    {
        file = LocalAddonFiles.Resolve(root, root, file);
        using var input = AddonDependencies.OpenRead(file, 16 * 1024 * 1024);
        if (input.Length > 16 * 1024 * 1024)
            throw new InvalidDataException("The packed SSC manifest exceeds 16 MiB.");
        using var document = JsonDocument.Parse(input);
        JsonElement pack = document.RootElement;
        int format = pack.GetProperty("formatVersion").GetInt32();
        if (format is < 1 or > 19)
            throw new NotSupportedException($"Packed SSC format version {format} is outside 1–19.");
        JsonElement[] catalogues = pack.TryGetProperty("sscCatalogs", out var cataloguesNode)
            ? cataloguesNode.EnumerateArray().ToArray() : [];
        if (catalogues.Length > 64)
            throw new InvalidDataException("A body pack may contain at most 64 SSC documents.");

        var documents = new List<SscDocument>();
        var assets = new Dictionary<string, Dictionary<(string Kind, string Source), string>>(StringComparer.Ordinal);
        var kernels = new List<(string Root, string Directory, string Name)>();
        if (pack.TryGetProperty("spiceLoads", out var loads))
        {
            foreach (var load in loads.EnumerateArray())
            {
                cancellation.ThrowIfCancellationRequested();
                // Replay preview loads through the original embedded SSC fields that own them.
                if (Text(load, "policy") != "furnish")
                    continue;
                foreach (var name in load.GetProperty("files").EnumerateArray())
                {
                    string kernelFile = SscSampledCatalog.FindAsset(AddonStage.Kernel, root, root, name.GetString()!, "data");
                    kernels.Add((root, Path.GetDirectoryName(kernelFile)!, Path.GetFileName(kernelFile)));
                }
            }
        }
        if (catalogues.Length == 0 && kernels.Count == 0)
            throw new NotSupportedException("This pack has no embedded SSC catalogues or furnished SPICE kernels.");
        foreach (var catalogue in catalogues)
        {
            cancellation.ThrowIfCancellationRequested();
            string name = Text(catalogue, "name");
            if (name is "." or ".." || Path.GetFileName(name) != name || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                throw new InvalidDataException("Packed SSC document name must be a filename.");
            string context = Path.Combine(root, ".ssc", documents.Count.ToString(System.Globalization.CultureInfo.InvariantCulture));
            string logicalFile = Path.Combine(context, name);
            string source = Text(catalogue, "text");
            if (Encoding.UTF8.GetByteCount(source) > 1024 * 1024)
                throw new InvalidDataException("Embedded SSC text exceeds 1 MiB.");
            documents.Add(new(logicalFile, root, source));
            var resources = new Dictionary<(string Kind, string Source), string>();
            if (catalogue.TryGetProperty("resources", out var references))
            {
                if (references.GetArrayLength() > 8192)
                    throw new InvalidDataException("An embedded SSC document exceeds its resource limit.");
                foreach (var resource in references.EnumerateArray())
                {
                    string kind = Text(resource, "kind");
                    string logical = Text(resource, "source").Replace('\\', '/');
                    string path = Text(resource, "file").Replace('\\', '/');
                    if (!resources.TryAdd((kind, logical), path) && resources[(kind, logical)] != path)
                        throw new InvalidDataException($"Ambiguous packed SSC {kind} resource {logical}.");
                }
            }
            assets.Add(context, resources);
        }

        return new(documents.AsReadOnly(), Resolve, kernels.AsReadOnly());

        string Resolve(SscDefinition definition, string field, AddonStage stage, string name, params string[] ignored)
        {
            var (kind, directory) = PackedResourceKind(field, stage);
            var index = assets[definition.DirectoryFor(field)];
            string logical = name.Replace('\\', '/');
            if (!index.TryGetValue((kind, logical), out string? asset) &&
                (kind != "texture" || !index.TryGetValue(("map", logical), out asset)) &&
                (kind != "script" || !index.TryGetValue((kind, logical.EndsWith(".lua", StringComparison.Ordinal)
                    ? logical[..^4].Replace('/', '.') : logical), out asset)))
                throw new FileNotFoundException($"Packed SSC {kind} resource {name} is not declared.", name);
            return SscSampledCatalog.FindAsset(stage, root, root, asset!, directory);
        }

        static string Text(JsonElement item, string property) =>
            item.GetProperty(property).GetString() is { Length: > 0 } value ? value :
                throw new InvalidDataException($"Packed SSC field {property} is missing.");
    }

    private static (string Kind, string Directory) PackedResourceKind(string field, AddonStage stage) => field switch
    {
        "ScriptedOrbit" or "ScriptedRotation" => ("script", "scripts"),
        "Mesh" => ("model", "models"),
        "Texture" or "Rings" => ("texture", "textures"),
        "SpiceOrbit" or "SpiceRotation" => ("data", "data"),
        "Timeline" when stage == AddonStage.Kernel => ("data", "data"),
        "SampledOrbit" or "SampledTrajectory" or "SampledOrientation" or "Timeline" => ("trajectory", "trajectories"),
        _ => throw new NotSupportedException($"Packed SSC resource kind for {field} is unavailable.")
    };
}
