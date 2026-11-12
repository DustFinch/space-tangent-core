// SPDX-License-Identifier: GPL-2.0-or-later
using SpaceTangent.Core.Observing;
using Xunit;

namespace SpaceTangent.Core.Tests;

public sealed class ObservingLandscapeTests
{
    [LandscapeCorpusFact]
    [Trait("Category", "ExternalData")]
    public void TheOriginalSixteenDefinitionsAndGenevaHorizonRetainTheirCoordinates()
    {
        string root = Environment.GetEnvironmentVariable("SPACETANGENT_LANDSCAPES")!;
        var originals = Directory.EnumerateDirectories(root).Select(path =>
        {
            using var reader = File.OpenText(Path.Combine(path, "landscape.ini"));
            return ObservingLandscape.Read(Path.GetFileName(path), reader);
        }).ToDictionary(landscape => landscape.Id, StringComparer.Ordinal);
        Assert.Equal(16, originals.Count);
        Assert.Equal(4, originals.Values.Select(item => item.Projection).Distinct().Count());
        Assert.Equal("Moon", originals["moon"].Body);
        var grossmugl = originals["grossmugl"].Bounds(LandscapeLayer.Ground);
        Assert.Equal(new PanoramaBounds(11.25, -90, -89.1), grossmugl);
        Assert.InRange(2048 * grossmugl.Column(113.66), 639, 643);
        Assert.Equal(0, grossmugl.Row(11.25));
        Assert.Equal(1, grossmugl.Row(-90));
        var geneva = originals["geneva"];
        string source = File.ReadAllText(Path.Combine(root, "geneva", "horizon_Geneve.txt"));
        var horizon = PanoramaBaker.ReadHorizon(source, 0, "azDeg_altDeg");
        Assert.Equal(3.5, PanoramaBaker.HorizonAltitude(horizon, 85), 8);
        Assert.Equal(.5, PanoramaBaker.HorizonAltitude(horizon, 40), 8);
        Assert.Equal(1.5, PanoramaBaker.HorizonAltitude(horizon, 2.5), 8);
        var pixels = Assert.IsType<PanoramaPixels>(PanoramaBaker.Bake(geneva, LandscapeLayer.Ground, _ => null,
            _ => source, 1440, 720));
        Assert.Equal((38, 115, 115, 255), PixelAt(pixels, 85, 2.5));
        Assert.Equal((0, 0, 0, 0), PixelAt(pixels, 85, 4.5));
        Assert.Equal((0, 0, 0, 0), PixelAt(pixels, 40, 1.5));
    }

    [Fact]
    public void WrappedPanelsAndFisheyePreserveTheirOrientationAndTransparency()
    {
        using var ini = new StringReader("""
            [landscape]
            type=old_style
            nbsidetex=2
            nbside=2
            tex0=red.png
            tex1=blue.png
            side0=tex0:0:0:1:1
            side1=tex1:0:0:1:1
            decor_alt_angle=30
            decor_angle_shift=-10
            decor_angle_rotatez=20
            calibrated=true
            """);
        var panels = ObservingLandscape.Read("panels", ini);
        var panorama = PanoramaBaker.Bake(panels, LandscapeLayer.Ground,
            file => new(1, 1, file == "red.png" ? [255, 0, 0, 255] : [0, 0, 255, 255]), _ => null, 720, 360)!;
        Assert.Equal((255, 0, 0, 255), PixelAt(panorama, 120, 5));
        Assert.Equal((0, 0, 255, 255), PixelAt(panorama, 300, 5));
        Assert.Equal((0, 0, 0, 0), PixelAt(panorama, 120, 25));
        Assert.Null(PanoramaBaker.Bake(panels, LandscapeLayer.Lights, _ => null, _ => null, 64, 32));

        using var fisheyeIni = new StringReader("[landscape]\ntype=fisheye\nmaptex=eye.png\ntexturefov=180\n");
        var fisheye = ObservingLandscape.Read("eye", fisheyeIni);
        var source = new PanoramaPixels(2, 2, [255, 0, 0, 255, 0, 255, 0, 255, 0, 0, 255, 255, 255, 255, 255, 255]);
        var baked = PanoramaBaker.Bake(fisheye, LandscapeLayer.Ground, _ => source, _ => null, 720, 360)!;
        var north = PixelAt(baked, 0, 15);
        var south = PixelAt(baked, 180, 15);
        Assert.Equal(255, north.B);
        Assert.Equal(0, south.B);
        Assert.False(ObservingLandscape.ValidId("../archive"));
    }

    private static (int R, int G, int B, int A) PixelAt(PanoramaPixels picture, double azimuth, double altitude)
    {
        var bounds = PanoramaBounds.WholeSky;
        int x = (int)(bounds.Column(azimuth) * picture.Width);
        int y = (int)(bounds.Row(altitude) * picture.Height);
        int at = 4 * (y * picture.Width + x);
        return (picture.Rgba[at], picture.Rgba[at + 1], picture.Rgba[at + 2], picture.Rgba[at + 3]);
    }

    private sealed class LandscapeCorpusFactAttribute : FactAttribute
    {
        public LandscapeCorpusFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SPACETANGENT_LANDSCAPES")))
                Skip = "Set the path to the archived landscape folders.";
        }
    }
}
