// SPDX-License-Identifier: GPL-2.0-or-later
using SpaceTangent.Core.Observing;
using Xunit;

namespace SpaceTangent.Core.Tests;

public sealed class DeepSkyInputBoundaryTests : IDisposable
{
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("st-deep-sky-inputs-");
    private DeepSkyIndex Read(string source)
    {
        File.WriteAllText(Path.Combine(_root.FullName, "witness.dsc"), source);
        return DeepSkyIndex.Load(_root.FullName);
    }

    [Theory]
    [InlineData("Unknown \"Witness\" {}")]
    [InlineData("Galaxy \"\" {}")]
    [InlineData("Galaxy \"Witness\" { RA 25 }")]
    [InlineData("Galaxy \"Witness\" { Dec 91 }")]
    [InlineData("Galaxy \"Witness\" { Distance 0 }")]
    [InlineData("Galaxy \"Witness\" { Radius 0 }")]
    [InlineData("Galaxy \"Witness\" { Angle 10 Axis [1 2] }")]
    public void InvalidDeepSkyGeometryOrMetadataCannotPublishAnIndex(string source)
    {
        Assert.Throws<InvalidDataException>(() => Read(source));
    }

    [Fact]
    public void ExactDuplicateRecordsDoNotCreateUnstableAlternativeIdentifiers()
    {
        const string record = "Galaxy \"Authored Witness\" { RA 1 Dec 2 Distance 10 Radius 1 }\n";
        var index = Read(record + record);
        Assert.Equal(DeepSkyIndex.Default.Objects.Count + 1, index.Objects.Count);
        Assert.Single(index.Objects, item => item.Name == "Authored Witness");
    }

    [Fact]
    public void NonAsciiNamesKeepTheirDisplayNameAndGetAStableHashedIdentifier()
    {
        var index = Read("Nebula \"Облако\" { Distance 10 Radius 1 }");
        var item = Assert.Single(index.Objects, item => item.Name == "Облако");
        Assert.Matches("^dso/[a-f0-9]+$", item.Id.Value);
        Assert.Equal(item.Id, Read("Nebula \"Облако\" { Distance 10 Radius 1 }").Objects.Single(entry => entry.Name == "Облако").Id);
    }

    [Fact]
    public void CatalogueRelativeModelsResolveInsideTheResourceRoot()
    {
        var model = _root.CreateSubdirectory("models"); string path = Path.Combine(model.FullName, "witness.obj");
        File.WriteAllText(path, "v 0 0 0\n");
        var index = Read("Nebula \"Model Witness\" { Mesh \"witness.obj\" Distance 10 Radius 1 }");
        var item = Assert.Single(index.Objects, item => item.Name == "Model Witness");
        Assert.Equal(path, DeepSkyIndex.ResolveMesh(_root.FullName, item));
        Assert.Throws<ArgumentException>(() => DeepSkyIndex.ResolveMesh(_root.FullName, item with { Mesh = null }));
    }

    [Fact]
    public void EmptyAndExcessiveCatalogueSourcesHaveExplicitBoundedBehavior()
    {
        Assert.Same(DeepSkyIndex.Default, DeepSkyIndex.Load(_root.FullName));
        for (int i = 0; i < 65; i++) File.WriteAllText(Path.Combine(_root.FullName, "source" + i + ".dsc"), "");
        Assert.Throws<InvalidDataException>(() => DeepSkyIndex.Load(_root.FullName));
    }

    public void Dispose() => _root.Delete(recursive: true);
}
