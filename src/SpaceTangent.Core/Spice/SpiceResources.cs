// SPDX-License-Identifier: GPL-2.0-or-later
using SpaceTangent.Core.Addons;

namespace SpaceTangent.Core.Spice;

internal sealed class SpiceResources
{
    private const int MaximumDepth = 12, MaximumLoads = 256;
    private readonly Dictionary<string, SpkKernel> _spk;
    internal IReadOnlyList<string> Files { get; }

    internal SpiceResources(string root, string directory, IEnumerable<string> names)
        : this(root, names.Select(name => (directory, name))) { }

    internal SpiceResources(string root, IEnumerable<(string Directory, string Name)> sources)
    {
        StringComparer comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var active = new HashSet<string>(comparer);
        var files = new List<string>();
        int visits = 0;
        foreach (var (directory, name) in sources)
            Expand(root, directory, name, active, files, ref visits, 0);
        Files = files.AsReadOnly();
        _spk = new Dictionary<string, SpkKernel>(comparer);
    }

    internal SpkKernel[] TrajectoryKernels()
    {
        var kernels = new List<SpkKernel>();
        foreach (string path in Files)
        {
            if (!Path.GetExtension(path).Equals(".bsp", StringComparison.OrdinalIgnoreCase))
                continue;
            if (!_spk.TryGetValue(path, out var kernel))
                _spk.Add(path, kernel = new SpkKernel(path));
            kernels.Add(kernel);
        }
        if (kernels.Count == 0)
            throw new InvalidDataException("SPICE trajectory has no SPK file.");
        return [.. kernels];
    }

    private static void Expand(string root, string directory, string name, HashSet<string> active, List<string> result,
        ref int visits, int depth)
    {
        if (++visits > MaximumLoads || depth > MaximumDepth)
            throw new InvalidDataException("SPICE metakernel load limit exceeded.");
        string file = LocalAddonFiles.ResolveRelative(root, directory, name);
        if (!Path.GetExtension(file).Equals(".tm", StringComparison.OrdinalIgnoreCase))
        {
            result.Add(file);
            return;
        }
        if (!active.Add(file))
            throw new InvalidDataException("SPICE metakernel load cycle.");

        var text = new SpiceTextKernel();
        text.Furnish(file);
        var symbols = new Dictionary<string, string>(StringComparer.Ordinal);
        if (text.Contains("PATH_SYMBOLS") || text.Contains("PATH_VALUES"))
        {
            string[] labels = text.Get("PATH_SYMBOLS"), values = text.Get("PATH_VALUES");
            if (labels.Length != values.Length)
                throw new InvalidDataException("SPICE metakernel path labels and values must match.");
            for (int i = 0; i < labels.Length; i++)
            {
                if (!symbols.TryAdd(labels[i], values[i]))
                    throw new InvalidDataException("Duplicate SPICE metakernel path symbol.");
            }
        }

        foreach (string item in text.Get("KERNELS_TO_LOAD"))
            Expand(root, Path.GetDirectoryName(file)!, Substitute(item, symbols), active, result, ref visits, depth + 1);
        active.Remove(file);
    }

    private static string Substitute(string item, Dictionary<string, string> symbols)
    {
        if (!item.StartsWith('$'))
            return item;

        int separator = item.IndexOfAny(['/', '\\']);
        string symbol = separator < 0 ? item[1..] : item[1..separator];
        if (!symbols.TryGetValue(symbol, out string? value))
            throw new InvalidDataException($"Unknown SPICE metakernel path symbol {symbol}.");
        return separator < 0 ? value : Path.Combine(value, item[(separator + 1)..]);
    }
}
