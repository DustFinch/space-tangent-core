// SPDX-License-Identifier: GPL-2.0-or-later
using System.Text.Json.Nodes;
using SpaceTangent.Contracts;
using SpaceTangent.Core.Addons;
using SpaceTangent.Core.World;
using Xunit;

namespace SpaceTangent.Core.Tests;

public sealed class MissionOverlayContractTests : IDisposable
{
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("st-mission-overlay-");
    private static readonly ObjectId Earth = new("earth"), Moon = new("moon");
    private static ObjectId? Resolve(string name) => name switch { "Earth" => Earth, "Moon" => Moon, _ => null };
    private IReadOnlyList<SampledMission> Load(JsonObject geometry, List<string>? warnings = null)
    {
        var item = new JsonObject { ["name"] = "Witness", ["center"] = "Earth", ["geometry"] = geometry,
            ["trajectory"] = new JsonObject { ["type"] = "FixedPoint", ["position"] = new JsonArray(1, 2, 3) } };
        File.WriteAllText(Path.Combine(_root.FullName, "mission.json"),
            new JsonObject { ["version"] = "1.0", ["items"] = new JsonArray(item) }.ToJsonString());
        return SampledMissionCatalog.Load("mission.json", _root.FullName, Resolve, warnings is null ? null : warnings.Add);
    }

    [Theory]
    [InlineData(true, 0, 0, 1)]
    [InlineData(true, .3, 12, 12)]
    [InlineData(true, 1, 1000, 100)]
    [InlineData(false, 0, 0, 1)]
    [InlineData(false, .3, 12, 12)]
    [InlineData(false, 1, 1000, 100)]
    public void SourceVisualizersPreserveColorSizeVisibilityAndBoundTheirDisplayParameters(bool plane, double opacity, int input, int subdivisions)
    {
        var geometry = new JsonObject { ["type"] = plane ? "PlaneVisualizer" : "DirectionVisualizer", ["target"] = "Moon",
            ["size"] = 250, ["color"] = "#4080ff", ["opacity"] = opacity, ["gridSubdivision"] = input, ["visible"] = false };
        var mission = Assert.Single(Load(geometry));
        var style = Assert.IsType<MissionVectorStyle>(Assert.Single(mission.Geometry!).Shape.VectorOverlay);
        Assert.Equal(250, style.SizeKilometers);
        Assert.Equal(new Vector3d(64 / 255d, 128 / 255d, 1), style.Color);
        Assert.Equal(Math.Clamp(opacity, 0, 1), style.Opacity);
        Assert.Equal(subdivisions, style.Subdivisions);
        Assert.Equal(plane ? null : Moon, style.Target);
        Assert.False(style.Visible);
    }

    [Theory]
    [InlineData("size", "0")]
    [InlineData("size", "-1")]
    [InlineData("size", "1e100")]
    [InlineData("color", "[1,2,3]")]
    [InlineData("color", "\"invalid\"")]
    [InlineData("color", "[1,0]")]
    public void InvalidVisualizerDimensionsAndColorAreRejected(string key, string json)
    {
        var geometry = new JsonObject { ["type"] = "PlaneVisualizer", [key] = JsonNode.Parse(json) };
        Assert.Throws<InvalidDataException>(() => Load(geometry));
    }

    [Fact]
    public void DirectionRequiresADistinctAvailableTargetAndNormalizesTheRay()
    {
        var mission = Assert.Single(Load(new JsonObject { ["type"] = "DirectionVisualizer", ["target"] = "Moon" }));
        var style = Assert.IsType<MissionVectorStyle>(Assert.Single(mission.Geometry!).Shape.VectorOverlay);
        Assert.Equal(1, style.SizeKilometers); Assert.Equal(1, style.Opacity); Assert.Equal(10, style.Subdivisions);
        var graph = new WorldGraphBuilder(); graph.AddTrajectory(Moon, _ => new(new(0, 3, 4), default));
        var world = graph.Build().Evaluate(new(2451545, 0, TimeScale.Tdb));
        Assert.True(style.TryDirection(world, default, out var ray));
        Assert.Equal(new Vector3d(0, .6, .8), ray);
        Assert.False(style.TryDirection(world, new(0, 3, 4), out _));
        Assert.False((style with { Target = Earth }).TryDirection(world, default, out _));
        Assert.False((style with { Target = null }).TryDirection(world, default, out _));
    }

    [Fact]
    public void UnsupportedGeometryIsRejectedWithASpecificWarning()
    {
        var warnings = new List<string>();
        Assert.Empty(Load(new JsonObject { ["type"] = "UnrecognizedGeometry" }, warnings));
        Assert.Contains(warnings, warning => warning.Contains("UnrecognizedGeometry", StringComparison.Ordinal));
    }

    [Fact]
    public void NestedAndOversizedGeometrySequencesAreBounded()
    {
        JsonObject geometry = new() { ["type"] = "PlaneVisualizer" };
        for (int i = 0; i < 17; i++) geometry = new JsonObject { ["type"] = "TimeSwitched", ["sequence"] =
            new JsonArray(new JsonObject { ["startTime"] = 2451545, ["geometry"] = geometry }) };
        Assert.Throws<InvalidDataException>(() => Load(geometry));
        var steps = new JsonArray(); for (int i = 0; i < 129; i++) steps.Add(new JsonObject());
        Assert.Throws<InvalidDataException>(() => Load(new JsonObject { ["type"] = "TimeSwitched", ["sequence"] = steps }));
        Assert.Throws<InvalidDataException>(() => Load(new JsonObject { ["type"] = "TimeSwitched", ["sequence"] = new JsonArray(new JsonObject()) }));
        Assert.Empty(Assert.Single(Load(new JsonObject { ["type"] = "TimeSwitched", ["sequence"] = new JsonArray() })).Geometry!);
    }

    public void Dispose() => _root.Delete(recursive: true);
}
