// SPDX-License-Identifier: GPL-2.0-or-later
using System.Globalization;
using System.Text.Json;
using SpaceTangent.Contracts;
using SpaceTangent.Core.Addons;
using SpaceTangent.Core.Ephemerides;
using SpaceTangent.Core.World;
using Xunit;

namespace SpaceTangent.Core.Tests;

public sealed class SampledMissionAttitudeTests
{
    [AttitudeFact]
    [Trait("Category", "ExternalData")]
    public void NativeOrientationAndSignedRatesMatchArchivedScalarOracle()
    {
        string root = Environment.GetEnvironmentVariable("SPACETANGENT_NATIVE_ATTITUDE")!;
        using var metadata = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(root, "attitude-native-reference.json")));
        using var states = new BinaryReader(File.OpenRead(Path.Combine(root, "attitude-native-states.bin")));
        string file = Path.GetTempFileName();
        try
        {
            int accepted = 0, invalid = 0, queries = 0;
            foreach (JsonElement data in metadata.RootElement.GetProperty("cases").EnumerateArray())
            {
                var records = data.GetProperty("records").EnumerateArray().Select(record =>
                    string.Join(" ", record.EnumerateArray().Select(column => column.GetDouble().ToString("R", CultureInfo.InvariantCulture))));
                File.WriteAllText(file, string.Join(" # record\n", records));
                bool legacyAxes = data.GetProperty("celestia").GetBoolean();
                if (data.GetProperty("nonfiniteScalars").GetInt32() != 0)
                {
                    Assert.Throws<InvalidDataException>(() => SampledMissionAttitude.Read(file, legacyAxes));
                    invalid++;
                    continue;
                }

                var track = SampledMissionAttitude.Read(file, legacyAxes);
                states.BaseStream.Position = data.GetProperty("offsetBytes").GetInt64();
                for (int index = 0; index < data.GetProperty("sampleCount").GetInt32(); index++)
                {
                    var epoch = JulianInstant.FromJulianDate(states.ReadDouble(), TimeScale.Tdb);
                    double w = states.ReadDouble(), x = states.ReadDouble(), y = states.ReadDouble(), z = states.ReadDouble();
                    var rate = new Vector3d(states.ReadDouble(), states.ReadDouble(), states.ReadDouble());
                    var frame = track.Sample(epoch);
                    Compare(new(1 - (2 * y * y + 2 * z * z), 2 * x * y + 2 * z * w, 2 * x * z - 2 * y * w), frame.ToWorld.X);
                    Compare(new(2 * x * y - 2 * z * w, 1 - (2 * x * x + 2 * z * z), 2 * y * z + 2 * x * w), frame.ToWorld.Y);
                    Compare(new(2 * x * z + 2 * y * w, 2 * y * z - 2 * x * w, 1 - (2 * x * x + 2 * y * y)), frame.ToWorld.Z);
                    Compare(rate, frame.AngularVelocityRadiansPerSecond!.Value);
                    queries++;
                }
                accepted++;
            }
            Assert.Equal(54, accepted + invalid);
            Assert.True(invalid > 0);
            Assert.True(queries > 400);
        }
        finally
        {
            File.Delete(file);
        }
    }

    [AttitudeFact]
    [Trait("Category", "ExternalData")]
    public void OriginalCatalogueInstallsWithItsPublishedAssetsAndDeclaredAttitudeWitness()
    {
        string root = Environment.GetEnvironmentVariable("SPACETANGENT_NATIVE_ATTITUDE")!;
        var messages = new List<string>();
        var missions = SampledMissionCatalog.Load(Path.Combine(root, "catalog", "test.json"), root,
            name => name == "Sun" ? SolarSystemSample.SunId : null, messages.Add);
        Assert.Equal(4, missions.Count);
        var craft = Assert.Single(missions, mission => mission.Name == "CubeSat");
        var cassini = Assert.Single(missions, mission => mission.Name == "Cassini");
        Assert.EndsWith("jason.obj", craft.MeshPath, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(craft.MeshPath, cassini.MeshPath);
        var graph = new WorldGraphBuilder();
        graph.AddTrajectory(SolarSystemSample.SunId, _ => new CartesianState(default, default));
        foreach (SampledMission mission in missions)
            graph.AddSampledMission(mission);
        var world = graph.Build();
        var at = JulianInstant.FromJulianDate(2455755.0008232058, TimeScale.Tdb);
        var frame = world.Evaluate(at);
        Assert.True(frame.TryState(craft.Id, out var state));
        Assert.True(frame.TryState(new("mission/ceres"), out var parent));
        Compare(new(7294.0129687500003, 575.30470507812504, 922.5128125),
            (state - parent).PositionKilometers, 5e-7);
        Assert.True(frame.TryOrientation(craft.Id, out var attitude));
        Assert.InRange(Math.Abs(attitude.ToWorld.X.Length - 1), 0, .01);
        Assert.DoesNotContain(messages, message => message.Contains("mesh", StringComparison.OrdinalIgnoreCase));
    }

    private static void Compare(Vector3d expected, Vector3d actual, double tolerance = 1e-12) =>
        Assert.InRange((actual - expected).Length, 0, tolerance * Math.Max(1, expected.Length));

    private sealed class AttitudeFactAttribute : FactAttribute
    {
        public AttitudeFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SPACETANGENT_NATIVE_ATTITUDE")))
                Skip = "Set the original mission catalogue and native attitude oracle.";
        }
    }
}
