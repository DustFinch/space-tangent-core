// SPDX-License-Identifier: GPL-2.0-or-later
using System.Text.Json;
using SpaceTangent.Contracts;
using SpaceTangent.Core.Addons;
using Xunit;

namespace SpaceTangent.Core.Tests;

public sealed class SpiceStatePoolTests
{
    [StateChainFact]
    [Trait("Category", "ExternalData")]
    public void OriginalCassiniAndNativeMultiFrameStatesUseOrderedCentersAndAngularRates()
    {
        string root = Environment.GetEnvironmentVariable("SPACETANGENT_SPK_STATE_CHAIN")!;
        var center = new ObjectId("test/state-origin");
        var missions = SampledMissionCatalog.Load("missions.json", root,
            name => name == "State Origin" ? center : null).ToDictionary(mission => mission.Name);
        using var reference = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(root, "spk-state-reference.json")));
        Assert.Equal(22, reference.RootElement.GetArrayLength());
        foreach (var row in reference.RootElement.EnumerateArray())
        {
            string name = row.GetProperty("name").GetString()!;
            double seconds = row.GetProperty("seconds").GetDouble();
            var sample = missions[name].Arcs[0].LocalState(JulianInstant.FromTdbSeconds(seconds));
            var state = Assert.IsType<CartesianState>(sample);
            var expected = row.GetProperty("state");
            var position = new Vector3d(expected[0].GetDouble(), expected[1].GetDouble(), expected[2].GetDouble());
            var velocity = new Vector3d(expected[3].GetDouble(), expected[4].GetDouble(), expected[5].GetDouble());
            Assert.InRange((state.PositionKilometers - position).Length, 0, 2e-5);
            Assert.InRange((state.VelocityKilometersPerSecond - velocity).Length, 0, 1e-8);
        }
        Assert.Null(missions["State 0"].Arcs[0].LocalState(JulianInstant.FromTdbSeconds(101)));
    }

    private sealed class StateChainFactAttribute : FactAttribute
    {
        public StateChainFactAttribute()
        {
            if (Environment.GetEnvironmentVariable("SPACETANGENT_SPK_STATE_CHAIN") is null)
                Skip = "Set the native SPK state-chain oracle directory.";
        }
    }
}
