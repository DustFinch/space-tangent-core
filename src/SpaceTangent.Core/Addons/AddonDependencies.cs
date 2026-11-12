// SPDX-License-Identifier: GPL-2.0-or-later
using System.Buffers;
using System.Globalization;
using System.Security.Cryptography;

namespace SpaceTangent.Core.Addons;

/// <summary>The filesystem observations used by one synchronously decoded add-on.</summary>
public sealed class AddonDependencies
{
    private enum Observation { Contents, File, Directory, Attributes, Listing }
    private readonly record struct Input(Observation Kind, string Path, string? Pattern = null);
    private sealed record Recorder(Dictionary<Input, string?> Inputs, CancellationToken Cancellation);
    [ThreadStatic] private static Recorder? _recording;
    private readonly KeyValuePair<Input, string?>[] _inputs;

    private AddonDependencies(Dictionary<Input, string?> inputs) => _inputs = [.. inputs];

    public static (T Value, AddonDependencies Dependencies) Capture<T>(Func<T> load, CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(load);
        cancellation.ThrowIfCancellationRequested();
        var previous = _recording;
        var recorder = new Recorder(new(), cancellation);
        _recording = recorder;
        try
        {
            T value = load();
            cancellation.ThrowIfCancellationRequested();
            return (value, new(recorder.Inputs));
        }
        finally
        {
            _recording = previous;
        }
    }

    public bool IsCurrent(CancellationToken cancellation = default)
    {
        try
        {
            foreach (var (input, expected) in _inputs)
            {
                cancellation.ThrowIfCancellationRequested();
                string? actual = input.Kind switch
                {
                    Observation.File => File.Exists(input.Path) ? "1" : "0",
                    Observation.Directory => Directory.Exists(input.Path) ? "1" : "0",
                    Observation.Attributes => Attributes(File.GetAttributes(input.Path)),
                    Observation.Listing => JoinFiles(Directory.GetFiles(input.Path, input.Pattern!)),
                    Observation.Contents => FileHash(input.Path, expected, cancellation),
                    _ => throw new InvalidOperationException("Unknown add-on filesystem observation.")
                };
                if (actual != expected)
                    return false;
            }
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public static bool FileExists(string path)
    {
        bool exists = File.Exists(path);
        Note(Observation.File, path, exists ? "1" : "0");
        return exists;
    }

    public static bool DirectoryExists(string path)
    {
        bool exists = Directory.Exists(path);
        Note(Observation.Directory, path, exists ? "1" : "0");
        return exists;
    }

    public static FileAttributes GetAttributes(string path)
    {
        FileAttributes attributes = File.GetAttributes(path);
        Note(Observation.Attributes, path, Attributes(attributes));
        return attributes;
    }

    public static string[] ListFiles(string path, string pattern)
    {
        string[] files = Directory.GetFiles(path, pattern);
        string listing = JoinFiles(files);
        Note(Observation.Listing, path, listing, pattern);
        return files;
    }

    /// <summary>Records the content of the same file handle used by the parser.</summary>
    public static FileStream OpenRead(string path, long maximumBytes) => OpenRead(path, maximumBytes, FileShare.Read);

    internal static FileStream OpenRead(string path, long maximumBytes, FileShare sharing)
    {
        FileStream file;
        try
        {
            file = File.Open(path, FileMode.Open, FileAccess.Read, sharing);
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
        {
            Note(Observation.Contents, path, null);
            throw;
        }
        if (_recording is not { } recorder)
            return file;
        try
        {
            // Parsers check their own limits before consuming the file.
            if (file.Length <= maximumBytes)
            {
                Note(Observation.Contents, file.Name, LengthPrefix(file) + Hash(file, recorder.Cancellation));
                file.Position = 0;
            }
            return file;
        }
        catch
        {
            file.Dispose();
            throw;
        }
    }

    private static void Note(Observation kind, string path, string? value, string? pattern = null)
    {
        if (_recording is not { } recorder)
            return;
        var input = new Input(kind, Path.GetFullPath(path), pattern);
        if (!recorder.Inputs.TryAdd(input, value) && recorder.Inputs[input] != value)
            throw new IOException($"Add-on dependency changed while loading: {input.Path}.");
    }

    private static string Attributes(FileAttributes attributes) =>
        (attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)).ToString();

    private static string JoinFiles(string[] files)
    {
        Array.Sort(files, StringComparer.Ordinal);
        return string.Join('\0', files.Select(Path.GetFullPath));
    }

    private static string? FileHash(string path, string? expected, CancellationToken cancellation)
    {
        try
        {
            using FileStream file = File.OpenRead(path);
            string prefix = LengthPrefix(file);
            return expected?.StartsWith(prefix, StringComparison.Ordinal) == true ? prefix + Hash(file, cancellation) : "";
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
    }

    private static string LengthPrefix(FileStream file) => file.Length.ToString(CultureInfo.InvariantCulture) + ":";

    private static string Hash(FileStream file, CancellationToken cancellation)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            int read;
            do
            {
                cancellation.ThrowIfCancellationRequested();
                read = file.Read(buffer);
                hash.AppendData(buffer.AsSpan(0, read));
            } while (read != 0);
            return Convert.ToHexString(hash.GetHashAndReset());
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}
