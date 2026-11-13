// SPDX-License-Identifier: GPL-2.0-or-later
using System.Text.Json;
using SpaceTangent.Contracts;
using SpaceTangent.Core.Addons;
using SpaceTangent.Core.Ephemerides;
using SpaceTangent.Core.World;
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
        var timed = Assert.Single(packed, body => body.Name == "TimeSwitchModel");
        Assert.DoesNotContain(packed, body => body.Name.StartsWith("TimeSwitchModel (", StringComparison.Ordinal));
        var originalTimed = Assert.Single(SampledMissionCatalog.Load("time-switch-verification.json", source, Resolve));
        foreach (double day in new[] { 2455754.99, 2455755, 2455755.01, 2455755.02, 2455755.03, 2455755.04 })
        {
            string? expected = MeshAt(originalTimed, day);
            Assert.Equal(expected, MeshAt(timed, day));
        }

        static string? MeshAt(SampledMission body, double day)
        {
            var epoch = JulianInstant.FromJulianDate(day, TimeScale.Tdb);
            var span = body.Geometry!.SingleOrDefault(span => epoch.CompareTo(span.Start) >= 0 && epoch.CompareTo(span.End) < 0);
            return Path.GetFileNameWithoutExtension(span?.Shape.MeshPath);
        }
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

    [ArchivedSensorFact]
    [Trait("Category", "ExternalData")]
    public void ArchivedNestedTrajectoriesRetainWorldPositionAndNativeVelocity()
    {
        string root = Environment.GetEnvironmentVariable("SPACETANGENT_ARCHIVED_SENSOR_PACK")!;
        var satellites = PackedSatelliteCatalog.Load(root);
        ObjectId? Resolve(string name) => name switch
        {
            "Earth" => SolarSystemSample.EarthId, "Sun" or "Sol" => SolarSystemSample.SunId,
            _ => satellites.FirstOrDefault(satellite => satellite.Name == name)?.Id
        };
        var missions = SampledMissionCatalog.Load("pack.json", root, Resolve);
        var earth = new CartesianState(new(8000, -3000, 2000), new(5, 12, -2));
        var builder = new WorldGraphBuilder();
        builder.AddTrajectory(SolarSystemSample.EarthId, _ => earth);
        builder.AddTrajectory(SolarSystemSample.SunId, _ => default(CartesianState));
        foreach (var satellite in satellites)
            builder.AddScienceSatellite(satellite);
        foreach (var mission in missions)
            builder.AddSampledMission(mission);
        var graph = builder.Build();
        foreach (double day in new[] { 2455755.0, 2455755.01, 2455755.03 })
        {
            var snapshot = graph.Evaluate(JulianInstant.FromJulianDate(day, TimeScale.Tdb));
            var jason = Resolve("Jason-1")!.Value;
            var sorce = Resolve("SORCE")!.Value;
            Assert.True(snapshot.TryOrientation(jason, out var jasonAttitude));
            Assert.True(snapshot.TryOrientation(sorce, out var sorceAttitude));
            Assert.True(snapshot.TryState(jason, out var jasonState));
            var jasonRadial = (jasonState - earth).PositionKilometers;
            Check("sciencenestedprobe", jasonAttitude.ToWorld.X * 12000);
            double obliquity = 84381.448 * Math.PI / (180 * 3600);
            Check("sciencenestedsorceprobe", sorceAttitude.ToWorld.Transform(
                new(0, -12000 * Math.Sin(obliquity), 12000 * Math.Cos(obliquity))));
            Check("sciencetwovectorprobe", jasonRadial * (12000 / jasonRadial.Length));

            void Check(string id, Vector3d expected)
            {
                Assert.True(snapshot.TryState(new("mission/" + id), out var actual), id);
                Assert.True((actual.PositionKilometers - earth.PositionKilometers - expected).Length <= 2e-6,
                    $"{id}: {actual.PositionKilometers - earth.PositionKilometers} vs {expected}");
                Assert.Equal(earth.VelocityKilometersPerSecond, actual.VelocityKilometersPerSecond);
            }
        }
    }

    [ArchivedSpinFact]
    [Trait("Category", "ExternalData")]
    public void ArchivedFollowSpinCarriesOnlyTheLocalRateThroughMovingOffsets()
    {
        string root = Environment.GetEnvironmentVariable("SPACETANGENT_ARCHIVED_FOLLOW_SPIN")!;
        var satellites = PackedSatelliteCatalog.Load(root);
        var jason = Assert.Single(satellites, satellite => satellite.Name == "Jason-1");
        var earth = SolarSystemSample.EarthId;
        ObjectId? Resolve(string name) => name == "Earth" ? earth :
            satellites.FirstOrDefault(satellite => satellite.Name == name)?.Id;
        var missions = SampledMissionCatalog.Load("pack.json", root, Resolve);
        var witness = Assert.Single(missions, body => body.Name == "Jason Spin Witness");
        var offset = Assert.Single(missions, body => body.Name == "Jason Spin Offset");
        var builder = new WorldGraphBuilder();
        builder.AddTrajectory(earth, _ => default(CartesianState));
        builder.AddTrajectory(jason.Id, epoch => jason.TrySample(epoch, out var sample) ? sample.EarthRelativeState : null, earth);
        var parentAxes = new Basis3d(new(0, 1, 0), new(0, 0, 1), new(1, 0, 0));
        builder.AddOrientation(jason.Id, _ => new(parentAxes, new Vector3d(.001, .002, .003)));
        foreach (var mission in missions)
            builder.AddSampledMission(mission);
        var graph = builder.Build();
        foreach (double day in new[] { 2455755d, 2455755.005, 2455755.02 })
        {
            var epoch = JulianInstant.FromJulianDate(day, TimeScale.Tdb);
            var world = graph.Evaluate(epoch);
            double phase = Math.PI + Radians(33) + (day - 2455755) * Math.Tau * 36;
            Vector3d Tilt(Vector3d vector)
            {
                double tilt = Radians(17);
                return Z(new(vector.X, Math.Cos(tilt) * vector.Y - Math.Sin(tilt) * vector.Z,
                    Math.Sin(tilt) * vector.Y + Math.Cos(tilt) * vector.Z), Radians(5));
            }
            Vector3d Turn(Vector3d vector) => parentAxes.Transform(Tilt(Z(vector, phase)));
            var expectedRate = parentAxes.Transform(Tilt(new(0, 0, Math.Tau / 2400)));
            Assert.True(world.TryOrientation(witness.Id, out var attitude));
            Close(Turn(new(1, 0, 0)), attitude.ToWorld.X);
            Close(Turn(new(0, 1, 0)), attitude.ToWorld.Y);
            Close(Turn(new(0, 0, 1)), attitude.ToWorld.Z);
            Close(expectedRate, attitude.AngularVelocityRadiansPerSecond!.Value);
            Assert.True(world.TryMissionReferenceFrame(witness.Id, orbit: false, epoch, out _, out var frame));
            Close(parentAxes.X, frame.ToWorld.X);
            Assert.True(world.TryState(offset.Id, out var actual));
            Assert.True(world.TryState(jason.Id, out var center));
            Close(Turn(new(-100, -200, 300)), (actual - center).PositionKilometers);
            Close(Vector3d.Cross(expectedRate, new(100, 200, 300)), (actual - center).VelocityKilometersPerSecond);
        }

        static double Radians(double degrees) => degrees * Math.PI / 180;
        static Vector3d Z(Vector3d p, double angle) => new(Math.Cos(angle) * p.X - Math.Sin(angle) * p.Y,
            Math.Sin(angle) * p.X + Math.Cos(angle) * p.Y, p.Z);
        static void Close(Vector3d expected, Vector3d actual) => Assert.True((expected - actual).Length < 2e-6,
            $"{expected} vs {actual}");
    }

    private sealed class ArchivedSpinFactAttribute : FactAttribute
    {
        public ArchivedSpinFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SPACETANGENT_ARCHIVED_FOLLOW_SPIN")))
                Skip = "Set the original science pack with the exported local-spin witness.";
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
