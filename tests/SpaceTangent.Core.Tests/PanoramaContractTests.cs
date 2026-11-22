// SPDX-License-Identifier: GPL-2.0-or-later
using SpaceTangent.Core.Observing;
using Xunit;

namespace SpaceTangent.Core.Tests;

public sealed class PanoramaContractTests
{
    private static ObservingLandscape Landscape(string fields) => ObservingLandscape.Read("test", new StringReader("[landscape]\n" + fields));
    private static PanoramaPixels Solid(byte r, byte g, byte b, byte a = 255) => new(1, 1, [r, g, b, a]);
    private static PanoramaPixels? Bake(ObservingLandscape landscape, LandscapeLayer layer = LandscapeLayer.Ground,
        Func<string, PanoramaPixels?>? image = null, Func<string, string?>? text = null, int width = 16, int height = 8) =>
        PanoramaBaker.Bake(landscape, layer, image ?? (_ => null), text ?? (_ => null), width, height);

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 0)]
    [InlineData(-1, 10)]
    [InlineData(4097, 4096)]
    public void OutputAllocationIsBoundedBeforeReadingAnyAssets(int width, int height)
    {
        var landscape = Landscape("type=fisheye\nmaptex=eye.png");
        Assert.Throws<ArgumentOutOfRangeException>(() => Bake(landscape, width: width, height: height,
            image: _ => throw new InvalidOperationException("No asset may be read before validating dimensions.")));
    }

    [Fact]
    public void AbsentOptionalLayersDoNotProduceOpaqueOrInventedTextures()
    {
        var eye = Landscape("type=fisheye\nmaptex=eye.png");
        Assert.Null(Bake(eye));
        Assert.Null(Bake(eye, LandscapeLayer.Lights));
        var polygon = Landscape("type=polygonal\npolygonal_horizon_list=horizon.txt");
        Assert.Null(Bake(polygon));
        Assert.Null(Bake(polygon, LandscapeLayer.Fog));
        var panels = Landscape("type=old_style\nnbside=1\nnbsidetex=1\ntex0=side.png");
        Assert.Null(Bake(panels, LandscapeLayer.Fog));
        Assert.Null(Bake(panels, LandscapeLayer.Lights));
        Assert.Throws<InvalidDataException>(() => Bake(panels));
        Assert.Throws<ArgumentException>(() => Bake(Landscape("type=spherical\nmaptex=sky.png")));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(361)]
    public void FisheyeRejectsImpossibleAngularCoverage(int field)
    {
        var eye = Landscape($"type=fisheye\nmaptex=eye.png\ntexturefov={field}");
        Assert.Throws<InvalidDataException>(() => Bake(eye, image: _ => Solid(255, 0, 0)));
    }

    [Theory]
    [InlineData("not-a-texture")]
    [InlineData("tex1")]
    [InlineData("tex0:0:0:1:1:2")]
    public void PanelReferencesCannotEscapeTheDeclaredTextureSet(string panel)
    {
        var landscape = Landscape($"type=old_style\nnbside=1\nnbsidetex=1\ntex0=side.png\nside0={panel}");
        Assert.Throws<InvalidDataException>(() => Bake(landscape, image: _ => Solid(0, 0, 255)));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void GroundOrderingAndFogUseAuthoredAlphaAndTangentProjection(bool groundFirst, bool calibrated)
    {
        var landscape = Landscape($"""
            type=old_style
            nbside=1
            nbsidetex=1
            tex0=side.png
            light0=light.png
            groundtex=ground.png
            fogtex=fog.png
            decor_alt_angle=150
            decor_angle_shift=-75
            ground_angle_shift=-45
            fog_alt_angle=45
            fog_angle_shift=-20
            tan_mode=true
            draw_ground_first={groundFirst.ToString().ToLowerInvariant()}
            calibrated={calibrated.ToString().ToLowerInvariant()}
            """);
        PanoramaPixels Image(string name) => name switch
        { "side.png" => Solid(255, 0, 0, 128), "ground.png" => Solid(0, 0, 255), _ => Solid(0, 255, 0, 128) };
        var ground = Bake(landscape, image: Image, width: 8, height: 4)!;
        Assert.Equal(8 * 4 * 4, ground.Rgba.Length);
        // The southern output row sees the ground with the authored compositing order.
        int index = 4 * (3 * ground.Width);
        Assert.Equal(255, ground.Rgba[index + 3]);
        Assert.True(ground.Rgba[index + 2] > 0);
        var fog = Bake(landscape, LandscapeLayer.Fog, Image)!;
        Assert.Contains((byte)128, fog.Rgba);
        Assert.Contains((byte)0, fog.Rgba);
        var lights = Bake(landscape, LandscapeLayer.Lights, Image)!;
        Assert.All(lights.Rgba.Where((_, i) => i % 4 == 0), r => Assert.Equal(0, r));
    }

    [Theory]
    [InlineData("azDeg_altDeg", "90 30", 90, 30)]
    [InlineData("azDeg_zdDeg", "90 60", 90, 30)]
    [InlineData("azRad_altRad", "1.5707963267948966 0.5235987755982988", 90, 30)]
    [InlineData("azRad_zdRad", "1.5707963267948966 1.0471975511965976", 90, 30)]
    [InlineData("azGrad_altGrad", "100 50", 90, 45)]
    [InlineData("azGrad_zdGrad", "100 50", 90, 45)]
    public void HorizonUnitsAndZenithDistanceHaveIndependentAngularExpectations(string mode, string text, double azimuth, double altitude)
    {
        var point = Assert.Single(PanoramaBaker.ReadHorizon("# comment\n; comment\n\n" + text, 20, mode));
        Assert.Equal(azimuth + 20, point.Azimuth, 10);
        Assert.Equal(altitude, point.Altitude, 10);
        Assert.Equal(altitude, PanoramaBaker.HorizonAltitude([point], -500), 10);
    }

    [Theory]
    [InlineData("", "azDeg_altDeg")]
    [InlineData("90", "azDeg_altDeg")]
    [InlineData("90 91", "azDeg_altDeg")]
    [InlineData("90 -91", "azDeg_altDeg")]
    [InlineData("90 0", "unknown")]
    public void InvalidHorizonCoordinatesDoNotCreateAPanorama(string text, string mode) =>
        Assert.Throws<InvalidDataException>(() => PanoramaBaker.ReadHorizon(text, 0, mode));

    [Fact]
    public void PolygonLineColorAndTransparencyHaveExactOutputPixels()
    {
        var landscape = Landscape("type=polygonal\npolygonal_horizon_list=h.txt\nground_color=0.25,0.5,0.75\nhorizon_line_color=1,0,0");
        var line = Bake(landscape, text: _ => "0 0", width: 8, height: 1)!;
        Assert.Equal(Enumerable.Repeat(new byte[] { 255, 0, 0, 255 }, 8).SelectMany(x => x), line.Rgba);
        var ground = Bake(landscape, text: _ => "0 0", width: 1, height: 2)!;
        Assert.Equal(new byte[] { 0, 0, 0, 0, 64, 128, 191, 255 }, ground.Rgba);
        Assert.Throws<InvalidDataException>(() => Bake(Landscape("type=polygonal\npolygonal_horizon_list=h.txt\nground_color=2,0,0"), text: _ => "0 0"));
    }

    [Fact]
    public void CancellationStopsTheBoundedBake()
    {
        var landscape = Landscape("type=fisheye\nmaptex=eye.png");
        Assert.ThrowsAny<OperationCanceledException>(() => PanoramaBaker.Bake(landscape, LandscapeLayer.Ground,
            _ => Solid(255, 255, 255), _ => null, 16, 8, new(true)));
    }
}
