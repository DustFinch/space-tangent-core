// SPDX-License-Identifier: GPL-2.0-or-later
using SpaceTangent.Contracts;
using SpaceTangent.Core.Ephemerides;
using SpaceTangent.Core.World;

namespace SpaceTangent.Core.Addons;

/// <summary>Built-in models available in the host. Orbits return ICRF states relative to their native center.</summary>
public sealed record SscCustomModels(
    Func<string, Func<JulianInstant, CartesianState?>?> Orbit,
    Func<string, Func<JulianInstant, FrameKinematics?>?> Rotation)
{
    private static readonly string[] PlanetNames =
        ["Mercury", "Venus", "Earth", "Mars", "Jupiter", "Saturn", "Uranus", "Neptune"];

    private static readonly ObjectId[] SystemIds =
    [
        SolarSystemSample.MercuryId, SolarSystemSample.VenusId, SolarSystemSample.EarthId,
        SolarSystemSample.MarsBarycenterId, SolarSystemSample.JupiterBarycenterId, SolarSystemSample.SaturnBarycenterId,
        SolarSystemSample.UranusBarycenterId, SolarSystemSample.NeptuneBarycenterId
    ];

    /// <summary>Binds the standard catalogue model names to the caller's ephemeris and planetary frame data.</summary>
    public static SscCustomModels FromSolarSystem(ISolarSystemEphemeris ephemeris,
        Func<int, Func<JulianInstant, FrameKinematics?>?> planetaryRotation)
    {
        ArgumentNullException.ThrowIfNull(ephemeris);
        ArgumentNullException.ThrowIfNull(planetaryRotation);
        return new(Orbit, RotationForName);

        Func<JulianInstant, CartesianState?>? Orbit(string name)
        {
            string key = name.ToLowerInvariant();
            if (key.StartsWith("vsop87-", StringComparison.Ordinal))
            {
                int index = PlanetIndex(key[7..]);
                return ephemeris is HistoricalSolarEphemeris historical && index >= 0
                    ? epoch => historical.SamplePlanetarySeries(index + 1, epoch) : null;
            }
            bool barycentric = key.StartsWith("jpl-", StringComparison.Ordinal) && key.EndsWith("-ssb", StringComparison.Ordinal);
            string body = key.StartsWith("jpl-", StringComparison.Ordinal) && (barycentric || key.EndsWith("-sun", StringComparison.Ordinal)) ? key[4..^4] :
                key.EndsWith("-sun", StringComparison.Ordinal) || key.EndsWith("-jpl", StringComparison.Ordinal) ? key[..^4] : key;
            int planet = PlanetIndex(body);
            ObjectId? target = planet >= 0 ? SystemIds[planet] :
                body == "sun" ? SolarSystemSample.SunId : body == "emb" ? SolarSystemSample.EarthMoonBarycenterId : null;
            if (target is not { } id)
                return null;
            var origin = barycentric || body == "sun" ? SolarSystemSample.BarycenterId : SolarSystemSample.SunId;
            return epoch => epoch.CompareTo(ephemeris.Start) >= 0 && epoch.CompareTo(ephemeris.End) <= 0
                ? ephemeris.Sample(epoch).RelativeState(id, origin) : null;
        }

        Func<JulianInstant, FrameKinematics?>? RotationForName(string name)
        {
            int planet = name.StartsWith("iau-", StringComparison.OrdinalIgnoreCase) ? PlanetIndex(name[4..]) : -1;
            return planet >= 0 ? planetaryRotation((planet + 1) * 100 + 99) : null;
        }
    }

    private static int PlanetIndex(string name) =>
        Array.FindIndex(PlanetNames, planet => planet.Equals(name, StringComparison.OrdinalIgnoreCase));
}
