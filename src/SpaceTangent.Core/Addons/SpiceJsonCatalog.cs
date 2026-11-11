// SPDX-License-Identifier: GPL-2.0-or-later
using System.Text.Json;
using SpaceTangent.Core.Spice;

namespace SpaceTangent.Core.Addons;

public sealed record SpiceCatalogResult(IReadOnlyList<SpiceSpacecraft> Spacecraft, int SensorCount);

public static class SpiceJsonCatalog
{
    public static SpiceCatalogResult Load(string catalogPath, string assetRoot)
    {
        string root = Path.GetFullPath(assetRoot);
        string path = LocalAddonFiles.Resolve(root, root, catalogPath);
        string directory = Path.GetDirectoryName(path)!;
        using FileStream stream = File.OpenRead(path);
        if (stream.Length > 1024 * 1024)
            throw new InvalidDataException("SPICE JSON catalogue exceeds 1 MiB.");
        using JsonDocument json = JsonDocument.Parse(stream);
        JsonElement catalog = json.RootElement;
        Require(catalog, "version", "1.0");
        var resources = new SpiceResources(root, directory,
            catalog.GetProperty("spiceKernels").EnumerateArray().Select(item => Text(item)));

        var tracks = new List<SpiceSpacecraft>();
        int sensors = 0;
        foreach (JsonElement item in catalog.GetProperty("items").EnumerateArray())
        {
            string kind = Text(item.GetProperty("class"));
            if (kind == "sensor")
            {
                sensors++;
                continue;
            }
            if (kind != "spacecraft")
                throw new NotSupportedException($"Unsupported SPICE JSON item class {kind}.");

            JsonElement trajectory = item.GetProperty("trajectory");
            Require(trajectory, "type", "Spice");
            JsonElement bodyFrame = item.GetProperty("bodyFrame");
            Require(bodyFrame, "type", "Spice");
            JsonElement geometry = item.GetProperty("geometry");
            Require(geometry, "type", "Mesh");
            string mesh = LocalAddonFiles.ResolveRelative(root, directory, Text(geometry.GetProperty("source")));
            double size = geometry.GetProperty("size").GetDouble();
            if (!double.IsFinite(size) || size <= 0)
                throw new InvalidDataException("SPICE mesh size must be finite and positive.");

            tracks.Add(new(Text(item.GetProperty("name")), Text(item.GetProperty("center")),
                NaifIds.Resolve(Text(trajectory.GetProperty("target"))), NaifIds.Resolve(Text(trajectory.GetProperty("center"))),
                mesh, size, Text(bodyFrame.GetProperty("name")), "J2000", resources));
        }
        return new(tracks.AsReadOnly(), sensors);
    }

    private static string Text(JsonElement element)
    {
        string? text = element.GetString();
        return string.IsNullOrWhiteSpace(text) ? throw new InvalidDataException("Expected SPICE JSON text.") : text;
    }

    private static void Require(JsonElement element, string field, string value)
    {
        if (Text(element.GetProperty(field)) != value)
            throw new NotSupportedException($"Expected SPICE JSON {field} {value}.");
    }
}
