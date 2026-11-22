// SPDX-License-Identifier: GPL-2.0-or-later
using System.Text.Json.Nodes;
using SpaceTangent.Contracts;
using SpaceTangent.Core.Addons;
using Xunit;

namespace SpaceTangent.Core.Tests;

public sealed class PackedTrajectoryBoundaryTests : IDisposable
{
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("st-packed-trajectories-");
    private static JsonObject Axes() => new() { ["kind"] = "equatorJ2000" };
    private static JsonObject Fixed(double x = 4) => new() { ["kind"] = "fixed", ["frame"] = Axes(), ["positionKm"] = new JsonArray(x, 0, 0) };
    private static JsonObject Cheb() => new() { ["kind"] = "chebyshev", ["frame"] = Axes(),
        ["terms"] = new JsonArray(new JsonObject { ["file"] = "witness.cheb", ["weight"] = 3 }) };
    private static JsonObject Term(JsonObject orbit, double weight = 1) => new() { ["weight"] = weight,
        ["phases"] = new JsonArray(new JsonObject { ["center"] = "Earth", ["endingJdTdb"] = 2451548, ["orbit"] = orbit }) };
    private static JsonObject Combination() => new() { ["kind"] = "combination", ["frame"] = Axes(),
        ["terms"] = new JsonArray(Term(Cheb()), Term(Fixed(), .5)) };
    private IReadOnlyList<SampledMission> Load(JsonObject orbit, List<string>? warnings = null)
    {
        var body = new JsonObject { ["path"] = "Earth/Witness", ["name"] = "Witness", ["parent"] = "Earth", ["radiusKm"] = 1, ["display"] = new JsonObject(),
            ["lifetime"] = new JsonObject { ["beginningJdTdb"] = 2451545, ["endingJdTdb"] = 2451548 },
            ["orbit"] = orbit, ["rotation"] = new JsonObject { ["kind"] = "quaternion", ["frame"] = Axes(), ["quaternion"] = new JsonArray(1, 0, 0, 0) } };
        File.WriteAllText(Path.Combine(_root.FullName, "pack.json"),
            new JsonObject { ["formatVersion"] = 19, ["bodies"] = new JsonArray(body) }.ToJsonString());
        return SampledMissionCatalog.Load("pack.json", _root.FullName, name => name == "Earth" ? new ObjectId("earth") : null,
            warnings is null ? null : warnings.Add);
    }
    private void WritePolynomial()
    {
        using var writer = new BinaryWriter(File.Create(Path.Combine(_root.FullName, "witness.cheb")));
        writer.Write("CHEBPOLY"u8); writer.Write(1); writer.Write(1); writer.Write(0d); writer.Write(86400d);
        foreach (double value in new[] { 10d, 2, 5, 0, -3, 0 }) writer.Write(value);
    }

    [Theory]
    [InlineData(0, 26)] [InlineData(43200, 32)] [InlineData(86400, 38)]
    public void WeightedPackedPolynomialAndConstantHaveAnalyticStateAndVelocity(double seconds, double x)
    {
        WritePolynomial();
        var mission = Assert.Single(Load(Combination()));
        var state = mission.Arcs[0].LocalState(JulianInstant.FromTdbSeconds(seconds))!.Value;
        Assert.InRange((state.PositionKilometers - new Vector3d(x, 15, -9)).Length, 0, 1e-10);
        Assert.InRange((state.VelocityKilometersPerSecond - new Vector3d(12d / 86400, 0, 0)).Length, 0, 1e-12);
    }

    [Theory]
    [InlineData(false, 0)] [InlineData(false, 17)]
    [InlineData(true, 1)] [InlineData(true, 17)]
    public void CompositeTermCountsAreBoundedBeforeResourcesAreOpened(bool combination, int count)
    {
        var orbit = combination ? Combination() : Cheb(); var terms = new JsonArray();
        for (int i = 0; i < count; i++) terms.Add(combination ? Term(Fixed()) : new JsonObject());
        orbit["terms"] = terms;
        Assert.Throws<InvalidDataException>(() => Load(orbit));
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("too-many")]
    [InlineData("wrong-center")]
    [InlineData("reversed-ends")]
    [InlineData("deep")]
    public void InvalidCompositePhasesCannotPublishAPartialTrajectory(string fault)
    {
        var orbit = Combination(); var term = orbit["terms"]![0]!;
        switch (fault)
        {
            case "empty": term["phases"] = new JsonArray(); break;
            case "too-many":
                var phases = new JsonArray(); for (int i = 0; i < 129; i++) phases.Add(new JsonObject()); term["phases"] = phases; break;
            case "wrong-center": term["phases"]![0]!["center"] = "Moon"; break;
            case "reversed-ends":
                var phase = term["phases"]![0]!.DeepClone(); term["phases"]!.AsArray().Add(phase); break;
            case "deep":
                JsonObject nested = Fixed();
                for (int i = 0; i < 17; i++) nested = new() { ["kind"] = "combination", ["frame"] = Axes(),
                    ["terms"] = new JsonArray(Term(nested), Term(Fixed())) };
                orbit = nested; break;
        }
        // Avoid making a missing polynomial file mask phase validation.
        WritePolynomial();
        if (fault == "deep")
            Assert.ThrowsAny<System.Text.Json.JsonException>(() => Load(orbit));
        else
            Assert.Throws<InvalidDataException>(() => Load(orbit));
    }

    [Theory]
    [InlineData("center")]
    [InlineData("nested")]
    public void MovingFramesRejectCenterChangesAndRecursiveFrameWrapping(string fault)
    {
        JsonObject Moving(JsonObject child, string center = "Earth") => new() { ["kind"] = "movingFrame", ["body"] = "Earth",
            ["axes"] = new JsonArray(1, 0, 0, 0), ["trajectory"] = new JsonObject { ["center"] = center, ["orbit"] = child } };
        var orbit = fault == "center" ? Moving(Fixed(), "Moon") : Moving(Moving(Fixed()));
        Assert.Throws<InvalidDataException>(() => Load(orbit));
    }

    [Fact]
    public void UnknownMovingVelocityConventionIsAnExplicitUnsupportedDiagnostic()
    {
        var orbit = new JsonObject { ["kind"] = "movingFrame", ["body"] = "Earth", ["axes"] = new JsonArray(1, 0, 0, 0),
            ["velocityConvention"] = "unlisted-policy", ["trajectory"] = new JsonObject { ["center"] = "Earth", ["orbit"] = Fixed() } };
        var warnings = new List<string>(); Assert.Empty(Load(orbit, warnings));
        Assert.Contains(warnings, text => text.Contains("unlisted-policy", StringComparison.Ordinal));
    }

    public void Dispose() => _root.Delete(recursive: true);
}
