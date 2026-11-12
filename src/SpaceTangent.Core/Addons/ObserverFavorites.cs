// SPDX-License-Identifier: GPL-2.0-or-later
using System.Globalization;
using SpaceTangent.Contracts;
using SpaceTangent.Core.Observing;

namespace SpaceTangent.Core.Addons;

/// <summary>A saved universal observer with an ecliptic Y-up position and a world-to-camera rotation.</summary>
public sealed record ObserverFavorite(string Name, string? Folder, Vector3d PositionKilometers, Vector3d Axis,
    double AngleRadians, double JulianDay, string? Selection, string CoordinateSystem);

/// <summary>Reads the named records in a legacy favorites file.</summary>
public static class ObserverFavorites
{
    public const int MaximumCharacters = 256 * 1024;

    public static IReadOnlyList<ObserverFavorite> Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length > MaximumCharacters)
            throw new InvalidDataException("The favorites file is too large.");
        var result = new List<ObserverFavorite>();
        foreach (var (name, properties) in new SscReader(text.TrimStart('\uFEFF'), "favorites.cel").ReadNamedRecords())
        {
            if (OptionalText(properties, "isFolder") == "true")
                continue;
            if (result.Count == 256)
                throw new InvalidDataException("The favorites file contains more than 256 entries.");
            Vector3d position = (Vector(properties, "base", default) + Vector(properties, "offset", default) * 1e-6)
                * StellarEntry.KilometersPerLightYear;
            Vector3d axis = Vector(properties, "axis", new(1, 0, 0));
            double angle = Number(properties, "angle", 0);
            double day = Number(properties, "time", 0);
            if (!position.IsFinite || !double.IsFinite(axis.Length) || (axis.Length == 0 && angle != 0))
                throw new InvalidDataException("A favorite has an invalid observer position or rotation axis.");
            axis = axis.Length > 0 ? axis / axis.Length : new(1, 0, 0);
            result.Add(new(name, OptionalText(properties, "parentFolder"), position, axis, angle, day,
                OptionalText(properties, "selection"), OptionalText(properties, "coordsys") ?? "universal"));
        }
        return result.AsReadOnly();
    }

    private static string? OptionalText(SscValue.Map properties, string key) =>
        properties.Fields.TryGetValue(key, out var value) ? value.AsText() : null;

    private static double Number(SscValue.Map properties, string key, double fallback) =>
        properties.Fields.TryGetValue(key, out var value) ? Number(value, key) : fallback;

    private static Vector3d Vector(SscValue.Map properties, string key, Vector3d fallback)
    {
        if (!properties.Fields.TryGetValue(key, out var value))
            return fallback;
        if (value is not SscValue.List { Items: [var x, var y, var z] })
            throw new InvalidDataException($"A favorite requires three components for {key}.");
        return new(Number(x, key), Number(y, key), Number(z, key));
    }

    private static double Number(SscValue value, string key) =>
        double.TryParse(value.AsText(), NumberStyles.Float, CultureInfo.InvariantCulture, out double result) &&
        double.IsFinite(result) ? result : throw new InvalidDataException($"A favorite has an invalid {key}.");
}
