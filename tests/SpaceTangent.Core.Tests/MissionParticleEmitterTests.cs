// SPDX-License-Identifier: GPL-2.0-or-later
using System.Security.Cryptography;
using System.Text.Json;
using SpaceTangent.Contracts;
using SpaceTangent.Core.Addons;
using SpaceTangent.Core.World;
using Xunit;

namespace SpaceTangent.Core.Tests;

public sealed class MissionParticleEmitterTests
{
    [ParticleCorpusFact]
    [Trait("Category", "ExternalData")]
    public void ArchivedGeneratorsAndOriginalPlumeMatchNativeSamples()
    {
        string root = Environment.GetEnvironmentVariable("SPACETANGENT_PARTICLE_ORACLE")!;
        using var reference = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(root, "particle-reference.json")));
        var samples = reference.RootElement.GetProperty("samples");
        Assert.Equal(170, samples.GetArrayLength());
        int verified = 0;
        foreach (JsonElement row in samples.EnumerateArray())
        {
            var emitter = MakeEmitter(row);
            double epoch = row.GetProperty("jd").GetDouble();
            var actual = new List<MissionParticle>();
            Assert.Equal(row.GetProperty("count").GetInt32(), emitter.Sample(epoch, 2048, actual));
            double[] values = Numbers(row, "particles");
            Assert.Equal(values.Length / 11, actual.Count);
            for (int index = 0; index < actual.Count; index++)
                Compare(values, 11 * index, actual[index]);
            var reverse = new List<MissionParticle>();
            emitter.Sample(epoch + 1, 2048, reverse);
            reverse.Clear();
            emitter.Sample(epoch, 2048, reverse);
            Assert.Equal(actual, reverse);
            if (actual.Count > 4)
            {
                var thinned = new List<MissionParticle>();
                Assert.Equal(actual.Count, emitter.Sample(epoch, 4, thinned));
                Assert.InRange(thinned.Count, 1, 4);
                Assert.All(thinned, sample => Assert.Contains(actual, value => (value.Position - sample.Position).Length < 1e-4));
            }
            verified += actual.Count;
        }
        Assert.Equal(1470, verified);

        string assets = Environment.GetEnvironmentVariable("SPACETANGENT_PARTICLE_SOURCE")!;
        var missions = SampledMissionCatalog.Load(Directory.GetFiles(assets, "*.json"), assets,
            name => new ObjectId("body/" + name.ToLowerInvariant()));
        Assert.Equal(3, missions.Count);
        var imported = Assert.Single(Assert.Single(missions.Single(mission => mission.Name == "Tvashtar").Geometry!).Shape.ParticleEmitters!);
        Assert.Equal(MissionParticleGenerator.Box, imported.Generator);
        Assert.Equal(5, imported.SpawnRate);
        Assert.Equal(400, imported.LifetimeSeconds);
        Assert.Equal("69E2B9DB6AF7F0B0FF5BBD992602DB216393E35A113F802781B38D685A748D2E",
            Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(imported.TexturePath!))));
        foreach (var row in reference.RootElement.GetProperty("originalTvashtar").GetProperty("samples").EnumerateArray())
        {
            var particles = new List<MissionParticle>();
            Assert.Equal(row.GetProperty("count").GetInt32(), imported.Sample(row.GetProperty("jd").GetDouble(), 8192, particles));
            double[] values = Numbers(row, "particles");
            var indices = row.GetProperty("indices").EnumerateArray().Select(value => value.GetInt32()).ToArray();
            for (int index = 0; index < indices.Length; index++)
                Compare(values, 11 * index, particles[indices[index]]);
        }
        var aurora = Assert.Single(Assert.Single(missions.Single(mission => mission.Name == "Aurora").Geometry!).Shape.ParticleEmitters!);
        Assert.Equal(20, aurora.Strip.Count);
        var example = Assert.Single(Assert.Single(missions.Single(mission => mission.Name == "Particle test").Geometry!).Shape.ParticleEmitters!);
        Assert.Equal(new(1, 1, 0), example.Colors[0].Color);
    }

    [ParticleCorpusFact(packed: true)]
    [Trait("Category", "ExternalData")]
    public void ArchivedPackedPlumesPreserveTheSourceStatesAndTextures()
    {
        string assets = Environment.GetEnvironmentVariable("SPACETANGENT_PARTICLE_SOURCE")!;
        string packs = Environment.GetEnvironmentVariable("SPACETANGENT_PARTICLE_PACKS")!;
        var io = new ObjectId("sol/io");
        var original = Assert.Single(SampledMissionCatalog.Load("tvashtar.json", assets, Resolve));
        foreach (string folder in packs.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var packed = Assert.Single(SampledMissionCatalog.Load("pack.json", folder, Resolve));
            Assert.Equal(original.Id, packed.Id);
            Assert.Equal(original.Start, packed.Start);
            var a = Assert.Single(Assert.Single(original.Geometry!).Shape.ParticleEmitters!);
            var b = Assert.Single(Assert.Single(packed.Geometry!).Shape.ParticleEmitters!);
            Assert.Equal(File.ReadAllBytes(a.TexturePath!), File.ReadAllBytes(b.TexturePath!));

            foreach (double date in new[] { 2455755.0, 2455755.009838, 2455755.04 })
            {
                var epoch = JulianInstant.FromJulianDate(date, TimeScale.Tdb);
                var source = World(original).Evaluate(epoch);
                var binary = World(packed).Evaluate(epoch);
                Assert.True(source.TryState(original.Id, out var state));
                Assert.True(binary.TryState(packed.Id, out var packedState));
                Assert.InRange((state.PositionKilometers - packedState.PositionKilometers).Length, 0, 1e-8);
                Assert.InRange((state.VelocityKilometersPerSecond - packedState.VelocityKilometersPerSecond).Length, 0, 1e-12);
                Assert.True(source.TryOrientation(original.Id, out var attitude));
                Assert.True(binary.TryOrientation(packed.Id, out var packedAttitude));
                Assert.InRange((attitude.ToWorld.Y - packedAttitude.ToWorld.Y).Length, 0, 1e-12);
                Assert.InRange((attitude.ToWorld.Z - packedAttitude.ToWorld.Z).Length, 0, 1e-12);
                var first = new List<MissionParticle>();
                var second = new List<MissionParticle>();
                Assert.Equal(a.Sample(date, 8192, first), b.Sample(date, 8192, second));
                Assert.Equal(first, second);
            }
        }

        ObjectId? Resolve(string name) => name.Equals("Io", StringComparison.OrdinalIgnoreCase) ? io : null;
        WorldGraph World(SampledMission mission)
        {
            var world = new WorldGraphBuilder();
            world.AddTrajectory(io, _ => new(new(1e7, -2e7, 3e6), default));
            world.AddOrientation(io, _ => new(new(new(0, 1, 0), new(-1, 0, 0), new(0, 0, 1)), new Vector3d(0, 0, .0001)));
            world.AddSampledMission(mission);
            return world.Build();
        }
    }

    private static MissionParticleEmitter MakeEmitter(JsonElement row)
    {
        double[] c = Numbers(row, "config"), g = Numbers(row, "generatorData");
        return new()
        {
            Generator = (MissionParticleGenerator)row.GetProperty("generator").GetInt32(),
            Center = Vector(g, 0), Velocity = Vector(g, 3), Sides = Vector(g, 6), DiscRadiusKilometers = g[9],
            Strip = Enumerable.Range(0, 3).Select(index => new MissionParticleBirth(Vector(g, 10 + 6 * index), Vector(g, 13 + 6 * index))).ToArray(),
            Arcs = Enumerable.Range(0, 3).Select(index => new MissionParticleArc(Vector(g, 10 + 6 * index), g[13 + 6 * index])).ToArray(),
            StartJulianDate = 2451545 + c[0] / 86400, EndJulianDate = 2451545 + c[1] / 86400,
            SpawnRate = c[2], LifetimeSeconds = c[3], StartSize = c[4], EndSize = c[5], VelocityVariation = c[6], Force = Vector(c, 7),
            Colors = Enumerable.Range(0, 3).Select(index => new MissionParticleColor(Vector(c, 10 + 4 * index), c[13 + 4 * index])).ToArray()
        };
    }

    private static double[] Numbers(JsonElement row, string key) => row.GetProperty(key).EnumerateArray().Select(value => value.GetDouble()).ToArray();
    private static Vector3d Vector(double[] values, int index) => new(values[index], values[index + 1], values[index + 2]);

    private static void Compare(double[] values, int at, MissionParticle actual)
    {
        Close(values[at], actual.Position.X); Close(values[at + 1], actual.Position.Y); Close(values[at + 2], actual.Position.Z);
        Close(values[at + 3], actual.Velocity.X); Close(values[at + 4], actual.Velocity.Y); Close(values[at + 5], actual.Velocity.Z);
        Close(values[at + 6], actual.HalfSizeKilometers);
        Close(values[at + 7], actual.Color.X); Close(values[at + 8], actual.Color.Y); Close(values[at + 9], actual.Color.Z);
        Close(values[at + 10], actual.Opacity);
    }

    private static void Close(double expected, double actual) =>
        Assert.True(Math.Abs(actual - expected) <= 2e-7 + Math.Abs(expected) * 5e-7, $"Expected {expected:R}; actual {actual:R}.");

    private sealed class ParticleCorpusFactAttribute : FactAttribute
    {
        public ParticleCorpusFactAttribute(bool packed = false)
        {
            if (Environment.GetEnvironmentVariable("SPACETANGENT_PARTICLE_ORACLE") is null ||
                Environment.GetEnvironmentVariable("SPACETANGENT_PARTICLE_SOURCE") is null ||
                packed && Environment.GetEnvironmentVariable("SPACETANGENT_PARTICLE_PACKS") is null)
                Skip = "Set the native oracle and original particle catalogue directories.";
        }
    }
}
