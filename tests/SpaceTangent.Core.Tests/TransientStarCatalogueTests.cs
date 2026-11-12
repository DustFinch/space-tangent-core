// SPDX-License-Identifier: GPL-2.0-or-later
using SpaceTangent.Core.Observing;
using Xunit;

namespace SpaceTangent.Core.Tests;

public sealed class TransientStarCatalogueTests
{
    [OriginalTransientsFact]
    [Trait("Category", "ExternalData")]
    public void OriginalCurvesKeepTheCrabSupernovaAndNovaCygniPeaks()
    {
        var warnings = new List<string>();
        var catalogue = TransientStarCatalogue.Load(Environment.GetEnvironmentVariable("SPACETANGENT_TRANSIENT_ROOT")!, warnings.Add);
        Assert.Empty(warnings);
        Assert.Equal(50, catalogue.Stars.Count(star => star.Supernova));
        Assert.Equal(69, catalogue.Stars.Count(star => !star.Supernova));

        TransientStar supernova = Assert.IsType<TransientStar>(catalogue.Find(new("transient/sn-1054a")));
        Assert.Equal(2106215, supernova.PeakJulianDay);
        Assert.Equal(99, supernova.MagnitudeAt(2105215));
        Assert.Equal(-6, supernova.MagnitudeAt(2106215));
        Assert.Equal(-4.5, supernova.MagnitudeAt(2106245), 8);
        Assert.Equal(-3.85, supernova.MagnitudeAt(2106295), 8);
        Assert.Equal(-2.35, supernova.MagnitudeAt(2106315), 8);
        Assert.InRange(Math.Abs(Math.Atan2(supernova.Direction.Y, supernova.Direction.X) * 180 / Math.PI - 83.625), 0, 1e-12);

        TransientStar nova = Assert.IsType<TransientStar>(catalogue.Find(new("transient/v1500-cyg")));
        Assert.Equal("Nova Cygni 1975", nova.Name);
        Assert.Equal(21, nova.MagnitudeAt(2442651));
        Assert.Equal(1.69, nova.MagnitudeAt(2442655), 8);
        Assert.Equal(3.69, nova.MagnitudeAt(2442657), 8);
        Assert.Equal(4.69, nova.MagnitudeAt(2442659), 8);
        Assert.Equal(7.69, nova.MagnitudeAt(2442687), 8);
        Assert.Equal(10.69, nova.MagnitudeAt(2442918), 8);
    }

    private sealed class OriginalTransientsFactAttribute : FactAttribute
    {
        public OriginalTransientsFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SPACETANGENT_TRANSIENT_ROOT")))
                Skip = "Set SPACETANGENT_TRANSIENT_ROOT to the original two historical catalogues.";
        }
    }
}
