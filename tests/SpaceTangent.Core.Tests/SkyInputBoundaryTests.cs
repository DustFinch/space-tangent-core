// SPDX-License-Identifier: GPL-2.0-or-later
using System.Text.Json.Nodes;
using SpaceTangent.Core.Addons;
using SpaceTangent.Core.Observing;
using Xunit;

namespace SpaceTangent.Core.Tests;

public sealed class SkyInputBoundaryTests : IDisposable
{
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("st-sky-inputs-");
    private static JsonObject Figure() => JsonNode.Parse("""{"id":"W","lines":[[1,2]],"common_name":{"english":"Witness","native":"Native","context":"constellation"}}""")!.AsObject();
    private string Culture(JsonObject document)
    {
        string folder = Path.Combine(_root.FullName, "witness"); Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "index.json"), document.ToJsonString());
        return folder;
    }

    [Theory]
    [InlineData("mismatch")]
    [InlineData("duplicate-figure")]
    [InlineData("many-figures")]
    [InlineData("many-chains")]
    [InlineData("many-vertices")]
    public void InvalidCultureIsReportedAndDoesNotPublishAPartialFigure(string fault)
    {
        var figure = Figure(); var figures = new JsonArray(figure);
        var document = new JsonObject { ["id"] = "witness", ["constellations"] = figures };
        switch (fault)
        {
            case "mismatch": document["id"] = "other"; break;
            case "duplicate-figure": figures.Add(Figure()); break;
            case "many-figures": for (int i = 1; i < 1025; i++) figures.Add(new JsonObject()); break;
            case "many-chains":
                var chains = new JsonArray(); for (int i = 0; i < 1025; i++) chains.Add(new JsonArray(1, 2)); figure["lines"] = chains; break;
            case "many-vertices":
                var vertices = new JsonArray(); for (int i = 0; i < 1025; i++) vertices.Add(i + 1); figure["lines"] = new JsonArray(vertices); break;
        }
        Culture(document); var warnings = new List<string>();
        Assert.Empty(SkyCultureLibrary.Load(_root.FullName, warnings.Add).Cultures);
        Assert.StartsWith("witness:", Assert.Single(warnings));
    }

    [Fact]
    public void TooManyCultureFoldersFailBeforeReadingIndividualIndices()
    {
        for (int i = 0; i < 129; i++) Directory.CreateDirectory(Path.Combine(_root.FullName, "culture-" + i));
        Assert.Throws<InvalidDataException>(() => SkyCultureLibrary.Load(_root.FullName));
    }

    [Fact]
    public void MissingIndicesAndUnusableArtworkDoNotHideValidNamedLineChains()
    {
        Directory.CreateDirectory(Path.Combine(_root.FullName, "no-index"));
        var figure = Figure(); figure["lines"] = JsonNode.Parse("[[\"thin\",\"1\",false,null,2,\"bold\"]]");
        figure["image"] = JsonNode.Parse("""{"file":"unsupported.gif","size":[4,4],"anchors":[{"pos":[0,0],"hip":1},{"pos":[1,0],"hip":2},{"pos":[0,1],"hip":3}]}""");
        string folder = Culture(new() { ["id"] = "witness", ["constellations"] = new JsonArray(figure) });
        File.WriteAllText(Path.Combine(folder, "unsupported.gif"), "Not decoded by this parser.");
        File.WriteAllText(Path.Combine(folder, "description.md"), "# Authored title\n");
        File.WriteAllText(Path.Combine(folder, "ru.po"), "msgctxt \"constellation\"\nmsgid \"Witness\"\nmsgstr \"Свидетель\"\n");
        var library = SkyCultureLibrary.Load(_root.FullName);
        var actual = Assert.Single(Assert.Single(library.Cultures).Figures);
        Assert.Equal("Authored title", library.Find("witness")!.Title);
        Assert.Null(library.Find(null));
        Assert.Equal("Свидетель", actual.RussianName);
        Assert.Equal(new uint?[] { 1, null, null, 2 }, Assert.Single(actual.Lines));
        Assert.Null(actual.Artwork);
    }

    [Theory]
    [InlineData("wrong-format")]
    [InlineData("wrong-version")]
    [InlineData("no-catalogues")]
    [InlineData("empty-list")]
    [InlineData("many-catalogues")]
    [InlineData("non-string")]
    [InlineData("traversal")]
    [InlineData("duplicate")]
    [InlineData("extension")]
    public void UnsafeSkyManifestCannotPublishAnIndex(string fault)
    {
        var names = new JsonArray("a.dsc");
        var manifest = new JsonObject { ["format"] = "spacetangent-sky-catalogues", ["version"] = 1, ["catalogues"] = names };
        switch (fault)
        {
            case "wrong-format": manifest["format"] = "unknown"; break;
            case "wrong-version": manifest["version"] = 2; break;
            case "no-catalogues": manifest.Remove("catalogues"); break;
            case "empty-list": names.Clear(); break;
            case "many-catalogues": for (int i = 0; i < 128; i++) names.Add("other" + i + ".dsc"); break;
            case "non-string": names[0] = 1; break;
            case "traversal": names[0] = "../a.dsc"; break;
            case "duplicate": names.Add("A.DSC"); break;
            case "extension": names[0] = "a.json"; break;
        }
        File.WriteAllText(Path.Combine(_root.FullName, "a.dsc"), "");
        File.WriteAllText(Path.Combine(_root.FullName, SkyCataloguePack.ManifestName), manifest.ToJsonString());
        Assert.Throws<InvalidDataException>(() => DeepSkyIndex.Load(_root.FullName));
    }

    [Theory]
    [InlineData("nested")]
    [InlineData("exists")]
    [InlineData("manifest")]
    [InlineData("empty")]
    [InlineData("many-files")]
    [InlineData("cancelled")]
    public void RejectedPackBuildLeavesNoStagingDirectoryOrPublishedTarget(string fault)
    {
        string source = Path.Combine(_root.FullName, "source"); Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "a.dsc"), "");
        string destination = Path.Combine(_root.FullName, "packed");
        switch (fault)
        {
            case "nested": destination = Path.Combine(source, "nested"); break;
            case "exists": Directory.CreateDirectory(destination); break;
            case "manifest": File.WriteAllText(Path.Combine(source, SkyCataloguePack.ManifestName), "{}"); break;
            case "empty": File.Delete(Path.Combine(source, "a.dsc")); break;
            case "many-files": for (int i = 0; i < 64; i++) File.WriteAllText(Path.Combine(source, "other" + i + ".dsc"), ""); break;
        }
        if (fault == "cancelled") Assert.Throws<OperationCanceledException>(() => SkyCataloguePack.Build(source, destination, new CancellationToken(true)));
        else if (fault == "exists") Assert.Throws<IOException>(() => SkyCataloguePack.Build(source, destination));
        else Assert.Throws<InvalidDataException>(() => SkyCataloguePack.Build(source, destination));
        Assert.Empty(Directory.GetDirectories(_root.FullName, ".sky-pack-*"));
        if (fault != "exists") Assert.False(Directory.Exists(destination));
    }

    public void Dispose() => _root.Delete(recursive: true);
}
