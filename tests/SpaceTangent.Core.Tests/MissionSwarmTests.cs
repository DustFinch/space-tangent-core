// SPDX-License-Identifier: GPL-2.0-or-later
using System.Runtime.InteropServices;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using SpaceTangent.Contracts;
using SpaceTangent.Core.Addons;
using Xunit;

namespace SpaceTangent.Core.Tests;

public sealed class MissionSwarmTests
{
    [Fact]
    public void FrozenDiscoveryOracleHasItsPinnedLengthAndContent()
    {
        byte[] bytes = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "astorb-discovery-reference.bin"));
        using var metadata = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "astorb-discovery-reference.json")));
        Assert.Equal(metadata.RootElement.GetProperty("count").GetInt32() * 4, bytes.Length);
        Assert.Equal(metadata.RootElement.GetProperty("sha256").GetString(), Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
    }

    [Fact]
    public void AstorbEpochsStartAtMidnightTerrestrialTime()
    {
        // An ASTORB record dated 2000-01-01 refers to 0h TT, half a day before J2000 (JD 2451545.0 TT).
        var line = new char[200];
        Array.Fill(line, ' ');
        void Put(int offset, string value) => value.CopyTo(0, line, offset, value.Length);
        Put(0, "     1");
        Put(7, "Ceres");
        Put(106, "20000101");
        Put(115, "  10.00000");
        Put(126, "  80.00000");
        Put(137, "  70.00000");
        Put(148, " 10.00000");
        Put(158, "0.07600000");
        Put(169, "   2.7660000");
        using var stream = new MemoryStream(System.Text.Encoding.ASCII.GetBytes(new string(line).TrimEnd() + "\n"));
        var swarm = MissionSwarm.Read(stream, MissionSwarmFormat.Astorb);
        Assert.Equal(1, swarm.Count);
        Assert.InRange(swarm.EpochSeconds, -43_200.01, -43_199.99);
    }

    [SwarmCorpusFact]
    [Trait("Category", "ExternalData")]
    public void ArchivedSwarmAttributesMatchTheIndependentNativeOracle()
    {
        string root = Environment.GetEnvironmentVariable("SPACETANGENT_SWARM_CORPUS")!;
        using var reference = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(root, "swarm-native-reference.json")));
        byte[] expected = File.ReadAllBytes(Path.Combine(root, "swarm-native-objects.bin"));
        byte[] discoveryReference = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "astorb-discovery-reference.bin"));
        int count = 0;
        var epochs = File.ReadLines(Path.Combine(AppContext.BaseDirectory, "Fixtures", "astorb-epoch-reference.csv"))
            .Where(line => !line.StartsWith('#')).Select(line => line.Split(','))
            .ToDictionary(fields => fields[0], fields => double.Parse(fields[1], System.Globalization.CultureInfo.InvariantCulture));
        foreach (var catalogue in reference.RootElement.GetProperty("catalogues").EnumerateArray())
        {
            var format = Enum.Parse<MissionSwarmFormat>(catalogue.GetProperty("format").GetString()!, ignoreCase: true);
            string file = catalogue.GetProperty("file").GetString()!;
            using var stream = File.OpenRead(Path.Combine(root, file));
            var swarm = MissionSwarm.Read(stream, format);
            Assert.Equal(catalogue.GetProperty("count").GetInt32(), swarm.Count);
            var sourceLines = format == MissionSwarmFormat.Astorb
                ? File.ReadLines(Path.Combine(root, file)).Where(line => line.Length > 0).ToArray() : [];
            double expectedEpoch = format == MissionSwarmFormat.Astorb ? epochs[sourceLines[0].Substring(106, 8)]
                : catalogue.GetProperty("epochSeconds").GetDouble();
            if (format == MissionSwarmFormat.Astorb)
                Assert.InRange(swarm.EpochSeconds - expectedEpoch, -.001, .001); // Compact TDB-TT series versus ERFA.
            else
                Assert.Equal(expectedEpoch, swarm.EpochSeconds);
            Assert.Equal(catalogue.GetProperty("bound").GetSingle(), swarm.BoundingRadiusKilometers);
            var oracle = MemoryMarshal.Cast<byte, float>(expected.AsSpan(catalogue.GetProperty("offsetBytes").GetInt32(),
                catalogue.GetProperty("attributesBytes").GetInt32()));
            Assert.Equal(oracle.Length, swarm.Attributes.Length);
            for (int i = 0; i < oracle.Length; i++)
            {
                float actual = swarm.Attributes[i];
                if (format == MissionSwarmFormat.Astorb && i % MissionSwarm.FieldsPerObject == 8)
                {
                    // Frozen native addObject output after applying the independent ERFA midnight-TT epochs.
                    int bits = BinaryPrimitives.ReadInt32LittleEndian(discoveryReference.AsSpan(i / MissionSwarm.FieldsPerObject * 4));
                    Assert.Equal(bits, BitConverter.SingleToInt32Bits(actual));
                    continue;
                }
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

    [SwarmInstallFact]
    [Trait("Category", "ExternalData")]
    public void MixedPackResolvesInstalledScienceBodyAndKeepsNativeSwarmAxes()
    {
        string root = Environment.GetEnvironmentVariable("SPACETANGENT_SWARM_INSTALL")!;
        var satellite = new ObjectId("science/sorce");
        var earth = new ObjectId("earth");
        var warnings = new List<string>();
        string packedRoot = Path.Combine(root, "packed-addons", "science");
        var packed = SampledMissionCatalog.Load("pack.json", packedRoot, Resolve, warnings.Add);
        Assert.Empty(warnings);
        Assert.Equal(2, packed.Count);

        string sourceRoot = Path.Combine(root, "source-addons", "science");
        var source = SampledMissionCatalog.Load("swarm-native-verification.json", sourceRoot, Resolve, warnings.Add);
        Assert.Empty(warnings);
        Assert.Equal(2, source.Count);

        foreach (var mission in packed)
        {
            var original = Assert.Single(source, item => item.Name == mission.Name);
            var originalStyle = Assert.IsType<MissionSwarmStyle>(Assert.Single(original.Geometry!).Shape.Swarm);
            var style = Assert.IsType<MissionSwarmStyle>(Assert.Single(mission.Geometry!).Shape.Swarm);
            Assert.Equal(2740, style.Elements.Count);
            Assert.True(style.Elements.Attributes.SequenceEqual(originalStyle.Elements.Attributes));
            Assert.False(originalStyle.ReverseEquatorialAxes);
            var arc = Assert.Single(mission.Arcs);
            Assert.Equal(earth, arc.Center);
            bool natural = mission.Name == "NativeSwarmNatural";
            Assert.Equal(natural, style.ReverseEquatorialAxes);
            if (!natural)
                Assert.Equal(satellite, Assert.IsType<MissionFrame.BodyFixed>(
                    Assert.IsType<MissionFrame.Rotating>(arc.BodyFrame).Parent).Body);
        }

        ObjectId? Resolve(string name) => name switch
        {
            "Earth" or "Sol/Earth" => earth,
            "SORCE" => satellite,
            "Jason-1" => new("science/jason"),
            _ => null
        };
    }

    [LegacySwarmPackFact]
    [Trait("Category", "ExternalData")]
    public void OriginalVersionFivePackLoadsAllThreeStandardSwarms()
    {
        string root = Environment.GetEnvironmentVariable("SPACETANGENT_LEGACY_SWARM_PACK")!;
        var diagnostics = new List<string>();
        var sun = new ObjectId("sol/sun");
        var earth = new ObjectId("sol/earth");
        var missions = SampledMissionCatalog.Load("pack.json", root, name => name switch
        {
            "Sol" => sun,
            "Sol/Earth" => earth,
            _ => null
        }, diagnostics.Add);
        Assert.Empty(diagnostics);
        Assert.Equal(new[] { "Original ASTORB", "Fengyun 1C Debris", "Hilda Family" },
            missions.Select(mission => mission.Name));
        Assert.Equal(new[] { sun, earth, sun }, missions.Select(mission => Assert.Single(mission.Arcs).Center));
        foreach (var mission in missions)
        {
            var swarm = Assert.IsType<MissionSwarmStyle>(Assert.Single(mission.Geometry!).Shape.Swarm);
            Assert.NotEmpty(swarm.Elements.Attributes.ToArray());
            Assert.False(swarm.ReverseEquatorialAxes);
        }
    }

    private sealed class LegacySwarmPackFactAttribute : FactAttribute
    {
        public LegacySwarmPackFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SPACETANGENT_LEGACY_SWARM_PACK")))
                Skip = "Set the unmodified version-five archive swarm pack.";
        }
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
