// SPDX-License-Identifier: GPL-2.0-or-later
using System.Text.Json;
using SpaceTangent.Contracts;
using SpaceTangent.Core.Addons;
using SpaceTangent.Core.Ephemerides;
using Xunit;

namespace SpaceTangent.Core.Tests;

public sealed class MissionSensorTests
{
    [NativeSensorFact]
    [Trait("Category", "ExternalData")]
    public void NativeSensorFieldsAndRangeLimitsMatchIndependentReference()
    {
        string archive = Environment.GetEnvironmentVariable("SPACETANGENT_CHEBYSHEV_ROOT")!;
        using var oracle = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(archive, "sensor-reference.json")));
        var reference = oracle.RootElement;
        double angleTolerance = reference.GetProperty("tolerance").GetProperty("direction").GetDouble();
        double distanceTolerance = reference.GetProperty("tolerance").GetProperty("distanceKm").GetDouble();
        var folder = Directory.CreateTempSubdirectory();
        try
        {
            int rays = 0;
            foreach (var item in reference.GetProperty("sensors").EnumerateArray())
            {
                File.WriteAllText(Path.Combine(folder.FullName, "sensor.json"),
                    """{"version":"1.0","items":[{"name":"Sensor","center":"Earth","trajectory":{"type":"FixedPoint","position":[0,0,0]},"geometry":""" +
                    item.GetProperty("geometry").GetRawText() + "}]}");
                var body = Assert.Single(SampledMissionCatalog.Load("sensor.json", folder.FullName, _ => SolarSystemSample.EarthId));
                var sensor = Assert.IsType<MissionSensor>(Assert.Single(body.Geometry!).Shape.Sensor);
                Assert.Equal(SolarSystemSample.EarthId, sensor.Target);
                Assert.InRange((sensor.Boresight - Vector(item.GetProperty("boresight"))).Length, 0, angleTolerance);
                var directions = item.GetProperty("rays");
                Assert.Equal(directions.GetArrayLength() / 3, sensor.Boundary.Count);
                for (int index = 0; index < sensor.Boundary.Count; index++)
                {
                    int at = 3 * index;
                    var expected = new Vector3d(directions[at].GetDouble(), directions[at + 1].GetDouble(), directions[at + 2].GetDouble());
                    Assert.InRange((expected - sensor.Boundary[index]).Length, 0, angleTolerance);
                    rays++;
                }
            }
            Assert.Equal(1728, rays);
            var cases = reference.GetProperty("intersections");
            Assert.Equal(212, cases.GetArrayLength());
            foreach (var item in cases.EnumerateArray())
            {
                Vector3d origin = Vector(item.GetProperty("origin")), direction = Vector(item.GetProperty("direction")),
                    axes = Vector(item.GetProperty("semiAxes"));
                double range = item.GetProperty("range").GetDouble();
                bool found = MissionSensorProjection.TryCrossing(origin, direction, axes, out double hit);
                Assert.Equal(item.GetProperty("found").GetBoolean(), found);
                if (found)
                    Assert.InRange(Math.Abs(hit - item.GetProperty("distance").GetDouble()), 0, distanceTolerance);
                var actual = MissionSensorProjection.Project(origin, direction, axes, range);
                var expectedEnd = origin + direction * item.GetProperty("clipped").GetDouble();
                Assert.InRange((expectedEnd - actual.FrustumEnd).Length, 0, distanceTolerance);
                Assert.Equal(found && hit <= range, actual.SurfacePoint.HasValue);
            }
        }
        finally { folder.Delete(recursive: true); }
    }

    [ArchivedSensorFact]
    [Trait("Category", "ExternalData")]
    public void VersionTwoSciencePackRestoresBothOrientedSensorFields()
    {
        string archive = Environment.GetEnvironmentVariable("SPACETANGENT_ARCHIVED_SENSOR_PACK")!;
        string source = Environment.GetEnvironmentVariable("SPACETANGENT_PROBE_MISSIONS_ROOT")!;
        var jason = new ObjectId("satellite/jason");
        var sorce = new ObjectId("satellite/sorce");
        ObjectId? Resolve(string name) => name switch
        {
            "Earth" => SolarSystemSample.EarthId, "Sun" or "Sol" => SolarSystemSample.SunId, "Jason-1" => jason, "SORCE" => sorce, _ => null
        };
        var loose = SampledMissionCatalog.Load("sensor-verification.json", source, Resolve);
        var packed = SampledMissionCatalog.Load("pack.json", archive, Resolve);
        foreach (var original in loose)
        {
            var actual = Assert.Single(packed, body => body.Id == original.Id);
            Assert.Equal(original.Arcs[0].Center, actual.Arcs[0].Center);
            var a = Assert.IsType<MissionSensor>(Assert.Single(original.Geometry!).Shape.Sensor);
            var b = Assert.IsType<MissionSensor>(Assert.Single(actual.Geometry!).Shape.Sensor);
            Assert.Equal(a.Target, b.Target);
            Assert.Equal(a.RangeKilometers, b.RangeKilometers);
            Assert.Equal(a.Color, b.Color);
            Assert.Equal(a.Opacity, b.Opacity);
            Assert.Equal(a.GridSpokes, b.GridSpokes);
            Assert.Equal(1, b.FootprintOpacity);
            Assert.Equal(48, b.Boundary.Count);
            Assert.InRange((a.Boresight - b.Boresight).Length, 0, 1e-5);
            for (int ray = 0; ray < b.Boundary.Count; ray++)
                Assert.InRange((a.Boundary[ray] - b.Boundary[ray]).Length, 0, 1e-5);
        }
    }

    private sealed class ArchivedSensorFactAttribute : FactAttribute
    {
        public ArchivedSensorFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SPACETANGENT_ARCHIVED_SENSOR_PACK")) ||
                string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SPACETANGENT_PROBE_MISSIONS_ROOT")))
                Skip = "Set the original v2 science export and source mission corpus.";
        }
    }

    private static Vector3d Vector(JsonElement array) => new(array[0].GetDouble(), array[1].GetDouble(), array[2].GetDouble());

    private sealed class NativeSensorFactAttribute : FactAttribute
    {
        public NativeSensorFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SPACETANGENT_CHEBYSHEV_ROOT")))
                Skip = "Set the directory with the independent sensor reference.";
        }
    }
}
