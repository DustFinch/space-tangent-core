// SPDX-License-Identifier: GPL-2.0-or-later
using System.Text.Json.Nodes;
using SpaceTangent.Contracts;
using SpaceTangent.Core.Addons;
using SpaceTangent.Core.Ephemerides;
using SpaceTangent.Core.World;
using Xunit;

namespace SpaceTangent.Core.Tests;

public sealed class PackedTimelineContractTests : IDisposable
{
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("st-packed-phases-");
    private static JsonObject Axes() => new() { ["kind"] = "equatorJ2000" };
    private static JsonObject Orbit(double x) => new() { ["kind"] = "fixed", ["frame"] = Axes(), ["positionKm"] = new JsonArray(x, 0, 0) };
    private static JsonObject Spin(bool half = false) => new()
        { ["kind"] = "quaternion", ["frame"] = Axes(), ["quaternion"] = half ? new JsonArray(0, 0, 0, 1) : new JsonArray(1, 0, 0, 0) };
    private static JsonObject Body() => new()
    {
        ["path"] = "Sol/Earth/Probe", ["name"] = "Probe", ["parent"] = "Sol/Earth",
        ["radiusKm"] = 1, ["polarRadiusKm"] = .5, ["rotation"] = Spin(), ["orbit"] = Orbit(10),
        ["display"] = new JsonObject(), ["appearance"] = new JsonObject { ["texture"] = "" },
        ["lifetime"] = new JsonObject { ["beginningJdTdb"] = 2451545, ["endingJdTdb"] = 2451548 }
    };

    private IReadOnlyList<SampledMission> Load(JsonObject body, JsonObject? child = null, List<string>? warnings = null)
    {
        var bodies = new JsonArray(body); if (child is not null) bodies.Add(child);
        File.WriteAllText(Path.Combine(_root.FullName, "pack.json"), new JsonObject { ["formatVersion"] = 19, ["bodies"] = bodies }.ToJsonString());
        return SampledMissionCatalog.Load("pack.json", _root.FullName,
            name => name is "Earth" or "Sol/Earth" ? SolarSystemSample.EarthId : null,
            warnings is null ? null : warnings.Add);
    }

    [Fact]
    public void FollowAttitudeCarriesTheParentPoseButReportsOnlyItsDeclaredLocalRate()
    {
        var body = Body();
        body["rotation"] = new JsonObject { ["kind"] = "follow", ["body"] = "Sol/Earth", ["turn"] = new JsonArray(1, 0, 0, 0),
            ["localRotation"] = new JsonObject { ["kind"] = "cosmographiaUniform", ["periodDays"] = 2,
                ["epochJdTdb"] = 2451545, ["frame"] = new JsonObject { ["kind"] = "eclipticJ2000" } } };
        var mission = Assert.Single(Load(body));
        var graph = new WorldGraphBuilder(); graph.AddTrajectory(SolarSystemSample.EarthId, _ => default(CartesianState));
        graph.AddOrientation(SolarSystemSample.EarthId, epoch =>
        {
            double seconds = (double)epoch.DifferenceNanoseconds(new(2451545, 0, TimeScale.Tdb)) / JulianInstant.NanosecondsPerSecond;
            double angle = .01 * seconds;
            return new(new(new(Math.Cos(angle), Math.Sin(angle), 0), new(-Math.Sin(angle), Math.Cos(angle), 0), new(0, 0, 1)), new(0, 0, .01));
        });
        graph.AddSampledMission(mission); var world = graph.Build();
        double ownRate = Math.Tau / (2 * 86400);
        foreach (double seconds in new[] { 0d, 50, 100 })
        {
            var epoch = new JulianInstant(2451545, 0, TimeScale.Tdb).AddSeconds(seconds);
            Assert.True(world.Evaluate(epoch).TryOrientation(mission.Id, out var orientation));
            double angle = .01 * seconds + Math.PI + ownRate * seconds;
            Assert.InRange((orientation.ToWorld.X - new Vector3d(Math.Cos(angle), Math.Sin(angle), 0)).Length, 0, 2e-9);
            Assert.InRange((orientation.AngularVelocityRadiansPerSecond!.Value - new Vector3d(0, 0, ownRate)).Length, 0, 1e-12);
        }
    }

    [Fact]
    public void FollowLocalRotationRejectsAnAdditionalCompetingBodyFrame()
    {
        var body = Body(); body["bodyFrame"] = Axes();
        body["rotation"] = new JsonObject { ["kind"] = "follow", ["body"] = "Sol/Earth", ["turn"] = new JsonArray(1, 0, 0, 0),
            ["localRotation"] = Spin() };
        var warnings = new List<string>();
        Assert.Empty(Load(body, warnings: warnings));
        Assert.Contains(warnings, warning => warning.Contains("inertial local axes", StringComparison.Ordinal));
    }

    [Fact]
    public void IndependentOrbitRotationAndReferencePhasesSplitAtEveryBoundary()
    {
        var body = Body(); body.Remove("orbit");
        body["timeline"] = new JsonArray(Phase(2451546, 10), Phase(2451547, 20), Phase(2451548, 30));
        body["rotation"] = new JsonObject { ["kind"] = "timeline", ["phases"] = new JsonArray(
            new JsonObject { ["untilJdTdb"] = 2451545.5, ["rotation"] = Spin() },
            new JsonObject { ["untilJdTdb"] = 2451548, ["rotation"] = Spin(true) }) };
        body["bodyFrames"] = new JsonArray(
            new JsonObject { ["untilJdTdb"] = 2451546.5, ["frame"] = Axes() },
            new JsonObject { ["untilJdTdb"] = 2451548, ["frame"] = new JsonObject { ["kind"] = "eclipticJ2000" } });
        var mission = Assert.Single(Load(body));
        Assert.Equal(new[] { 2451545.5, 2451546, 2451546.5, 2451547, 2451548 }, mission.Arcs.Select(arc => arc.End.ToJulianDate()));
        Assert.Equal(new[] { 10d, 10, 20, 20, 30 }, mission.Arcs.Select(arc => arc.LocalState(arc.Start)!.Value.PositionKilometers.X));
        var builder = new WorldGraphBuilder(); builder.AddTrajectory(SolarSystemSample.EarthId, _ => default(CartesianState));
        builder.AddSampledMission(mission); var graph = builder.Build();
        Assert.True(graph.Evaluate(JulianInstant.FromJulianDate(2451545.25, TimeScale.Tdb)).TryOrientation(mission.Id, out var first));
        Assert.True(graph.Evaluate(JulianInstant.FromJulianDate(2451546.25, TimeScale.Tdb)).TryOrientation(mission.Id, out var half));
        Assert.InRange((first.ToWorld.X + half.ToWorld.X).Length, 0, 1e-12);

        static JsonObject Phase(double end, double x) => new()
            { ["endingJdTdb"] = end, ["center"] = "Sol/Earth", ["orbit"] = Orbit(x) };
    }

    [Fact]
    public void TimedChildGeometryIsClippedToItsOwnersLifetimeAndKeepsItsOwnShape()
    {
        var parent = Body(); parent["shown"] = new JsonObject { ["fromJdTdb"] = 2451544, ["untilJdTdb"] = 2451549 };
        var child = new JsonObject
        {
            ["path"] = "Sol/Earth/Probe/Globe (1)", ["name"] = "Globe component", ["parent"] = "Sol/Earth/Probe", ["displayName"] = "Globe", ["class"] = "component",
            ["radiusKm"] = .5, ["polarRadiusKm"] = .25,
            ["shown"] = new JsonObject { ["fromJdTdb"] = 2451546, ["untilJdTdb"] = 2451547 }
        };
        var mission = Assert.Single(Load(parent, child));
        Assert.Equal(2, mission.Geometry!.Count);
        Assert.Equal(mission.Start, mission.Geometry[0].Start); Assert.Equal(mission.End, mission.Geometry[0].End);
        Assert.Equal(new Vector3d(1, 1, .5), mission.Geometry[0].Shape.EllipsoidRadiiKilometers);
        Assert.Equal(new Vector3d(.5, .5, .25), mission.Geometry[1].Shape.EllipsoidRadiiKilometers);
        Assert.Equal(2451546, mission.Geometry[1].Start.ToJulianDate());
        Assert.Equal(2451547, mission.Geometry[1].End.ToJulianDate());
    }

    [Fact]
    public void PackedCombinationUsesSignedWeightsAndHalfOpenTermPhases()
    {
        var body = Body();
        body["orbit"] = new JsonObject { ["kind"] = "combination", ["frame"] = Axes(), ["terms"] = new JsonArray(
            new JsonObject { ["weight"] = 2, ["phases"] = new JsonArray(
                Term(2451546, 10), Term(2451548, 20)) },
            new JsonObject { ["weight"] = -.5, ["phases"] = new JsonArray(Term(2451548, 8)) }) };
        var mission = Assert.Single(Load(body)); var arc = Assert.Single(mission.Arcs);
        Assert.Equal(new Vector3d(16, 0, 0), arc.LocalState(new(2451545, 0, TimeScale.Tdb))!.Value.PositionKilometers);
        Assert.Equal(new Vector3d(36, 0, 0), arc.LocalState(new(2451546, 0, TimeScale.Tdb))!.Value.PositionKilometers);
        Assert.Equal(default, arc.LocalState(new(2451546, 0, TimeScale.Tdb))!.Value.VelocityKilometersPerSecond);
        static JsonObject Term(double end, double x) => new()
            { ["endingJdTdb"] = end, ["center"] = "Sol/Earth", ["orbit"] = Orbit(x) };
    }

    [Theory]
    [InlineData("lifetime")]
    [InlineData("empty-timeline")]
    [InlineData("reversed-timeline")]
    [InlineData("outside-timeline")]
    [InlineData("empty-rotation")]
    [InlineData("reversed-rotation")]
    [InlineData("empty-frames")]
    [InlineData("too-many-phases")]
    public void InvalidPhaseBoundariesCannotPublishAPartialMission(string fault)
    {
        var body = Body();
        JsonObject phase = new() { ["endingJdTdb"] = 2451545, ["center"] = "Sol/Earth", ["orbit"] = Orbit(10) };
        switch (fault)
        {
            case "lifetime": body["lifetime"]!["endingJdTdb"] = 2451545; break;
            case "empty-timeline": body["timeline"] = new JsonArray(); break;
            case "reversed-timeline": body["timeline"] = new JsonArray(phase); break;
            case "outside-timeline": phase["endingJdTdb"] = 2451549; body["timeline"] = new JsonArray(phase); break;
            case "empty-rotation": body["rotation"] = new JsonObject { ["kind"] = "timeline", ["phases"] = new JsonArray() }; break;
            case "reversed-rotation": body["rotation"] = new JsonObject { ["kind"] = "timeline", ["phases"] = new JsonArray(
                new JsonObject { ["untilJdTdb"] = 2451546, ["rotation"] = Spin() },
                new JsonObject { ["untilJdTdb"] = 2451545, ["rotation"] = Spin() }) }; break;
            case "empty-frames": body["bodyFrames"] = new JsonArray(); break;
            case "too-many-phases": body["timeline"] = new JsonArray(Enumerable.Range(0, 129).Select(_ => phase.DeepClone()).ToArray()); break;
        }
        Assert.Throws<InvalidDataException>(() => Load(body));
    }

    public void Dispose() => _root.Delete(true);
}
