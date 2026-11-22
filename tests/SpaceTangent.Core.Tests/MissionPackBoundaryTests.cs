// SPDX-License-Identifier: GPL-2.0-or-later
using System.Text.Json;
using System.Text.Json.Nodes;
using SpaceTangent.Contracts;
using SpaceTangent.Core.Addons;
using Xunit;

namespace SpaceTangent.Core.Tests;

public sealed class MissionPackBoundaryTests : IDisposable
{
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("st-mission-pack-");
    private static ObjectId? Resolve(string name) => name == "Earth" ? new("earth") : null;
    private const string Source = """
        {"version":"1.0","items":[{"name":"Beacon","center":"Earth",
          "trajectory":{"type":"FixedPoint","position":[1,2,3]}}]}
        """;

    [Theory]
    [InlineData("version", "2")]
    [InlineData("version", "\"1\"")]
    [InlineData("catalogues", "[]")]
    [InlineData("catalogues", "[42]")]
    [InlineData("catalogues", "[\"\"]")]
    [InlineData("catalogues", "[\"input.ssc\"]")]
    [InlineData("catalogues", "[\"../outside.json\"]")]
    [InlineData("catalogues", "[\"catalogue.json\",\"./catalogue.json\"]")]
    public void InvalidManifestCannotPublishAMission(string field, string json)
    {
        string content = _root.CreateSubdirectory("content").FullName;
        File.WriteAllText(Path.Combine(content, "catalogue.json"), Source);
        var manifest = new JsonObject { ["format"] = "spacetangent-mission-catalogues", ["version"] = 1,
            ["catalogues"] = new JsonArray("catalogue.json") };
        manifest[field] = JsonNode.Parse(json);
        string path = Path.Combine(_root.FullName, "pack.json"); File.WriteAllText(path, manifest.ToJsonString());
        Assert.True(MissionCataloguePack.IsManifest(path));
        Assert.Throws<InvalidDataException>(() => SampledMissionCatalog.Load("pack.json", _root.FullName, Resolve));
    }

    [Fact]
    public void RootAndCatalogSubfolderArePackedInOrderAndRemainRelocatable()
    {
        string source = _root.CreateSubdirectory("source").FullName;
        string catalog = Directory.CreateDirectory(Path.Combine(source, "catalog")).FullName;
        File.WriteAllText(Path.Combine(source, "a.json"), Source);
        File.WriteAllText(Path.Combine(catalog, "b.json"), Source.Replace("Beacon", "Other Beacon"));
        string destination = Path.Combine(_root.FullName, "output");
        Assert.Equal((2, 0, 0), MissionCataloguePack.Build(source, destination, Resolve));
        var before = SampledMissionCatalog.Load("pack.json", destination, Resolve);
        Assert.Equal(new[] { "Beacon", "Other Beacon" }, before.Select(mission => mission.Name));
        string relocated = Path.Combine(_root.FullName, "relocated"); Directory.Move(destination, relocated);
        var after = SampledMissionCatalog.Load("pack.json", relocated, Resolve);
        Assert.Equal(before.Select(mission => mission.Name), after.Select(mission => mission.Name));
        Assert.Equal(new Vector3d(1, 2, 3), after[0].Arcs[0].LocalState(new(2451545, 0, TimeScale.Tdb))!.Value.PositionKilometers);
        Assert.Empty(Directory.GetDirectories(_root.FullName, ".mission-pack-*"));
    }

    [Fact]
    public void FailedAndCancelledBuildsLeaveNoOutputOrStagingDirectory()
    {
        string source = _root.CreateSubdirectory("source").FullName;
        File.WriteAllText(Path.Combine(source, "input.json"), "{ invalid json");
        string output = Path.Combine(_root.FullName, "output");
        Assert.ThrowsAny<JsonException>(() => MissionCataloguePack.Build(source, output, Resolve));
        Assert.False(Directory.Exists(output));
        Assert.Empty(Directory.GetDirectories(_root.FullName, ".mission-pack-*"));
        File.WriteAllText(Path.Combine(source, "input.json"), Source);
        Assert.Throws<OperationCanceledException>(() => MissionCataloguePack.Build(source, output, Resolve, cancellation: new(true)));
        Assert.False(Directory.Exists(output));
        Assert.Empty(Directory.GetDirectories(_root.FullName, ".mission-pack-*"));
    }

    [Fact]
    public void BuildRefusesMissingExistingAndNestedPathsBeforeCopying()
    {
        string source = _root.CreateSubdirectory("source").FullName;
        string output = Path.Combine(_root.FullName, "output");
        Assert.Throws<DirectoryNotFoundException>(() => MissionCataloguePack.Build(Path.Combine(_root.FullName, "missing"), output, Resolve));
        Assert.Throws<InvalidDataException>(() => MissionCataloguePack.Build(source, Path.Combine(source, "nested"), Resolve));
        Directory.CreateDirectory(output);
        Assert.Throws<IOException>(() => MissionCataloguePack.Build(source, output, Resolve));
        Assert.Throws<ArgumentNullException>(() => MissionCataloguePack.Build(source, output, null!));
        File.WriteAllText(Path.Combine(source, "pack.json"), "{}");
        Assert.Throws<InvalidDataException>(() => MissionCataloguePack.Build(source, Path.Combine(_root.FullName, "other"), Resolve));
        Assert.False(MissionCataloguePack.IsManifest(Path.Combine(source, "pack.json")));
    }

    [Fact]
    public void SourceCatalogueAndResourceCountsAreBounded()
    {
        string source = _root.CreateSubdirectory("source").FullName;
        string output = Path.Combine(_root.FullName, "output");
        Assert.Throws<InvalidDataException>(() => MissionCataloguePack.Build(source, output, Resolve));
        for (int i = 0; i < 64; i++) File.WriteAllText(Path.Combine(source, $"catalogue{i}.json"), Source);
        Assert.Throws<InvalidDataException>(() => MissionCataloguePack.Build(source, output, Resolve));
        for (int i = 1; i < 64; i++) File.Delete(Path.Combine(source, $"catalogue{i}.json"));
        for (int i = 0; i < 4096; i++) File.WriteAllText(Path.Combine(source, $"asset{i}.txt"), "fixture");
        Assert.Throws<InvalidDataException>(() => MissionCataloguePack.Build(source, output, Resolve));
        Assert.False(Directory.Exists(output));
        Assert.Empty(Directory.GetDirectories(_root.FullName, ".mission-pack-*"));
    }

    public void Dispose() => _root.Delete(recursive: true);
}
