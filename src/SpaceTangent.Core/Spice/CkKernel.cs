// SPDX-License-Identifier: GPL-2.0-or-later
using SpaceTangent.Contracts;
using SpaceTangent.Core.Coordinates;

namespace SpaceTangent.Core.Spice;

internal sealed class CkKernel
{
    private readonly record struct Segment(DafSegment Descriptor, int Size, int Times, int RecordCount,
        int Intervals, int IntervalCount);
    private readonly DafFile _file;
    private readonly Segment[] _segments;

    internal CkKernel(string path)
    {
        _file = new DafFile(path, "CK");
        _segments = _file.Segments().Select(Prepare).ToArray();
    }

    internal bool TryAttitude(int instrument, double ticks, out Basis3d bodyToReference)
    {
        for (int index = _segments.Length - 1; index >= 0; index--)
        {
            Segment segment = _segments[index];
            DafSegment descriptor = segment.Descriptor;
            if (descriptor.FirstId != instrument || ticks < descriptor.Begin || ticks > descriptor.End)
                continue;
            if (descriptor.SecondId != 1 || descriptor.ThirdId != 3)
                throw new NotSupportedException("The selected CK segment requires J2000 and type 3 pointing.");

            int interval = LastAtOrBefore(segment.Intervals, segment.IntervalCount, ticks);
            int record = LastAtOrBefore(segment.Times, segment.RecordCount, ticks);
            if (interval < 0 || record < 0)
                continue;

            double start = _file.Word(segment.Times + record);
            int address = descriptor.FirstWord + record * segment.Size;
            if (ticks == start)
            {
                bodyToReference = ReadQuaternion(address).InverseBasis();
                return true;
            }
            if (record + 1 >= segment.RecordCount)
                continue;
            double end = _file.Word(segment.Times + record + 1);
            if (interval + 1 < segment.IntervalCount && end >= _file.Word(segment.Intervals + interval + 1))
                continue;

            var rotation = RigidQuaternion.Interpolate(ReadQuaternion(address), ReadQuaternion(address + segment.Size),
                (ticks - start) / (end - start));
            bodyToReference = rotation.InverseBasis();
            return true;
        }

        bodyToReference = default;
        return false;
    }

    private Segment Prepare(DafSegment descriptor)
    {
        if (descriptor.ThirdId != 3)
            return new(descriptor, 0, 0, 0, 0, 0);
        if (descriptor.FourthId is not (0 or 1))
            throw new InvalidDataException("Invalid CK angular-rate flag.");
        int size = descriptor.FourthId == 0 ? 4 : 7;
        int words = descriptor.LastWord - descriptor.FirstWord + 1;
        if (words < size + 4)
            throw new InvalidDataException("Invalid CK type 3 layout.");
        int records = DafFile.ExactInteger(_file.Word(descriptor.LastWord), 1, words / size, "CK record count");
        int intervals = DafFile.ExactInteger(_file.Word(descriptor.LastWord - 1), 1, records, "CK interval count");
        long expected = (long)(size + 1) * records + (records - 1) / 100 + intervals + (intervals - 1) / 100 + 2;
        if (words != expected)
            throw new InvalidDataException("CK type 3 records do not fit the segment.");

        int timeIndex = descriptor.FirstWord + records * size;
        int intervalIndex = timeIndex + records + (records - 1) / 100;
        ValidateTimes(timeIndex, records);
        ValidateTimes(intervalIndex, intervals);
        if (_file.Word(timeIndex) != _file.Word(intervalIndex))
            throw new InvalidDataException("CK first interval must start at its first record.");
        for (int i = 0; i < intervals; i++)
        {
            double interval = _file.Word(intervalIndex + i);
            int record = LastAtOrBefore(timeIndex, records, interval);
            if (record < 0 || _file.Word(timeIndex + record) != interval)
                throw new InvalidDataException("CK interval does not start at a pointing record.");
        }
        return new(descriptor, size, timeIndex, records, intervalIndex, intervals);
    }

    private void ValidateTimes(int address, int count)
    {
        double previous = double.NegativeInfinity;
        for (int i = 0; i < count; i++)
        {
            double time = _file.Word(address + i);
            if (!double.IsFinite(time) || time <= previous)
                throw new InvalidDataException("CK times must be finite and strictly increasing.");
            previous = time;
        }
    }

    private int LastAtOrBefore(int address, int count, double ticks)
    {
        int lower = 0, upper = count;
        while (lower < upper)
        {
            int middle = lower + (upper - lower) / 2;
            if (_file.Word(address + middle) <= ticks)
                lower = middle + 1;
            else
                upper = middle;
        }
        return lower - 1;
    }

    private RigidQuaternion ReadQuaternion(int address) =>
        new RigidQuaternion(_file.Word(address), _file.Word(address + 1), _file.Word(address + 2),
            _file.Word(address + 3)).Normalize();
}
