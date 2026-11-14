// SPDX-License-Identifier: GPL-2.0-or-later
using System.Text;
using SpaceTangent.Core.Addons;

namespace SpaceTangent.Core.Spice;

internal sealed class SpiceResources
{
    private enum KernelKind { Unknown, Meta, Text, Trajectory, Attitude }

    private const int MaximumLoads = 256;
    private static readonly HashSet<string> LoadControls = new(StringComparer.OrdinalIgnoreCase)
        { "KERNELS_TO_LOAD", "PATH_SYMBOLS", "PATH_VALUES" };
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
                    pool.Apply(GetKernel(files.Text, path, SpiceTextKernel.Read).Where(value => !LoadControls.Contains(value.Name)));
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
                    Expand(group.Root, physicalDirectory, physicalName, ordered, ref visits, dependency: false,
                        group.Unavailable is not null || group.SkipRepeatedExplicit ? Validate : null, ReadText, explicitSscLoads, pending, group.SkipRepeatedExplicit ? name : null);
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

        }

        IReadOnlyList<SpiceTextKernel.Assignment> ReadText(string path) =>
            GetKernel(parsedText, path, SpiceTextKernel.Read);
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


    private static void Expand(string root, string directory, string name,
        List<(string Path, KernelKind Kind)> result, ref int visits, bool dependency,
        Action<string, KernelKind>? validate, Func<string, IReadOnlyList<SpiceTextKernel.Assignment>> readText,
        HashSet<(string File, string Authored)>? previousExplicit = null,
        HashSet<(string File, string Authored)>? pending = null, string? authored = null)
    {
        string file = Path.Combine(directory, name);
        try
        {
            file = LocalAddonFiles.ResolveRelative(root, directory, name);
            if (authored is not null && (previousExplicit!.Contains((file, authored)) || !pending!.Add((file, authored))))
                return;
            if (++visits > MaximumLoads)
                throw new InvalidDataException("SPICE metakernel load limit exceeded.");
            KernelKind kind = Classify(file);
            var assignments = kind is KernelKind.Text or KernelKind.Meta ? readText(file) : [];
            validate?.Invoke(file, kind);
            result.Add((file, kind));
            if (!assignments.Any(value => LoadControls.Contains(value.Name)))
                return;
            if (dependency)
                throw new InvalidDataException("SPICE(RECURSIVELOADING): a metakernel child contains loading instructions.");

            var text = new SpiceTextKernel();
            text.Apply(assignments);
            foreach (string relative in KernelPaths(text))
            {
                string owner = Path.GetDirectoryName(file)!;
                // Archived pack tools can place a metakernel in data/ while leaving data/ prefixes in its children.
                if (FileNames.Equals(owner, Path.Combine(root, "data")) &&
                    relative.Replace('\\', '/').StartsWith("data/", StringComparison.OrdinalIgnoreCase) &&
                    !AddonDependencies.FileExists(Path.Combine(owner, relative)))
                    owner = root;
                Expand(root, owner, relative, result, ref visits, dependency: true, validate, readText);
            }
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
        using var file = AddonDependencies.OpenRead(path, 256L * 1024 * 1024);
        Span<byte> prefix = stackalloc byte[1024];
        int read = file.ReadAtLeast(prefix, prefix.Length, throwOnEndOfStream: false);
        string header = Encoding.ASCII.GetString(prefix[..Math.Min(8, read)]).TrimEnd();
        if (header.StartsWith("KPL/MK", StringComparison.Ordinal))
            return KernelKind.Meta;
        if (header.StartsWith("KPL/", StringComparison.Ordinal))
            return KernelKind.Text;
        return header switch
        {
            "DAF/SPK" => KernelKind.Trajectory,
            "DAF/CK" => KernelKind.Attitude,
            _ when prefix[..read].IndexOf("\\begindata"u8) >= 0 => KernelKind.Text,
            _ => byExtension
        };
    }

    private static IEnumerable<string> KernelPaths(SpiceTextKernel text)
    {
        const int pathLimit = 255;
        var names = Entries("PATH_SYMBOLS").Select(value => value.TrimEnd()).ToArray();
        var values = Continue(Entries("PATH_VALUES")).ToArray();
        if (names.Length > 0 && values.Any(value => value.Length > pathLimit))
            throw new InvalidDataException("SPICE(PATHTOOLONG): a metakernel path value exceeds 255 characters.");
        if (names.Length > 0 && names.Length != values.Length)
            throw new InvalidDataException("SPICE(PATHMISMATCH): metakernel path labels and values must match.");
        var symbols = names.Zip(values).OrderByDescending(pair => pair.First.Length).ToArray();
        foreach (string listed in Continue(Entries("KERNELS_TO_LOAD")))
        {
            if (listed.Length > pathLimit)
                throw new InvalidDataException("SPICE(FILENAMETOOLONG): a metakernel filename exceeds 255 characters.");
            string file = listed;
            for (int offset = 0; offset < file.Length;)
            {
                int dollar = file.IndexOf('$', offset);
                if (dollar < 0)
                    break;
                foreach (var (symbol, value) in symbols)
                {
                    if (symbol.Length == 0 || !file.AsSpan(dollar + 1).StartsWith(symbol, StringComparison.Ordinal))
                        continue;
                    if (listed.Length + value.Length - symbol.Length - 1 > pathLimit)
                        throw new InvalidDataException("SPICE(FILENAMETOOLONG): the expanded metakernel filename exceeds 255 characters.");
                    file = string.Concat(file.AsSpan(0, dollar), value, file.AsSpan(dollar + 1 + symbol.Length));
                    if (file.Length > pathLimit)
                        file = file[..pathLimit];
                    break;
                }
                offset = dollar + 1;
            }
            yield return file;
        }

        IEnumerable<string> Entries(string name) => text.ValuesOrEmpty(name);

        static IEnumerable<string> Continue(IEnumerable<string> entries)
        {
            var joined = new StringBuilder();
            foreach (string entry in entries)
            {
                string part = entry.TrimEnd();
                if (part.EndsWith('+'))
                {
                    joined.Append(part.AsSpan(0, part.Length - 1));
                    continue;
                }
                joined.Append(part);
                yield return joined.ToString();
                joined.Clear();
            }
            if (joined.Length > 0)
                yield return joined.ToString();
        }
    }
}
