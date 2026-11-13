// SPDX-License-Identifier: GPL-2.0-or-later
using System.Runtime.InteropServices;
using System.Text.Json;
using SpaceTangent.Contracts;
using SpaceTangent.Core.Addons;
using Xunit;

namespace SpaceTangent.Core.Tests;

public sealed class MissionSwarmTests
{
    [SwarmCorpusFact]
    [Trait("Category", "ExternalData")]
    public void ArchivedSwarmAttributesMatchTheIndependentNativeOracle()
    {
        string root = Environment.GetEnvironmentVariable("SPACETANGENT_SWARM_CORPUS")!;
        using var reference = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(root, "swarm-native-reference.json")));
        byte[] expected = File.ReadAllBytes(Path.Combine(root, "swarm-native-objects.bin"));
        int count = 0;
        foreach (var catalogue in reference.RootElement.GetProperty("catalogues").EnumerateArray())
        {
            var format = Enum.Parse<MissionSwarmFormat>(catalogue.GetProperty("format").GetString()!, ignoreCase: true);
            string file = catalogue.GetProperty("file").GetString()!;
            using var stream = File.OpenRead(Path.Combine(root, file));
            var swarm = MissionSwarm.Read(stream, format);
            Assert.Equal(catalogue.GetProperty("count").GetInt32(), swarm.Count);
            Assert.Equal(catalogue.GetProperty("epochSeconds").GetDouble(), swarm.EpochSeconds);
            Assert.Equal(catalogue.GetProperty("bound").GetSingle(), swarm.BoundingRadiusKilometers);
            var oracle = MemoryMarshal.Cast<byte, float>(expected.AsSpan(catalogue.GetProperty("offsetBytes").GetInt32(),
                catalogue.GetProperty("attributesBytes").GetInt32()));
            Assert.Equal(oracle.Length, swarm.Attributes.Length);
            for (int i = 0; i < oracle.Length; i++)
            {
                float actual = swarm.Attributes[i];
                if (BitConverter.SingleToInt32Bits(oracle[i]) != BitConverter.SingleToInt32Bits(actual))
                    Assert.Fail($"{file}, object {i / MissionSwarm.FieldsPerObject}, field {i % MissionSwarm.FieldsPerObject}: {oracle[i]:R} != {actual:R}");
            }
            count += swarm.Count;
        }
        Assert.Equal(10_834, count);
    }

    [SwarmInstallFact]
    [Trait("Category", "ExternalData")]
    public void OriginalSourceAndPackedInstallsUseAllThreeSwarms()
    {
        string root = Environment.GetEnvironmentVariable("SPACETANGENT_SWARM_INSTALL")!;
        string sourceRoot = Path.Combine(root, "source-addons", "swarms");
        string packedRoot = Path.Combine(root, "packed-addons", "swarms");
        var warnings = new List<string>();
        var source = SampledMissionCatalog.Load(Directory.GetFiles(sourceRoot, "*.json"), sourceRoot, Resolve, warnings.Add);
        var packed = SampledMissionCatalog.Load("pack.json", packedRoot, Resolve, warnings.Add);
        Assert.Empty(warnings);
        Assert.Equal(3, source.Count);
        Assert.Equal(3, packed.Count);
        foreach (var mission in source)
        {
            var archived = Assert.Single(packed, item => item.Name == mission.Name);
            var left = Assert.IsType<MissionSwarmStyle>(Assert.Single(mission.Geometry!).Shape.Swarm);
            var right = Assert.IsType<MissionSwarmStyle>(Assert.Single(archived.Geometry!).Shape.Swarm);
            Assert.Equal(left with { SourcePath = right.SourcePath, Elements = right.Elements }, right);
            Assert.True(left.Elements.Attributes.SequenceEqual(right.Elements.Attributes));
            Assert.Equal(Assert.Single(mission.Arcs).Center, Assert.Single(archived.Arcs).Center);
        }

        static ObjectId? Resolve(string name) => name switch
        {
            "Sun" or "Sol" => new("sun"),
            "Earth" or "Sol/Earth" => new("earth"),
            _ => null
        };
    }

    private sealed class SwarmInstallFactAttribute : FactAttribute
    {
        public SwarmInstallFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SPACETANGENT_SWARM_INSTALL")))
                Skip = "Set the original source and packed swarm installations.";
        }
    }

    private sealed class SwarmCorpusFactAttribute : FactAttribute
    {
        public SwarmCorpusFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SPACETANGENT_SWARM_CORPUS")))
                Skip = "Set the original swarm inputs and native attribute oracle.";
        }
    }
}
