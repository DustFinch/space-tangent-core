// SPDX-License-Identifier: GPL-2.0-or-later
using System.Text.Json;
using SpaceTangent.Contracts;
using SpaceTangent.Core.Addons;
using SpaceTangent.Core.Ephemerides;
using SpaceTangent.Core.World;
using Xunit;

namespace SpaceTangent.Core.Tests;

public sealed class PackedMissionBodyTests
{
    [ArchiveFact]
    [Trait("Category", "ExternalData")]
    public void OriginalPackRestoresItsCentresLocalStatesAttitudesAndModels()
    {
        string root = Environment.GetEnvironmentVariable("SPACETANGENT_PACKED_MISSION_ROOT")!;
        string oraclePath = Environment.GetEnvironmentVariable("SPACETANGENT_PACKED_FRAME_ORACLE")!;
        var warnings = new List<string>();
        var packed = SampledMissionCatalog.Load("pack.json", root, Resolve, warnings.Add);
        Assert.Equal(10, packed.Count);
        Assert.Contains(warnings, text => text.StartsWith("InvalidRig:", StringComparison.Ordinal));
        Assert.Contains(warnings, text => text.StartsWith("InvalidChild:", StringComparison.Ordinal));
        Assert.Contains(warnings, text => text.Contains("moon.dds", StringComparison.Ordinal));
        Assert.DoesNotContain(packed, body => body.Name is "InvalidRig" or "InvalidChild");

        string source = Environment.GetEnvironmentVariable("SPACETANGENT_NATIVE_ATTITUDE")!;
        var unpacked = SampledMissionCatalog.Load(Path.Combine(source, "catalog", "test.json"), source, Resolve);
        var world = Build(packed);
        var sourceWorld = Build(unpacked);
        var ceres = Find("Ceres");
        var cassini = Find("Cassini");
        var cube = Find("CubeSat");
        var compatible = Find("CompatibleRig");
        var witness = Find("CompatibleRateWitness");
        Assert.Equal(new Vector3d(500, 500, 480), Assert.Single(ceres.Geometry!).Shape.EllipsoidRadiiKilometers);
        Assert.Equal(ceres.Id, Assert.Single(cassini.Arcs).Center);
        var cassiniShape = Assert.Single(cassini.Geometry!).Shape;
        Assert.Equal(10, cassiniShape.RadiusKilometers);
        Assert.EndsWith("jason.glb", cassiniShape.MeshPath, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(cassiniShape.MeshPath, Assert.Single(cube.Geometry!).Shape.MeshPath);
        Compare(Basis3d.Identity.X, cassiniShape.MeshRotation.X, 2e-14);
        Compare(Basis3d.Identity.Y, cassiniShape.MeshRotation.Y, 2e-14);

        using var oracle = JsonDocument.Parse(File.ReadAllBytes(oraclePath));
        Basis3d b1950 = Basis("EquatorB1950");
        Basis3d icrf = Basis("ICRF");
        foreach (double seconds in new[] { -3d, 3, 20.5, 41, 63, 100 })
        {
            var epoch = JulianInstant.FromJulianDate(2455755 + seconds / 86400, TimeScale.Tdb);
            WorldSnapshot state = world.Evaluate(epoch), original = sourceWorld.Evaluate(epoch);
            foreach (var body in new[] { ceres, cassini, cube, Find("Test Point") })
            {
                Assert.True(state.TryState(body.Id, out var actual), body.Name);
                Assert.True(original.TryState(body.Id, out var expected), body.Name);
                Compare(expected.PositionKilometers, actual.PositionKilometers, .0003);
                if (body.Name != "Test Point")
                    Compare(expected.VelocityKilometersPerSecond, actual.VelocityKilometersPerSecond, 2e-6);
            }
            Assert.True(state.TryMissionReferenceFrame(compatible.Id, orbit: false, epoch, out _, out var frame));
            Compare(b1950.X, frame.ToWorld.X, 2e-12);
            Compare(b1950.Y, frame.ToWorld.Y, 2e-12);
            Compare(b1950.Z, frame.ToWorld.Z, 2e-12);
            Assert.True(state.TryState(compatible.Id, out var carrier));
            Assert.True(state.TryState(witness.Id, out var child));
            Assert.True(state.TryOrientation(compatible.Id, out var attitude));
            Assert.NotNull(attitude.AngularVelocityRadiansPerSecond);
            var local = new Vector3d(2, 3, 4);
            Compare(attitude.ToWorld.Transform(local), (child - carrier).PositionKilometers, 2e-7);
            Compare(Vector3d.Cross(attitude.AngularVelocityRadiansPerSecond.Value, icrf.Transform(local)),
                (child - carrier).VelocityKilometersPerSecond, 2e-10);
            Assert.True(state.TryOrientation(Find("frame axes").Id, out _));
            Assert.True(state.TryOrientation(Find("body axes").Id, out _));
        }

        SampledMission Find(string name) => Assert.Single(packed, body => body.Name == name);

        Basis3d Basis(string frame)
        {
            var sample = oracle.RootElement.GetProperty("samples").EnumerateArray().Single(item =>
                item.GetProperty("frame").GetString() == frame &&
                item.GetProperty("modelWxyz").EnumerateArray().Select(value => value.GetDouble()).SequenceEqual(new[] { 1d, 0, 0, 0 }));
            var q = sample.GetProperty("commonWxyz").EnumerateArray().Select(value => value.GetDouble()).ToArray();
            var axis = new Vector3d(q[1], q[2], q[3]);
            return new(Convert(new(1, 0, 0)), Convert(new(0, 0, -1)), Convert(new(0, 1, 0)));

            Vector3d Convert(Vector3d native)
            {
                Vector3d twice = Vector3d.Cross(axis, native) * 2;
                Vector3d rotated = native + twice * q[0] + Vector3d.Cross(axis, twice);
                var (sin, cos) = Math.SinCos(23.4392911 * Math.PI / 180);
                return new(rotated.X, -rotated.Z * cos - rotated.Y * sin, rotated.Y * cos - rotated.Z * sin);
            }
        }
    }

    private static ObjectId? Resolve(string name) => name is "Sun" or "Sol" ? SolarSystemSample.SunId : null;

    private static WorldGraph Build(IReadOnlyList<SampledMission> missions)
    {
        var graph = new WorldGraphBuilder();
        graph.AddTrajectory(SolarSystemSample.SunId, _ => default(CartesianState));
        foreach (var mission in missions)
            graph.AddSampledMission(mission);
        return graph.Build();
    }

    private static void Compare(Vector3d expected, Vector3d actual, double tolerance) =>
        Assert.True((expected - actual).Length <= tolerance, $"Expected {expected}, found {actual}, tolerance {tolerance:G6}.");

    private sealed class ArchiveFactAttribute : FactAttribute
    {
        public ArchiveFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SPACETANGENT_PACKED_MISSION_ROOT")) ||
                string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SPACETANGENT_PACKED_FRAME_ORACLE")) ||
                string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SPACETANGENT_NATIVE_ATTITUDE")))
                Skip = "Set the archived packed mission and frame oracle paths.";
        }
    }
}
