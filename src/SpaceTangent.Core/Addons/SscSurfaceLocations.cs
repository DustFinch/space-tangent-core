// SPDX-License-Identifier: GPL-2.0-or-later
using System.Globalization;
using System.Text;

namespace SpaceTangent.Core.Addons;

public sealed record SscSurfaceLocation(string Name, string BodyPath, double LongitudeDegrees, double LatitudeDegrees,
    double AltitudeKilometers, double SizeKilometers, string? Type, double? Importance);

/// <summary>Reads geographical locations from SSC sources without treating them as orbiting bodies.</summary>
public static class SscSurfaceLocations
{
    private const int MaximumFileBytes = 1024 * 1024;
    private const int MaximumLocations = 32_768;

    public static IReadOnlyList<SscSurfaceLocation> Load(string root, CancellationToken cancellation = default)
    {
        root = Path.GetFullPath(root);
        if (!AddonDependencies.DirectoryExists(root))
            return [];
        string[] files = AddonDependencies.ListFiles(root, "*_locs.ssc").Order(StringComparer.OrdinalIgnoreCase).ToArray();
        if (files.Length > 64)
            throw new InvalidDataException("The surface catalog directory contains more than 64 SSC files.");
        var locations = new List<SscSurfaceLocation>();
        foreach (string path in files)
        {
            cancellation.ThrowIfCancellationRequested();
            string file = LocalAddonFiles.Resolve(root, root, path);
            using FileStream input = AddonDependencies.OpenRead(file, MaximumFileBytes);
            using var reader = new StreamReader(input, new UTF8Encoding(false, true));
            foreach (var (_, kind, name, body, properties) in new SscReader(reader.ReadToEnd(), file).ReadDefinitions())
            {
                cancellation.ThrowIfCancellationRequested();
                if (kind != "Location")
                    continue;
                if (locations.Count == MaximumLocations)
                    throw new InvalidDataException("The surface catalog contains too many locations.");
                locations.Add(AddonDiagnostics.Read(AddonStage.Catalog, file, () => Read(name, body, properties)));
            }
        }
        return locations.AsReadOnly();
    }

    private static SscSurfaceLocation Read(string name, string body, SscValue.Map properties)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 256 || name.Any(char.IsControl) ||
            string.IsNullOrWhiteSpace(body) || body.Length > 256)
            throw new InvalidDataException("A surface location requires a name and a parent body.");
        if (properties.Get("LongLat") is not SscValue.List { Items: [var x, var y, var z] })
            throw new InvalidDataException("A surface location requires longitude, latitude and altitude.");
        double longitude = Number(x), latitude = Number(y), altitude = Number(z);
        double size = Optional("Size") ?? 0;
        double? importance = Optional("Importance");
        if (latitude is < -90 or > 90 || Math.Abs(longitude) > 360 || Math.Abs(altitude) > 2_000_000 || size < 0)
            throw new InvalidDataException("The surface location is outside the supported scale or latitude.");
        string? type = properties.Fields.GetValueOrDefault("Type")?.AsText();
        return new(name, body, Math.IEEERemainder(longitude, 360), latitude, altitude, size, type, importance);

        double? Optional(string key) => properties.Fields.TryGetValue(key, out var value) ? Number(value) : null;
    }

    private static double Number(SscValue value) =>
        double.TryParse(value.AsText(), NumberStyles.Float, CultureInfo.InvariantCulture, out var result) &&
        double.IsFinite(result) ? result : throw new InvalidDataException("The surface location requires finite coordinates.");
}
