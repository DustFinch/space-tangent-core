// SPDX-License-Identifier: GPL-2.0-or-later
using SpaceTangent.Contracts;
using SpaceTangent.Core.Addons;
using SpaceTangent.Core.World;
using Xunit;

namespace SpaceTangent.Core.Tests;

public sealed class MissionRequirementTests
{
    private static readonly ObjectId Sun = new("sol/sun");

    [Fact]
    public void RequiredCatalogueUsesItsOwnAssetDirectoryAndLoadsOnce()
    {
        var folder = Directory.CreateTempSubdirectory();
        try
        {
            string root = folder.FullName;
            string child = Directory.CreateDirectory(Path.Combine(root, "nested")).FullName;
            File.WriteAllText(Path.Combine(root, "main.json"), """
                {"version":"1.0","require":["nested/partner.json"],"items":[
                  {"name":"Beacon","center":"Partner","trajectory":{"type":"FixedPoint","position":[0,5,0]}}
                ]}
                """);
            File.WriteAllText(Path.Combine(child, "partner.json"), """
                {"version":"1.0","items":[
                  {"name":"Partner","center":"Sun","trajectory":{"type":"InterpolatedStates","source":"point.xyzv"}}
                ]}
                """);
            File.WriteAllText(Path.Combine(child, "point.xyzv"), "2451545 42 0 0 0 0 0");
            File.WriteAllText(Path.Combine(root, "point.xyzv"), "2451545 9000 0 0 0 0 0");
            var tracks = SampledMissionCatalog.Load(["main.json", "nested/partner.json"], root,
                name => name == "Sun" ? Sun : null);
            Assert.Collection(tracks, track => Assert.Equal("Partner", track.Name), track => Assert.Equal("Beacon", track.Name));
            var graph = new WorldGraphBuilder();
            graph.AddTrajectory(Sun, _ => default(CartesianState));
            foreach (var mission in tracks)
                graph.AddSampledMission(mission);
            var world = graph.Build().Evaluate(new(2451545, 0, TimeScale.Tdb));
            Assert.True(world.TryState(tracks[1].Id, out var state));
            Assert.Equal(new Vector3d(42, 5, 0), state.PositionKilometers);

            File.WriteAllText(Path.Combine(child, "partner.json"), """{"version":"1.0","require":["../main.json"]}""");
            var cycle = Assert.Throws<InvalidDataException>(() => SampledMissionCatalog.Load("main.json", root, _ => null));
            Assert.Contains("dependency cycle", cycle.Message, StringComparison.OrdinalIgnoreCase);
            File.Delete(Path.Combine(child, "partner.json"));
            Assert.Throws<InvalidDataException>(() => SampledMissionCatalog.Load("main.json", root, _ => null));
            var messages = new List<string>();
            var fallback = Assert.Single(SampledMissionCatalog.Load("main.json", root, _ => Sun, messages.Add));
            Assert.Equal(Sun, fallback.Arcs[0].Center);
            Assert.Contains(messages, message => message.Contains("nested/partner.json", StringComparison.Ordinal));
        }
        finally { folder.Delete(recursive: true); }
    }
}
