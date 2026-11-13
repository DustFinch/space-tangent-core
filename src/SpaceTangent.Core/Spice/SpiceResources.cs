// SPDX-License-Identifier: GPL-2.0-or-later
using System.Text;
using SpaceTangent.Core.Addons;

namespace SpaceTangent.Core.Spice;

internal sealed class SpiceResources
{
    private enum KernelKind { Unknown, Meta, Text, Trajectory, Attitude }

    private const int MaximumDepth = 12, MaximumLoads = 256;
    private static readonly StringComparer FileNames = OperatingSystem.IsWindows() ?
        StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    private readonly Lazy<SpiceTextKernel> _text;
    private readonly Lazy<SpkKernel[]> _trajectories;
    private readonly Lazy<CkKernel[]> _attitudes;
    private readonly Lazy<SpiceFramePool> _frames;
    private readonly Lazy<SpiceStatePool> _states;

    internal SpiceResources(string root, string directory, IEnumerable<string> names)
        : this(root, names.Select(name => (directory, name))) { }

    internal SpiceResources(string root, IEnumerable<(string Directory, string Name)> sources)
        : this(sources.Select(source => (root, source.Directory, source.Name))) { }

    internal sealed record KernelGroup(string Root, IReadOnlyList<(string Directory, string Name)> Files,
        Action<FileNotFoundException>? Missing = null);

    internal SpiceResources(IEnumerable<(string Root, string Directory, string Name)> sources)
        : this(sources.Select(source => new KernelGroup(source.Root, [(source.Directory, source.Name)]))) { }

    internal SpiceResources(IEnumerable<KernelGroup> groups)
    {
        var active = new HashSet<string>(FileNames);
        var ordered = new List<(string Path, KernelKind Kind)>();
        int visits = 0;
        foreach (var group in groups)
        {
            int first = ordered.Count;
            try
            {
                foreach (var (directory, name) in group.Files)
                    Expand(group.Root, directory, name, active, ordered, ref visits, 0);
            }
            catch (FileNotFoundException error) when (group.Missing is not null)
            {
                ordered.RemoveRange(first, ordered.Count - first);
                active.Clear();
                group.Missing(error);
            }
        }
        _text = new(() =>
        {
            var pool = new SpiceTextKernel();
            foreach (var (path, kind) in ordered)
                if (kind is KernelKind.Meta or KernelKind.Text)
                    pool.Furnish(path);
            return pool;
        });
        _trajectories = new(() => Load(KernelKind.Trajectory, path => new SpkKernel(path)));
        _attitudes = new(() => Load(KernelKind.Attitude, path => new CkKernel(path)));
        _frames = new(() => new SpiceFramePool(Text, () => _attitudes.Value));
        _states = new(() => new SpiceStatePool(TrajectoryKernels(), Frames));

        T[] Load<T>(KernelKind wanted, Func<string, T> parse)
        {
            var parsed = new Dictionary<string, T>(FileNames);
            var output = new List<T>();
            foreach (var (path, kind) in ordered)
            {
                if (kind != wanted)
                    continue;
                if (!parsed.TryGetValue(path, out var kernel))
                    parsed.Add(path, kernel = parse(path));
                output.Add(kernel);
            }
            return output.ToArray();
        }
    }

    internal SpiceTextKernel Text => _text.Value;

    internal SpiceFramePool Frames => _frames.Value;
    internal SpiceStatePool States => _states.Value;

    private SpkKernel[] TrajectoryKernels() => _trajectories.Value is { Length: > 0 } kernels ? kernels :
        throw new InvalidDataException("SPICE trajectory has no SPK file.");


    private static void Expand(string root, string directory, string name, HashSet<string> active,
        List<(string Path, KernelKind Kind)> result, ref int visits, int depth)
    {
        string file = Path.Combine(directory, name);
        try
        {
            if (++visits > MaximumLoads || depth > MaximumDepth)
                throw new InvalidDataException("SPICE metakernel load limit exceeded.");
            file = LocalAddonFiles.ResolveRelative(root, directory, name);
            KernelKind kind = Classify(file);
            result.Add((file, kind));
            if (kind != KernelKind.Meta)
                return;
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
                for (int index = 0; index < labels.Length; index++)
                    if (!symbols.TryAdd(labels[index], values[index]))
                        throw new InvalidDataException("Duplicate SPICE metakernel path symbol.");
            }
            if (text.Contains("KERNELS_TO_LOAD"))
                foreach (string item in text.Get("KERNELS_TO_LOAD"))
                {
                    string relative = Substitute(item, symbols);
                    string owner = Path.GetDirectoryName(file)!;
                    // Archived pack tools can place a metakernel in data/ while leaving data/ prefixes in its children.
                    if (FileNames.Equals(owner, Path.Combine(root, "data")) &&
                        relative.Replace('\\', '/').StartsWith("data/", StringComparison.OrdinalIgnoreCase) &&
                        !AddonDependencies.FileExists(Path.Combine(owner, relative)))
                        owner = root;
                    Expand(root, owner, relative, active, result, ref visits, depth + 1);
                }
            active.Remove(file);
        }
        catch (Exception error) when (AddonDiagnostics.Annotate(error, AddonStage.Kernel, file))
        {
            throw;
        }
    }

    private static KernelKind Classify(string path)
    {
        KernelKind byExtension = Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".tm" => KernelKind.Meta,
            ".bsp" => KernelKind.Trajectory,
            ".bc" => KernelKind.Attitude,
            ".tf" or ".ti" or ".tsc" or ".tpc" or ".tls" => KernelKind.Text,
            _ => KernelKind.Unknown
        };
        if (byExtension != KernelKind.Unknown)
            return byExtension;
        using var file = AddonDependencies.OpenRead(path, 256L * 1024 * 1024);
        Span<byte> prefix = stackalloc byte[8];
        int read = file.ReadAtLeast(prefix, prefix.Length, throwOnEndOfStream: false);
        string header = Encoding.ASCII.GetString(prefix[..read]).TrimEnd();
        if (header.StartsWith("KPL/MK", StringComparison.Ordinal))
            return KernelKind.Meta;
        if (header.StartsWith("KPL/", StringComparison.Ordinal))
            return KernelKind.Text;
        return header switch
        {
            "DAF/SPK" => KernelKind.Trajectory,
            "DAF/CK" => KernelKind.Attitude,
            _ => KernelKind.Unknown
        };
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
