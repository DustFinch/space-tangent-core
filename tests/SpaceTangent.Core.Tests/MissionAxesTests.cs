// SPDX-License-Identifier: GPL-2.0-or-later
using System.Text.Json;
using SpaceTangent.Core.Addons;
using SpaceTangent.Core.Ephemerides;
using Xunit;

namespace SpaceTangent.Core.Tests;

public sealed class MissionAxesTests
{
    [AxesFact]
    [Trait("Category", "ExternalData")]
    public void MatchesBothArrowProfilesAndTheOriginalMissionVisualizers()
    {
        string oracle = Environment.GetEnvironmentVariable("SPACETANGENT_MISSION_AXES_ORACLE")!;
        using var file = JsonDocument.Parse(File.ReadAllBytes(oracle));
        var profiles = file.RootElement.GetProperty("profiles");
        foreach (MissionAxesKind kind in Enum.GetValues<MissionAxesKind>())
        {
            var expected = profiles[kind == MissionAxesKind.Geometry ? 0 : 1];
            var actual = MissionAxesMesh.Build(kind);
            var coordinates = expected.GetProperty("vertices").EnumerateArray().Select(value => value.GetSingle()).ToArray();
            Assert.Equal(coordinates.Length, actual.Vertices.Count);
            for (int index = 0; index < coordinates.Length; index++)
                Assert.InRange(Math.Abs(coordinates[index] - actual.Vertices[index]), 0, 2e-8f);
            Assert.Equal(expected.GetProperty("triangles").EnumerateArray().Select(value => value.GetInt32()), actual.Triangles);
            Assert.Equal(expected.GetProperty("boundingRadius").GetSingle(), actual.BoundingRadius, 7);
        }

        string raw = Environment.GetEnvironmentVariable("SPACETANGENT_NATIVE_ATTITUDE")!;
        string archive = Environment.GetEnvironmentVariable("SPACETANGENT_PACKED_MISSION_ROOT")!;
        var source = SampledMissionCatalog.Load(Path.Combine("catalog", "test.json"), raw,
            name => name == "Sun" ? SolarSystemSample.SunId : null);
        var packed = SampledMissionCatalog.Load("pack.json", archive,
            name => name == "Sol" ? SolarSystemSample.SunId : null);
        var body = Style(source, "body axes");
        Assert.Equal(MissionAxesKind.BodyVisualizer, body.Kind);
        Assert.Equal(900, body.ScaleKilometers);
        Assert.Equal(body, Style(packed, "body axes"));
        var frame = Style(source, "frame axes");
        Assert.Equal(MissionAxesKind.FrameVisualizer, frame.Kind);
        Assert.Equal(2, frame.ScaleKilometers);
        Assert.Equal(.3f, frame.Opacity);
        Assert.Equal("mission/cubesat", frame.ReferenceOwner?.Value);
        Assert.Equal(frame with { ReferenceOwner = null }, Style(packed, "frame axes"));
        Assert.True(body.Visible && frame.Visible);
    }

    private static MissionAxesStyle Style(IReadOnlyList<SampledMission> missions, string name) =>
        Assert.IsType<MissionAxesStyle>(Assert.Single(Assert.Single(missions, mission => mission.Name == name).Geometry!).Shape.Axes);

    private sealed class AxesFactAttribute : FactAttribute
    {
        public AxesFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SPACETANGENT_MISSION_AXES_ORACLE")) ||
                string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SPACETANGENT_NATIVE_ATTITUDE")) ||
                string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SPACETANGENT_PACKED_MISSION_ROOT")))
                Skip = "Set the native axes oracle and the original source and packed catalogues.";
        }
    }
}
