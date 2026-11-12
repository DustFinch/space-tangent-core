// SPDX-License-Identifier: GPL-2.0-or-later
using SpaceTangent.Core.Observing;
using Xunit;

namespace SpaceTangent.Core.Tests;

public sealed class EarthPlaceCatalogueTests
{
    [OriginalPlacesFact]
    [Trait("Category", "ExternalData")]
    public void ArchiveRetainsLocalizedCitiesAndOriginalObservatoryCoordinates()
    {
        var warnings = new List<string>();
        var catalogue = EarthPlaceCatalogue.Load(Environment.GetEnvironmentVariable("SPACETANGENT_EARTH_PLACES")!, warning: warnings.Add);
        Assert.Empty(warnings);
        Assert.Equal(6822, catalogue.Places.Count);
        var moscow = Assert.Single(catalogue.Places, place => place.EnglishName == "Moscow" && place.Region == "Moscow");
        Assert.Equal("Москва", moscow.Name);
        Assert.Equal(new EarthSite(55.752, 37.6178, .155), moscow.ObserverSite);
        Assert.Equal(9, moscow.Bortle);
        Assert.Equal(3, moscow.OffsetHours(new(2024, 7, 1, 0, 0, 0, TimeSpan.Zero)));

        var paranal = Assert.Single(catalogue.Places, place => place.EnglishName == "Paranal Observatory");
        Assert.Equal('O', paranal.Kind);
        Assert.Equal(1, paranal.Bortle);
        Assert.Equal(new EarthSite(-24.6272, -70.4042, 2.635), paranal.ObserverSite);
        Assert.Equal(paranal, catalogue.Nearest(-24.63, -70.40, .05));
        Assert.Equal(-4, paranal.OffsetHours(new(2024, 7, 1, 0, 0, 0, TimeSpan.Zero)));
        Assert.Equal(-3, paranal.OffsetHours(new(2024, 1, 1, 0, 0, 0, TimeSpan.Zero)));
    }

    [Fact]
    public void ArchivedUserSitesKeepStableNamesAndHandleTheDateLine()
    {
        var folder = Directory.CreateTempSubdirectory();
        try
        {
            string saved = Path.Combine(folder.FullName, "my-places.tsv");
            string missing = Path.Combine(folder.FullName, "archive.txt");
            File.WriteAllText(saved, "Home observatory\t-12\t179.98\t110\t11.5\n");
            var first = Assert.Single(EarthPlaceCatalogue.Load(missing, saved).Places);
            File.WriteAllText(saved, "Home observatory\t-12\t179.99\t-20\t11.5\n");
            var loaded = EarthPlaceCatalogue.Load(missing, saved);
            var second = Assert.Single(loaded.Places);
            Assert.Equal(first.Id, second.Id);
            Assert.Equal(-20, second.AltitudeMeters);
            Assert.Equal(0, second.ObserverSite.HeightKilometers);
            Assert.Equal(11.5, second.OffsetHours(DateTimeOffset.UnixEpoch));
            Assert.Equal(second, loaded.Nearest(-12, -179.99, .03));
            Assert.Null(loaded.Nearest(-12, -179.99, .001));
        }
        finally { folder.Delete(recursive: true); }
    }

    private sealed class OriginalPlacesFactAttribute : FactAttribute
    {
        public OriginalPlacesFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SPACETANGENT_EARTH_PLACES")))
                Skip = "Set the archived Earth site list.";
        }
    }
}
