// SPDX-License-Identifier: GPL-2.0-or-later
namespace SpaceTangent.Core.Coordinates;

public static class AstronomicalUnits
{
    private const long MetersPerLightYear = 299_792_458L * 31_557_600;

    public const double KilometersPerAstronomicalUnit = 149_597_870.7;
    public const double KilometersPerLightYear = MetersPerLightYear / 1_000d;
    public const double KilometersPerMicroLightYear = MetersPerLightYear / 1_000_000_000d;
}
