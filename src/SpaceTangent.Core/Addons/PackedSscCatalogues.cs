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
        file = LocalAddonFiles.Resolve(root, root, file);
        using var input = AddonDependencies.OpenRead(file, 16 * 1024 * 1024);
        if (input.Length > 16 * 1024 * 1024)
            throw new InvalidDataException("The packed SSC manifest exceeds 16 MiB.");
        using var document = JsonDocument.Parse(input);
        JsonElement pack = document.RootElement;
        int format = pack.GetProperty("formatVersion").GetInt32();
        if (format is < 1 or > 19)
            throw new NotSupportedException($"Packed SSC format version {format} is outside 1–19.");
        if (!pack.TryGetProperty("sscCatalogs", out var catalogues) || catalogues.GetArrayLength() == 0)
            throw new NotSupportedException("This pack has no embedded SSC catalogues.");
        if (catalogues.GetArrayLength() > 64)
            throw new InvalidDataException("A body pack may contain at most 64 SSC documents.");

        var texts = new Dictionary<string, string>(StringComparer.Ordinal);
        var assets = new Dictionary<string, Dictionary<(string Kind, string Source), string>>(StringComparer.Ordinal);
        foreach (var catalogue in catalogues.EnumerateArray())
        {
            cancellation.ThrowIfCancellationRequested();
            string name = Text(catalogue, "name");
            if (name is "." or ".." || Path.GetFileName(name) != name || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                throw new InvalidDataException("Packed SSC document name must be a filename.");
            string context = Path.Combine(root, ".ssc", texts.Count.ToString(System.Globalization.CultureInfo.InvariantCulture));
            string logicalFile = Path.Combine(context, name);
            string source = Text(catalogue, "text");
            if (Encoding.UTF8.GetByteCount(source) > 1024 * 1024)
                throw new InvalidDataException("Embedded SSC text exceeds 1 MiB.");
            texts.Add(logicalFile, source);
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

        var definitions = SscDefinitions.Load(texts.Keys, root, cancellation, texts);
        return ReadBodyDefinitions(definitions, root, scripts, warning, Resolve, cancellation);

        string Resolve(SscDefinition definition, string field, AddonStage stage, string name, params string[] ignored)
        {
            var (kind, directory) = field switch
            {
                "ScriptedOrbit" or "ScriptedRotation" => ("script", "scripts"),
                "Mesh" => ("model", "models"),
                "SampledOrbit" or "SampledTrajectory" or "SampledOrientation" => ("trajectory", "trajectories"),
                _ => throw new NotSupportedException($"Packed SSC resource kind for {field} is unavailable.")
            };
            var index = assets[definition.DirectoryFor(field)];
            string logical = name.Replace('\\', '/');
            if (!index.TryGetValue((kind, logical), out string? asset) &&
                (kind != "script" || !index.TryGetValue((kind, logical.EndsWith(".lua", StringComparison.Ordinal)
                    ? logical[..^4].Replace('/', '.') : logical), out asset)))
                throw new FileNotFoundException($"Packed SSC {kind} resource {name} is not declared.", name);
            return SscSampledCatalog.FindAsset(stage, root, root, asset!, directory);
        }

        static string Text(JsonElement item, string property) =>
            item.GetProperty(property).GetString() is { Length: > 0 } value ? value :
                throw new InvalidDataException($"Packed SSC field {property} is missing.");
    }
}
