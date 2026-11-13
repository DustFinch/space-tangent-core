// SPDX-License-Identifier: GPL-2.0-or-later
using System.Text.Json;
using SpaceTangent.Contracts;
using SpaceTangent.Core.Addons;
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
