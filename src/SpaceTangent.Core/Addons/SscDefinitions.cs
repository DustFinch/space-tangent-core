// SPDX-License-Identifier: GPL-2.0-or-later
using System.Text;

namespace SpaceTangent.Core.Addons;

internal sealed record SscDefinition(string Names, string Parent, SscValue.Map Properties,
    IReadOnlyDictionary<string, string> FieldDirectories)
{
    internal string Name => Names.Split(':')[0];

    internal string DirectoryFor(string field) => FieldDirectories.TryGetValue(field, out string? directory)
        ? directory : throw new InvalidDataException($"Missing SSC field {field}.");
}

internal static class SscDefinitions
{
    private const int MaximumCatalogBytes = 1024 * 1024;
    private static readonly string[] OrbitModels = ["Timeline", "CustomOrbit", "SpiceOrbit", "ScriptedOrbit",
        "SampledTrajectory", "SampledOrbit", "EllipticalOrbit", "FixedPosition", "LongLat"];
    private static readonly string[] RotationModels = ["CustomRotation", "SpiceRotation", "ScriptedRotation",
        "SampledOrientation", "PrecessingRotation", "UniformRotation", "FixedRotation", "FixedAttitude"];

    internal static IReadOnlyList<SscDefinition> Load(IEnumerable<string> paths, string? root,
        CancellationToken cancellation = default)
    {
        var bodies = new List<SscDefinition>();
        var aliases = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        int longestAliasPath = 0;
        foreach (string path in paths)
        {
            cancellation.ThrowIfCancellationRequested();
            string file = root is null ? Path.GetFullPath(path) : LocalAddonFiles.Resolve(root, root, path);
            string directory = Path.GetDirectoryName(file)!;
            using FileStream stream = File.OpenRead(file);
            if (stream.Length > MaximumCatalogBytes)
                throw new InvalidDataException("SSC catalogue exceeds 1 MiB.");
            using var reader = new StreamReader(stream, new UTF8Encoding(false, true));
            foreach (var (disposition, kind, nameList, parentPath, properties) in new SscReader(reader.ReadToEnd()).ReadDefinitions())
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

                if (aliases.TryGetValue(primaryPath, out int index))
                {
                    if (disposition == SscDisposition.Add)
                        throw new InvalidDataException($"Duplicate SSC body {primaryPath}.");
                    SscDefinition previous = bodies[index];
                    bodies[index] = disposition == SscDisposition.Modify
                        ? Modify(previous, properties, directories)
                        : previous with { Properties = properties, FieldDirectories = directories };
                    continue;
                }

                foreach (string name in names.Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    string aliasPath = parent + "/" + name;
                    if (!aliases.TryAdd(aliasPath, bodies.Count))
                        throw new InvalidDataException($"Duplicate SSC body alias {parent}/{name}.");
                    longestAliasPath = Math.Max(longestAliasPath, aliasPath.Length);
                }
                bodies.Add(new(string.Join(':', names), parent, properties, directories));
            }
        }
        return bodies.AsReadOnly();

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

    private static SscDefinition Modify(SscDefinition previous, SscValue.Map patch, Dictionary<string, string> directories)
    {
        var fields = new Dictionary<string, SscValue>(previous.Properties.Fields, StringComparer.Ordinal);
        var origins = new Dictionary<string, string>(previous.FieldDirectories, StringComparer.Ordinal);
        RemoveSuperseded(OrbitModels);
        RemoveSuperseded(RotationModels);
        foreach (var (key, value) in patch.Fields)
        {
            fields[key] = value;
            origins[key] = directories[key];
        }
        return previous with { Properties = new(fields), FieldDirectories = origins };

        void RemoveSuperseded(string[] family)
        {
            if (!family.Any(patch.Fields.ContainsKey))
                return;
            foreach (string key in family)
            {
                fields.Remove(key);
                origins.Remove(key);
            }
        }
    }
}
