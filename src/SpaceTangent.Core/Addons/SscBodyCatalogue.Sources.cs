// SPDX-License-Identifier: GPL-2.0-or-later
using SpaceTangent.Contracts;
using SpaceTangent.Core.Spice;

namespace SpaceTangent.Core.Addons;

public sealed record InstalledMissionCatalogues(IReadOnlyList<SscBodyDefinition> Bodies, SampledMissionContent Missions)
{
    public SpiceCatalogResult? DedicatedMission { get; init; }
}

public static partial class SscBodyCatalogue
{
    /// <summary>Applies enabled SSC sources in order while resolving each inherited field in its owning source.</summary>
    public static IReadOnlyList<SscBodyDefinition> LoadSources(IEnumerable<string> directories, Action<string> warning,
        CancellationToken cancellation = default, ISscScriptModels? scripts = null) =>
        LoadSources(directories, warning, cancellation, scripts, null, out _);

    /// <summary>Loads SSC sources followed by mission catalogues into one ordered world SPICE pool.</summary>
    public static InstalledMissionCatalogues LoadWithMissions(IEnumerable<string> sscDirectories,
        IEnumerable<string> missionCatalogues, string missionRoot, Func<string, ObjectId?> resolveBody,
        Action<string> sscWarning, Action<string>? missionWarning = null, CancellationToken cancellation = default,
        ISscScriptModels? scripts = null, string? dedicatedMission = null,
        IEnumerable<string>? dedicatedSscCatalogues = null)
    {
        IReadOnlyList<SscBodyDefinition> bodies = [];
        IReadOnlyList<SpiceResources.KernelGroup> dedicatedLoads = [];
        Func<SpiceResources, SpiceCatalogResult>? bindDedicated = null;
        if (dedicatedMission is not null)
        {
            string file = Path.GetFullPath(dedicatedMission);
            string root = Path.GetDirectoryName(file)!;
            try
            {
                bindDedicated = Path.GetExtension(file).ToLowerInvariant() switch
                {
                    ".ssc" => SscSpiceCatalog.Read(dedicatedSscCatalogues ?? [file], root, out dedicatedLoads),
                    ".json" => SpiceJsonCatalog.Read([file], root, out dedicatedLoads),
                    _ => throw new NotSupportedException("Use an SSC or JSON mission catalogue.")
                };
            }
            catch (Exception error) when (AddonDiagnostics.Annotate(error, AddonStage.Catalog, file, source: "SPICE mission"))
            {
                throw;
            }
        }
        SpiceResources? pool = null;
        var missions = SampledMissionCatalog.LoadContent(missionCatalogues, missionRoot, resolveBody, missionWarning,
            cancellation, loads =>
            {
                try
                {
                    bodies = LoadSources(sscDirectories, sscWarning, cancellation, scripts, [.. loads, .. dedicatedLoads], out var resources);
                    pool = resources;
                    return resources;
                }
                catch (Exception error) when (AddonDiagnostics.Annotate(error, AddonStage.Catalog, source: "SSC body catalogue"))
                {
                    throw;
                }
            });
        return new(bodies, missions)
        {
            DedicatedMission = bindDedicated is null ? null : AddonDiagnostics.Read(AddonStage.Catalog, dedicatedMission,
                () => bindDedicated(pool!))
        };
    }

    private static IReadOnlyList<SscBodyDefinition> LoadSources(IEnumerable<string> directories, Action<string> warning,
        CancellationToken cancellation, ISscScriptModels? scripts,
        IReadOnlyList<SpiceResources.KernelGroup>? missionLoads, out SpiceResources resources)
    {
        ArgumentNullException.ThrowIfNull(directories);
        ArgumentNullException.ThrowIfNull(warning);
        string[] roots = directories.Select(Path.GetFullPath).ToArray();
        if (roots.Length == 1 && missionLoads is null)
        {
            resources = null!; // The standalone caller does not consume the pool.
            return LoadWithMpc(roots[0], warning, cancellation, scripts);
        }
        var documents = new List<SscDocument>();
        var kernels = new List<PackedKernel>();
        var kernelOrder = new Dictionary<string, int>(StringComparer.Ordinal);
        var owners = new Dictionary<string, (string Root, SscResource Resolve)>(StringComparer.OrdinalIgnoreCase);
        foreach (string root in roots)
        {
            cancellation.ThrowIfCancellationRequested();
            if (!AddonDependencies.DirectoryExists(root))
                continue;
            string manifest = Path.Combine(root, "pack.json");
            var source = AddonDependencies.FileExists(manifest)
                ? AddonDiagnostics.Read(AddonStage.Catalog, manifest, () => ReadPackedDocuments(root, manifest, cancellation))
                : ReadLooseDocuments(root);
            kernels.AddRange(source.Kernels);
            if (source.KernelOrder is { } positions)
                foreach (var position in positions)
                    kernelOrder[position.Key] = position.Value;
            foreach (var document in source.Documents)
            {
                documents.Add(document);
                owners[Path.GetDirectoryName(document.File)!] = (root, source.Resolve);
            }
        }
        var definitions = SscDefinitions.Load(documents, cancellation);
        var bodies = ReadBodyDefinitions(definitions, directory => owners[directory].Root, scripts, warning,
            (body, field, stage, name, fallbacks) => owners[body.DirectoryFor(field)].Resolve(body, field, stage, name, fallbacks),
            cancellation, roots, kernels, out resources, missionLoads, kernelOrder).ToList();
        foreach (string root in roots)
            AppendMpc(root, bodies, warning, cancellation);
        return bodies.AsReadOnly();
    }
}
