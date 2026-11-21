// SPDX-License-Identifier: GPL-2.0-or-later
using System.Text.Json.Nodes;
using SpaceTangent.Contracts;
using SpaceTangent.Core.Addons;
using SpaceTangent.Core.World;
using Xunit;

namespace SpaceTangent.Core.Tests;

public sealed class PackedBodyContractTests : IDisposable
{
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("st-packed-contract-");
    private static JsonObject Body() => JsonNode.Parse("""
        {"name":"Probe","path":"Sol/Probe","parent":"Sol","radiusKm":2,
         "lifetime":{"beginningJdTdb":2451545,"endingJdTdb":2451547},
         "geometryBounds":[],
         "orbit":{"kind":"fixed","positionKm":[100,200,300],"frame":{"kind":"equatorJ2000"}},
         "rotation":{"kind":"quaternion","quaternion":[1,0,0,0],"frame":{"kind":"equatorJ2000"}}}
        """)!.AsObject();
    private IReadOnlyList<SampledMission> Load(JsonObject body, List<string>? warnings = null, CancellationToken cancellation = default)
    {
        var document = new JsonObject { ["formatVersion"] = 19, ["bodies"] = new JsonArray(body) };
        File.WriteAllText(Path.Combine(_root.FullName, "pack.json"), document.ToJsonString());
        return SampledMissionCatalog.Load("pack.json", _root.FullName,
            name => name == "Sol" ? new ObjectId("sol/sun") : null, warnings is null ? null : warnings.Add, cancellation);
    }
    private static WorldGraph Graph(SampledMission mission)
    {
        var graph = new WorldGraphBuilder();
        graph.AddTrajectory(new("sol/sun"), _ => new(default, default));
        graph.AddSampledMission(mission);
        return graph.Build();
    }

    [Fact]
    public void FixedBodyRetainsItsPhysicalStateAndIdentity()
    {
        var mission = Assert.Single(Load(Body()));
        Assert.Equal("Probe", mission.Name);
        Assert.Equal(new ObjectId("mission/probe"), mission.Id);
        Assert.Equal(2, mission.RadiusKilometers);
        Assert.Equal(new Vector3d(100, 200, 300), mission.Arcs[0].LocalState(new(2451545, 0, TimeScale.Tdb))!.Value.PositionKilometers);
    }

    [Fact]
    public void LifetimeIsEnforcedByThePublishedWorld()
    {
        var mission = Assert.Single(Load(Body()));
        var graph = Graph(mission);
        Assert.False(graph.Evaluate(new(2451544, 0, TimeScale.Tdb)).TryState(mission.Id, out _));
        Assert.True(graph.Evaluate(new(2451545, 0, TimeScale.Tdb)).TryState(mission.Id, out _));
        Assert.False(graph.Evaluate(new(2451547, 0, TimeScale.Tdb)).TryState(mission.Id, out _));
    }

    [Fact]
    public void TimelineJoinSelectsTheNextPhaseExactly()
    {
        var body = Body();
        body.Remove("orbit");
        body["timeline"] = JsonNode.Parse("""
            [{"endingJdTdb":2451546,"center":"Sol","orbit":{"kind":"fixed","positionKm":[1,0,0],"frame":{"kind":"equatorJ2000"}}},
             {"endingJdTdb":2451547,"center":"Sol","orbit":{"kind":"fixed","positionKm":[2,0,0],"frame":{"kind":"equatorJ2000"}}}]
            """);
        var mission = Assert.Single(Load(body));
        var graph = Graph(mission);
        var join = new JulianInstant(2451546, 0, TimeScale.Tdb);
        Assert.True(graph.Evaluate(join.AddNanoseconds(-1)).TryState(mission.Id, out var before));
        Assert.True(graph.Evaluate(join).TryState(mission.Id, out var after));
        Assert.Equal(new Vector3d(1, 0, 0), before.PositionKilometers);
        Assert.Equal(new Vector3d(2, 0, 0), after.PositionKilometers);
    }

    [Fact]
    public void OrientationPhaseSplitsAnOtherwiseFixedTrajectory()
    {
        var body = Body();
        body["rotation"] = JsonNode.Parse("""
            {"kind":"timeline","phases":[
             {"untilJdTdb":2451546,"rotation":{"kind":"quaternion","quaternion":[1,0,0,0],"frame":{"kind":"equatorJ2000"}}},
             {"untilJdTdb":2451547,"rotation":{"kind":"quaternion","quaternion":[0,0,0,1],"frame":{"kind":"equatorJ2000"}}}]}
            """);
        var mission = Assert.Single(Load(body));
        Assert.Equal(2, mission.Arcs.Count);
        var graph = Graph(mission);
        Assert.True(graph.Evaluate(new(2451545, 0, TimeScale.Tdb)).TryOrientation(mission.Id, out var first));
        Assert.True(graph.Evaluate(new(2451546, 0, TimeScale.Tdb)).TryOrientation(mission.Id, out var second));
        Assert.Equal(new Vector3d(1, 0, 0), first.ToWorld.X);
        Assert.InRange((second.ToWorld.X - new Vector3d(-1, 0, 0)).Length, 0, 1e-14);
    }

    [Fact]
    public void EmptyLifetimeIsRejectedInsteadOfPublishingAnUnreachableBody()
    {
        var body = Body();
        body["lifetime"]!["endingJdTdb"] = 2451545;
        Assert.Throws<InvalidDataException>(() => Load(body));
    }

    [Fact]
    public void EmptyTimelineIsRejected()
    {
        var body = Body(); body["timeline"] = new JsonArray();
        Assert.Throws<InvalidDataException>(() => Load(body));
    }

    [Fact]
    public void TimelineCannotEndAfterTheDeclaredLifetime()
    {
        var body = Body();
        body["timeline"] = new JsonArray(new JsonObject { ["endingJdTdb"] = 2451548,
            ["center"] = "Sol", ["orbit"] = body["orbit"]!.DeepClone() });
        Assert.Throws<InvalidDataException>(() => Load(body));
    }

    [Fact]
    public void OrientationEndsMustIncrease()
    {
        var body = Body(); var rotation = body["rotation"]!.DeepClone();
        body["rotation"] = new JsonObject { ["kind"] = "timeline", ["phases"] = new JsonArray(
            new JsonObject { ["untilJdTdb"] = 2451546, ["rotation"] = rotation.DeepClone() },
            new JsonObject { ["untilJdTdb"] = 2451546, ["rotation"] = rotation.DeepClone() }) };
        Assert.Throws<InvalidDataException>(() => Load(body));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void NonpositiveRadiusIsRejected(double radius)
    {
        var body = Body(); body["radiusKm"] = radius;
        Assert.Throws<InvalidDataException>(() => Load(body));
    }

    [Fact]
    public void UnsupportedOrbitIsReportedAndOmitted()
    {
        var body = Body(); body["orbit"]!["kind"] = "future-orbit";
        var warnings = new List<string>();
        Assert.Empty(Load(body, warnings));
        Assert.Contains("future-orbit", Assert.Single(warnings));
    }

    [Fact]
    public void UnsupportedRotationIsReportedAndOmitted()
    {
        var body = Body(); body["rotation"]!["kind"] = "future-rotation";
        var warnings = new List<string>();
        Assert.Empty(Load(body, warnings));
        Assert.Contains("future-rotation", Assert.Single(warnings));
    }

    [Fact]
    public void UnsupportedFrameIsReportedAndOmitted()
    {
        var body = Body(); body["orbit"]!["frame"]!["kind"] = "future-frame";
        var warnings = new List<string>();
        Assert.Empty(Load(body, warnings));
        Assert.Contains("future-frame", Assert.Single(warnings));
    }

    [Fact]
    public void MissingMeshDoesNotDiscardTheValidTrajectory()
    {
        var body = Body();
        body["appearance"] = JsonNode.Parse("""{"model":{"file":"missing.glb","orientation":[1,0,0,0]}}""");
        var warnings = new List<string>();
        var mission = Assert.Single(Load(body, warnings));
        Assert.Null(Assert.Single(mission.Geometry!).Shape.MeshPath);
        Assert.Contains("missing.glb", Assert.Single(warnings));
        Assert.True(Graph(mission).Evaluate(new(2451545, 0, TimeScale.Tdb)).TryState(mission.Id, out _));
    }

    [Fact]
    public void SphereBoundsUseTheLargestPhysicalRadius()
    {
        var body = Body();
        body["geometryBounds"] = JsonNode.Parse("""[{"kind":"sphere","radiusKm":3},{"kind":"sphere","radiusKm":7}]""");
        var bounds = Assert.IsType<MissionGeometryBounds>(Assert.Single(Load(body)).GeometryBounds);
        Assert.True(bounds.Complete);
        Assert.Equal(7, bounds.Resolve(_ => throw new InvalidOperationException()));
    }

    [Fact]
    public void CancellationDoesNotPublishAPartialMission()
    {
        Assert.Throws<OperationCanceledException>(() => Load(Body(), cancellation: new CancellationToken(true)));
    }

    public void Dispose() => _root.Delete(recursive: true);
}
