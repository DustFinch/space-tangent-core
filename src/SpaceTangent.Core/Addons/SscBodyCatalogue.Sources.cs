// SPDX-License-Identifier: GPL-2.0-or-later
namespace SpaceTangent.Core.Addons;

public static partial class SscBodyCatalogue
{
    /// <summary>Applies enabled SSC sources in order while resolving each inherited field in its owning source.</summary>
    public static IReadOnlyList<SscBodyDefinition> LoadSources(IEnumerable<string> directories, Action<string> warning,
        CancellationToken cancellation = default, ISscScriptModels? scripts = null)
    {
        ArgumentNullException.ThrowIfNull(directories);
        ArgumentNullException.ThrowIfNull(warning);
        string[] roots = directories.Select(Path.GetFullPath).ToArray();
        if (roots.Length == 1)
            return LoadWithMpc(roots[0], warning, cancellation, scripts);
        var documents = new List<SscDocument>();
        var kernels = new List<(string Root, string Directory, string Name)>();
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
            foreach (var document in source.Documents)
            {
                documents.Add(document);
                owners[Path.GetDirectoryName(document.File)!] = (root, source.Resolve);
            }
        }
        var definitions = SscDefinitions.Load(documents, cancellation);
        var bodies = ReadBodyDefinitions(definitions, directory => owners[directory].Root, scripts, warning,
            (body, field, stage, name, fallbacks) => owners[body.DirectoryFor(field)].Resolve(body, field, stage, name, fallbacks),
            cancellation, roots, kernels).ToList();
        foreach (string root in roots)
            AppendMpc(root, bodies, warning, cancellation);
        return bodies.AsReadOnly();
    }
}
