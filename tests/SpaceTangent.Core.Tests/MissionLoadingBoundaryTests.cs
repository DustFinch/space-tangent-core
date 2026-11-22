// SPDX-License-Identifier: GPL-2.0-or-later
using System.Text.Json.Nodes;
using SpaceTangent.Contracts;
using SpaceTangent.Core.Addons;
using Xunit;

namespace SpaceTangent.Core.Tests;

public sealed class MissionLoadingBoundaryTests : IDisposable
{
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("st-mission-loading-");
    private static JsonObject Item() => new() { ["name"] = "Witness", ["center"] = "Earth",
        ["startTime"] = 2451545, ["endTime"] = 2451548,
        ["trajectory"] = new JsonObject { ["type"] = "FixedPoint", ["position"] = new JsonArray(1, 2, 3) } };
    private void Write(string name, JsonObject value) => File.WriteAllText(Path.Combine(_root.FullName, name), value.ToJsonString());
    private static ObjectId? Resolve(string name) => name switch { "Earth" => new("earth"), "Absent Sensor" => new("absent/sensor"), _ => null };
    private IReadOnlyList<SampledMission> Load(JsonObject item)
    {
        Write("root.json", new() { ["version"] = "1.0", ["items"] = new JsonArray(item) });
        return SampledMissionCatalog.Load("root.json", _root.FullName, Resolve);
    }

    [Theory]
    [InlineData("empty-name")]
    [InlineData("non-ascii-name")]
    [InlineData("no-trajectory")]
    [InlineData("empty-arcs")]
    [InlineData("many-arcs")]
    [InlineData("reversed-window")]
    [InlineData("out-of-window")]
    [InlineData("overlap")]
    [InlineData("missing-sensor")]
    public void InvalidMissionDefinitionsCannotPublishPartialTracks(string fault)
    {
        var item = Item();
        switch (fault)
        {
            case "empty-name": item["name"] = ""; break;
            case "non-ascii-name": item["name"] = "😀"; break;
            case "no-trajectory": item.Remove("trajectory"); break;
            case "empty-arcs": item["arcs"] = new JsonArray(); break;
            case "many-arcs": var many = new JsonArray(); for (int i = 0; i < 129; i++) many.Add(new JsonObject()); item["arcs"] = many; break;
            case "reversed-window": item["endTime"] = 2451545; break;
            case "out-of-window": var outside = item.DeepClone(); outside["startTime"] = 2451544; item["arcs"] = new JsonArray(outside); break;
            case "overlap":
                var a = item.DeepClone(); a["endTime"] = 2451547;
                var b = item.DeepClone(); b["startTime"] = 2451546;
                item["arcs"] = new JsonArray(a, b); break;
            case "missing-sensor": item["geometry"] = new JsonObject { ["type"] = "Observations", ["sensor"] = "Absent Sensor",
                ["groups"] = new JsonArray(new JsonObject { ["startTime"] = 2451545, ["endTime"] = 2451546 }) }; break;
        }
        Assert.Throws<InvalidDataException>(() => Load(item));
    }

    [Fact]
    public void DuplicateIdentifiersAndExcessDefinitionsAreRejected()
    {
        var first = Item(); var other = Item();
        Write("root.json", new() { ["version"] = "1.0", ["items"] = new JsonArray(first, other) });
        Assert.Throws<InvalidDataException>(() => SampledMissionCatalog.Load("root.json", _root.FullName, Resolve));
        var list = new JsonArray();
        for (int i = 0; i < 513; i++) { var item = Item(); item["name"] = "Witness " + i; list.Add(item); }
        Write("root.json", new() { ["version"] = "1.0", ["items"] = list });
        Assert.Throws<InvalidDataException>(() => SampledMissionCatalog.Load("root.json", _root.FullName, Resolve));
    }

    [Fact]
    public void DependencyDepthAndTotalFileCountAreBounded()
    {
        for (int i = 0; i < 18; i++) Write($"depth{i}.json", new() { ["version"] = "1.0",
            ["require"] = i == 17 ? new JsonArray() : new JsonArray($"depth{i + 1}.json") });
        Assert.Throws<InvalidDataException>(() => SampledMissionCatalog.Load("depth0.json", _root.FullName, Resolve));
        string[] roots = Enumerable.Range(0, 65).Select(i => $"root{i}.json").ToArray();
        foreach (string name in roots) Write(name, new() { ["version"] = "1.0" });
        Assert.Throws<InvalidDataException>(() => SampledMissionCatalog.Load(roots, _root.FullName, Resolve));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ARelocatablePackCannotRecursivelyContainOtherPacks(bool archived)
    {
        Directory.CreateDirectory(Path.Combine(_root.FullName, "content"));
        var manifest = new JsonObject { ["format"] = "spacetangent-mission-catalogues", ["version"] = 1,
            ["catalogues"] = new JsonArray("nested.json") };
        Write("pack.json", manifest);
        var nested = archived ? new JsonObject { ["formatVersion"] = 19, ["bodies"] = new JsonArray() } : manifest.DeepClone();
        File.WriteAllText(Path.Combine(_root.FullName, "content", "nested.json"), nested.ToJsonString());
        Assert.Throws<InvalidDataException>(() => SampledMissionCatalog.Load("pack.json", _root.FullName, Resolve));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(20)]
    public void UnknownArchivedVersionsAreNotTreatedAsSourceCatalogues(int version)
    {
        Write("root.json", new() { ["formatVersion"] = version, ["bodies"] = new JsonArray() });
        Assert.Throws<NotSupportedException>(() => SampledMissionCatalog.Load("root.json", _root.FullName, Resolve));
    }

    [Fact]
    public void EmptyArchivedBodiesAndUnknownSourceVersionsAreRejected()
    {
        foreach (var document in new[] { new JsonObject { ["formatVersion"] = 19 },
            new JsonObject { ["formatVersion"] = 19, ["bodies"] = new JsonArray() }, new JsonObject { ["version"] = "2.0" } })
        {
            Write("root.json", document);
            Assert.Throws<InvalidDataException>(() => SampledMissionCatalog.Load("root.json", _root.FullName, Resolve));
        }
    }

    [Fact]
    public void UnsupportedVisualizerReportsItsTagAndDoesNotPublishAnObject()
    {
        Write("root.json", new() { ["version"] = "1.0", ["items"] = new JsonArray(new JsonObject
            { ["type"] = "Visualizer", ["tag"] = "Unsupported witness", ["body"] = "Earth",
                ["style"] = new JsonObject { ["type"] = "Unknown" } }) });
        var warnings = new List<string>();
        Assert.Empty(SampledMissionCatalog.Load("root.json", _root.FullName, Resolve, warnings.Add));
        Assert.Contains("Unsupported witness", Assert.Single(warnings));
    }

    [Theory]
    [InlineData("bad-q-extension")]
    [InlineData("uniform-zero")]
    [InlineData("short-quaternion")]
    [InlineData("zero-quaternion")]
    [InlineData("overflow-quaternion")]
    public void InvalidAttitudeInputsCannotPublishAMission(string fault)
    {
        File.WriteAllText(Path.Combine(_root.FullName, "bad.xyz"), "not a Q file");
        var item = Item();
        item["rotationModel"] = fault switch
        {
            "bad-q-extension" => new JsonObject { ["type"] = "Interpolated", ["source"] = "bad.xyz" },
            "uniform-zero" => new JsonObject { ["type"] = "Uniform", ["period"] = 0 },
            _ => new JsonObject { ["type"] = "Fixed", ["quaternion"] = fault == "short-quaternion" ? new JsonArray(1, 0, 0) :
                fault == "zero-quaternion" ? new JsonArray(0, 0, 0, 0) : new JsonArray(1e200, 0, 0, 0) }
        };
        Assert.Throws<InvalidDataException>(() => Load(item));
    }

    [Theory]
    [InlineData(true)] [InlineData(false)]
    public void SpiceAttitudeAcceptsBothDeclaredFrameNameFields(bool explicitFrame)
    {
        var item = Item(); item["rotationModel"] = new JsonObject { ["type"] = "Spice", [explicitFrame ? "frame" : "name"] = "J2000" };
        Assert.IsType<MissionFrame.Spice>(Assert.Single(Assert.Single(Load(item)).Arcs).BodyFrame);
    }

    [Theory]
    [InlineData("Unlisted")]
    [InlineData("Interpolated")]
    public void UnsupportedAttitudeConventionsAreExplicitFailures(string type)
    {
        File.WriteAllText(Path.Combine(_root.FullName, "witness.q"), "");
        var item = Item(); item["rotationModel"] = new JsonObject { ["type"] = type, ["source"] = "witness.q", ["compatibility"] = "unlisted" };
        Assert.Throws<NotSupportedException>(() => Load(item));
    }

    public void Dispose() => _root.Delete(recursive: true);
}
