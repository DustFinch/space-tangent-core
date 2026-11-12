// SPDX-License-Identifier: GPL-2.0-or-later
using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using SpaceTangent.Contracts;
using SpaceTangent.Core.Addons;
using SpaceTangent.Core.Ephemerides;

namespace SpaceTangent.Core.Observing;

public readonly record struct DistantStar(uint CatalogNumber, Vector3d PositionLightYears, double AbsoluteMagnitude,
    double TemperatureKelvin, Vector3d Color)
{
    public ObjectId? Id => CatalogNumber == 0 ? null : StellarCatalogue.CatalogId(CatalogNumber);

    public double MagnitudeAt(double distanceLightYears)
    {
        double distanceParsecs = Math.Max(distanceLightYears, 1e-12) * StellarEntry.KilometersPerLightYear / StellarEntry.KilometersPerParsec;
        return AbsoluteMagnitude + 5 * (Math.Log10(distanceParsecs) - 1);
    }

    internal double RadiusKilometers
    {
        get
        {
            double kelvin = Math.Max(TemperatureKelvin, 1000), t = Math.Log10(kelvin) - 4;
            double correction = -.438 + t * (-3.901 + t * (-8.131 + t * (13.421 - t * 8.499)));
            return 695700 * Math.Pow(10, -.2 * (AbsoluteMagnitude + correction - 4.74)) * Math.Pow(5772 / kelvin, 2);
        }
    }
}

/// <summary>Streams magnitude-ranked leaves from an archived spatial catalogue in observer-relative order.</summary>
public sealed class DistantStarIndex : IDisposable
{
    private const int BucketCount = 401, LeafBytes = 36, NumberedStarBytes = 20;
    private const long MaximumFileBytes = 256L * 1024 * 1024;
    private const long DefaultCacheBytes = 64L * 1024 * 1024;
    private readonly Stream _source;
    private readonly Leaf[] _leaves;
    private readonly byte[] _colors;
    private readonly long _recordsOffset;
    private readonly int _recordBytes;
    private readonly long _cacheBudget;
    private readonly Dictionary<int, (byte[] Data, LinkedListNode<int> Recent)> _cache = new();
    private readonly LinkedList<int> _recent = new();
    private readonly Dictionary<uint, string[]> _names = new();
    private readonly Dictionary<uint, StellarEntry?> _lookups = new();
    private readonly object _gate = new();
    private long _cachedBytes;
    private int _leavesRead;
    private bool _disposed;

    private readonly record struct Leaf(Vector3d Low, Vector3d High, float Brightest, int First, int Count);

    private DistantStarIndex(Stream source, Leaf[] leaves, byte[] colors, long recordsOffset,
        int count, int recordBytes, long cacheBudget)
    {
        _source = source;
        _leaves = leaves;
        _colors = colors;
        _recordsOffset = recordsOffset;
        _recordBytes = recordBytes;
        _cacheBudget = cacheBudget;
        StarCount = count;
    }

    public int StarCount { get; }
    public int LeafCount => _leaves.Length;
    public int LeavesRead { get { lock (_gate) return _leavesRead; } }
    public long CachedBytes { get { lock (_gate) return _cachedBytes; } }
    public IEnumerable<(ObjectId Id, string Name, IReadOnlyList<string> Aliases)> NamedStars => _names.Select(pair =>
        (StellarCatalogue.CatalogId(pair.Key), pair.Value[0], (IReadOnlyList<string>)Array.AsReadOnly(pair.Value)));

    public static DistantStarIndex Load(string file, string? names = null, long cacheBytes = DefaultCacheBytes)
    {
        var source = AddonDependencies.OpenRead(file, MaximumFileBytes, FileShare.Read | FileShare.Delete);
        try
        {
            var index = Open(source, cacheBytes);
            if (names is not null && AddonDependencies.FileExists(names) && index._recordBytes == NumberedStarBytes)
                index.ReadNames(names);
            return index;
        }
        catch
        {
            source.Dispose();
            throw;
        }
    }

    /// <summary>Reads only the header and leaf table. The index owns the stream after a successful open.</summary>
    public static DistantStarIndex Open(Stream source, long cacheBytes = DefaultCacheBytes)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!source.CanRead || !source.CanSeek || cacheBytes <= 0)
            throw new ArgumentException("A distant star index needs a seekable stream and positive cache size.");
        if (source.Length is < 20 or > MaximumFileBytes)
            throw new InvalidDataException("The distant star index is outside the supported size range.");
        Span<byte> header = stackalloc byte[20];
        source.Position = 0;
        source.ReadExactly(header);
        int version = BinaryPrimitives.ReadUInt16LittleEndian(header[8..]);
        if (!header[..8].SequenceEqual("CELWFAR\0"u8) || version is not (1 or 2) ||
            BinaryPrimitives.ReadUInt16LittleEndian(header[10..]) != 0)
            throw new InvalidDataException("Unknown distant star catalogue header.");
        int recordBytes = version == 1 ? 16 : NumberedStarBytes;
        uint count = BinaryPrimitives.ReadUInt32LittleEndian(header[12..]);
        uint leafCount = BinaryPrimitives.ReadUInt32LittleEndian(header[16..]);
        long recordsOffset = 20 + BucketCount * 4L + leafCount * (long)LeafBytes;
        if (count > 10_000_000 || leafCount > 65_536 || source.Length != recordsOffset + count * (long)recordBytes)
            throw new InvalidDataException("The distant star index length does not match its header.");
        var table = new byte[BucketCount * 4 + (int)leafCount * LeafBytes];
        source.ReadExactly(table);
        var leaves = new Leaf[leafCount];
        uint covered = 0;
        for (int index = 0; index < leaves.Length; index++)
        {
            var bytes = table.AsSpan(BucketCount * 4 + index * LeafBytes, LeafBytes);
            Vector3d low = Vector(bytes), high = Vector(bytes[12..]);
            float brightest = BinaryPrimitives.ReadSingleLittleEndian(bytes[24..]);
            uint first = BinaryPrimitives.ReadUInt32LittleEndian(bytes[28..]);
            uint size = BinaryPrimitives.ReadUInt32LittleEndian(bytes[32..]);
            if (!low.IsFinite || !high.IsFinite || low.X > high.X || low.Y > high.Y || low.Z > high.Z ||
                !float.IsFinite(brightest) || first != covered || size == 0 || size > count - covered)
                throw new InvalidDataException($"Invalid distant star leaf {index}.");
            leaves[index] = new(low, high, brightest, (int)first, (int)size);
            covered += size;
        }
        if (covered != count)
            throw new InvalidDataException("The distant leaves do not cover the catalogue.");
        return new(source, leaves, table[..(BucketCount * 4)], recordsOffset, (int)count, recordBytes, cacheBytes);
    }

    public int Query(Vector3d observerLightYears, double limit, List<DistantStar> result, CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (!observerLightYears.IsFinite || !double.IsFinite(limit))
            throw new ArgumentOutOfRangeException(nameof(observerLightYears));
        var (sin, cos) = Math.SinCos(23.4392911 * Math.PI / 180);
        var native = new Vector3d(observerLightYears.X, -observerLightYears.Y * sin + observerLightYears.Z * cos,
            -observerLightYears.Y * cos - observerLightYears.Z * sin);
        int start = result.Count;
        for (int index = 0; index < _leaves.Length; index++)
        {
            cancellation.ThrowIfCancellationRequested();
            Leaf leaf = _leaves[index];
            var nearest = new Vector3d(Math.Clamp(native.X, leaf.Low.X, leaf.High.X),
                Math.Clamp(native.Y, leaf.Low.Y, leaf.High.Y), Math.Clamp(native.Z, leaf.Low.Z, leaf.High.Z));
            double distance = Math.Max(0, Math.BitDecrement((native - nearest).Length));
            double minimumModulus = 5 * (Math.Log10(distance * StellarEntry.KilometersPerLightYear /
                StellarEntry.KilometersPerParsec) - 1);
            if (leaf.Brightest + minimumModulus > limit)
                continue;
            byte[] data = LoadLeaf(index, retain: true);
            for (int offset = 0; offset < leaf.Count; offset++)
            {
                var record = ReadRecord(data, offset);
                if (record.AbsoluteMagnitude + minimumModulus > limit)
                    break;
                if (record.MagnitudeAt((record.PositionLightYears - observerLightYears).Length) <= limit)
                    result.Add(record);
            }
        }
        return result.Count - start;
    }

    public StellarEntry? Find(ObjectId id)
    {
        if (_recordBytes != NumberedStarBytes || ParseId(id) is not { } number)
            return null;
        lock (_gate)
        {
            if (_lookups.TryGetValue(number, out var known))
                return known;
            var star = FindNumber(number);
            if (_lookups.Count >= 512)
                _lookups.Clear();
            _lookups[number] = star;
            return star;
        }
    }

    private StellarEntry? FindNumber(uint number)
    {
        for (int index = 0; index < _leaves.Length; index++)
        {
            var data = LoadLeaf(index, retain: false);
            for (int offset = 0; offset < _leaves[index].Count; offset++)
            {
                if (BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset * _recordBytes + 16)) != number)
                    continue;
                var star = ReadRecord(data, offset);
                string designation = StellarCatalogue.CatalogDesignation(number);
                string[] aliases = _names.GetValueOrDefault(number) ?? [];
                Vector3d parsecs = star.PositionLightYears * (StellarEntry.KilometersPerLightYear / StellarEntry.KilometersPerParsec);
                return new(StellarCatalogue.CatalogId(number), aliases.FirstOrDefault() ?? designation, parsecs.Length,
                    parsecs, default, star.MagnitudeAt(star.PositionLightYears.Length), .65, star.RadiusKilometers)
                {
                    Aliases = Array.AsReadOnly(aliases.Append(designation).Distinct(StringComparer.OrdinalIgnoreCase).ToArray()),
                    AbsoluteMagnitude = star.AbsoluteMagnitude,
                    ColorSrgb = star.Color
                };
            }
        }
        return null;
    }

    private static uint? ParseId(ObjectId id)
    {
        if (id.Value is not { } value)
            return null;
        if ((value.StartsWith("hip/", StringComparison.Ordinal) || value.StartsWith("stc/", StringComparison.Ordinal)) &&
            uint.TryParse(value.AsSpan(4), NumberStyles.None, CultureInfo.InvariantCulture, out uint number) && number > 0)
            return number;
        if (!value.StartsWith("tyc/", StringComparison.Ordinal))
            return null;
        string[] fields = value[4..].Split('-');
        if (fields.Length == 3 && uint.TryParse(fields[0], out uint region) && uint.TryParse(fields[1], out uint star) &&
            uint.TryParse(fields[2], out uint component) && region is > 0 and < 10_000 && star < 100_000 && component is >= 1 and <= 3)
            return region + star * 10_000 + component * 1_000_000_000;
        return null;
    }

    private void ReadNames(string path)
    {
        const int maximum = 8 * 1024 * 1024;
        using var stream = AddonDependencies.OpenRead(path, maximum);
        if (stream.Length > maximum)
            throw new InvalidDataException("The distant star names exceed 8 MiB.");
        using var reader = new StreamReader(stream, new UTF8Encoding(false, true));
        while (reader.ReadLine() is { } line)
        {
            var parts = line.Split(':');
            if (parts.Length < 2 || !uint.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out uint number))
                continue;
            string[] names = parts.Skip(1).Select(name => name.Trim()).Where(name => name.Length is > 0 and <= 256).Take(16).ToArray();
            if (number != 0 && names.Length > 0)
                _names[number] = names;
        }
    }

    private DistantStar ReadRecord(byte[] data, int index)
    {
        var record = data.AsSpan(index * _recordBytes, _recordBytes);
        var source = Vector(record);
        int bucket = BinaryPrimitives.ReadUInt16LittleEndian(record[14..]);
        if (!source.IsFinite || bucket >= BucketCount)
            throw new InvalidDataException("A distant star contains invalid coordinates or colour.");
        Vector3d position = HistoricalSolarEphemeris.Rotate(new(source.X, -source.Z, source.Y));
        double absolute = BinaryPrimitives.ReadInt16LittleEndian(record[12..]) / 256d;
        uint number = _recordBytes == NumberedStarBytes ? BinaryPrimitives.ReadUInt32LittleEndian(record[16..]) : 0;
        int color = bucket * 4;
        return new(number, position, absolute, bucket * 100d,
            new(_colors[color] / 255d, _colors[color + 1] / 255d, _colors[color + 2] / 255d));
    }

    private byte[] LoadLeaf(int index, bool retain)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_cache.TryGetValue(index, out var hit))
            {
                if (retain)
                {
                    _recent.Remove(hit.Recent);
                    _recent.AddLast(hit.Recent);
                }
                return hit.Data;
            }
            var leaf = _leaves[index];
            var data = new byte[leaf.Count * _recordBytes];
            _source.Position = _recordsOffset + leaf.First * (long)_recordBytes;
            _source.ReadExactly(data);
            _leavesRead++;
            if (!retain || data.Length > _cacheBudget)
                return data;
            while (_cachedBytes + data.Length > _cacheBudget && _recent.First is { } oldest)
            {
                _cachedBytes -= _cache[oldest.Value].Data.Length;
                _cache.Remove(oldest.Value);
                _recent.RemoveFirst();
            }
            _cache.Add(index, (data, _recent.AddLast(index)));
            _cachedBytes += data.Length;
            return data;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            _cache.Clear();
            _recent.Clear();
            _lookups.Clear();
            _cachedBytes = 0;
            _source.Dispose();
        }
    }

    private static Vector3d Vector(ReadOnlySpan<byte> bytes) => new(BinaryPrimitives.ReadSingleLittleEndian(bytes),
        BinaryPrimitives.ReadSingleLittleEndian(bytes[4..]), BinaryPrimitives.ReadSingleLittleEndian(bytes[8..]));
}
