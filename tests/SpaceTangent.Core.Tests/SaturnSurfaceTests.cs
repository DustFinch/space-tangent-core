// SPDX-License-Identifier: GPL-2.0-or-later
using System.Globalization;
using System.Security.Cryptography;
using SpaceTangent.Contracts;
using SpaceTangent.Core.Addons;
using SpaceTangent.Core.Spice;
using Xunit;

namespace SpaceTangent.Core.Tests;

public sealed class SaturnSurfaceTests
{
    [SurfaceFact]
    public void CassiniRaysMatchCspiceHitsAndMissesOnTheSaturnEllipsoid()
    {
        string centers = Environment.GetEnvironmentVariable("SPACETANGENT_SAT480")!;
        string pck = Environment.GetEnvironmentVariable("SPACETANGENT_SATURN_PCK")!;
        string root = Environment.GetEnvironmentVariable("SPACETANGENT_CASSINI_JSON_ROOT")!;
        Assert.Equal("3DFF7B1DBECEAA01F25467767D3FA25816051C85D162D1EDF04ACB310EE28BB1",
            Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(pck))));
        var surface = new SaturnSurface(centers, pck);
        SpiceCatalogResult catalog = SpiceJsonCatalog.Load(Path.Combine(root, "cassini.json"), root);
        Assert.Equal(new Vector3d(60268, 60268, 54364), surface.RadiiKilometers);
        string reference = Path.Combine(AppContext.BaseDirectory, "Fixtures", "saturn-intercepts-reference.csv");
        int hits = 0, misses = 0;
        foreach (string line in File.ReadLines(reference).Where(line => !line.StartsWith('#')))
        {
            double[] row = line.Split(',').Select(value => double.Parse(value, CultureInfo.InvariantCulture)).ToArray();
            JulianInstant epoch = JulianInstant.FromTdbSeconds(row[0]);
            SpiceSensor sensor = Assert.Single(catalog.Sensors, sensor => sensor.InstrumentId == row[1]);
            Assert.True(sensor.Spacecraft.TrySample(epoch, out var state));
            Assert.True(sensor.Spacecraft.TryAttitude(epoch, out var attitude));
            Assert.True(surface.TrySample(epoch, out var pose));

            Vector3d origin = state.PositionKilometers;
            Assert.InRange((origin - new Vector3d(row[3], row[4], row[5])).Length, 0, 1e-6);
            Vector3d cspice = new(row[6], row[7], row[8]);
            cspice /= cspice.Length;
            Vector3d ray = row[2] == 0 ? attitude.Transform(sensor.BoresightInBody)
                : sensor.CornersInBody.Select(attitude.Transform).MinBy(candidate => (candidate - cspice).Length);
            Assert.InRange((ray - cspice).Length, 0, 3e-8);
            bool found = pose.TryIntercept(origin, ray, sensor.RangeKilometers, out var hit);
            Assert.Equal(row[9] == 1, found);
            if (!found)
            {
                misses++;
                continue;
            }
            hits++;
            Vector3d targetFixed = pose.InertialToFixed.Transform(hit - pose.CenterKilometers);
            Assert.InRange((targetFixed - new Vector3d(row[10], row[11], row[12])).Length, 0, .005);
            Assert.False(pose.TryIntercept(origin, ray, (hit - origin).Length - 1, out _));
        }
        Assert.Equal(10, hits);
        Assert.Equal(40, misses);
    }

    private sealed class SurfaceFactAttribute : FactAttribute
    {
        public SurfaceFactAttribute()
        {
            foreach (string variable in new[] { "SPACETANGENT_SAT480", "SPACETANGENT_SATURN_PCK", "SPACETANGENT_CASSINI_JSON_ROOT" })
            {
                if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(variable)))
                {
                    Skip = "Set " + variable + " to the original Cassini or Saturn input.";
                    break;
                }
            }
        }
    }
}
