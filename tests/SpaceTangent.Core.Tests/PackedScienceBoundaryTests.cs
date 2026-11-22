// SPDX-License-Identifier: GPL-2.0-or-later
using System.Text.Json.Nodes;
using SpaceTangent.Contracts;
using SpaceTangent.Core.Addons;
using Xunit;

namespace SpaceTangent.Core.Tests;

public sealed class PackedScienceBoundaryTests : IDisposable
{
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("st-packed-science-");
    private static JsonObject Body() => JsonNode.Parse("""
        {"name":"Probe","class":"spacecraft","radiusKm":0.01,
         "timeline":[{"center":"Sol/Earth","orbit":{"kind":"tle","frame":{"kind":"equatorJ2000"},
          "line1":"1 26997U 01055A   11192.81984987 -.00000038  00000-0  10000-3 0  3699",
          "line2":"2 26997  66.0463 303.1838 0007562 261.9351  98.0768 12.80928955448691"}}],
         "rotation":{"kind":"quaternion","frame":{"kind":"equatorJ2000"},"quaternion":[1,0,0,0]},
         "appearance":{"model":{"file":"probe.obj","centre":[0,0,0],"orientation":[1,0,0,0]}}}
        """)!.AsObject();
    private IReadOnlyList<ScienceSatellite> Read(JsonObject body, int version = 19, bool duplicate = false, List<string>? warnings = null)
    {
        File.WriteAllText(Path.Combine(_root.FullName, "probe.obj"), "v 0 0 0\nv 1 0 0\nv 0 1 0\nf 1 2 3\n");
        var bodies = new JsonArray(body);
        if (duplicate) { var other = body.DeepClone(); other["name"] = "Other Probe"; bodies.Add(other); }
        File.WriteAllText(Path.Combine(_root.FullName, "pack.json"), new JsonObject { ["formatVersion"] = version,
            ["id"] = "witness", ["name"] = "Authored witness", ["bodies"] = bodies }.ToJsonString());
        return PackedSatelliteCatalog.Load(_root.FullName, warnings is null ? null : warnings.Add);
    }

    [Fact]
    public void FixedAttitudeRetainsTheIndependentScienceEpochState()
    {
        var satellite = Assert.Single(Read(Body()));
        Assert.True(satellite.TrySample(satellite.Epoch, out var sample));
        Assert.InRange((sample.EarthRelativeState.PositionKilometers - new Vector3d(4224.4245982988114, -6459.9662457795475, -.47199931797623285)).Length, 0, 1e-5);
        Assert.Equal(Basis3d.Identity, sample.BodyToTrajectory);
        Assert.True(satellite.TrySample(satellite.Epoch.AddSeconds(3600), out var later));
        Assert.Equal(sample.BodyToTrajectory, later.BodyToTrajectory);
        Assert.False(satellite.TrySample(satellite.Epoch.AddSeconds(366 * 86400), out _));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(1)]
    public void DeclaredContinuationIsContinuousAndConservesTwoBodyInvariants(int sign)
    {
        var body = Body(); body["timeline"]![0]!["orbit"]!["convention"] = "cosmographia";
        var satellite = Assert.Single(Read(body));
        double boundary = sign * 365 * 86400d;
        Assert.True(satellite.TrySample(satellite.Epoch.AddSeconds(boundary), out var edge));
        Assert.True(satellite.TrySample(satellite.Epoch.AddSeconds(boundary + sign), out var adjacent));
        var a = edge.EarthRelativeState; var b = adjacent.EarthRelativeState;
        Assert.InRange((b.PositionKilometers - a.PositionKilometers - a.VelocityKilometersPerSecond * sign).Length, 0, .02);
        Assert.True(satellite.TrySample(satellite.Epoch.AddSeconds(sign * 10 * 365 * 86400d), out var far));
        var c = far.EarthRelativeState;
        Assert.InRange(Math.Abs(Energy(c) / Energy(a) - 1), 0, 1e-10);
        Assert.InRange((Vector3d.Cross(c.PositionKilometers, c.VelocityKilometersPerSecond) -
            Vector3d.Cross(a.PositionKilometers, a.VelocityKilometersPerSecond)).Length, 0, 1e-6);
        Assert.Equal(edge.BodyToTrajectory, far.BodyToTrajectory);
        static double Energy(CartesianState state) => Vector3d.Dot(state.VelocityKilometersPerSecond, state.VelocityKilometersPerSecond) / 2 - 398600.4418 / state.PositionKilometers.Length;
    }

    [Theory]
    [InlineData("radius", false)]
    [InlineData("quaternion", false)]
    [InlineData("vector", false)]
    [InlineData("nonfinite-vector", false)]
    [InlineData("body-frame", true)]
    [InlineData("mesh-format", true)]
    [InlineData("mesh-center", true)]
    [InlineData("rotation", true)]
    [InlineData("rotation-frame", true)]
    public void InvalidPackedPhysicalPropertiesFailBeforePublishing(string fault, bool unsupported)
    {
        var body = Body();
        switch (fault)
        {
            case "radius": body["radiusKm"] = 0; break;
            case "quaternion": body["rotation"]!["quaternion"] = new JsonArray(0, 0, 0, 0); break;
            case "vector": body["appearance"]!["model"]!["centre"] = new JsonArray(0, 0); break;
            case "nonfinite-vector": body["appearance"]!["model"]!["centre"] = JsonNode.Parse("[1e309,0,0]"); break;
            case "body-frame": body["bodyFrame"] = new JsonObject { ["kind"] = "unknown" }; break;
            case "mesh-format": File.WriteAllText(Path.Combine(_root.FullName, "probe.3ds"), "fixture"); body["appearance"]!["model"]!["file"] = "probe.3ds"; break;
            case "mesh-center": body["appearance"]!["model"]!["centre"] = new JsonArray(1, 0, 0); break;
            case "rotation": body["rotation"]!["kind"] = "uniform"; break;
            case "rotation-frame": body["rotation"]!["frame"]!["kind"] = "unknown"; break;
        }
        if (unsupported) Assert.Throws<NotSupportedException>(() => Read(body));
        else Assert.Throws<InvalidDataException>(() => Read(body));
    }

    [Fact]
    public void UnknownTrajectoryFramesAreReportedAndDuplicateSatellitesAreRejected()
    {
        var body = Body(); body["timeline"]![0]!["orbit"]!["frame"]!["kind"] = "unknown";
        var warnings = new List<string>();
        Assert.Throws<InvalidDataException>(() => Read(body, warnings: warnings));
        Assert.Contains("unsupported packed satellite trajectory frame", Assert.Single(warnings));
        Assert.Throws<InvalidDataException>(() => Read(Body(), duplicate: true));
        Assert.Throws<NotSupportedException>(() => Read(Body(), version: 0));
        Assert.Throws<NotSupportedException>(() => Read(Body(), version: 20));
    }

    public void Dispose() => _root.Delete(recursive: true);
}
