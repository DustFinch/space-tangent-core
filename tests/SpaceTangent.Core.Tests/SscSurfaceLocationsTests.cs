// SPDX-License-Identifier: GPL-2.0-or-later
using SpaceTangent.Core.Addons;
using Xunit;

namespace SpaceTangent.Core.Tests;

public sealed class SscSurfaceLocationsTests
{
    [LocationFact]
    [Trait("Category", "ExternalData")]
    public void ReadsTheOriginalSurfaceCatalogsWithTheirCoordinates()
    {
        string root = Environment.GetEnvironmentVariable("SPACETANGENT_SURFACE_LOCATIONS")!;
        var locations = SscSurfaceLocations.Load(root);
        Assert.Equal(15_957, locations.Count);
        var olympus = Assert.Single(locations, site => site.Name == "Olympus Mons" && site.BodyPath == "Sol/Mars");
        Assert.Equal((-133.8, 18.65, 610.13, "MO"),
            (olympus.LongitudeDegrees, olympus.LatitudeDegrees, olympus.SizeKilometers, olympus.Type));
        var tycho = Assert.Single(locations, site => site.Name == "Tycho" && site.BodyPath == "Sol/Earth/Moon");
        Assert.Equal((-11.22, -43.3, 85.29, "AA"), (tycho.LongitudeDegrees, tycho.LatitudeDegrees, tycho.SizeKilometers, tycho.Type));
        Assert.Contains(locations, site => site.Type == "Ring" && site.AltitudeKilometers == 39_500);
    }

    private sealed class LocationFactAttribute : FactAttribute
    {
        public LocationFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SPACETANGENT_SURFACE_LOCATIONS")))
                Skip = "Set SPACETANGENT_SURFACE_LOCATIONS to the original surface catalog directory.";
        }
    }
}
