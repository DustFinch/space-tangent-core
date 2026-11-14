// SPDX-License-Identifier: GPL-2.0-or-later
using System.Text;

namespace SpaceTangent.Core.Addons;

internal sealed record SscDefinition(string Names, string Parent, SscValue.Map Properties,
    IReadOnlyDictionary<string, string> FieldDirectories, IReadOnlyDictionary<string, int> FieldOrder, string SourceFile)
{
    internal string Name => Names.Split(':')[0];

    internal string DirectoryFor(string field) => FieldDirectories.TryGetValue(field, out string? directory)
        ? directory : throw new InvalidDataException($"Missing SSC field {field}.");
}

internal sealed record SscDocument(string File, string? Root, string? Text = null);

internal static class SscDefinitions
{
    private const int MaximumCatalogBytes = 1024 * 1024;
    private static readonly string[] OrbitModels = ["Timeline", "CustomOrbit", "SpiceOrbit", "ScriptedOrbit",
        "SampledTrajectory", "SampledOrbit", "EllipticalOrbit", "FixedPosition", "LongLat"];
    private static readonly string[] RotationModels = ["CustomRotation", "SpiceRotation", "ScriptedRotation",
        "SampledOrientation", "PrecessingRotation", "UniformRotation", "FixedRotation", "FixedAttitude", "RotationPeriod"];

    internal static IReadOnlyList<SscDefinition> Load(IEnumerable<string> paths, string? root,
        CancellationToken cancellation = default, IReadOnlyDictionary<string, string>? embedded = null)
        => Load(paths.Select(path =>
        {
            string file = root is null ? Path.GetFullPath(path) : Path.GetFullPath(path, root);
            return new SscDocument(file, root, embedded?[file]);
        }), cancellation);

    internal static IReadOnlyList<SscDefinition> Load(IEnumerable<SscDocument> documents, CancellationToken cancellation,
        List<SscDefinition>? declarations = null)
    {
        var bodies = new List<SscDefinition>();
        var aliases = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        int longestAliasPath = 0, operation = 0;
        foreach (var document in documents)
        {
            cancellation.ThrowIfCancellationRequested();
            string file = document.File;
            try
            {
                LoadFile(document);
            }
            catch (Exception error) when (AddonDiagnostics.Annotate(error, AddonStage.Catalog, file))
            {
                throw;
            }
        }
        return bodies.AsReadOnly();

        void LoadFile(SscDocument document)
        {
            string file = document.File;
            string directory = Path.GetDirectoryName(file)!;
            string source = document.Text ?? ReadFile(file, document.Root);
            foreach (var (disposition, kind, nameList, parentPath, properties) in new SscReader(source, file).ReadDefinitions())
            {
                cancellation.ThrowIfCancellationRequested();
                if (kind is "Location" or "AltSurface")
                    continue;
                string[] names = nameList.Split(':', StringSplitOptions.RemoveEmptyEntries);
                if (names.Length == 0 || parentPath.Length == 0)
                    throw new InvalidDataException("SSC name and parent are required.");
                string parent = ResolveParent(parentPath);
                string primaryPath = parent + "/" + names[0];
                var directories = properties.Fields.Keys.ToDictionary(key => key, _ => directory, StringComparer.Ordinal);
                int sequence = operation++;
                var positions = properties.Fields.Keys.ToDictionary(key => key, _ => sequence, StringComparer.Ordinal);

                var declaration = new SscDefinition(string.Join(':', names), parent, properties, directories, positions, file);
                declarations?.Add(declaration);

                if (aliases.TryGetValue(primaryPath, out int index))
                {
                    if (disposition == SscDisposition.Add)
                        throw new InvalidDataException($"Duplicate SSC body {primaryPath}.");
                    SscDefinition previous = bodies[index];
                    var effective = disposition == SscDisposition.Modify
                        ? Modify(previous, properties, directories, positions)
                        : previous with { Properties = properties, FieldDirectories = directories, FieldOrder = positions };
                    bodies[index] = effective with { SourceFile = file };
                    continue;
                }

                foreach (string name in names.Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    string aliasPath = parent + "/" + name;
                    if (!aliases.TryAdd(aliasPath, bodies.Count))
                        throw new InvalidDataException($"Duplicate SSC body alias {parent}/{name}.");
                    longestAliasPath = Math.Max(longestAliasPath, aliasPath.Length);
                }
                bodies.Add(declaration);
            }
        }
        static string ReadFile(string file, string? root)
        {
            if (root is not null)
                file = LocalAddonFiles.Resolve(root, root, file);
            using FileStream stream = AddonDependencies.OpenRead(file, MaximumCatalogBytes);
            if (stream.Length > MaximumCatalogBytes)
                throw new InvalidDataException("SSC catalogue exceeds 1 MiB.");
            using var reader = new StreamReader(stream, new UTF8Encoding(false, true));
            return reader.ReadToEnd();
        }

        string ResolveParent(string path)
        {
            if (aliases.TryGetValue(path, out int index))
                return bodies[index].Parent + "/" + bodies[index].Name;
            int separator = path.IndexOf('/');
            while (separator >= 0 && separator <= longestAliasPath)
            {
                if (aliases.TryGetValue(path[..separator], out index))
                {
                    SscDefinition body = bodies[index];
                    string prefix = body.Parent + "/" + body.Name;
                    path = prefix + path[separator..];
                    separator = prefix.Length;
                }
                separator = path.IndexOf('/', separator + 1);
            }
            return aliases.TryGetValue(path, out index)
                ? bodies[index].Parent + "/" + bodies[index].Name : path;
        }
    }

    private static SscDefinition Modify(SscDefinition previous, SscValue.Map patch,
        Dictionary<string, string> directories, Dictionary<string, int> positions)
    {
        var fields = new Dictionary<string, SscValue>(previous.Properties.Fields, StringComparer.Ordinal);
        var origins = new Dictionary<string, string>(previous.FieldDirectories, StringComparer.Ordinal);
        var order = new Dictionary<string, int>(previous.FieldOrder, StringComparer.Ordinal);
        RemoveSuperseded(OrbitModels);
        RemoveSuperseded(RotationModels);
        foreach (var (key, value) in patch.Fields)
        {
            fields[key] = value;
            origins[key] = directories[key];
            order[key] = positions[key];
        }
        return previous with { Properties = new(fields), FieldDirectories = origins, FieldOrder = order };

        void RemoveSuperseded(string[] family)
        {
            if (!patch.Fields.Keys.Any(Matches))
                return;
            foreach (string key in fields.Keys.Where(Matches).ToArray())
            {
                fields.Remove(key);
                origins.Remove(key);
                order.Remove(key);
            }

            bool Matches(string key)
            {
                int unit = key.IndexOf('<');
                return family.Contains(unit >= 0 ? key[..unit] : key, StringComparer.Ordinal);
            }
        }
    }
}
