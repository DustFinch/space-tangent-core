// SPDX-License-Identifier: GPL-2.0-or-later
using System.Buffers.Binary;
using SpaceTangent.Core.Addons;
using System.Text;

namespace SpaceTangent.Core.Spice;

internal readonly record struct DafSegment(double Begin, double End, int FirstId, int SecondId, int ThirdId,
    int FourthId, int FirstWord, int LastWord);

internal sealed class DafFile
{
    private const int RecordBytes = 1024, SummaryBytes = 40;
    private const long MaximumKernelBytes = 256L * 1024 * 1024;
    private readonly byte[] _bytes;
    private readonly bool _littleEndian;

    internal DafFile(string path, string expectedType)
    {
        try
        {
            using FileStream stream = AddonDependencies.OpenRead(path, MaximumKernelBytes);
            if (stream.Length < RecordBytes || stream.Length > MaximumKernelBytes || stream.Length % RecordBytes != 0)
                throw new InvalidDataException("Invalid DAF file length.");
            _bytes = GC.AllocateUninitializedArray<byte>((int)stream.Length);
            stream.ReadExactly(_bytes);

            string identifier = Encoding.ASCII.GetString(_bytes, 0, 8).TrimEnd();
            if (identifier != $"DAF/{expectedType}" && identifier != "NAIF/DAF")
                throw new InvalidDataException($"Expected a DAF/{expectedType} kernel.");
            _littleEndian = Encoding.ASCII.GetString(_bytes, 88, 8) switch
            {
                "LTL-IEEE" => true,
                "BIG-IEEE" => false,
                _ => throw new InvalidDataException("Unsupported DAF binary format.")
            };
            if (IntegerAt(8) != 2 || IntegerAt(12) != 6)
                throw new InvalidDataException("Unexpected DAF summary dimensions.");
        }
        catch (Exception error) when (AddonDiagnostics.Annotate(error, AddonStage.Kernel, path))
        {
            throw;
        }
    }

    internal IEnumerable<DafSegment> Segments()
    {
        int maximumRecord = _bytes.Length / RecordBytes;
        int record = IntegerAt(76), last = IntegerAt(80), previous = 0;
        var seen = new HashSet<int>();
        while (record != 0)
        {
            if (record is < 2 || record >= maximumRecord || !seen.Add(record))
                throw new InvalidDataException("Invalid DAF summary chain.");
            int offset = (record - 1) * RecordBytes;
            int next = ExactInteger(DoubleAt(offset), 0, maximumRecord, "next record");
            int linkedPrevious = ExactInteger(DoubleAt(offset + 8), 0, maximumRecord, "previous record");
            if (linkedPrevious != previous)
                throw new InvalidDataException("Inconsistent DAF summary links.");
            int count = ExactInteger(DoubleAt(offset + 16), 0, (RecordBytes - 24) / SummaryBytes, "summary count");
            for (int index = 0; index < count; index++)
            {
                int summary = offset + 24 + index * SummaryBytes;
                double begin = DoubleAt(summary), end = DoubleAt(summary + 8);
                int first = IntegerAt(summary + 32), stop = IntegerAt(summary + 36);
                if (!(double.IsFinite(begin) && double.IsFinite(end) && begin <= end) ||
                    first < 1 || stop < first || stop > _bytes.Length / 8)
                    throw new InvalidDataException("Invalid DAF segment bounds.");
                yield return new(begin, end, IntegerAt(summary + 16), IntegerAt(summary + 20),
                    IntegerAt(summary + 24), IntegerAt(summary + 28), first, stop);
            }
            previous = record;
            record = next;
        }
        if (previous != last)
            throw new InvalidDataException("DAF summary chain does not reach the last record.");
    }

    internal double Word(int oneBasedIndex) => DoubleAt((oneBasedIndex - 1) * sizeof(double));
    internal bool LittleEndian => _littleEndian;
    internal ReadOnlySpan<byte> WordBytes(int oneBasedIndex, int count) =>
        _bytes.AsSpan((oneBasedIndex - 1) * sizeof(double), count * sizeof(double));

    internal static int ExactInteger(double value, int minimum, int maximum, string field)
    {
        if (!double.IsFinite(value) || value < minimum || value > maximum || value != Math.Truncate(value))
            throw new InvalidDataException($"Invalid DAF {field}.");
        return (int)value;
    }

    private int IntegerAt(int offset) => _littleEndian
        ? BinaryPrimitives.ReadInt32LittleEndian(_bytes.AsSpan(offset))
        : BinaryPrimitives.ReadInt32BigEndian(_bytes.AsSpan(offset));

    private double DoubleAt(int offset) => _littleEndian
        ? BinaryPrimitives.ReadDoubleLittleEndian(_bytes.AsSpan(offset))
        : BinaryPrimitives.ReadDoubleBigEndian(_bytes.AsSpan(offset));
}
