// SPDX-License-Identifier: GPL-2.0-or-later
using System.Globalization;
using System.Security.Cryptography;
using SpaceTangent.Contracts;
using SpaceTangent.Core.Spice;
using SpaceTangent.Core.Surfaces;
using Xunit;

namespace SpaceTangent.Core.Tests;

public sealed class MoonTerrainTests
{
    [MoonDataFact("SPACETANGENT_SATURN_PCK")]
    public void MoonRotationAndAngularVelocityMatchCspice()
    {
        var model = new SpicePolynomialBodyFrame(Environment.GetEnvironmentVariable("SPACETANGENT_SATURN_PCK")!, 301);
        Assert.Equal(new Vector3d(1737.4, 1737.4, 1737.4), model.RadiiKilometers);
        string reference = Path.Combine(AppContext.BaseDirectory, "Fixtures", "moon-frame-reference.csv");
        foreach (string line in File.ReadLines(reference).Where(line => !line.StartsWith('#')))
        {
            double[] row = line.Split(',').Select(value => double.Parse(value, CultureInfo.InvariantCulture)).ToArray();
            var frame = model.Sample(JulianInstant.FromTdbSeconds(row[0]));
            Vector3d spin = Assert.IsType<Vector3d>(frame.AngularVelocityRadiansPerSecond);
            Vector3d[] axes = [frame.ToWorld.X, frame.ToWorld.Y, frame.ToWorld.Z];
            for (int i = 0; i < 3; i++)
            {
                Assert.InRange((axes[i] - ReadColumn(1, i)).Length, 0, 2e-11);
                Assert.InRange((Vector3d.Cross(spin, axes[i]) - ReadColumn(10, i)).Length, 0, 2e-16);
            }

            Vector3d ReadColumn(int start, int i) => new(row[start + i], row[start + i + 3], row[start + i + 6]);
        }
    }

    [MoonDataFact("SPACETANGENT_MOON_DEM")]
    public void LoadsRealLolaPackAndMatchesIndependentSignedElevations()
    {
        string file = Environment.GetEnvironmentVariable("SPACETANGENT_MOON_DEM")!;
        using var stream = File.OpenRead(file);
        Assert.Equal("05F006DB0116E97E022C3608C26C14FE4891F5EAD16C7922AF1D4A3053BE750C",
            Convert.ToHexString(SHA256.HashData(stream)));
        ElevationMap map = ElevationMap.Load(file);
        Assert.Equal(4096, map.Width);
        Assert.Equal(2048, map.Height);
        Assert.Equal(1737.4, map.ReferenceRadiusKilometers);
        Assert.Equal(-3295.941757728392, map.SampleMeters(-20.08, 9.62), 8);
        Assert.Equal(-1924.264655111028, map.SampleMeters(-11.36, -43.31), 8);
        Assert.Equal(-1912.5191727130969, map.SampleMeters(23.473, .674), 8);
        Assert.Equal(2678.625, map.SampleMeters(-180, 0), 9);
        Assert.Equal(2678.625, map.SampleMeters(180, 0), 9);
        Assert.Equal(-9.833333333333485, map.SampleMeters(105, 90), 9);
        Assert.Equal(952.4666666666567, map.SampleMeters(-42, -90), 9);
        Assert.Throws<ArgumentOutOfRangeException>(() => map.SampleMeters(0, 91));
    }

    [Fact]
    public void ReadsSignedHeightsWithScaleAndRejectsTruncation()
    {
        string path = Path.GetTempFileName();
        try
        {
            using (var file = new BinaryWriter(File.Create(path)))
            {
                file.Write("CELWTRN\0"u8);
                file.Write((ushort)1);
                file.Write((ushort)0);
                file.Write(2u);
                file.Write(2u);
                file.Write(.5);
                file.Write(100d);
                file.Write(1737.4);
                foreach (short sample in new short[] { -20, 20, -60, 60 })
                    file.Write(sample);
            }
            var map = ElevationMap.Load(path);
            var heights = new float[4];
            map.CopyHeightsMeters(heights);
            Assert.Equal(new[] { 90f, 110f, 70f, 130f }, heights);
            Assert.Equal(90, map.SampleMeters(-90, 45));
            Assert.Equal(130, map.SampleMeters(90, -45));
            Assert.Equal(100, map.SampleMeters(180, 0));
            Assert.Throws<OperationCanceledException>(() => ElevationMap.Load(path, new CancellationToken(true)));
            using (var file = File.OpenWrite(path))
                file.SetLength(file.Length - 1);
            Assert.Throws<InvalidDataException>(() => ElevationMap.Load(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    private sealed class MoonDataFactAttribute : FactAttribute
    {
        public MoonDataFactAttribute(string variable)
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(variable)))
                Skip = "Set " + variable + " to the original lunar data.";
        }
    }
}
