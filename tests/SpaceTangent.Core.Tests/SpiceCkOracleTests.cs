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
