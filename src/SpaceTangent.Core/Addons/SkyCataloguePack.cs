// SPDX-License-Identifier: GPL-2.0-or-later
using System.Text.Json;
using SpaceTangent.Core.Observing;

namespace SpaceTangent.Core.Addons;

/// <summary>A relocatable index of source catalogues, retaining their relative resource paths.</summary>
public static class SkyCataloguePack
{
    public const string ManifestName = "sky-pack.json";
    private const string Format = "spacetangent-sky-catalogues";
    private const int Version = 1;
    private const int MaxFiles = 64;
    private const int MaxManifestBytes = 128 * 1024;

    internal static string[] List(string root, string extension)
    {
        using var manifest = ReadManifest(root);
        if (manifest is null)
            return AddonDependencies.ListFiles(root, "*" + extension).Order(StringComparer.OrdinalIgnoreCase).ToArray();

        JsonElement document = manifest.RootElement;
        if (!document.TryGetProperty("version", out var version) || version.ValueKind != JsonValueKind.Number ||
            !version.TryGetInt32(out int number) || number != Version ||
            !document.TryGetProperty("catalogues", out var files) || files.ValueKind != JsonValueKind.Array ||
            files.GetArrayLength() is < 1 or > 2 * MaxFiles)
            throw new InvalidDataException("Unsupported sky catalogue pack manifest.");

        var paths = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files.EnumerateArray())
        {
            string? name = file.ValueKind == JsonValueKind.String ? file.GetString() : null;
            if (string.IsNullOrWhiteSpace(name) || !string.Equals(name, Path.GetFileName(name), StringComparison.Ordinal) ||
                name.Contains('/') || name.Contains('\\') || Path.GetExtension(name).ToLowerInvariant() is not (".stc" or ".dsc") ||
                !seen.Add(name))
                throw new InvalidDataException("Invalid or duplicate sky catalogue pack entry.");
            if (Path.GetExtension(name).Equals(extension, StringComparison.OrdinalIgnoreCase))
                paths.Add(LocalAddonFiles.ResolveRelative(root, root, name));
        }
        return paths.ToArray();
    }

    public static (int Stellar, int DeepSky) Build(string source, string destination, CancellationToken cancellation = default)
    {
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
            throw new InvalidDataException("Supply a loose STC/DSC source folder.");

        for (DirectoryInfo? directory = new(root); directory is not null; directory = directory.Parent)
            if ((directory.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException($"Linked add-on directory: {directory.FullName}.");

        var stc = List(root, ".stc");
        var dsc = List(root, ".dsc");
        if (stc.Length > MaxFiles || dsc.Length > MaxFiles || stc.Length + dsc.Length == 0)
            throw new InvalidDataException("Supply one to 64 loose files per catalogue type.");
        var catalogues = stc.Concat(dsc).Select(path => LocalAddonFiles.Resolve(root, root, path))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray();

        string parent = Path.GetDirectoryName(target) ?? throw new IOException("The output requires a parent directory.");
        Directory.CreateDirectory(parent);
        string stage = Path.Combine(parent, ".sky-pack-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stage);
        try
        {
            // Catalogue-relative models can themselves refer to nearby textures or buffers.
            // Keep companion layout intact, as the SSC pack builder does.
            var options = new EnumerationOptions
            {
                RecurseSubdirectories = true, MaxRecursionDepth = 8,
                AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = false
            };
            int count = 0;
            foreach (string path in Directory.EnumerateFiles(root, "*", options))
            {
                cancellation.ThrowIfCancellationRequested();
                if (++count > 4096)
                    throw new InvalidDataException("The source contains more than 4096 files.");
                string input = LocalAddonFiles.Resolve(root, root, path);
                string output = Path.Combine(stage, Path.GetRelativePath(root, input));
                Directory.CreateDirectory(Path.GetDirectoryName(output)!);
                File.Copy(input, output);
            }

            using (var stream = File.Create(Path.Combine(stage, ManifestName)))
            {
                JsonSerializer.Serialize(stream, new
                {
                    format = Format, version = Version, catalogues = catalogues.Select(Path.GetFileName).ToArray()
                }, new JsonSerializerOptions { WriteIndented = true });
            }
            // STC may depend on the installed base catalogue. Structural errors still fail here.
            _ = StellarCatalogue.Empty.WithStc(stage, cancellation);
            _ = DeepSkyIndex.Load(stage, cancellation);
            cancellation.ThrowIfCancellationRequested();
            Directory.Move(stage, target);
            return (stc.Length, dsc.Length);
        }
        finally
        {
            if (Directory.Exists(stage))
            {
                string resolvedStage = new DirectoryInfo(stage).FullName;
                if (!string.Equals(Path.GetDirectoryName(resolvedStage), parent, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("The staging directory is outside the pack destination parent.");
                Directory.Delete(resolvedStage, recursive: true);
            }
        }
    }

    private static JsonDocument? ReadManifest(string root)
    {
        string path = Path.Combine(root, ManifestName);
        if (!AddonDependencies.FileExists(path))
            return null;
        using var stream = AddonDependencies.OpenRead(LocalAddonFiles.Resolve(root, root, path), MaxManifestBytes);
        JsonDocument manifest;
        try { manifest = JsonDocument.Parse(stream); }
        catch (JsonException error) { throw new InvalidDataException("Invalid sky catalogue pack manifest.", error); }
        if (manifest.RootElement.ValueKind == JsonValueKind.Object &&
            manifest.RootElement.TryGetProperty("format", out var format) &&
            format.ValueKind == JsonValueKind.String && format.GetString() == Format)
            return manifest;
        manifest.Dispose();
        throw new InvalidDataException("Unrecognized sky catalogue pack format.");
    }
}
