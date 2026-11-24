// SPDX-License-Identifier: GPL-2.0-or-later
using SpaceTangent.Contracts;
using SpaceTangent.Core.Spice;

namespace SpaceTangent.Core.Addons;

public static partial class SscBodyCatalogue
{
    // Owns parent-first binding, rejection propagation and cycle detection.
    private sealed class SscRuntimeBinding
    {
        private readonly IReadOnlyList<SscDefinition> definitions;
        private readonly Func<SscDefinition, SscBodyDefinition?> read;
        private readonly Action<string>? warning;
        private readonly CancellationToken cancellation;
        private readonly Dictionary<string, SscDefinition> byPath;
        private readonly List<SscBodyDefinition> result = [];
        private readonly Dictionary<string, bool> visited = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> reading = new(StringComparer.OrdinalIgnoreCase);

        internal SscRuntimeBinding(IReadOnlyList<SscDefinition> definitions, Func<SscDefinition, SscBodyDefinition?> read,
            Action<string>? warning, CancellationToken cancellation)
        {
            this.definitions = definitions; this.read = read; this.warning = warning; this.cancellation = cancellation;
            byPath = definitions.ToDictionary(item => item.Parent + "/" + item.Name, StringComparer.OrdinalIgnoreCase);
        }
        internal IReadOnlyList<SscBodyDefinition> Bind()
        {
            foreach (var item in definitions) Read(item);
            return result.AsReadOnly();
        }

        private bool Read(SscDefinition item)
        {
            cancellation.ThrowIfCancellationRequested();
            string path = item.Parent + "/" + item.Name;
            if (visited.TryGetValue(path, out bool installed))
                return installed;
            if (!reading.Add(path))
                throw new InvalidDataException($"SSC parent cycle at {path}.");
            if (!byPath.TryGetValue(item.Parent, out var parent) || Read(parent))
            {
                try
                {
                    var body = read(item);
                    installed = body is not null;
                    if (body is not null)
                        result.Add(body);
                }
                catch (RejectedSampleTable error)
                {
                    warning?.Invoke($"{path}: {error.Message}");
                }
            }
            reading.Remove(path);
            visited[path] = installed;
            return installed;
        }
    }
}
