// SPDX-License-Identifier: GPL-2.0-or-later
using System.Text.Json;
using SpaceTangent.Contracts;
using SpaceTangent.Core.World;
using Xunit;

namespace SpaceTangent.Core.Tests;

public sealed class DemoWorldTests
{
    [ArchiveFact]
    [Trait("Category", "ExternalData")]
    public void TheAuthoredSystemsMatchArchivedStatesAndOrientations()
    {
        var graph = Graph();
        string path = Environment.GetEnvironmentVariable("SPACETANGENT_DEMO_WORLD_ORACLE")!;
        using var reference = JsonDocument.Parse(File.ReadAllText(path));
        var ids = DemoWorlds.Bodies.ToDictionary(body => body.Name, body => body.Id, StringComparer.Ordinal);
        Assert.Equal(45, reference.RootElement.GetArrayLength());
        foreach (var sample in reference.RootElement.EnumerateArray())
        {
            string name = sample.GetProperty("name").GetString()!;
            ObjectId id = ids[name.Split('/')[^1]];
            var epoch = JulianInstant.FromJulianDate(sample.GetProperty("jd").GetDouble(), TimeScale.Tdb);
            var snapshot = graph.Evaluate(epoch);
            Assert.True(snapshot.TryState(id, out var state));
            Assert.True(snapshot.TryState(name.StartsWith("Tessera", StringComparison.Ordinal) ? DemoWorlds.TesseraA : DemoWorlds.Lumen, out var parent));
            Assert.True(snapshot.TryOrientation(id, out var orientation));
            var relative = state - parent;
            Assert.InRange((relative.PositionKilometers - Vector(sample.GetProperty("relative"))).Length, 0, .8);
            Assert.InRange((relative.VelocityKilometersPerSecond - Vector(sample.GetProperty("speed"))).Length, 0, 1e-5);
            Assert.InRange((state.PositionKilometers - Vector(sample.GetProperty("position"))).Length, 0, .8);
            var axes = sample.GetProperty("axes");
            Assert.InRange((orientation.ToWorld.X - Vector(axes[0])).Length, 0, 2e-6);
            Assert.InRange((orientation.ToWorld.Y - Vector(axes[1])).Length, 0, 2e-6);
            Assert.InRange((orientation.ToWorld.Z - Vector(axes[2])).Length, 0, 2e-6);
        }

        static Vector3d Vector(JsonElement array) => new(array[0].GetDouble(), array[1].GetDouble(), array[2].GetDouble());
    }

    [Fact]
    public void NovaReceivesTwoDistinctLightsAndTheBinaryEclipseChangesTheirFlux()
    {
        var graph = Graph();
        var epoch = JulianInstant.FromJulianDate(2460410.263, TimeScale.Tdb);
        var normal = DemoWorlds.Lights(graph.Evaluate(epoch), DemoWorlds.Nova);
        Assert.Equal(2, normal.Count);
        Assert.Equal(DemoWorlds.TesseraA, normal[0].Star);
        Assert.Equal(DemoWorlds.TesseraB, normal[1].Star);
        Assert.InRange(normal[0].RelativeFlux, .62, .8);
        Assert.InRange(normal[1].RelativeFlux, .2, .38);
        Assert.InRange(normal.Sum(light => light.RelativeFlux), .95, 1.001);
        Assert.True((normal[0].Direction - normal[1].Direction).Length > .03);
        double minimum = 1;
        for (int step = 0; step <= 300; step++)
        {
            var snapshot = graph.Evaluate(epoch.AddSeconds((22 + step * .01) * JulianInstant.SecondsPerDay));
            minimum = Math.Min(minimum, DemoWorlds.Lights(snapshot, DemoWorlds.Nova).Sum(light => light.RelativeFlux));
        }
        Assert.InRange(minimum, .4, .85);
        var giant = Assert.Single(DemoWorlds.Lights(graph.Evaluate(epoch), DemoWorlds.Zephyr));
        Assert.Equal(DemoWorlds.Lumen, giant.Star);
        Assert.Equal(1, giant.RelativeFlux, 12);
        Assert.Equal(150_000, DemoWorlds.Find(DemoWorlds.Zephyr)!.RingOuterKilometers);
    }

    private static WorldGraph Graph()
    {
        var builder = new WorldGraphBuilder();
        DemoWorlds.AddTo(builder);
        return builder.Build();
    }

    private sealed class ArchiveFactAttribute : FactAttribute
    {
        public ArchiveFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SPACETANGENT_DEMO_WORLD_ORACLE")))
                Skip = "Set the archived demonstration-system states.";
        }
    }
}
