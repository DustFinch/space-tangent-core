// SPDX-License-Identifier: GPL-2.0-or-later
using System.Text.Json;
using SpaceTangent.Contracts;
using SpaceTangent.Core.Spice;

namespace SpaceTangent.Core.Addons;

public sealed record SpiceCatalogResult(IReadOnlyList<SpiceSpacecraft> Spacecraft, IReadOnlyList<SpiceSensor> Sensors)
{
    public int SensorCount => Sensors.Count;
}

public static class SpiceJsonCatalog
{
    public static SpiceCatalogResult Load(string catalogPath, string assetRoot)
    {
        string root = Path.GetFullPath(assetRoot);
        string path = LocalAddonFiles.Resolve(root, root, catalogPath);
        string directory = Path.GetDirectoryName(path)!;
        using FileStream stream = AddonDependencies.OpenRead(path, 1024 * 1024);
        if (stream.Length > 1024 * 1024)
            throw new InvalidDataException("SPICE JSON catalogue exceeds 1 MiB.");
        using JsonDocument json = JsonDocument.Parse(stream);
        JsonElement catalog = json.RootElement;
        Require(catalog, "version", "1.0");
        var resources = new SpiceResources(root, directory,
            catalog.GetProperty("spiceKernels").EnumerateArray().Select(item => Text(item)));

        var tracks = new List<SpiceSpacecraft>();
        var sensorItems = new List<JsonElement>();
        foreach (JsonElement item in catalog.GetProperty("items").EnumerateArray())
        {
            string kind = Text(item.GetProperty("class"));
            if (kind == "sensor")
            {
                sensorItems.Add(item);
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
        var sensors = new List<SpiceSensor>(sensorItems.Count);
        if (sensorItems.Count > 0)
        {
            var pool = new SpiceTextKernel();
            foreach (string file in resources.Files)
            {
                string extension = Path.GetExtension(file);
                if (extension.Equals(".tf", StringComparison.OrdinalIgnoreCase) ||
                    extension.Equals(".ti", StringComparison.OrdinalIgnoreCase))
                    pool.Furnish(file);
            }
            foreach (JsonElement sensor in sensorItems)
                sensors.Add(ReadSensor(sensor, tracks, pool));
        }
        return new(tracks.AsReadOnly(), sensors.AsReadOnly());
    }

    private static SpiceSensor ReadSensor(JsonElement item, List<SpiceSpacecraft> tracks, SpiceTextKernel pool)
    {
        string parent = Text(item.GetProperty("parent"));
        if (Text(item.GetProperty("center")) != parent)
            throw new NotSupportedException("An instrument must be centered on its parent spacecraft.");
        SpiceSpacecraft[] matches = tracks.Where(track => track.Name == parent).ToArray();
        if (matches.Length != 1 || matches[0].AttitudeFrame is not { } bodyFrame)
            throw new InvalidDataException($"SPICE sensor parent {parent} needs a unique spacecraft attitude.");
        JsonElement geometry = item.GetProperty("geometry");
        Require(geometry, "type", "Spice");
        string instrument = Text(geometry.GetProperty("instrName"));
        double range = geometry.GetProperty("range").GetDouble();
        if (!double.IsFinite(range) || range <= 0)
            throw new InvalidDataException("SPICE sensor range must be finite and positive.");
        Vector3d color = new(1, 1, 1);
        if (geometry.TryGetProperty("frustumColor", out JsonElement colorValue))
        {
            if (colorValue.GetArrayLength() != 3)
                throw new InvalidDataException("SPICE sensor color requires three components.");
            color = new(UnitInterval(colorValue[0]), UnitInterval(colorValue[1]), UnitInterval(colorValue[2]));
        }
        return new(Text(item.GetProperty("name")), matches[0], instrument, Text(geometry.GetProperty("target")), range,
            color, Opacity(geometry, "frustumOpacity", .3), Opacity(geometry, "footprintOpacity", 1),
            SpiceFieldOfView.Load(pool, instrument, bodyFrame));
    }

    private static double Opacity(JsonElement geometry, string key, double fallback) =>
        geometry.TryGetProperty(key, out JsonElement value) ? UnitInterval(value) : fallback;

    private static double UnitInterval(JsonElement value)
    {
        double number = value.GetDouble();
        return double.IsFinite(number) && number is >= 0 and <= 1 ? number
            : throw new InvalidDataException("SPICE sensor color and opacity must be between zero and one.");
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
