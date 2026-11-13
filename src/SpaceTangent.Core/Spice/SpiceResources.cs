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
        Action<Exception>? Unavailable = null, bool SkipRepeatedExplicit = false)
    {
        internal Func<string, string>? ResolveExplicit { get; init; }
        internal Action<int>? RecordPrefix { get; init; }
    }

    internal SpiceResources(IEnumerable<(string Root, string Directory, string Name)> sources)
        : this(sources.Select(source => new KernelGroup(source.Root, [(source.Directory, source.Name)]))) { }

    internal SpiceResources(IEnumerable<KernelGroup> groups) : this(ReadGroups(groups), null) { }

    private SpiceResources(KernelFiles files, int? count)
    {
        _text = new(() =>
        {
            var pool = new SpiceTextKernel();
            foreach (var (path, kind) in files.Ordered.Take(count ?? files.Ordered.Count))
                if (kind is KernelKind.Meta or KernelKind.Text)
                    pool.Apply(GetKernel(files.Text, path, SpiceTextKernel.Read));
            return pool;
        });
        _trajectories = new(() => Load(KernelKind.Trajectory, files.Spk, path => new SpkKernel(path)));
        _attitudes = new(() => Load(KernelKind.Attitude, files.Ck, path => new CkKernel(path)));
        _frames = new(() => new SpiceFramePool(Text, () => _attitudes.Value));
        _states = new(() => new SpiceStatePool(_trajectories.Value, Frames));
        Files = files;

        T[] Load<T>(KernelKind wanted, Dictionary<string, T> parsed, Func<string, T> parse) =>
            files.Ordered.Take(count ?? files.Ordered.Count).Where(file => file.Kind == wanted)
                .Select(file => GetKernel(parsed, file.Path, parse)).ToArray();
    }

    private sealed record KernelFiles(List<(string Path, KernelKind Kind)> Ordered,
        Dictionary<string, SpkKernel> Spk, Dictionary<string, CkKernel> Ck,
        Dictionary<string, IReadOnlyList<SpiceTextKernel.Assignment>> Text);

    private KernelFiles Files { get; }
    internal SpiceResources Prefix(int count) => new(Files, count);

    private static KernelFiles ReadGroups(IEnumerable<KernelGroup> groups)
    {
        var active = new HashSet<string>(FileNames);
        var ordered = new List<(string Path, KernelKind Kind)>();
        var parsedSpk = new Dictionary<string, SpkKernel>(FileNames);
        var parsedCk = new Dictionary<string, CkKernel>(FileNames);
        var parsedText = new Dictionary<string, IReadOnlyList<SpiceTextKernel.Assignment>>(FileNames);
        var explicitSscLoads = new HashSet<(string File, string Authored)>();
        var missingExplicit = new Dictionary<(string Directory, string Name), FileNotFoundException>();
        int visits = 0;
        foreach (var group in groups)
        {
            try
            {
                FileNotFoundException? skippedMissing = null;
                bool hasOtherKernel = false;
                foreach (var (directory, name) in group.Files)
                {
                    string physicalDirectory = directory, physicalName = name;
                    if (group.ResolveExplicit is { } resolve)
                    {
                        var key = (directory, name);
                        if (missingExplicit.TryGetValue(key, out var previousFailure))
                        {
                            skippedMissing ??= previousFailure;
                            continue;
                        }
                        try
                        {
                            string file = resolve(name);
                            physicalDirectory = Path.GetDirectoryName(file)!;
                            physicalName = Path.GetFileName(file);
                        }
                        catch (FileNotFoundException error) when (group.Unavailable is not null)
                        {
                            missingExplicit.TryAdd(key, error);
                            throw;
                        }
                    }

                    HashSet<(string File, string Authored)>? pending = group.SkipRepeatedExplicit ? [] : null;
                    Expand(group.Root, physicalDirectory, physicalName, active, ordered, ref visits, 0,
                        group.Unavailable is null ? null : Validate, explicitSscLoads, pending, group.SkipRepeatedExplicit ? name : null);
                    if (pending is not null)
                        explicitSscLoads.UnionWith(pending);
                    hasOtherKernel = true;
                }
                if (!hasOtherKernel && skippedMissing is not null)
                    throw skippedMissing;
                group.RecordPrefix?.Invoke(ordered.Count);
            }
            catch (Exception error) when (group.Unavailable is not null && IsKernelInputError(error))
            {
                active.Clear();
                group.Unavailable(error);
            }
        }
        return new(ordered, parsedSpk, parsedCk, parsedText);

        void Validate(string path, KernelKind kind)
        {
            if (kind == KernelKind.Trajectory)
                GetKernel(parsedSpk, path, file => new SpkKernel(file));
            else if (kind == KernelKind.Attitude)
                GetKernel(parsedCk, path, file => new CkKernel(file));
            else if (kind is KernelKind.Text or KernelKind.Meta)
                GetKernel(parsedText, path, SpiceTextKernel.Read);
        }
    }

    private static bool IsKernelInputError(Exception error) =>
        error is InvalidDataException or EndOfStreamException or FileNotFoundException or DecoderFallbackException;

    private static T GetKernel<T>(Dictionary<string, T> parsed, string path, Func<string, T> parse)
    {
        lock (parsed)
        {
            if (!parsed.TryGetValue(path, out var kernel))
            {
                kernel = AddonDiagnostics.Read(AddonStage.Kernel, path, () => parse(path));
                parsed.Add(path, kernel);
            }
            return kernel;
        }
    }

    internal SpiceTextKernel Text => _text.Value;

    internal SpiceFramePool Frames => _frames.Value;
    internal SpiceStatePool States => _trajectories.Value.Length > 0 ? _states.Value :
        throw new InvalidDataException("SPICE trajectory has no SPK file.");
    internal SpiceStatePool StatesAtInitialization => _states.Value;


    private static void Expand(string root, string directory, string name, HashSet<string> active,
        List<(string Path, KernelKind Kind)> result, ref int visits, int depth,
        Action<string, KernelKind>? validate,
        HashSet<(string File, string Authored)>? previousExplicit = null,
        HashSet<(string File, string Authored)>? pending = null, string? authored = null)
    {
        string file = Path.Combine(directory, name);
        try
        {
            file = LocalAddonFiles.ResolveRelative(root, directory, name);
            if (authored is not null && (previousExplicit!.Contains((file, authored)) || !pending!.Add((file, authored))))
                return;
            if (++visits > MaximumLoads || depth > MaximumDepth)
                throw new InvalidDataException("SPICE metakernel load limit exceeded.");
            KernelKind kind = Classify(file);
            validate?.Invoke(file, kind);
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
                    Expand(root, owner, relative, active, result, ref visits, depth + 1, validate);
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
