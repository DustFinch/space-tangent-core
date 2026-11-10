// SPDX-License-Identifier: GPL-2.0-or-later
using System.Buffers.Binary;
using System.Security.Cryptography;
using SpaceTangent.Contracts;

namespace SpaceTangent.Core.Ephemerides;

/// <summary>
/// Evaluates Sun, Earth and Moon from the published little-endian classic DE440
/// file or the pinned 1900–2100 subset of that file. No network or native runtime is required.
/// </summary>
public sealed class De440Ephemeris
{
    private const int RecordBytes = 1018 * sizeof(double);
    private const int FullBytes = 102_272_352;
    private const int SubsetBytes = 18_617_184;
    private const string FullSha256 = "29915576d0a6555766b99485ac3056ee415e86df4fce282611c31afb329ad062";
    private const string SubsetSha256 = "d4c198e8ba6ca9dc75631ac22d742bb4f4d95d4c7ca87c7961b40b9800d36c67";
    private const long RecordNanoseconds = 32 * JulianInstant.NanosecondsPerDay;
    private readonly byte[] _records;
    private readonly Series _earthMoonBarycenter;
    private readonly Series _moonRelativeToEarth;
    private readonly Series _sun;
    private readonly double _earthShare;
    private readonly int _dataRecords;

    private De440Ephemeris(byte[] records)
    {
        _records = records;
        _dataRecords = records.Length / RecordBytes - 2;
        Start = JulianInstant.FromJulianDate(ReadDouble(records, 2652), TimeScale.Tdb);
        End = JulianInstant.FromJulianDate(ReadDouble(records, 2660), TimeScale.Tdb);
        _earthShare = 1 / (1 + ReadDouble(records, 2688));
        _earthMoonBarycenter = ReadSeries(records, 2);
        _moonRelativeToEarth = ReadSeries(records, 9);
        _sun = ReadSeries(records, 10);
    }

    public JulianInstant Start { get; }
    public JulianInstant End { get; }

    public static De440Ephemeris Load(string path)
    {
        using FileStream stream = File.OpenRead(path);
        string expectedHash = stream.Length switch
        {
            FullBytes => FullSha256,
            SubsetBytes => SubsetSha256,
            _ => throw new InvalidDataException("Expected a published classic DE440 file or its pinned subset.")
        };

        byte[] bytes = GC.AllocateUninitializedArray<byte>((int)stream.Length);
        stream.ReadExactly(bytes);
        if (!Convert.ToHexString(SHA256.HashData(bytes)).Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The DE440 file does not match its published SHA-256 pin.");

        return new(bytes);
    }

    public SolarSystemSample Sample(JulianInstant tdb)
    {
        if (tdb.Scale != TimeScale.Tdb)
            throw new ArgumentException("DE440 requires a TDB epoch.", nameof(tdb));
        if (tdb.CompareTo(Start) < 0 || tdb.CompareTo(End) > 0)
            throw new ArgumentOutOfRangeException(nameof(tdb), "Epoch is outside DE440 coverage.");

        Int128 elapsed = tdb.DifferenceNanoseconds(Start);
        int recordIndex = Math.Min((int)(elapsed / RecordNanoseconds), _dataRecords - 1);
        ReadOnlySpan<byte> record = _records.AsSpan((recordIndex + 2) * RecordBytes, RecordBytes);
        long withinRecord = (long)(elapsed - (Int128)recordIndex * RecordNanoseconds);

        CartesianState emb = Evaluate(record, withinRecord, _earthMoonBarycenter);
        CartesianState lunarOffset = Evaluate(record, withinRecord, _moonRelativeToEarth);
        CartesianState earth = emb - lunarOffset * _earthShare;
        return new(tdb, Evaluate(record, withinRecord, _sun), earth, earth + lunarOffset);
    }

    private static CartesianState Evaluate(ReadOnlySpan<byte> record, long withinRecord, Series series)
    {
        long granuleLength = RecordNanoseconds / series.Granules;
        int granule = Math.Min((int)(withinRecord / granuleLength), series.Granules - 1);
        double u = 2 * ((withinRecord - granule * granuleLength) / (double)granuleLength) - 1;
        int axisBytes = series.Coefficients * sizeof(double);
        int offset = series.ByteOffset + granule * 3 * axisBytes;
        var (x, vx) = EvaluateAxis(record.Slice(offset, axisBytes), u);
        var (y, vy) = EvaluateAxis(record.Slice(offset + axisBytes, axisBytes), u);
        var (z, vz) = EvaluateAxis(record.Slice(offset + 2 * axisBytes, axisBytes), u);
        double rate = 2d * JulianInstant.NanosecondsPerSecond / granuleLength;
        return new(new(x, y, z), new(vx * rate, vy * rate, vz * rate));
    }

    private static (double Position, double Derivative) EvaluateAxis(ReadOnlySpan<byte> coefficients, double u)
    {
        // Clenshaw recurrence and its analytic derivative on the normalized interval.
        double next = 0, second = 0, dNext = 0, dSecond = 0;
        for (int offset = coefficients.Length - sizeof(double); offset > 0; offset -= sizeof(double))
        {
            double current = ReadDouble(coefficients, offset) + 2 * u * next - second;
            double dCurrent = 2 * next + 2 * u * dNext - dSecond;
            (next, second) = (current, next);
            (dNext, dSecond) = (dCurrent, dNext);
        }

        return (ReadDouble(coefficients, 0) + u * next - second, next + u * dNext - dSecond);
    }

    private static Series ReadSeries(ReadOnlySpan<byte> data, int index)
    {
        int offset = 2696 + 12 * index;
        int firstWord = BinaryPrimitives.ReadInt32LittleEndian(data[offset..]);
        int coefficients = BinaryPrimitives.ReadInt32LittleEndian(data[(offset + 4)..]);
        int granules = BinaryPrimitives.ReadInt32LittleEndian(data[(offset + 8)..]);
        return new((firstWord - 1) * sizeof(double), coefficients, granules);
    }

    private static double ReadDouble(ReadOnlySpan<byte> bytes, int offset) =>
        BinaryPrimitives.ReadDoubleLittleEndian(bytes[offset..]);

    private readonly record struct Series(int ByteOffset, int Coefficients, int Granules);
}
