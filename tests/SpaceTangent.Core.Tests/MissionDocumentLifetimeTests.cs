// SPDX-License-Identifier: GPL-2.0-or-later
using SpaceTangent.Contracts;
using SpaceTangent.Core.Addons;
using SpaceTangent.Core.Spice;
using Xunit;

namespace SpaceTangent.Core.Tests;

public sealed class MissionDocumentLifetimeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "spacetangent-source-scope-" + Guid.NewGuid().ToString("N"));
    public MissionDocumentLifetimeTests() => Directory.CreateDirectory(_root);
    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void RequiredDocumentsRemainAliveInSourceOrderUntilBindingScopeEnds()
    {
        File.WriteAllText(Path.Combine(_root, "dependency.json"), """
            {"version":"1.0","items":[{"name":"Dependency","geometry":{"type":"Mesh"}}]}
            """);
        File.WriteAllText(Path.Combine(_root, "main.json"), """
            {"version":"1.0","require":["dependency.json"],"items":[{"name":"Main","geometry":{"type":"Mesh"}}]}
            """);
        var documents = SampledMissionCatalog.MissionDocumentSet.Open(["main.json"], _root, _ => null, null, default);
        Assert.Equal(new[] { "Dependency", "Main" }, documents.Definitions.Select(item => item.Item.GetProperty("name").GetString()));
        var element = documents.Definitions[0].Item;
        Assert.NotEqual(default, documents.Resolve("Dependency"));
        documents.Dispose(); documents.Dispose();
        Assert.Throws<ObjectDisposedException>(() => element.GetProperty("name"));
    }

    [Fact]
    public void ASourceCycleFailsBeforeTheInstallationPoolIsBound()
    {
        File.WriteAllText(Path.Combine(_root, "cycle.json"), """
            {"version":"1.0","require":["cycle.json"],"items":[]}
            """);
        bool bound = false;
        var error = Assert.Throws<InvalidDataException>(() => SampledMissionCatalog.LoadContent(["cycle.json"], _root,
            _ => null, null, default, groups => { bound = true; return new SpiceResources(groups); }));
        Assert.Contains("dependency cycle", error.Message);
        Assert.False(bound);
    }

    [Fact]
    public void EmptySourceEnumerationKeepsTheExistingCancellationBoundary()
    {
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        using var documents = SampledMissionCatalog.MissionDocumentSet.Open([], _root, _ => null, null, cancellation.Token);
        Assert.Empty(documents.Definitions);
        Assert.Empty(documents.KernelLoads);
    }
}
