// SPDX-License-Identifier: GPL-2.0-or-later
using System.Globalization;
using System.Text;
using SpaceTangent.Core.Spice;

namespace SpaceTangent.Core.Addons;

public static class SscSpiceCatalog
{
    private const int MaximumCatalogBytes = 1024 * 1024;

    public static IReadOnlyList<SpiceSpacecraft> Load(string catalogPath, string assetRoot)
    {
        string root = Path.GetFullPath(assetRoot);
        string catalog = LocalAddonFiles.Resolve(root, root, catalogPath);
        string directory = Path.GetDirectoryName(catalog)!;
        using FileStream stream = File.OpenRead(catalog);
        if (stream.Length > MaximumCatalogBytes)
            throw new InvalidDataException("SSC catalogue exceeds 1 MiB.");
        using var reader = new StreamReader(stream, new UTF8Encoding(false, true));
        var parser = new SscReader(reader.ReadToEnd());
        var result = new List<SpiceSpacecraft>();
        foreach (var (name, parent, fields) in parser.ReadDefinitions())
        {
            if (fields.Text("Class") != "spacecraft")
                throw new NotSupportedException("This SSC adapter expects spacecraft with a SpiceOrbit.");
            if (name.Length == 0 || parent.Length == 0)
                throw new InvalidDataException("SSC name and parent are required.");
            string mesh = LocalAddonFiles.ResolveRelative(root, directory, fields.Text("Mesh"));
            if (!double.TryParse(fields.Text("Radius"), NumberStyles.Float, CultureInfo.InvariantCulture, out double radius) ||
                !double.IsFinite(radius) || radius <= 0)
                throw new InvalidDataException("SSC radius must be finite and positive.");

            SscValue.Map orbit = fields.Object("SpiceOrbit");
            int target = NaifIds.Resolve(orbit.Text("Target"));
            int center = NaifIds.Resolve(orbit.Text("Origin"));
            SscValue entry = orbit.Get("Kernel");
            SscValue[] entries = entry is SscValue.List list ? list.Items : [entry];
            var resources = new SpiceResources(root, directory, entries.Select(source => source.AsText()));

            string? attitude = null, baseFrame = null;
            if (fields.Fields.TryGetValue("SpiceRotation", out var rotation))
            {
                if (rotation is not SscValue.Map definition)
                    throw new InvalidDataException("SSC SpiceRotation must be an object.");
                attitude = definition.Text("Frame");
                baseFrame = definition.Text("BaseFrame");
            }
            result.Add(new(name, parent, target, center, mesh, radius, attitude, baseFrame, resources));
        }
        return result.AsReadOnly();
    }
}
