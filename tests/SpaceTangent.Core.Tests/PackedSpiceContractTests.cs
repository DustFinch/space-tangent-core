// SPDX-License-Identifier: GPL-2.0-or-later
using System.Text.Json;
using System.Text.Json.Nodes;
using SpaceTangent.Contracts;
using SpaceTangent.Core.Addons;
using SpaceTangent.Core.World;
using Xunit;

namespace SpaceTangent.Core.Tests;

public sealed class PackedSpiceContractTests : IDisposable
{
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("st-packed-spice-");
    private static JsonObject Body() => JsonNode.Parse("""
        {"path":"Earth/Witness","name":"Witness","parent":"Earth","radiusKm":1,
         "orbit":{"kind":"spice","target":9000001,"spiceCenter":399,"kernel":"witness.bsp;field.ti;witness.bsp"},
         "rotation":{"kind":"spice","frame":"J2000","baseFrame":"J2000"}}
        """)!.AsObject();
    private IReadOnlyList<SampledMission> Load(JsonObject body, bool events = false, List<string>? warnings = null)
    {
        File.Copy(Path.Combine(AppContext.BaseDirectory, "Fixtures", "equinoctial-spk-reference.bin"), Path.Combine(_root.FullName, "witness.bsp"), true);
        File.WriteAllText(Path.Combine(_root.FullName, "field.ti"), """
            KPL/IK
            \begindata
            INS-1001_FOV_FRAME='J2000'
            INS-1001_FOV_SHAPE='CIRCLE'
            INS-1001_BORESIGHT=(0 0 1)
            INS-1001_FOV_BOUNDARY_CORNERS=(.1 0 1)
            \begintext
            """);
        var document = new JsonObject { ["formatVersion"] = 19, ["bodies"] = new JsonArray(body) };
        if (events) document["spiceLoads"] = new JsonArray(new JsonObject
            { ["policy"] = "furnish", ["files"] = new JsonArray("witness.bsp", "field.ti") });
        File.WriteAllText(Path.Combine(_root.FullName, "pack.json"), document.ToJsonString());
        return SampledMissionCatalog.Load("pack.json", _root.FullName, name => name == "Earth" ? new ObjectId("earth") : null,
            warnings is null ? null : warnings.Add);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void PackedKernelLoadFormsRetainTheIndependentNativeStatesAndPhysicalAttitude(bool events)
    {
        var body = Body(); body["rotation"]!["outputFrame"] = "EquatorJ2000";
        var mission = Assert.Single(Load(body, events));
        using var reference = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "equinoctial-spk-reference.json")));
        foreach (var row in reference.RootElement.GetProperty("rows").EnumerateArray().Where(row => row.GetProperty("target").GetInt32() == 9000001))
        {
            double[] expected = row.GetProperty("state").EnumerateArray().Select(item => item.GetDouble()).ToArray();
            var epoch = JulianInstant.FromTdbSeconds(row.GetProperty("seconds").GetDouble());
            var state = mission.Arcs[0].LocalState(epoch)!.Value;
            Assert.InRange((state.PositionKilometers - new Vector3d(expected[0], expected[1], expected[2])).Length, 0, 2e-7);
            Assert.InRange((state.VelocityKilometersPerSecond - new Vector3d(expected[3], expected[4], expected[5])).Length, 0, 2e-9);
        }
        var graph = new WorldGraphBuilder(); graph.AddTrajectory(new("earth"), _ => default(CartesianState)); graph.AddSampledMission(mission);
        Assert.True(graph.Build().Evaluate(new(2451545, 0, TimeScale.Tdb)).TryOrientation(mission.Id, out var attitude));
        Assert.Equal(Basis3d.Identity, attitude.ToWorld);
    }

    [Fact]
    public void ExplicitPackedSpiceCoverageIsInclusiveAndDoesNotExtrapolateOutsideItsWindow()
    {
        var body = Body(); body["orbit"]!["beginningJdTdb"] = 2451545; body["orbit"]!["endingJdTdb"] = 2451545.1;
        var mission = Assert.Single(Load(body)); var sample = mission.Arcs[0].LocalState;
        var start = new JulianInstant(2451545, 0, TimeScale.Tdb); var end = JulianInstant.FromJulianDate(2451545.1, TimeScale.Tdb);
        Assert.Null(sample(start.AddNanoseconds(-1))); Assert.NotNull(sample(start));
        Assert.NotNull(sample(end)); Assert.Null(sample(end.AddNanoseconds(1)));
    }

    [Fact]
    public void PackedSensorsRetainKernelBoundaryRaysAndDisplayParameters()
    {
        var body = Body(); body["sensor"] = JsonNode.Parse("""
            {"instrument":"-1001","target":"Earth","rangeKm":1000,"color":[0.2,0.4,0.6],"frustumOpacity":0.3,"footprintOpacity":0.7}
            """);
        var sensor = Assert.Single(Assert.Single(Load(body)).Geometry!).Shape.Sensor!;
        Assert.Equal(new Vector3d(0, 0, 1), sensor.Boresight); Assert.Equal(32, sensor.Boundary.Count);
        Assert.Equal((1000d, .3, .7), (sensor.RangeKilometers, sensor.Opacity, sensor.FootprintOpacity));
    }

    [Fact]
    public void ReversedPackedSpiceCoverageCannotPublishATrajectory()
    {
        var body = Body(); body["orbit"]!["beginningJdTdb"] = 2451546; body["orbit"]!["endingJdTdb"] = 2451545;
        Assert.Throws<InvalidDataException>(() => Load(body));
    }

    [Fact]
    public void PackedSpiceRequiresInertialOutputAxes()
    {
        var body = Body(); body["orbit"]!["frame"] = new JsonObject { ["kind"] = "bodyFixed", ["body"] = "Earth" };
        var warnings = new List<string>(); Assert.Empty(Load(body, warnings: warnings));
        Assert.Contains(warnings, text => text.Contains("inertial output axes", StringComparison.Ordinal));
    }

    public void Dispose() => _root.Delete(recursive: true);
}
