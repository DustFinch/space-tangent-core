// SPDX-License-Identifier: GPL-2.0-or-later
using System.Text;
using System.Text.Json;

namespace SpaceTangent.Core.Addons;

public static partial class SscBodyCatalogue
{
    private const int PackWriterVersion = 19;
    private const int MaximumPackFiles = 4096;

    /// <summary>Builds a relocatable body pack from the same loose SSC documents the body loader reads.</summary>
    public static int BuildPack(string source, string destination, Action<string> warning,
        CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(warning);
        string root = Path.GetFullPath(source);
        string target = Path.GetFullPath(destination);
        string name = Path.GetFileName(Path.TrimEndingDirectorySeparator(root));
        string id = new(name.Take(128).Select(character => char.IsAsciiLetterOrDigit(character) ||
            character is '-' or '_' or '.' ? character : '-').ToArray());
        string parent = Path.GetDirectoryName(target) ?? throw new IOException("The output requires a parent directory.");
        if (!Directory.Exists(root))
            throw new DirectoryNotFoundException(root);
        for (DirectoryInfo? directory = new(root); directory is not null; directory = directory.Parent)
            if ((directory.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException($"Linked add-on directory: {directory.FullName}.");
        if (Contains(root, target))
            throw new InvalidDataException("The pack output must be outside the source folder.");
        if (Path.Exists(target))
            throw new IOException($"The pack output already exists: {target}.");
        if (File.Exists(Path.Combine(root, "pack.json")))
            throw new InvalidDataException("The input already contains a pack manifest. Supply a loose SSC source.");

        var loose = ReadLooseDocuments(root);
        if (loose.Documents.Count == 0)
            throw new InvalidDataException("No loose SSC catalogues were found in the source folder or its data subfolder.");
        var documents = loose.Documents.Select(document =>
        {
            cancellation.ThrowIfCancellationRequested();
            using var file = File.OpenRead(LocalAddonFiles.Resolve(root, root, document.File));
            if (file.Length > 1024 * 1024)
                throw new InvalidDataException("SSC catalogue exceeds 1 MiB.");
            using var reader = new StreamReader(file, new UTF8Encoding(false, true));
            return document with { Text = reader.ReadToEnd() };
        }).ToArray();
        var declarations = new List<SscDefinition>();
        var definitions = SscDefinitions.Load(documents, cancellation, declarations);
        var resources = new Dictionary<string, Dictionary<(string Kind, string Source), PackResource>>(StringComparer.Ordinal);
        foreach (var document in documents)
            resources.TryAdd(Path.GetDirectoryName(document.File)!, new());
        foreach (var definition in definitions)
        foreach (string field in new[] { "ScriptedOrbit", "ScriptedRotation" })
        {
            cancellation.ThrowIfCancellationRequested();
            if (!definition.Properties.Fields.ContainsKey(field))
                continue;
            try
            {
                ScriptRequest(definition, field, root, Resolve);
            }
            catch (FileNotFoundException error)
            {
                warning($"{definition.Parent}/{definition.Name}: {field} module is missing ({error.FileName}).");
            }
        }

        var bodies = ReadBodyDefinitions(definitions, declarations, _ => root, null, warning, Resolve, cancellation, [root], []);
        Directory.CreateDirectory(parent);
        string stage = Path.Combine(parent, ".body-pack-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stage);
        try
        {
            var inputs = new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 8,
                AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = false };
            int count = 0;
            foreach (string file in Directory.EnumerateFiles(root, "*", inputs))
            {
                cancellation.ThrowIfCancellationRequested();
                if (++count > MaximumPackFiles)
                    throw new InvalidDataException($"The source contains more than {MaximumPackFiles} files.");
                string relative = Path.GetRelativePath(root, file);
                if (Path.GetExtension(file).Equals(".ssc", StringComparison.OrdinalIgnoreCase))
                {
                    if (!documents.Any(document => document.File == file))
                        warning($"SSC outside the body loader's source directories was omitted: {relative}");
                    continue;
                }

                // Keep the source layout for resources with nested references such as meshes, Lua and metakernels.
                string copy = Path.Combine(stage, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
                File.Copy(LocalAddonFiles.Resolve(root, root, file), copy);
            }

            string manifest = Path.Combine(stage, "pack.json");
            using (var file = File.Create(manifest))
            using (var writer = new Utf8JsonWriter(file, new() { Indented = true }))
            {
                writer.WriteStartObject();
                writer.WriteNumber("formatVersion", PackWriterVersion);
                writer.WriteString("id", id.Length > 0 ? id : "body-pack");
                writer.WriteString("name", name.Length > 0 ? name : "Body pack");
                writer.WriteStartArray("sscCatalogs");
                foreach (var document in documents)
                {
                    writer.WriteStartObject();
                    writer.WriteString("name", Path.GetFileName(document.File));
                    writer.WriteString("text", document.Text);
                    writer.WriteNumber("firstSpiceLoad", 0);
                    writer.WriteNumber("spiceLoadCount", 0);
                    writer.WriteNumber("firstBody", 0);
                    writer.WriteNumber("bodyCount", 0);
                    writer.WriteNumber("firstPlace", 0);
                    writer.WriteNumber("placeCount", 0);
                    writer.WriteStartArray("resources");
                    foreach (var resource in resources[Path.GetDirectoryName(document.File)!].Values)
                    {
                        writer.WriteStartObject();
                        writer.WriteString("kind", resource.Kind);
                        writer.WriteString("source", resource.Source);
                        writer.WriteString("file", resource.File);
                        writer.WriteEndObject();
                    }
                    writer.WriteEndArray();
                    writer.WriteEndObject();
                }
                writer.WriteEndArray();
                writer.WriteEndObject();
            }
            int installed = Load(stage, cancellation).Count;
            if (installed != bodies.Count)
                throw new InvalidDataException("The generated pack does not load the same number of bodies as its source.");
            cancellation.ThrowIfCancellationRequested();
            Directory.Move(stage, target);
            return installed;
        }
        finally
        {
            if (Directory.Exists(stage))
                Directory.Delete(stage, recursive: true);
        }

        string Resolve(SscDefinition body, string field, AddonStage stage, string authored, params string[] fallback)
        {
            string file = loose.Resolve(body, field, stage, authored, fallback);
            string kind = PackedResourceKind(field, stage).Kind;
            if (kind == "texture")
                kind = "map";
            string logical = authored.Replace('\\', '/');
            var entries = resources[body.DirectoryFor(field)];
            entries.TryAdd((kind, logical), new(kind, logical, Path.GetRelativePath(root, file).Replace('\\', '/')));
            return file;
        }

        static bool Contains(string directory, string path)
        {
            string relative = Path.GetRelativePath(directory, path);
            return relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                && !Path.IsPathRooted(relative);
        }
    }

    private sealed record PackResource(string Kind, string Source, string File);
}
