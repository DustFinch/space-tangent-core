// SPDX-License-Identifier: GPL-2.0-or-later
using System.Text.Json;
using SpaceTangent.Contracts;
using SpaceTangent.Core.Addons;
using SpaceTangent.Core.Ephemerides;
using SpaceTangent.Core.World;
using Xunit;

namespace SpaceTangent.Core.Tests;

public sealed class MissionGeometryTests
{
    [NativeGeometryFact]
    [Trait("Category", "ExternalData")]
    public void OriginalTimeSwitchIntervalsMatchTheNativeRenderer()
    {
        string archive = Environment.GetEnvironmentVariable("SPACETANGENT_CHEBYSHEV_ROOT")!;
        using var oracle = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(archive, "time-switch-reference.json")));
        var folder = Directory.CreateTempSubdirectory();
        try
        {
            int samples = 0;
            foreach (var item in oracle.RootElement.GetProperty("cases").EnumerateArray())
            {
                File.WriteAllText(Path.Combine(folder.FullName, "mission.json"),
                    """{"version":"1.0","items":[{"name":"Example","center":"Earth","trajectory":{"type":"FixedPoint","position":[1,0,0]},"geometry":""" +
                    item.GetProperty("geometry").GetRawText() + "}]}");
                var mission = Assert.Single(SampledMissionCatalog.Load("mission.json", folder.FullName, _ => SolarSystemSample.EarthId));
                Assert.NotNull(mission.Geometry);
                foreach (var sample in item.GetProperty("samples").EnumerateArray())
                {
                    var date = JulianInstant.FromJulianDate(sample.GetProperty("day").GetDouble(), TimeScale.Tdb);
                    var active = mission.Geometry.SingleOrDefault(span => date.CompareTo(span.Start) >= 0 && date.CompareTo(span.End) < 0);
                    var expected = sample.GetProperty("radius");
                    Assert.Equal(expected.ValueKind == JsonValueKind.Null ? null : expected.GetDouble(), active?.Shape.RadiusKilometers);
                    samples++;
                }
            }
            Assert.Equal(152, samples);
        }
        finally { folder.Delete(recursive: true); }
    }

    [NativeGeometryFact]
    [Trait("Category", "ExternalData")]
    public void OriginalAsteroidHidesItsGeometryBeforeChangingTheTrajectoryCenter()
    {
        string archive = Environment.GetEnvironmentVariable("SPACETANGENT_CHEBYSHEV_ROOT")!;
        var folder = Directory.CreateTempSubdirectory();
        try
        {
            string catalog = Directory.CreateDirectory(Path.Combine(folder.FullName, "catalog")).FullName;
            File.Copy(Path.Combine(archive, "time-switch-original-examples__2008TC3.json"), Path.Combine(catalog, "2008TC3.json"));
            File.Copy(Path.Combine(archive, "sampled-original-2008TC3.xyzv"), Path.Combine(catalog, "2008TC3.xyzv"));
            var warnings = new List<string>();
            var tracks = SampledMissionCatalog.Load("catalog/2008TC3.json", folder.FullName, name => name switch
            {
                "Sun" => SolarSystemSample.SunId,
                "Earth" => SolarSystemSample.EarthId,
                _ => null
            }, warnings.Add);
            Assert.Contains(warnings, issue => issue.Contains("8flora.cmod", StringComparison.Ordinal));
            var track = Assert.Single(tracks, mission => mission.Name == "2008 TC3");
            Assert.Equal(2, tracks.Count(mission => mission.Geometry?.Any(span => span.Shape.ParticleEmitters is { Count: > 0 }) == true));
            var shown = Assert.Single(track.Geometry!);
            Assert.Equal(.0035, shown.Shape.RadiusKilometers);
            Assert.InRange((double)track.Arcs[1].Start.DifferenceNanoseconds(shown.End) / 1e9, 8.199999, 8.200001);
            var graph = new WorldGraphBuilder();
            graph.AddTrajectory(SolarSystemSample.SunId, _ => new(default, default));
            graph.AddTrajectory(SolarSystemSample.EarthId, _ => new(new(1.5e8, 0, 0), new(0, 30, 0)));
            graph.AddMeanEarthRotation();
            graph.AddSampledMission(track);
            var world = graph.Build();
            var before = world.Evaluate(shown.End.AddSeconds(-1));
            Assert.True(before.TryOrientation(track.Id, out var attitude));
            Assert.InRange(Math.Abs(attitude.AngularVelocityRadiansPerSecond!.Value.Length - Math.Tau / (.0269409 * 3600)), 0, 1e-12);
            var after = world.Evaluate(track.Arcs[1].Start);
            Assert.True(after.TryStateCenter(track.Id, out var center));
            Assert.Equal(SolarSystemSample.EarthId, center);
            Assert.True(after.TryState(track.Id, out var state));
            Assert.True(after.TryState(center, out var earth));
            Assert.InRange(Math.Abs((state - earth).PositionKilometers.Length - new Vector3d(5013.573, 3267.2722, 2218.0255).Length), 0, 1e-6);
        }
        finally { folder.Delete(recursive: true); }
    }

    [NativeGeometryFact]
    [Trait("Category", "ExternalData")]
    public void TimeSwitchBoundsIncludeEveryNativeChildEvenWhenItsIntervalIsUnreachable()
    {
        string archive = Environment.GetEnvironmentVariable("SPACETANGENT_CHEBYSHEV_ROOT")!;
        using var oracle = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(archive, "geometry-bounds-reference.json")));
        var folder = Directory.CreateTempSubdirectory("st-bounds-");
        try
        {
            foreach (var item in oracle.RootElement.GetProperty("switches").EnumerateArray())
            {
                File.WriteAllText(Path.Combine(folder.FullName, "mission.json"),
                    """{"version":"1.0","items":[{"name":"Example","center":"Earth","trajectory":{"type":"FixedPoint","position":[1,0,0]},"geometry":""" +
                    item.GetProperty("geometry").GetRawText() + "}]}");
                var mission = Assert.Single(SampledMissionCatalog.Load("mission.json", folder.FullName, _ => SolarSystemSample.EarthId));
                var bounds = Assert.IsType<MissionGeometryBounds>(mission.GeometryBounds);
                Assert.True(bounds.Complete);
                Assert.Empty(bounds.Meshes);
                Assert.Equal(item.GetProperty("bound").GetDouble(), bounds.Resolve(_ => throw new InvalidOperationException()));
            }
        }
        finally { folder.Delete(recursive: true); }
    }

    [Fact]
    public void TimedMeshNavigationBoundUsesLongestExtentRawOriginAndOffset()
    {
        var bounds = new MissionGeometryBounds(4,
            [new("hidden-model", 6, new(3, 4, 0)), new("other-model", 1, default)], true);
        Assert.Equal(24d, bounds.Resolve(path => path == "hidden-model" ? (4, 3) : (1, 2)));
        Assert.Null((bounds with { Complete = false }).Resolve(_ => throw new InvalidOperationException()));
    }

    [PackedGeometryFact]
    [Trait("Category", "ExternalData")]
    public void ArchivedV5RestoresTimedChildrenAndTheHiddenMeshBound()
    {
        string root = Environment.GetEnvironmentVariable("SPACETANGENT_TIMED_BOUNDS_PACK")!;
        var earth = SolarSystemSample.EarthId;
        var sorce = new ObjectId("norad/27880");
        var jason = new ObjectId("norad/26997");
        var mission = Assert.Single(SampledMissionCatalog.Load("pack.json", root, name => name switch
        {
            "Earth" => earth,
            "SORCE" => sorce,
            "Jason-1" => jason,
            _ => null
        }));
        var geometry = Assert.IsAssignableFrom<IReadOnlyList<MissionGeometrySpan>>(mission.Geometry);
        Assert.Equal(3, geometry.Count);
        Assert.Equal("sorce.glb", Path.GetFileName(geometry[0].Shape.MeshPath));
        Assert.NotNull(geometry[1].Shape.EllipsoidRadiiKilometers);
        Assert.Equal("sorce.glb", Path.GetFileName(geometry[2].Shape.MeshPath));
        Assert.Equal(2455755.01, geometry[1].Start.ToJulianDate(), 8);
        Assert.Equal(2455755.02, geometry[1].End.ToJulianDate(), 8);
        var bounds = Assert.IsType<MissionGeometryBounds>(mission.GeometryBounds);
        Assert.True(bounds.Complete);
        var hidden = Assert.Single(bounds.Meshes, term => Path.GetFileName(term.Path) == "ghost-jason.glb");
        Assert.Equal(.01, hidden.SizeKilometers);
        Assert.Equal(Math.Sqrt(21.875), hidden.Offset.Length, 10);
        Assert.Equal((double)(float)((3f + MathF.Sqrt(21.875f)) * .005f),
            bounds.Resolve(_ => (4, 3)));
    }

    private sealed class PackedGeometryFactAttribute : FactAttribute
    {
        public PackedGeometryFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SPACETANGENT_TIMED_BOUNDS_PACK")))
                Skip = "Set the original v5 geometry pack.";
        }
    }

    private sealed class NativeGeometryFactAttribute : FactAttribute
    {
        public NativeGeometryFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SPACETANGENT_CHEBYSHEV_ROOT")))
                Skip = "Set the native geometry oracle and original catalogue directory.";
        }
    }
}
