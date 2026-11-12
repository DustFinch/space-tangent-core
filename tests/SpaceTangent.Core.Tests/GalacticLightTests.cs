// SPDX-License-Identifier: GPL-2.0-or-later
using System.Security.Cryptography;
using SpaceTangent.Contracts;
using SpaceTangent.Core.Observing;
using Xunit;

namespace SpaceTangent.Core.Tests;

public sealed class GalacticLightTests
{
    [Fact]
    public void TheSunSeesAnObscuredBandAndAnExternalObserverSeesTheBar()
    {
        var axes = GalacticLight.ToEquatorial;
        Assert.InRange(Math.Abs(Vector3d.Dot(axes.X, axes.Y)), 0, 1e-14);
        Assert.InRange((Vector3d.Cross(axes.X, axes.Y) - axes.Z).Length, 0, 1e-14);
        var sun = GalacticLight.SunKiloparsecs;
        Assert.Equal(sun, GalacticLight.Galactocentric(default));
        var center = GalacticLight.Galactocentric(axes.X * (8.178 * GalacticLight.KiloparsecLightYears));
        Assert.InRange(center.Length, .02, .021);
        double pole = Brightness(GalacticLight.Trace(sun, Direction(0, 90)));
        double outerBand = Brightness(GalacticLight.Trace(sun, Direction(180, 0)));
        double innerBand = Brightness(GalacticLight.Trace(sun, Direction(0, 0)));
        double window = Brightness(GalacticLight.Trace(sun, Direction(0, -4)));
        Assert.True(outerBand > pole * 4 && innerBand > outerBand && window > innerBand);

        var outside = new Vector3d(0, 0, 40);
        var down = new Vector3d(0, 0, -1);
        var rim = new Vector3d(12, 0, -40);
        Assert.Equal(default, GalacticLight.Trace(outside, down * -1));
        Assert.True(Brightness(GalacticLight.Trace(outside, down)) >
            3 * Brightness(GalacticLight.Trace(outside, rim / rim.Length)));

        var sunNeighbors = GalacticLight.Neighbours.Where(item => GalacticLight.IsVolumetric(item, sun)).ToArray();
        Assert.Equal(new[] { "LMC", "SMC" }, sunNeighbors.Select(item => item.Name));
        var cloud = sunNeighbors[0].CentreKiloparsecs - sun;
        Assert.InRange(Brightness(GalacticLight.Trace(sun, cloud / cloud.Length, sunNeighbors)) / outerBand, .5, 2);
        Assert.Throws<InvalidDataException>(() => GalacticDustGrid.Read(new byte[64]));
    }

    [DustArchiveFact]
    [Trait("Category", "ExternalData")]
    public void ReadsTheArchivedDustCubeAndReproducesItsUnequalColumns()
    {
        string file = Environment.GetEnvironmentVariable("SPACETANGENT_DUST_GRID")!;
        byte[] data = File.ReadAllBytes(file);
        Assert.Equal("408B93A109FD008658CA3DF22EFE8CF9FD2C1320F355D1B79E9B4663EE9A3066", Convert.ToHexString(SHA256.HashData(data)));
        var dust = GalacticDustGrid.Read(data);
        Assert.Equal((125, 125, 10), (dust.SizeX, dust.SizeY, dust.SizeZ));
        Assert.Equal(.08, dust.StepKiloparsecs, 12);
        Assert.Equal(new(-4.96, -4.96, -.36), dust.OriginKiloparsecs);
        Assert.Equal(0, dust.Weight(new(0, 0, .5)));
        var sun = GalacticLight.SunKiloparsecs;
        double Column(double longitude) => Enumerable.Range(0, 250)
            .Sum(index => dust.Density(Direction(longitude, 0) * (index * .02 + .01)) * .02);
        Assert.InRange(Column(80), 4, 6.5);
        Assert.InRange(Column(287), 1, 2.5);
        Assert.True(Column(80) > 2 * Column(287));
        var near = Direction(80, 0) * .5;
        var far = Direction(80, 0) * 2.5;
        Assert.Equal(GalacticLight.Sample(sun + near).Dust, GalacticLight.Sample(sun + near, dust).Dust);
        Assert.Equal(dust.Density(far), GalacticLight.Sample(sun + far, dust).Dust, 10);
        float[] pixels = GalacticLight.Bake(sun, 32, 16, map: dust);
        Assert.Equal(32 * 16 * 3, pixels.Length);
        Assert.All(pixels, value => Assert.True(float.IsFinite(value) && value >= 0));
        Assert.True(pixels.Skip(7 * 32 * 3).Take(2 * 32 * 3).Sum() >
            4 * (pixels.Take(32 * 3).Sum() + pixels.TakeLast(32 * 3).Sum()));
    }

    private static double Brightness(Vector3d light) => Vector3d.Dot(light, new(.2126, .7152, .0722));

    private static Vector3d Direction(double longitude, double latitude)
    {
        var (sinLongitude, cosLongitude) = Math.SinCos(longitude * Math.PI / 180);
        var (sinLatitude, cosLatitude) = Math.SinCos(latitude * Math.PI / 180);
        return new(cosLatitude * cosLongitude, cosLatitude * sinLongitude, sinLatitude);
    }

    private sealed class DustArchiveFactAttribute : FactAttribute
    {
        public DustArchiveFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SPACETANGENT_DUST_GRID")))
                Skip = "Set the path to the archived extinction grid.";
        }
    }
}
