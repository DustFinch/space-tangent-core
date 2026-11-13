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

    [OracleFact]
    [Trait("Category", "ExternalData")]
    public void Itrf93UsesItsOwnTextPckCoefficientsByNameAndId()
    {
        string original = Environment.GetEnvironmentVariable("SPACETANGENT_CK_ORACLE")!;
        var scratch = Directory.CreateTempSubdirectory("st-itrf93-");
        try
        {
            string source = Directory.CreateDirectory(Path.Combine(scratch.FullName, "source")).FullName;
            File.Copy(Path.Combine(original, "pck00011.tpc"), Path.Combine(source, "pck00011.tpc"));
            File.WriteAllText(Path.Combine(source, "terrestrial.tpc"), """
                KPL/PCK
                \begindata
                BODY3000_POLE_RA = ( -90 0 0 )
                BODY3000_POLE_DEC = ( 90 0 0 )
                BODY3000_PM = ( 0 360 0 )
                \begintext
                """);
            string catalog = Path.Combine(source, "ground.ssc");
            File.WriteAllText(catalog, """
                "Named frame" "Sol/Earth" {
                    FixedPosition [ 7000 0 0 ]
                    BodyFrame { EquatorJ2000 {} }
                    SpiceRotation { Frame "itrf93" BaseFrame "J2000" Kernel [ "pck00011.tpc" "terrestrial.tpc" ] }
                }
                "Numeric frame" "Sol/Earth" {
                    FixedPosition [ 7100 0 0 ]
                    BodyFrame { EquatorJ2000 {} }
                    SpiceRotation { Frame "13000" BaseFrame "J2000" }
                }
                """);
            Verify(SscBodyCatalogue.Load(source));
            string packed = Path.Combine(scratch.FullName, "packed");
            Assert.Equal(2, SscBodyCatalogue.BuildPack(source, packed, _ => { }));
            Verify(SscBodyCatalogue.Load(packed));

            File.WriteAllText(catalog, File.ReadAllText(catalog).Replace(" \"terrestrial.tpc\"", "", StringComparison.Ordinal));
            Assert.Throws<InvalidDataException>(() => SscBodyCatalogue.Load(source));
        }
        finally
        {
            scratch.Delete(recursive: true);
        }

        static void Verify(IReadOnlyList<SscBodyDefinition> bodies)
        {
            Assert.Equal(2, bodies.Count);
            foreach (var body in bodies)
            foreach (double hours in new[] { 0d, 6, 12 })
            {
                double angle = hours * Math.Tau / 24;
                var epoch = JulianInstant.FromTdbSeconds(hours * 3600);
                var attitude = Assert.IsType<FrameKinematics>(body.SampleRotation!(epoch));
                Assert.InRange((attitude.ToWorld.X - new Vector3d(Math.Cos(angle), Math.Sin(angle), 0)).Length, 0, 1e-13);
                Assert.InRange((attitude.AngularVelocityRadiansPerSecond!.Value -
                    new Vector3d(0, 0, Math.Tau / 86400)).Length, 0, 1e-16);
            }
        }
    }

    [OracleFact]
    [Trait("Category", "ExternalData")]
    public void LegacyFixedFrameDefaultsMatchTheNativeExplicitFrame()
    {
        string original = Environment.GetEnvironmentVariable("SPACETANGENT_CK_ORACLE")!;
        var scratch = Directory.CreateTempSubdirectory("st-tk-defaults-");
        try
        {
            string source = Directory.CreateDirectory(Path.Combine(scratch.FullName, "source")).FullName;
            string kernel = File.ReadAllText(Path.Combine(original, "oracle.tf"));
            kernel = kernel.Replace("TKFRAME_1001001_RELATIVE = 'J2000'", "", StringComparison.Ordinal)
                .Replace("TKFRAME_1001001_UNITS = 'DEGREES'", "", StringComparison.Ordinal)
                .Replace("TKFRAME_1001001_SPEC = 'ANGLES'", "TKFRAME_1001001_SPEC = 'angles'", StringComparison.Ordinal)
                .Replace("TKFRAME_1001001_ANGLES = (13 -27 19)",
                    FormattableString.Invariant($"TKFRAME_1001001_ANGLES = ({13 * Math.PI / 180:R} {-27 * Math.PI / 180:R} {19 * Math.PI / 180:R})"),
                    StringComparison.Ordinal);
            File.WriteAllText(Path.Combine(source, "legacy.tf"), kernel);
            File.WriteAllText(Path.Combine(source, "probe.ssc"), """
                "Test frame" "Sol/Earth" {
                    FixedPosition [ 7000 0 0 ]
                    BodyFrame { EquatorJ2000 {} }
                    SpiceRotation { Frame "NIGHT_TK" BaseFrame "J2000" Kernel "legacy.tf" }
                }
                """);
            var native = Load(original, "static-frames")["NIGHT_TK"];
            var epoch = JulianInstant.FromTdbSeconds(0);
            var expected = Assert.IsType<FrameKinematics>(native.Sample(epoch));
            Verify(SscBodyCatalogue.Load(source));
            string packed = Path.Combine(scratch.FullName, "packed");
            Assert.Equal(1, SscBodyCatalogue.BuildPack(source, packed, _ => { }));
            Verify(SscBodyCatalogue.Load(packed));

            void Verify(IReadOnlyList<SscBodyDefinition> bodies)
            {
                var actual = Assert.IsType<FrameKinematics>(Assert.Single(bodies).SampleRotation!(epoch));
                Assert.InRange((actual.ToWorld.X - expected.ToWorld.X).Length, 0, 2e-13);
                Assert.InRange((actual.ToWorld.Y - expected.ToWorld.Y).Length, 0, 2e-13);
                Assert.InRange((actual.ToWorld.Z - expected.ToWorld.Z).Length, 0, 2e-13);
                Assert.Equal(default(Vector3d), actual.AngularVelocityRadiansPerSecond);
            }
        }
        finally
        {
            scratch.Delete(recursive: true);
        }
    }

    [Theory]
    [InlineData("1001011", false)]
    [InlineData("1001010", false)]
    [InlineData("PROBE_CLASS", false)]
    [InlineData("PROBE_FK", false)]
    [InlineData("1001011", true)]
    [InlineData("10081", false)]
    [InlineData("EARTH_FIXED", false)]
    public void FixedFrameKeysSupportNativeAndArchivedIdentifiers(string key, bool conflictingFrameKey)
    {
        var scratch = Directory.CreateTempSubdirectory("st-tk-ids-");
        try
        {
            string source = Directory.CreateDirectory(Path.Combine(scratch.FullName, "source")).FullName;
            bool earth = key is "10081" or "EARTH_FIXED";
            string frame = earth ? "EARTH_FIXED" : "PROBE_FK";
            string definitions = earth ? "" : """
                FRAME_PROBE_FK = 1001010
                FRAME_1001010_NAME = 'PROBE_FK'
                FRAME_1001010_CLASS = 4
                FRAME_1001010_CLASS_ID = 1001011
                FRAME_1001010_CENTER = 399
                FRAME_PROBE_CLASS = 1001011
                FRAME_1001011_NAME = 'PROBE_CLASS'
                FRAME_1001011_CLASS = 4
                FRAME_1001011_CLASS_ID = 1001011
                FRAME_1001011_CENTER = 399
                """;
            string alternate = conflictingFrameKey ? Transform("1001010", 90) : "";
            File.WriteAllText(Path.Combine(source, "frames.tf"),
                "KPL/FK\n\\begindata\n" + definitions + "\n" + Transform(key, 30) + "\n" + alternate + "\n\\begintext\n");
            File.WriteAllText(Path.Combine(source, "frame.ssc"), $$"""
                "Probe" "Sol/Earth" {
                    FixedPosition [ 7000 0 0 ]
                    BodyFrame { EquatorJ2000 {} }
                    SpiceRotation { Frame "{{frame}}" BaseFrame "J2000" Kernel "frames.tf" }
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

        static string Transform(string key, int degrees) => FormattableString.Invariant($$"""
            TKFRAME_{{key}}_SPEC = 'ANGLES'
            TKFRAME_{{key}}_RELATIVE = 'J2000'
            TKFRAME_{{key}}_ANGLES = ({{degrees}} 0 0)
            TKFRAME_{{key}}_AXES = (3 2 1)
            TKFRAME_{{key}}_UNITS = 'DEGREES'
            """);

        static void Verify(IReadOnlyList<SscBodyDefinition> bodies)
        {
            var epoch = JulianInstant.FromTdbSeconds(0);
            var actual = Assert.IsType<FrameKinematics>(Assert.Single(bodies).SampleRotation!(epoch));
            Assert.InRange((actual.ToWorld.X - new Vector3d(Math.Sqrt(3) / 2, -.5, 0)).Length, 0, 1e-14);
            Assert.Equal(default(Vector3d), actual.AngularVelocityRadiansPerSecond);
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
