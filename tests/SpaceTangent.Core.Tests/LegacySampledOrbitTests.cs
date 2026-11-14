// SPDX-License-Identifier: GPL-2.0-or-later
using System.Globalization;
using System.Security.Cryptography;
using SpaceTangent.Contracts;
using SpaceTangent.Core.Addons;
using SpaceTangent.Core.World;
using Xunit;

namespace SpaceTangent.Core.Tests;

public sealed class LegacySampledOrbitTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MatchesNativeSinglePrecisionStatesInLooseAndPackedCatalogues(bool packed)
    {
        var root = Directory.CreateTempSubdirectory("st-legacy-samples-");
        try
        {
            string source = root.CreateSubdirectory("source").FullName;
            string fixtures = Path.Combine(AppContext.BaseDirectory, "Fixtures");
            foreach (string extension in new[] { "xyz", "xyzv" })
                File.Copy(Path.Combine(fixtures, $"sampled-orbit-legacy.{extension}"), Path.Combine(source, $"legacy.{extension}"));
            File.WriteAllText(Path.Combine(source, "world.ssc"), """
                "Legacy XYZ" "Sol" {
                    OrbitFrame { EquatorJ2000 {} }
                    SampledOrbit "legacy.xyz"
                    FixedPosition [ 99 0 0 ]
                }
                "Legacy XYZV" "Sol" {
                    OrbitFrame { EquatorJ2000 {} }
                    SampledOrbit "legacy.xyzv"
                }
                "Phased" "Sol" {
                    Timeline [
                        { Ending 2451548.5 OrbitFrame { EquatorJ2000 {} } SampledOrbit "legacy.xyzv" }
                        { Ending 2451549 OrbitFrame { EquatorJ2000 {} } SampledTrajectory { Source "legacy.xyzv" } }
                    ]
                }
                "Modern" "Sol" {
                    OrbitFrame { EquatorJ2000 {} }
                    SampledTrajectory { Source "legacy.xyzv" }
                    SampledOrbit "missing-unused.xyz"
                }
                "Fallback" "Sol" {
                    OrbitFrame { EquatorJ2000 {} }
                    SampledOrbit "bad.xyz"
                    FixedPosition [ 99 0 0 ]
                }
                """);
            File.WriteAllText(Path.Combine(source, "bad.xyz"), "2451545 1 2\n3\n");
            var warnings = new List<string>();
            string pack = Path.Combine(root.FullName, "pack");
            if (packed)
            {
                Assert.Equal(5, SscBodyCatalogue.BuildPack(source, pack, warnings.Add));
                Assert.Single(warnings);
                warnings.Clear();
            }
            var bodies = SscBodyCatalogue.Load(packed ? pack : source, warning: warnings.Add);
            Assert.Contains("SampledOrbit initialization failed: bad.xyz", Assert.Single(warnings));
            var phased = bodies.Single(body => body.Name == "Phased");
            var sun = new ObjectId("sun");
            var target = new ObjectId("probe");
            var builder = new WorldGraphBuilder();
            builder.AddTrajectory(sun, _ => new CartesianState(default, default));
            builder.AddSampledMission(phased.BindTimeline!(target, _ => sun));
            WorldGraph graph = builder.Build();

            foreach (string line in File.ReadLines(Path.Combine(fixtures, "sampled-orbit-reference.csv")).Skip(1))
            {
                string[] fields = line.Split(',');
                bool xyzv = fields[0] == "legacy.xyzv";
                var body = bodies.Single(body => body.Name == (xyzv ? "Legacy XYZV" : "Legacy XYZ"));
                var data = fields.Skip(1).Select(field => double.Parse(field, CultureInfo.InvariantCulture)).ToArray();
                var epoch = Epoch(data[0]);
                var position = new Vector3d(data[1], data[2], data[3]);
                var velocity = new Vector3d(data[4], data[5], data[6]);
                Check(body.Sample(epoch)!.Value);
                if (xyzv)
                {
                    Assert.True(graph.Evaluate(epoch).TryState(target, out var state));
                    Check(state);
                }

                void Check(CartesianState state)
                {
                    Assert.InRange((state.PositionKilometers - position).Length, 0, 1e-7);
                    Assert.InRange((state.VelocityKilometersPerSecond - velocity).Length, 0, 1e-11);
                }
            }

            Assert.Equal(16_777_217, bodies.Single(body => body.Name == "Modern").Sample(Epoch(2451545))!.Value.PositionKilometers.X);
            Assert.True(graph.Evaluate(Epoch(2451548.5)).TryState(target, out var modern));
            Assert.Equal(16_777_999, modern.PositionKilometers.X);
            Assert.Equal(99, bodies.Single(body => body.Name == "Fallback").Sample(Epoch(2451545))!.Value.PositionKilometers.X);
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [ArchivedCruisePackFact]
    [Trait("Category", "ExternalData")]
    public void ReadsTheVersionEighteenCruisePreviewWithoutItsEmbeddedSource()
    {
        string original = Environment.GetEnvironmentVariable("SPACETANGENT_ARCHIVED_SAMPLED_ORBIT")!;
        string manifest = Path.Combine(original, "pack.json");
        var preview = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllBytes(manifest))!.AsObject();
        Assert.Equal(18, preview["formatVersion"]!.GetValue<int>());
        Assert.True(preview.Remove("sscCatalogs"));
        var source = Assert.Single(SscBodyCatalogue.Load(original));

        var temp = Directory.CreateTempSubdirectory("st-archived-cruise-");
        try
        {
            string trajectories = temp.CreateSubdirectory("trajectories").FullName;
            string name = "cruise.xyzv";
            File.Copy(Path.Combine(original, "trajectories", name), Path.Combine(trajectories, name));
            File.WriteAllText(Path.Combine(temp.FullName, "pack.json"), preview.ToJsonString());
            var sun = new ObjectId("sun");
            var warnings = new List<string>();
            var mission = Assert.Single(SampledMissionCatalog.Load("pack.json", temp.FullName,
                body => body is "Sol" ? sun : null, warnings.Add));
            Assert.Empty(warnings);
            Assert.Equal("Mars Cruise", mission.Name);
            var builder = new WorldGraphBuilder();
            builder.AddTrajectory(sun, _ => default(CartesianState));
            builder.AddSampledMission(mission);
            var graph = builder.Build();
            foreach (double day in new[] { 2455892.16126, 2455893.25, 2456000.125, 2456145.7287, 2456200d })
            {
                var epoch = Epoch(day);
                Assert.True(graph.Evaluate(epoch).TryState(mission.Id, out var actual));
                Assert.Equal(source.Sample(epoch), actual);
            }
        }
        finally { temp.Delete(recursive: true); }
    }

    private sealed class ArchivedCruisePackFactAttribute : FactAttribute
    {
        public ArchivedCruisePackFactAttribute()
        {
            if (!Directory.Exists(Environment.GetEnvironmentVariable("SPACETANGENT_ARCHIVED_SAMPLED_ORBIT")))
                Skip = "Set the version eighteen cruise pack.";
        }
    }


    [OriginalCruiseFact]
    [Trait("Category", "ExternalData")]
    public void OriginalMarsCruiseFileMatchesNativeSinglePrecisionThroughAnAdaptedCatalogue()
    {
        string file = Environment.GetEnvironmentVariable("SPACETANGENT_MSL_XYZV")!;
        Assert.Equal("E0BCB09E212880D1335D83D5EB873FF18C4198FF6815813CE0225ED039FA32DD",
            Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file))));
        var root = Directory.CreateTempSubdirectory("st-legacy-cruise-");
        try
        {
            File.Copy(file, Path.Combine(root.FullName, "cruise.xyzv"));
            File.WriteAllText(Path.Combine(root.FullName, "cruise.ssc"), """
                "Mars Cruise" "Sol" { OrbitFrame { EquatorJ2000 {} } SampledOrbit "cruise.xyzv" }
                """);
            var orbit = Assert.Single(SscBodyCatalogue.Load(root.FullName));

            // Original native sampler f90fa68f, converted from native axes to standard XYZ.
            Check(2455892.16126, new(65032684, 132559072, -10953.837890625), default);
            Check(2455893.25, new(62237624.713651903, 134048993.86066411, -132663.86168809657),
                new(-29.614008187175738, 15.168681982734622, -1.0522395218920289));
            Check(2456000.125, new(-180231685.38283208, 64388245.538193651, -5515181.3032033276),
                new(-13.94343503005866, -22.091707900390965, -0.068850700084386887));
            Check(2456145.7287, new(-129376400, -189913888, -801889.9375),
                new(20.743297164351855, -11.671239872685186, -0.66514390733506956));
            Check(2456200, new(-129376400, -189913888, -801889.9375), default);

            void Check(double day, Vector3d position, Vector3d velocity)
            {
                var state = orbit.Sample(Epoch(day))!.Value;
                Assert.InRange((state.PositionKilometers - position).Length, 0, 1e-7);
                Assert.InRange((state.VelocityKilometersPerSecond - velocity).Length, 0, 1e-11);
            }
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    private sealed class OriginalCruiseFactAttribute : FactAttribute
    {
        public OriginalCruiseFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SPACETANGENT_MSL_XYZV")))
                Skip = "Set SPACETANGENT_MSL_XYZV to the original Mars cruise trajectory.";
        }
    }

    private static JulianInstant Epoch(double day) => JulianInstant.FromJulianDate(day, TimeScale.Tdb);
}
