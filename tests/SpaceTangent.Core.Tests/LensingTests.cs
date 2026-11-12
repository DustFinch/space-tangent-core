// SPDX-License-Identifier: GPL-2.0-or-later
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using SpaceTangent.Contracts;
using SpaceTangent.Core.Observing;
using Xunit;

namespace SpaceTangent.Core.Tests;

public sealed class LensingTests
{
    [Fact]
    public void HandlesWeakBendingCriticalCaptureAndNonspinningLimit()
    {
        Assert.Equal(6, KerrLens.Isco(0), 12);
        Assert.Equal(2, KerrLens.Horizon(0), 12);
        Assert.Equal(3, KerrLens.PhotonOrbit(0, true), 12);
        Assert.Equal(2e-5, SchwarzschildLens.Deflection(1e5), 8);
        Assert.True(double.IsNaN(SchwarzschildLens.SweptAngle(.1, .01)));
        Assert.True(SchwarzschildLens.Deflection(2.61) > SchwarzschildLens.Deflection(3));
    }

    [LensFact]
    [Trait("Category", "ExternalData")]
    public void ArchivedDeflectionDiskTracesAndColorTablesMatch()
    {
        string path = Environment.GetEnvironmentVariable("SPACETANGENT_LENS_ORACLE")!;
        using var file = JsonDocument.Parse(File.ReadAllBytes(path));
        var expected = file.RootElement;
        Assert.Equal(expected.GetProperty("deflection").GetString(), Hash(SchwarzschildLens.DeflectionTable()));
        Assert.Equal(expected.GetProperty("spectrum").GetString(), Hash(BlackbodyColor.Table()));
        foreach (var row in expected.GetProperty("angles").EnumerateArray())
            Near(row.GetProperty("angle").GetDouble(), SchwarzschildLens.Deflection(row.GetProperty("b").GetDouble()), 1e-12);

        var traces = expected.GetProperty("traces");
        Assert.Equal(28, traces.GetArrayLength());
        int captured = 0, intersections = 0;
        foreach (var row in traces.EnumerateArray())
        {
            KerrRay result = KerrLens.Trace(row.GetProperty("spin").GetDouble(), Read(row.GetProperty("eye")), Read(row.GetProperty("ray")), 40);
            Assert.Equal(row.GetProperty("captured").GetBoolean(), result.Captured);
            if (result.Captured)
                captured++;
            Assert.InRange((Read(row.GetProperty("direction")) - result.Direction).Length, 0, 1e-9);
            var disk = row.GetProperty("disk");
            Assert.Equal(disk.GetArrayLength(), result.Disk.Count);
            for (int i = 0; i < disk.GetArrayLength(); i++)
            {
                var hit = disk[i];
                KerrDiskHit actual = result.Disk[i];
                Assert.Equal(hit.GetProperty("Crossing").GetInt32(), actual.Crossing);
                Near(hit.GetProperty("Radius").GetDouble(), actual.Radius, 1e-9);
                Near(hit.GetProperty("Redshift").GetDouble(), actual.Redshift, 1e-9);
                Near(hit.GetProperty("Azimuth").GetDouble(), actual.Azimuth, 1e-9);
                intersections++;
            }
        }
        Assert.True(captured > 0 && intersections > 0);
        foreach (var row in expected.GetProperty("colors").EnumerateArray())
        {
            double temperature = row.GetProperty("t").GetDouble();
            var (r, g, b) = BlackbodyColor.LinearRgb(temperature);
            Assert.InRange((Read(row.GetProperty("rgb")) - new Vector3d(r, g, b)).Length, 0, 1e-12);
            Near(row.GetProperty("y").GetDouble(), BlackbodyColor.Tristimulus(temperature).Y, 1e-12);
        }
    }

    [LensFact]
    [Trait("Category", "ExternalData")]
    public void BuiltInCatalogueRetainsTheOriginalElevenMeasuredObjects()
    {
        string path = Environment.GetEnvironmentVariable("SPACETANGENT_LENS_ORACLE")!;
        using var text = File.OpenText(Path.Combine(Path.GetDirectoryName(path)!, "p3-original-blackholes.txt"));
        var archived = BlackHoleCatalogue.Read(text);
        var current = BlackHoleCatalogue.Default;
        Assert.Equal(11, archived.Entries.Count);
        Assert.Equal(archived.Entries.Count, current.Entries.Count);
        foreach (BlackHole source in archived.Entries)
        {
            BlackHole target = Assert.IsType<BlackHole>(current.Find(source.Id));
            Assert.Equal(source with { Aliases = target.Aliases }, target);
            Assert.Equal(source.Aliases, target.Aliases);
        }
        var cygnus = Assert.IsType<BlackHole>(current.Find(new("black-hole/cyg-x1")));
        Assert.Contains("Cygnus X-1", cygnus.Aliases);
        Assert.Equal(21.2, cygnus.MassSolar);
        Assert.Equal(.998, cygnus.Spin);
        Assert.InRange(cygnus.SchwarzschildRadiusKilometers, 62.60, 62.62);
        Vector3d disk = cygnus.DiskAxis()!.Value;
        Assert.InRange(Math.Abs(disk.Length - 1), 0, 1e-12);
        Assert.InRange(Math.Abs(Vector3d.Dot(disk, cygnus.Direction) + Math.Cos(27.51 * Math.PI / 180)), 0, 1e-12);
        Assert.Equal("M 87", current.Find(new("black-hole/m87"))?.Host);
    }


    private static string Hash(float[] samples) => Convert.ToHexString(SHA256.HashData(MemoryMarshal.AsBytes(samples.AsSpan())));
    private static Vector3d Read(JsonElement xyz) => new(xyz[0].GetDouble(), xyz[1].GetDouble(), xyz[2].GetDouble());
    private static void Near(double expected, double actual, double tolerance) =>
        Assert.InRange(Math.Abs(expected - actual), 0, tolerance * Math.Max(1, Math.Abs(expected)));

    private sealed class LensFactAttribute : FactAttribute
    {
        public LensFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SPACETANGENT_LENS_ORACLE")))
                Skip = "Set the archived gravitational lens reference.";
        }
    }
}
