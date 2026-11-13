// SPDX-License-Identifier: GPL-2.0-or-later
using System.Text.Json;
using SpaceTangent.Contracts;
using SpaceTangent.Core.Addons;
using SpaceTangent.Core.World;
using Xunit;

namespace SpaceTangent.Core.Tests;

public sealed class SpiceCkOracleTests
{
    [OracleFact]
    [Trait("Category", "ExternalData")]
    public void LocalCkFramesMatchNativePointingAndAngularRates()
    {
        string root = Environment.GetEnvironmentVariable("SPACETANGENT_CK_ORACLE")!;
        using var oracle = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "ck-reference.json")));
        using var rates = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "ck-rates-oracle.json")));
        var samples = oracle.RootElement.GetProperty("samples").EnumerateArray().ToArray();
        var rateSamples = rates.RootElement.EnumerateArray().ToDictionary(row => (Word(row, "group"), Number(row, "seconds")));
        Assert.Equal(440, samples.Length);
        Assert.Equal(samples.Length, rateSamples.Count);
        int validatedRates = 0;
        foreach (var group in samples.GroupBy(row => Word(row, "group")))
        {
            var frames = Load(root, group.Key);
            foreach (var row in group)
            {
                double seconds = Number(row, "seconds");
                var frame = frames[Word(row, "frame")].Sample(JulianInstant.FromTdbSeconds(seconds));
                Assert.True(frame.HasValue == row.GetProperty("found").GetBoolean(), $"{group.Key} coverage at {seconds:R}");
                if (frame is not { } attitude)
                    continue;
                Compare(row, attitude, group.Key == "original-Cassini" ? 2e-9 : 2e-13, group.Key);
                JsonElement nativeRate = rateSamples[(group.Key, seconds)];
                if (nativeRate.GetProperty("found").GetBoolean())
                {
                    var native = nativeRate.GetProperty("angularVelocity");
                    Vector3d expected = new(native[0].GetDouble(), native[1].GetDouble(), native[2].GetDouble());
                    Assert.True(attitude.AngularVelocityRadiansPerSecond is { } actual &&
                        (actual - expected).Length <= 2e-10, $"{group.Key} angular velocity at {seconds:R}: {attitude.AngularVelocityRadiansPerSecond}, expected {expected}");
                    validatedRates++;
                }
                else
                    Assert.Null(attitude.AngularVelocityRadiansPerSecond);
            }
        }
        Assert.Equal(139, validatedRates);

        var staticFrames = Load(root, "static-frames");
        foreach (JsonElement row in oracle.RootElement.GetProperty("frameSamples").EnumerateArray())
        {
            var sample = staticFrames[Word(row, "frame")].Sample(JulianInstant.FromTdbSeconds(Number(row, "seconds")));
            Assert.True(sample.HasValue, row.ToString());
            Compare(row, sample.GetValueOrDefault(), 2e-13, Word(row, "frame"));
        }
    }

    [OracleFact]
    [Trait("Category", "ExternalData")]
    public void SpkSegmentsInCkAxesIncludeTheRateCrossPositionTerm()
    {
        string root = Environment.GetEnvironmentVariable("SPACETANGENT_CK_ORACLE")!;
        var mission = Assert.Single(SampledMissionCatalog.Load(["catalogue-ck-frame-state.json"], root, _ => new ObjectId("sol/sun")));
        var arc = Assert.Single(mission.Arcs);
        using var reference = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "ck-frame-state.json")));
        Assert.Equal(7, reference.RootElement.GetArrayLength());
        foreach (var row in reference.RootElement.EnumerateArray())
        {
            var expected = row.GetProperty("state");
            var state = arc.LocalState(JulianInstant.FromTdbSeconds(Number(row, "seconds")));
            Assert.True(state.HasValue);
            var actual = state.GetValueOrDefault();
            Assert.InRange((actual.PositionKilometers - new Vector3d(expected[0].GetDouble(), expected[1].GetDouble(), expected[2].GetDouble())).Length, 0, 1e-9);
            Assert.InRange((actual.VelocityKilometersPerSecond - new Vector3d(expected[3].GetDouble(), expected[4].GetDouble(), expected[5].GetDouble())).Length, 0, 1e-10);
        }
        Assert.Null(arc.LocalState(JulianInstant.FromTdbSeconds(30.000001)));
    }

    [OracleFact]
    [Trait("Category", "ExternalData")]
    public void NamedCassiniFixedFrameMatchesNativePointingInSourceAndPack()
    {
        string original = Environment.GetEnvironmentVariable("SPACETANGENT_CK_ORACLE")!;
        var scratch = Directory.CreateTempSubdirectory("st-named-frame-");
        try
        {
            string source = Directory.CreateDirectory(Path.Combine(scratch.FullName, "source")).FullName;
            foreach (string name in new[] { "cas00172.tsc", "naif0012.tls", "04182_04183pa_soi_itl.bc" })
                File.Copy(Path.Combine(original, name), Path.Combine(source, name));
            const string numeric = "TKFRAME_-82001_";
            const string named = "TKFRAME_CASSINI_SRU-A_";
            string frames = File.ReadAllText(Path.Combine(original, "cas_v43.tf"));
            Assert.Contains(numeric + "SPEC", frames);
            File.WriteAllText(Path.Combine(source, "cas_v43.tf"), frames.Replace(numeric, named, StringComparison.Ordinal));
            File.WriteAllText(Path.Combine(source, "sensor.ssc"), """
                "Cassini SRU-A" "Sol/Saturn" {
                    FixedPosition [ 100 200 300 ]
                    BodyFrame { EquatorJ2000 {} }
                    SpiceRotation {
                        Frame "CASSINI_SRU-A" BaseFrame "J2000"
                        Kernel [ "cas_v43.tf" "cas00172.tsc" "naif0012.tls" "04182_04183pa_soi_itl.bc" ]
                    }
                }
                """);
            Verify(SscBodyCatalogue.Load(source));
            string packed = Path.Combine(scratch.FullName, "packed");
            Assert.Equal(1, SscBodyCatalogue.BuildPack(source, packed, _ => { }));
            Verify(SscBodyCatalogue.Load(packed));
        }
        finally
        {
            scratch.Delete(recursive: true);
        }

        static void Verify(IReadOnlyList<SscBodyDefinition> bodies)
        {
            var sample = Assert.Single(bodies).SampleRotation!;
            // CSPICE N0067 sxform, CASSINI_SRU-A to J2000, using the original FK and CK.
            Check(141912000, new(-.29818829848912765, .9234146414267265, .2416384461192152),
                new(-.9522948916092561, -.3050318781430439, -.009486449886131688),
                new(-.00019881138584362614, .0003027122205873244, .0002063032124288863));
            Check(141912324, new(-.29956797445865907, .9224392927456937, .24364888646895339),
                new(-.950455265467379, -.31076026674334917, .007927481247956761),
                new(.000020125434241992995, -.00004376917211805261, .000009493242369709686));
            Check(141913944, new(-.30012149026668167, .9227464875223685, .24179745831012625),
                new(-.9501700020021174, -.31158141021778823, .009694952397523831),
                new(-.0000013721825906598902, .00003828190977879471, .00002166441799417201));

            void Check(double seconds, Vector3d x, Vector3d y, Vector3d rate)
            {
                var actual = Assert.IsType<FrameKinematics>(sample(JulianInstant.FromTdbSeconds(seconds)));
                Assert.InRange((actual.ToWorld.X - x).Length, 0, 2e-9);
                Assert.InRange((actual.ToWorld.Y - y).Length, 0, 2e-9);
                Assert.InRange((actual.AngularVelocityRadiansPerSecond!.Value - rate).Length, 0, 2e-10);
            }
        }
    }

    private static Dictionary<string, MissionFrame.Spice> Load(string root, string group) =>
        SampledMissionCatalog.Load([$"catalogue-{group}.json"], root, _ => new ObjectId("sol/sun"))
            .ToDictionary(mission => mission.Name, mission => Assert.IsType<MissionFrame.Spice>(Assert.Single(mission.Arcs).BodyFrame));

    private static void Compare(JsonElement sample, FrameKinematics actual, double tolerance, string name)
    {
        JsonElement matrix = sample.GetProperty("matrix");
        Basis3d fromWorld = actual.ToWorld.Transpose();
        Vector3d[] columns = [fromWorld.X, fromWorld.Y, fromWorld.Z];
        for (int axis = 0; axis < 3; axis++)
        {
            var expected = new Vector3d(matrix[axis].GetDouble(), matrix[axis + 3].GetDouble(), matrix[axis + 6].GetDouble());
            double error = (columns[axis] - expected).Length;
            Assert.True(error <= tolerance, $"{name} at {Number(sample, "seconds"):R}, axis {axis}: {error:R}");
        }
    }

    private static string Word(JsonElement item, string key) => item.GetProperty(key).GetString()!;
    private static double Number(JsonElement item, string key) => item.GetProperty(key).GetDouble();

    private sealed class OracleFactAttribute : FactAttribute
    {
        public OracleFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SPACETANGENT_CK_ORACLE")))
                Skip = "Set the archived native CK oracle directory.";
        }
    }
}
