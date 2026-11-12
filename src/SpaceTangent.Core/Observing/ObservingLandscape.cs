// SPDX-License-Identifier: GPL-2.0-or-later
using System.Collections.ObjectModel;
using System.Globalization;

namespace SpaceTangent.Core.Observing;

public enum LandscapeProjection { Spherical, Panels, Fisheye, Polygonal }
public enum LandscapeLayer { Ground, Lights, Fog }

public readonly record struct PanoramaBounds(double Top, double Bottom, double Rotation)
{
    public static PanoramaBounds WholeSky { get; } = new(90, -90, 0);
    public double Column(double azimuth) => ObservingLandscape.Wrap(azimuth - 90 - Rotation) / 360;
    public double Row(double altitude) => (Top - altitude) / (Top - Bottom);
}

public sealed class ObservingLandscape
{
    public string Id { get; }
    public string Name { get; }
    public string Body { get; }
    public LandscapeProjection Projection { get; }
    private readonly IReadOnlyDictionary<string, string> _properties;

    private ObservingLandscape(string id, string name, string body, LandscapeProjection projection, Dictionary<string, string> properties)
    {
        Id = id;
        Name = name;
        Body = body;
        Projection = projection;
        _properties = new ReadOnlyDictionary<string, string>(properties);
    }

    public static bool ValidId(string id) => id.Length is > 0 and <= 80 &&
        id.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_');

    public static ObservingLandscape Read(string id, TextReader reader)
    {
        if (!ValidId(id))
            throw new ArgumentException("Invalid landscape identifier.", nameof(id));
        var properties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string? body = null;
        string section = "";
        while (reader.ReadLine() is { } raw)
        {
            var line = raw.AsSpan().Trim();
            if (line.IsEmpty || line[0] is '#' or ';')
                continue;
            if (line[0] == '[' && line[^1] == ']')
            {
                section = line[1..^1].Trim().ToString().ToLowerInvariant();
                continue;
            }
            int separator = line.IndexOf('=');
            if (separator <= 0)
                continue;
            string key = line[..separator].Trim().ToString();
            string value = line[(separator + 1)..].Trim().ToString();
            if (section == "landscape")
                properties[key] = value;
            else if (section == "location" && key.Equals("planet", StringComparison.OrdinalIgnoreCase))
                body = value;
        }
        var projection = properties.GetValueOrDefault("type")?.ToLowerInvariant() switch
        {
            "spherical" => LandscapeProjection.Spherical,
            "old_style" => LandscapeProjection.Panels,
            "fisheye" => LandscapeProjection.Fisheye,
            "polygonal" => LandscapeProjection.Polygonal,
            _ => throw new InvalidDataException($"Unknown landscape projection for {id}.")
        };
        var result = new ObservingLandscape(id, properties.GetValueOrDefault("name", id),
            string.IsNullOrWhiteSpace(body) ? "Earth" : body, projection, properties);
        switch (projection)
        {
            case LandscapeProjection.Spherical:
                _ = result.Bounds(LandscapeLayer.Ground);
                goto case LandscapeProjection.Fisheye;
            case LandscapeProjection.Fisheye:
                if (result.File(LandscapeLayer.Ground) is null)
                    throw new InvalidDataException($"{id} has no panorama texture.");
                break;
            case LandscapeProjection.Panels:
                result.Count("nbside", 0);
                result.Count("nbsidetex", 0);
                break;
            case LandscapeProjection.Polygonal:
                if (result.Value("polygonal_horizon_list") is null)
                    throw new InvalidDataException($"{id} has no horizon list.");
                break;
        }
        return result;
    }

    public string? File(LandscapeLayer layer) => Value(TextureKey(layer));

    public PanoramaBounds Bounds(LandscapeLayer layer)
    {
        if (Projection != LandscapeProjection.Spherical)
            return PanoramaBounds.WholeSky;
        string key = TextureKey(layer);
        double top = Number(key + "_top", 90);
        double bottom = Number(key + "_bottom", -90);
        if (top <= bottom || top > 90 || bottom < -90)
            throw new InvalidDataException($"{Id} has invalid {key} altitude bounds.");
        return new(top, bottom, Number("angle_rotatez", 0));
    }

    private static string TextureKey(LandscapeLayer layer) => layer switch
    {
        LandscapeLayer.Ground => "maptex",
        LandscapeLayer.Lights => "maptex_illum",
        LandscapeLayer.Fog => "maptex_fog",
        _ => throw new ArgumentOutOfRangeException(nameof(layer))
    };

    internal string? Value(string key) => _properties.GetValueOrDefault(key) is { Length: > 0 } value ? value : null;
    internal bool Flag(string key) => Value(key) is { } value &&
        (value == "1" || value.Equals("true", StringComparison.OrdinalIgnoreCase));

    internal double Number(string key, double fallback) => Value(key) is { } value
        ? ParseNumber(value) : fallback;

    internal int Count(string key, int fallback)
    {
        double value = Number(key, fallback);
        if (value is < 1 or > 256 || value != Math.Truncate(value))
            throw new InvalidDataException($"{Id} has an invalid {key} count.");
        return (int)value;
    }

    internal static double ParseNumber(string text) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value) && double.IsFinite(value)
            ? value : throw new InvalidDataException($"Invalid landscape number '{text}'.");

    internal static double Wrap(double degrees) => (degrees % 360 + 360) % 360;
}
