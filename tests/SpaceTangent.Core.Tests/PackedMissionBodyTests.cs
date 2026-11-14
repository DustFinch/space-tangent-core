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

    [OriginPacksFact]
    [Trait("Category", "ExternalData")]
    public void TwoOriginalPacksWithIdenticalResourceNamesKeepTheirOwners()
    {
        string archive = Environment.GetEnvironmentVariable("SPACETANGENT_ORIGIN_PACKS")!;
        var scratch = Directory.CreateTempSubdirectory("st-origin-packs-");
        try
        {
            var manifests = new List<string>();
            foreach (string owner in new[] { "0", "1" })
            {
                string original = Path.Combine(archive, owner, "same");
                string root = Path.Combine(scratch.FullName, "перенос", owner, "same");
                foreach (string file in Directory.EnumerateFiles(original, "*", SearchOption.AllDirectories))
                {
                    string copy = Path.Combine(root, Path.GetRelativePath(original, file));
                    Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
                    File.Copy(file, copy);
                }
                manifests.Add(Path.Combine(root, "pack.json"));
            }

            var earth = new ObjectId("sol/earth");
            var bodies = SampledMissionCatalog.Load(manifests, scratch.FullName,
                name => name is "Earth" or "Sol/Earth" ? earth : null,
                warning => throw new InvalidDataException(warning));
            Assert.Equal(4, bodies.Count);
            Verify(0, "OriginJason", "OriginRed", 12000, 5000,
                "A3739C2EE604ADB66930D69964A3282040425ADA199901BB4F769D963EBC5829",
                "4FF6AB670A58C14270E034E2090D9A432CAA263A14E0A25785386B0C12F880B5");
            Verify(1, "OriginSorce", "OriginBlue", 16000, -5000,
                "175B1C1AA5BD7F3E47EC36B63EB468596359D0C5CE153759FCB97D4B15B87A8D",
                "6A34118BA2E0BF5DA5AB14CB63B121E2E8B2987A876668A9B2F9C30E1357470B");

            void Verify(int owner, string spacecraft, string marker, double radius, double lateral,
                string meshHash, string textureHash)
            {
                var mission = Assert.Single(bodies, body => body.Name == spacecraft);
                var fixedBody = Assert.Single(bodies, body => body.Name == marker);
                var arc = Assert.Single(mission.Arcs);
                var markerArc = Assert.Single(fixedBody.Arcs);
                Assert.Equal(earth, arc.Center);
                Assert.Equal(earth, markerArc.Center);
                var epoch = JulianInstant.FromJulianDate(2455755.25, TimeScale.Tdb);
                Assert.Equal(new Vector3d(radius, 0, 0), arc.LocalState(epoch)!.Value.PositionKilometers);
                Assert.Equal(new Vector3d(14000, lateral, 0), markerArc.LocalState(epoch)!.Value.PositionKilometers);
                CheckFile(mission.MeshPath!, meshHash);
                CheckFile(Assert.Single(fixedBody.Geometry!).Shape.GlobeTexturePath!, textureHash);

                void CheckFile(string file, string hash)
                {
                    string directory = Path.GetDirectoryName(manifests[owner])!;
                    Assert.StartsWith(directory + Path.DirectorySeparatorChar, file, StringComparison.Ordinal);
                    Assert.Equal(hash, Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(file))));
                }
            }
        }
        finally { scratch.Delete(recursive: true); }
    }

    private sealed class OriginPacksFactAttribute : FactAttribute
    {
        public OriginPacksFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SPACETANGENT_ORIGIN_PACKS")))
                Skip = "Set the root containing the two archived same-name packs.";
        }
    }

    [MissionVersionsFact]
    [Trait("Category", "ExternalData")]
    public void OlderMissionPacksRestoreTheCeresSystemAtMatchingEpochs()
    {
        var packed = SampledMissionCatalog.Load("pack.json", Environment.GetEnvironmentVariable("SPACETANGENT_PACKED_MISSION_ROOT")!, Resolve);
        var baseline = Build(packed);
        var versions = new List<int>();
        foreach (string root in Environment.GetEnvironmentVariable("SPACETANGENT_PACKED_MISSION_VERSIONS")!
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            using var manifest = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(root, "pack.json")));
            int version = manifest.RootElement.GetProperty("formatVersion").GetInt32();
            versions.Add(version);
            var diagnostics = new List<string>();
            var missions = SampledMissionCatalog.Load("pack.json", root, Resolve, diagnostics.Add);
            var world = Build(missions);
            string[] targets = version == 7 ? ["Ceres", "Test Point", "body axes"]
                : ["Ceres", "Test Point", "body axes", "Cassini", "CubeSat", "frame axes"];
            foreach (string name in targets)
            {
                var mission = Assert.Single(missions, mission => mission.Name == name);
                foreach (double seconds in new[] { 3d, 20.5, 41 })
                {
                    var epoch = JulianInstant.FromJulianDate(2455755 + seconds / 86400, TimeScale.Tdb);
                    var actual = world.Evaluate(epoch);
                    var expected = baseline.Evaluate(epoch);
                    Assert.True(expected.TryState(mission.Id, out var reference), name);
                    Assert.True(actual.TryState(mission.Id, out var state), $"v{version} {name}");
                    Compare(reference.PositionKilometers, state.PositionKilometers, .0003);
                    Assert.True(expected.TryOrientation(mission.Id, out var referenceAttitude), name);
                    Assert.True(actual.TryOrientation(mission.Id, out var attitude), $"v{version} {name}");
                    if (version is 8 or 9 && name == "CubeSat")
                    {
                        Assert.True(actual.TryMissionReferenceFrame(mission.Id, orbit: false, epoch, out _, out var frame));
                        // Versions 8 and 9 share the earlier signed witness. Later packs carry an antipodal pair.
                        var first = System.Numerics.Quaternion.Normalize(new(.1f, -.2f, .3f, .9f));
                        var second = System.Numerics.Quaternion.Normalize(new(.2f, .1f, -.5f, -.8f));
                        double join = (2455755.0004745373 - 2455755) * JulianInstant.SecondsPerDay;
                        var turn = System.Numerics.Quaternion.Slerp(first, second,
                            (float)((epoch.ToJulianDate() - 2455755) * JulianInstant.SecondsPerDay / join));
                        var x = System.Numerics.Vector3.Transform(System.Numerics.Vector3.UnitX, turn);
                        var y = System.Numerics.Vector3.Transform(System.Numerics.Vector3.UnitY, turn);
                        Compare(frame.ToWorld.Transform(new(x.X, x.Y, x.Z)), attitude.ToWorld.X, 5e-7);
                        Compare(frame.ToWorld.Transform(new(y.X, y.Y, y.Z)), attitude.ToWorld.Y, 5e-7);
                    }
                    else
                    {
                        // Several v11 rotations use ICRF in place of the earlier J2000 axes.
                        Compare(referenceAttitude.ToWorld.X, attitude.ToWorld.X, 2e-7);
                        Compare(referenceAttitude.ToWorld.Y, attitude.ToWorld.Y, 2e-7);
                    }
                }
            }
        }
        Assert.Equal(new[] { 7, 8, 9, 10, 11 }, versions.Order());
    }

    private sealed class MissionVersionsFactAttribute : FactAttribute
    {
        public MissionVersionsFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SPACETANGENT_PACKED_MISSION_VERSIONS")) ||
                string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SPACETANGENT_PACKED_MISSION_ROOT")))
                Skip = "Set the original archived mission packs and the version 11 baseline.";
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
