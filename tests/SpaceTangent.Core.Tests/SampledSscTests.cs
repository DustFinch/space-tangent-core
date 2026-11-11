// SPDX-License-Identifier: GPL-2.0-or-later
using System.Globalization;
using System.Security.Cryptography;
using SpaceTangent.Contracts;
using SpaceTangent.Core.Addons;
using SpaceTangent.Core.Ephemerides;
using SpaceTangent.Core.World;
using Xunit;

namespace SpaceTangent.Core.Tests;

public sealed class SampledSscTests
{
    [TimelineFact]
    public void OriginalCassiniSwitchesFifteenPhasesAndAgreesWithIndependentStates()
    {
        string root = Environment.GetEnvironmentVariable("SPACETANGENT_SSC_WORLD_ROOT")!;
        string catalogue = Path.Combine(root, "ssc", "cassini.ssc");
        Assert.Equal("AFC0EDD743CE73C6032F7A2E034D0F61EB5B07104FE31C7BE0E2DD2A01C3258D",
            Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(catalogue))));
        SscSampledSpacecraft cassini = SscSampledCatalog.Load(catalogue, root, "Sol/Saturn/1997-061A");
        Assert.Equal("Cassini", cassini.Name);
        Assert.Equal(15, cassini.PhaseCount);
        Assert.EndsWith("cassini.3ds", cassini.MeshPath);
        Assert.False(cassini.TrySampleRelative(cassini.Start.AddNanoseconds(-1), out _, out _));
        Assert.True(cassini.TrySampleRelative(cassini.Start, out _, out _));
        Assert.True(cassini.TrySampleRelative(cassini.End.AddNanoseconds(-1), out _, out _));
        Assert.False(cassini.TrySampleRelative(cassini.End, out _, out _));

        var builder = new WorldGraphBuilder();
        builder.AddSolarSystem(De440Ephemeris.Load(Environment.GetEnvironmentVariable("SPACETANGENT_DE440")!));
        builder.AddSampledSpacecraft(cassini);
        WorldGraph graph = builder.Build();

        string reference = Path.Combine(AppContext.BaseDirectory, "Fixtures", "cassini-timeline-reference.csv");
        int rows = 0;
        foreach (string line in File.ReadLines(reference).Where(line => !line.StartsWith('#')))
        {
            double[] data = line.Split(',').Select(value => double.Parse(value, CultureInfo.InvariantCulture)).ToArray();
            JulianInstant epoch = rows++ == 0 ? cassini.Start : JulianInstant.FromJulianDate(data[0], TimeScale.Tdb);
            Assert.True(cassini.TrySampleRelative(epoch, out ObjectId center, out var relative));
            Assert.Equal(data[1] == 10 ? SolarSystemSample.SunId : SolarSystemSample.SaturnBarycenterId, center);
            Compare(relative, data.AsSpan(2));
            WorldSnapshot snapshot = graph.Evaluate(epoch);
            Assert.True(snapshot.TryStateCenter(cassini.Id, out ObjectId snapshotCenter));
            Assert.Equal(center, snapshotCenter);
            Assert.True(snapshot.TryState(cassini.Id, out var absolute));
            Compare(absolute, data.AsSpan(8));
            Assert.True(snapshot.TryOrientation(cassini.Id, out var attitude));
            Vector3d towardEarth = snapshot.SolarSystem!.Value.Earth.PositionKilometers - absolute.PositionKilometers;
            Assert.InRange((attitude.ToWorld.Z - towardEarth / towardEarth.Length).Length, 0, 1e-12);
        }
        Assert.Equal(45, rows);

        var firstJoin = JulianInstant.FromJulianDate(2453167.5, TimeScale.Tdb);
        Assert.True(cassini.TrySampleRelative(firstJoin.AddNanoseconds(-1), out var oldCenter, out _));
        Assert.True(cassini.TrySampleRelative(firstJoin, out var newCenter, out _));
        Assert.Equal(SolarSystemSample.SunId, oldCenter);
        Assert.Equal(SolarSystemSample.SaturnBarycenterId, newCenter);
        WorldSnapshot after = graph.Evaluate(cassini.End);
        Assert.False(after.TryState(cassini.Id, out _));
        Assert.False(after.TryStateCenter(cassini.Id, out _));
        Assert.False(after.TryOrientation(cassini.Id, out _));
    }

    private static void Compare(CartesianState actual, ReadOnlySpan<double> data)
    {
        Assert.InRange((actual.PositionKilometers - new Vector3d(data[0], data[1], data[2])).Length, 0, 2e-4);
        Assert.InRange((actual.VelocityKilometersPerSecond - new Vector3d(data[3], data[4], data[5])).Length, 0, 2e-7);
    }

    private sealed class TimelineFactAttribute : FactAttribute
    {
        public TimelineFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SPACETANGENT_SSC_WORLD_ROOT")) ||
                string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SPACETANGENT_DE440")))
                Skip = "Set SPACETANGENT_SSC_WORLD_ROOT and SPACETANGENT_DE440 to the original inputs.";
        }
    }
}
