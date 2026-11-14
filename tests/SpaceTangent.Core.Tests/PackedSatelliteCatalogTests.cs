// SPDX-License-Identifier: GPL-2.0-or-later
using System.Text.Json;
using SpaceTangent.Contracts;
using SpaceTangent.Core.Addons;
using SpaceTangent.Core.Ephemerides;
using SpaceTangent.Core.World;
using Xunit;

namespace SpaceTangent.Core.Tests;

public sealed class PackedSatelliteCatalogTests
{
    [PackedScienceFact]
    [Trait("Category", "ExternalData")]
    public void OriginalSourceAndPackedScienceSatellitesRetainOrbitsAndMeshAxes()
    {
        string source = Environment.GetEnvironmentVariable("SPACETANGENT_SCIENCE_ROOT")!;
        string packed = Environment.GetEnvironmentVariable("SPACETANGENT_PACKED_SCIENCE")!;
        var original = ScienceSatelliteCatalog.Load("catalog/sciencesat.json", source);
        var diagnostics = new List<string>();
        var converted = PackedSatelliteCatalog.Load(packed, diagnostics.Add);
        Assert.Equal(3, diagnostics.Count);
        Assert.Equal(2, converted.Count);
        foreach (var record in original)
        {
            var compiled = Assert.Single(converted, satellite => satellite.Id == record.Id);
            Assert.Equal(record.Name, compiled.Name);
            Assert.Equal(record.Epoch, compiled.Epoch);
            Assert.Equal(record.SizeKilometers, compiled.SizeKilometers);
            Assert.Equal(record.Display, compiled.Display);
            Assert.Equal(record.OrbitPeriodSeconds, compiled.OrbitPeriodSeconds);
            Assert.EndsWith(".glb", compiled.MeshPath, StringComparison.OrdinalIgnoreCase);
            var epoch = record.Epoch.AddSeconds(45);
            Assert.True(record.TrySample(epoch, out var a));
            Assert.True(compiled.TrySample(epoch, out var b));
            Assert.Equal(a.EarthRelativeState, b.EarthRelativeState);
            Assert.Equal(a.BodyToTrajectory, b.BodyToTrajectory);
            Compare(a.InstrumentToTrajectory.X, b.InstrumentToTrajectory.X);
            Compare(a.InstrumentToTrajectory.Y, b.InstrumentToTrajectory.Y);
            Compare(a.InstrumentToTrajectory.Z, b.InstrumentToTrajectory.Z);
            Compare(a.MeshToBody.X, b.MeshToBody.X);
            Compare(a.MeshToBody.Y, b.MeshToBody.Y);
            Compare(a.MeshToBody.Z, b.MeshToBody.Z);
        }

        static void Compare(Vector3d source, Vector3d packed) => Assert.InRange((source - packed).Length, 0, 1e-5);
    }

    [PackedScienceVersionsFact]
    [Trait("Category", "ExternalData")]
    public void ReadsOriginalSciencePacksAcrossTheirArchivedVersions()
    {
        string input = Environment.GetEnvironmentVariable("SPACETANGENT_PACKED_SCIENCE_VERSIONS")!;
        var reference = PackedSatelliteCatalog.Load(Environment.GetEnvironmentVariable("SPACETANGENT_PACKED_SCIENCE")!);
        var versions = new List<int>();
        foreach (string directory in input.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            using var manifest = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(directory, "pack.json")));
            int version = manifest.RootElement.GetProperty("formatVersion").GetInt32();
            versions.Add(version);
            var diagnostics = new List<string>();
            var actual = PackedSatelliteCatalog.Load(directory, diagnostics.Add);
            Assert.Contains(diagnostics, message => message.EndsWith("not a modeled Earth satellite in this pack.", StringComparison.Ordinal));
            foreach (var expected in reference)
            {
                var installed = Assert.Single(actual, satellite => satellite.Id == expected.Id);
                Assert.True(File.Exists(installed.MeshPath), $"Version {version} lost the mesh for {expected.Name}.");
                Assert.Equal(expected.Epoch, installed.Epoch);
                Assert.Equal(expected.SizeKilometers, installed.SizeKilometers);
                foreach (double offset in new[] { 0.0, 45.0, 3600.0 })
                {
                    var epoch = expected.Epoch.AddSeconds(offset);
                    Assert.True(expected.TrySample(epoch, out var source));
                    Assert.True(installed.TrySample(epoch, out var packed));
                    Assert.Equal(source.EarthRelativeState, packed.EarthRelativeState);
                    Assert.Equal(source.BodyToTrajectory, packed.BodyToTrajectory);
                }
            }
        }
        Assert.Equal(new[] { 1, 2, 4, 5, 6, 7, 12 }, versions.Order());
    }

    [ArchiveDisplayFact]
    [Trait("Category", "ExternalData")]
    public void ArchivedDisplayRetainsTheOriginalLabelAndAuthoredTrail()
    {
        string root = Environment.GetEnvironmentVariable("SPACETANGENT_ARCHIVED_DISPLAY")!;
        var source = Path.Combine(Path.GetDirectoryName(root)!, "source");
        var original = ScienceSatelliteCatalog.Load(Path.Combine(source, "sciencesat.json"), Path.GetDirectoryName(source)!);
        var packed = PackedSatelliteCatalog.Load(root);
        var jason = Assert.Single(packed, satellite => satellite.Name == "Jason-1");
        var display = Assert.IsType<MissionDisplay>(jason.Display);
        Assert.Equal(new Vector3d(.7, .7, .9), display.LabelColor);
        Assert.Equal(new Vector3d(.3, .8, .45), display.OrbitColor);
        Assert.Equal(2700, display.OrbitDurationSeconds);
        Assert.Equal(600, display.OrbitLeadSeconds, 9);
        Assert.Equal(.6, display.OrbitFade);
        Assert.InRange(jason.OrbitPeriodSeconds, 6000, 7500);
        Assert.Equal(original[0].Display! with { OrbitLeadSeconds = display.OrbitLeadSeconds }, display);
    }

    [DeepProbeArchiveFact]
    [Trait("Category", "ExternalData")]
    public void OriginalDeepProbeUsesItsB1950TrajectoryAndFixedAttitude()
    {
        string path = Environment.GetEnvironmentVariable("SPACETANGENT_ARCHIVED_DEEP_PROBE")!;
        var warnings = new List<string>();
        var pack = PackedSatelliteCatalog.Load(path, warnings.Add);
        Assert.Empty(warnings);
        Assert.Equal(3, pack.Count);
        var probe = Assert.Single(pack, body => body.Name == "DeepProbe");
        Assert.Equal(new ObjectId("norad/24208"), probe.Id);
        Assert.Equal("jason.glb", Path.GetFileName(probe.MeshPath));

        // The independent native reference reports Vesta ecliptic coordinates.
        // Map (x, y, z) to (x, -z, y), then rotate the ecliptic into the simulator equator.
        const double obliquity = 23.4392911 * Math.PI / 180;
        var (sin, cos) = Math.SinCos(obliquity);
        Vector3d World(double x, double y, double z) => new(x, -z * cos - y * sin, -z * sin + y * cos);
        var middle = new JulianInstant(2453912, 46774527584512, TimeScale.Tdb);
        var samples = new (JulianInstant Epoch, Vector3d Position, Vector3d Velocity)[]
        {
            (middle.AddSeconds(-86400), World(9051.372954388045, -138.67449879790934, -40955.96256990895),
                World(-3.004709869220577, .20744993224272446, -.6706885519893631)),
            (middle, World(7030.44299635239, -.0328708739989452, -41355.1791289267),
                World(-3.03375846669312, .207951496205923, -.521901275347844)),
            (middle.AddSeconds(86400), World(4993.614384000887, 138.37944951018608, -41654.25438198337),
                World(-3.0554561524270136, .20792663596964825, -.37197961687416115)),
            (middle.AddSeconds(-31536001), World(-23958.1890775586, 1390.12984372398, -34516.4883085335),
                World(-2.5293282833415103, .12147800255775865, 1.7509163044930096)),
            (middle.AddSeconds(31536001), World(34099.118077101, -2278.17919336934, -24253.0647626201),
                World(-1.778217587290219, .19459917619704273, -2.5174916932203404)),
            (middle.AddSeconds(-315360000), World(10230.742420237373, -715.6325482452726, 40775.97961247855),
                World(2.979571995918567, -.14921467672712635, -.7582389160332648)),
            (middle.AddSeconds(315360000), World(32536.19298571459, -3165.8568355547695, 26364.934128228182),
                World(1.9261556090122587, -.10798346704160118, -2.40312402541457))
        };
        Assert.True(probe.TrySample(middle, out var reference));
        foreach (var (epoch, position, velocity) in samples)
        {
            Assert.True(probe.TrySample(epoch, out var sample));
            Assert.True((sample.EarthRelativeState.PositionKilometers - position).Length < 5,
                $"Actual {sample.EarthRelativeState.PositionKilometers}, expected {position}.");
            Assert.InRange((sample.EarthRelativeState.VelocityKilometersPerSecond - velocity).Length, 0, .001);
            Assert.Equal(reference.BodyToTrajectory, sample.BodyToTrajectory);
        }
    }

    [DeepProbeArchiveFact]
    [Trait("Category", "ExternalData")]
    public void OriginalDeepProbeKeepsIndependentWorldHistoriesAcrossGraphReplacement()
    {
        string path = Environment.GetEnvironmentVariable("SPACETANGENT_ARCHIVED_DEEP_PROBE")!;
        var probe = Assert.Single(PackedSatelliteCatalog.Load(path), body => body.Id.Value == "norad/24208");
        var first = new WorldEvaluation();
        var second = new WorldEvaluation();
        var world = Build(probe);
        var initial = At(world, first, 0);
        Assert.Equal(initial, At(world, second, 0));
        foreach (double minutes in new[] { 1, 29.9, 30, 30.1, 31, 1440, 1439, -5 })
            At(world, first, minutes);

        var replacement = Build(probe);
        var after = At(replacement, first, 0);
        Assert.InRange((initial.PositionKilometers - after.PositionKilometers).Length, .0045, .0047);
        Assert.Equal(initial, At(replacement, second, 0));
        Assert.Equal(At(world, null, 0), At(replacement, null, 0));
        var replacementProbe = Assert.Single(PackedSatelliteCatalog.Load(path), body => body.Id == probe.Id);
        Assert.Equal(initial, At(Build(replacementProbe), first, 0));

        static WorldGraph Build(ScienceSatellite satellite)
        {
            var builder = new WorldGraphBuilder();
            builder.AddTrajectory(SolarSystemSample.EarthId, _ => default(CartesianState));
            builder.AddScienceSatellite(satellite);
            return builder.Build();
        }

        CartesianState At(WorldGraph graph, WorldEvaluation? history, double minutes)
        {
            var snapshot = graph.Evaluate(probe.Epoch.AddSeconds(minutes * 60), history);
            Assert.True(snapshot.TryState(probe.Id, out var state));
            return state;
        }
    }


    private sealed class DeepProbeArchiveFactAttribute : FactAttribute
    {
        public DeepProbeArchiveFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SPACETANGENT_ARCHIVED_DEEP_PROBE")))
                Skip = "Set the original DeepProbe science pack.";
        }
    }

    private sealed class ArchiveDisplayFactAttribute : FactAttribute
    {
        public ArchiveDisplayFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SPACETANGENT_ARCHIVED_DISPLAY")))
                Skip = "Set the archived science installation with the authored trail.";
        }
    }

    private sealed class PackedScienceVersionsFactAttribute : FactAttribute
    {
        public PackedScienceVersionsFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SPACETANGENT_PACKED_SCIENCE_VERSIONS")) ||
                string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SPACETANGENT_PACKED_SCIENCE")))
                Skip = "Set the archived science packs and their baseline.";
        }
    }

    private sealed class PackedScienceFactAttribute : FactAttribute
    {
        public PackedScienceFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SPACETANGENT_PACKED_SCIENCE")) ||
                string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SPACETANGENT_SCIENCE_ROOT")))
                Skip = "Set the original source science root and the archived packed installation.";
        }
    }
}
