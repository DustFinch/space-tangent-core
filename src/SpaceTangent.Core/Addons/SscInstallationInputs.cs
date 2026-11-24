// SPDX-License-Identifier: GPL-2.0-or-later
using SpaceTangent.Contracts;
using SpaceTangent.Core.Spice;

namespace SpaceTangent.Core.Addons;

public static partial class SscBodyCatalogue
{
    private sealed record SscInstallationInputs(IReadOnlyList<SscDefinition> Definitions,
        IReadOnlyList<SscDefinition> Declarations, IReadOnlyList<PackedKernel> Kernels,
        IReadOnlyDictionary<string, int> KernelOrder, SscResourceContext Resources)
    {
        internal static SscInstallationInputs Read(string[] roots, CancellationToken cancellation)
        {
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
            var declarations = new List<SscDefinition>();
            var definitions = SscDefinitions.Load(documents, cancellation, declarations);
            return new(definitions, declarations, kernels, kernelOrder, new(owners));
        }
    }

    private sealed class SscResourceContext(Dictionary<string, (string Root, SscResource Resolve)> owners)
    {
        internal string RootFor(string directory) => owners[directory].Root;
        internal string Resolve(SscDefinition body, string field, AddonStage stage, string name, params string[] fallbacks) =>
            owners[body.DirectoryFor(field)].Resolve(body, field, stage, name, fallbacks);
    }
}
