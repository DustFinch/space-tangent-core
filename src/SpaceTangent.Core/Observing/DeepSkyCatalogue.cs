// SPDX-License-Identifier: GPL-2.0-or-later
using SpaceTangent.Contracts;

namespace SpaceTangent.Core.Observing;

public enum DeepSkyKind { Galaxy, Nebula, OpenCluster, GlobularCluster }

public sealed record DeepSkyObject(ObjectId Id, string Designation, string Name, DeepSkyKind Kind,
    double RightAscensionDegrees, double DeclinationDegrees, double DistanceLightYears, double RadiusLightYears)
{
    public IReadOnlyList<string> Aliases { get; init; } = [];
    public double? AbsoluteMagnitude { get; init; }
    public string? Morphology { get; init; }
    public Basis3d Orientation { get; init; } = Basis3d.Identity;
    public double KingConcentration { get; init; }
    public double CoreRadiusArcminutes { get; init; }
    public string? Mesh { get; init; }
    internal string? CatalogueFile { get; init; }

    public double RadiusKilometers => RadiusLightYears * StellarEntry.KilometersPerLightYear;

    public Vector3d PositionKilometers
    {
        get
        {
            const double radians = Math.PI / 180;
            var (sinRa, cosRa) = Math.SinCos(RightAscensionDegrees * radians);
            var (sinDec, cosDec) = Math.SinCos(DeclinationDegrees * radians);
            return new Vector3d(cosDec * cosRa, cosDec * sinRa, sinDec) *
                (DistanceLightYears * StellarEntry.KilometersPerLightYear);
        }
    }
}

public static class DeepSkyCatalogue
{
    // Rounded ICRS positions and representative distance/visual extents for these nearby objects.
    public static IReadOnlyList<DeepSkyObject> Objects { get; } = Array.AsReadOnly(new DeepSkyObject[]
    {
        new(new("dso/m31"), "M31", "Andromeda Galaxy", DeepSkyKind.Galaxy, 10.6847, 41.2688, 2_500_000, 110_000),
        new(new("dso/m42"), "M42", "Orion Nebula", DeepSkyKind.Nebula, 83.8221, -5.3911, 1500, 12),
        new(new("dso/m45"), "M45", "Pleiades", DeepSkyKind.OpenCluster, 56.75, 24.1167, 445, 8)
    });

    public static DeepSkyObject? Find(ObjectId id) => Objects.FirstOrDefault(body => body.Id == id);
}
