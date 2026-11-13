// SPDX-License-Identifier: GPL-2.0-or-later
using System.Text.Json;
using SpaceTangent.Contracts;

namespace SpaceTangent.Core.Addons;

/// <summary>A relocatable set of ordered mission catalogues and their local resources.</summary>
public static class MissionCataloguePack
{
    public const string ManifestName = "pack.json";
    internal const string Format = "spacetangent-mission-catalogues";
    private const int Version = 1;
    private const string ContentDirectory = "content";

    internal static bool IsManifest(JsonElement json) =>
        json.ValueKind == JsonValueKind.Object && json.TryGetProperty("format", out var format) && format.ValueKind == JsonValueKind.String && format.ValueEquals(Format);

    internal static string[] ReadEntries(JsonElement json, string contentRoot)
    {
        if (!json.TryGetProperty("version", out var version) || version.ValueKind != JsonValueKind.Number ||
            !version.TryGetInt32(out int number) || number != Version ||
            !json.TryGetProperty("catalogues", out var entries) || entries.ValueKind != JsonValueKind.Array ||
            entries.GetArrayLength() is < 1 or > 63)
            throw new InvalidDataException("Unsupported mission catalogue pack manifest.");

        var seen = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        var paths = new List<string>();
        foreach (var entry in entries.EnumerateArray())
        {
            string? relative = entry.ValueKind == JsonValueKind.String ? entry.GetString() : null;
            if (string.IsNullOrWhiteSpace(relative) || !Path.GetExtension(relative).Equals(".json", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("A mission pack entry requires a relative JSON catalogue path.");
            string path = LocalAddonFiles.ResolveRelative(contentRoot, contentRoot, relative);
            if (!seen.Add(path))
                throw new InvalidDataException("Duplicate mission catalogue pack entry.");
            paths.Add(path);
        }
        return paths.ToArray();
    }

    internal static string ContentRoot(string manifest) => Path.Combine(Path.GetDirectoryName(manifest)!, ContentDirectory);

    public static bool IsManifest(string path)
    {
        using var stream = AddonDependencies.OpenRead(path, 16 * 1024 * 1024);
                using var json = JsonDocument.Parse(stream,
            new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
        return IsManifest(json.RootElement);
    }

    public static (int Missions, int Annotations, int Viewpoints) Build(string source, string destination, Func<string, ObjectId?> resolveBody,
        Action<string>? warning = null, CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(resolveBody);
        string root = Path.GetFullPath(source);
        string target = Path.GetFullPath(destination);
        if (!Directory.Exists(root))
            throw new DirectoryNotFoundException(root);
        string relative = Path.GetRelativePath(root, target);
        if (!Path.IsPathRooted(relative) && relative != ".." &&
            !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new InvalidDataException("The pack output must be outside the source folder.");
        if (Path.Exists(target))
            throw new IOException($"The pack output already exists: {target}.");
        if (File.Exists(Path.Combine(root, ManifestName)))
            throw new InvalidDataException("Supply loose version 1.0 mission catalogues.");
        for (DirectoryInfo? directory = new(root); directory is not null; directory = directory.Parent)
            if ((directory.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException($"Linked mission directory: {directory.FullName}.");

        var catalogues = Directory.EnumerateFiles(root, "*.json");
        string catalogueDirectory = Path.Combine(root, "catalog");
        if (Directory.Exists(catalogueDirectory))
            catalogues = catalogues.Concat(Directory.EnumerateFiles(catalogueDirectory, "*.json"));
        var entries = catalogues.Order(StringComparer.Ordinal).Take(64)
            .Select(path => Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/')).ToArray();
        if (entries.Length is < 1 or > 63)
            throw new InvalidDataException("Supply one to 63 mission catalogues at the source root or in its catalog folder.");

        string parent = Path.GetDirectoryName(target) ?? throw new IOException("The output requires a parent directory.");
        Directory.CreateDirectory(parent);
        string stage = Path.Combine(parent, ".mission-pack-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stage);
        try
        {
            string manifest = Path.Combine(stage, ManifestName);
            var options = new EnumerationOptions
            {
                RecurseSubdirectories = true, MaxRecursionDepth = 16,
                AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = false
            };
            int count = 0;
            foreach (string path in Directory.EnumerateFiles(root, "*", options))
            {
                cancellation.ThrowIfCancellationRequested();
                if (++count > 4096)
                    throw new InvalidDataException("The mission source contains more than 4096 files.");
                string input = LocalAddonFiles.Resolve(root, root, path);
                string output = Path.Combine(ContentRoot(manifest), Path.GetRelativePath(root, input));
                Directory.CreateDirectory(Path.GetDirectoryName(output)!);
                File.Copy(input, output);
            }
            using (var stream = File.Create(manifest))
                JsonSerializer.Serialize(stream, new { format = Format, version = Version, catalogues = entries },
                    new JsonSerializerOptions { WriteIndented = true });

            var content = SampledMissionCatalog.LoadContent([manifest], stage, resolveBody, warning, cancellation);
            cancellation.ThrowIfCancellationRequested();
            Directory.Move(stage, target);
            return (content.Missions.Count, content.Annotations.Count, content.Viewpoints.Count);
        }
        finally
        {
            if (Directory.Exists(stage))
            {
                string resolvedStage = new DirectoryInfo(stage).FullName;
                if (!string.Equals(Path.GetDirectoryName(resolvedStage), parent, StringComparison.Ordinal))
                    throw new IOException("The staging directory is outside the pack destination parent.");
                Directory.Delete(resolvedStage, recursive: true);
            }
        }
    }
}
