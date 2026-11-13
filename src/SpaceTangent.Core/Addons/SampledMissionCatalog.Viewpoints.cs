// SPDX-License-Identifier: GPL-2.0-or-later
using System.Text.Json;
using SpaceTangent.Contracts;

namespace SpaceTangent.Core.Addons;

/// <summary>A camera position above a body's surface, evaluated when the observer opens it.</summary>
public sealed record MissionViewpoint(string Name, ObjectId Center, ObjectId? Reference, double AltitudeKilometers,
    double AzimuthDegrees, double ElevationDegrees, bool EquatorialUp);

public static partial class SampledMissionCatalog
{
    private static MissionViewpoint ReadViewpoint(JsonElement value, bool packed, Func<string, ObjectId> resolve)
    {
        string name = Text(value, "name");
        var center = resolve(Text(value, "center"));
        ObjectId? reference = value.TryGetProperty("reference", out var target) ? resolve(target.GetString() ?? "") : null;
        string distance = packed ? "distanceKm" : value.TryGetProperty("altitude", out _) ? "altitude" : "distance";
        double altitude = value.TryGetProperty(distance, out _) ? Distance(value, distance) : 100_000;
        double azimuth = Degrees(packed ? "azimuthDeg" : "azimuth");
        double elevation = Degrees(packed ? "elevationDeg" : "elevation");
        if (altitude < 0)
            throw new InvalidDataException($"Viewpoint {name} has negative altitude.");
        bool equatorial = value.TryGetProperty("up", out var up) &&
            up.GetString()?.Contains("equator", StringComparison.OrdinalIgnoreCase) == true;
        return new(name, center, reference, altitude, azimuth, elevation, equatorial);

        double Degrees(string field) => value.TryGetProperty(field, out var angle) ? ParticleNumber(angle) : 0;
    }
}
