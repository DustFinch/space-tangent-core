// SPDX-License-Identifier: GPL-2.0-or-later
using SpaceTangent.Contracts;
using SpaceTangent.Core.Coordinates;

namespace SpaceTangent.Core.Spice;

internal sealed class CkKernel
{
    internal readonly record struct Pointing(int Reference, Basis3d ToReference, Vector3d? AngularVelocity);
    private readonly record struct Segment(DafSegment Descriptor, int Size, int Times, int RecordCount,
        int Intervals, int IntervalCount);

    private readonly DafFile _file;
    private readonly Segment[] _segments;

    internal CkKernel(string path)
    {
        _file = new DafFile(path, "CK");
        _segments = _file.Segments().Select(Prepare).ToArray();
    }

    internal bool TryPointing(int instrument, double ticks, bool needRates, out Pointing pointing)
    {
        for (int index = _segments.Length - 1; index >= 0; index--)
        {
            Segment segment = _segments[index];
            DafSegment descriptor = segment.Descriptor;
            if (descriptor.FirstId != instrument || ticks < descriptor.Begin || ticks > descriptor.End ||
                needRates && descriptor.FourthId == 0)
                continue;
            if (descriptor.ThirdId is not (2 or 3))
                throw new NotSupportedException($"CK type {descriptor.ThirdId} pointing is unavailable.");

            int record = LastAtOrBefore(segment.Times, segment.RecordCount, ticks);
            if (record < 0)
                continue;
            int address = descriptor.FirstWord + record * segment.Size;
            double start = _file.Word(segment.Times + record);
            if (descriptor.ThirdId == 2)
            {
                if (ticks > _file.Word(segment.Intervals + record))
                    continue;
                Vector3d omega = ReadRates(address);
                double elapsed = (ticks - start) * _file.Word(address + 7);
                var current = Turn(ReadQuaternion(address).Normalize(), omega, -omega.Length * elapsed);
                pointing = new(descriptor.SecondId, current.InverseBasis(), omega);
                return true;
            }

            int interval = LastAtOrBefore(segment.Intervals, segment.IntervalCount, ticks);
            if (interval < 0)
                continue;
            if (ticks == start)
            {
                pointing = new(descriptor.SecondId, ReadQuaternion(address).Normalize().InverseBasis(), segment.Size == 7 ? ReadRates(address) : null);
                return true;
            }
            if (record + 1 >= segment.RecordCount)
                continue;
            double end = _file.Word(segment.Times + record + 1);
            if (interval + 1 < segment.IntervalCount && end >= _file.Word(segment.Intervals + interval + 1))
                continue;

            double fraction = (ticks - start) / (end - start);
            var first = ReadQuaternion(address);
            var last = ReadQuaternion(address + segment.Size);
            var rotation = Interpolate(first, last, fraction);
            Vector3d? rates = segment.Size == 7 ?
                ReadRates(address) * (1 - fraction) + ReadRates(address + segment.Size) * fraction : null;
            pointing = new(descriptor.SecondId, rotation.InverseBasis(), rates);
            return true;
        }

        pointing = default;
        return false;
    }

    private Segment Prepare(DafSegment descriptor)
    {
        if (descriptor.ThirdId is not (2 or 3))
            return new(descriptor, 0, 0, 0, 0, 0);
        if (descriptor.FourthId is not (0 or 1))
            throw new InvalidDataException("Invalid CK angular-rate flag.");
        int words = descriptor.LastWord - descriptor.FirstWord + 1;
        if (descriptor.ThirdId == 2)
        {
            const int recordSize = 8;
            int count = (int)(words * 100L / 1001 + 1);
            if (descriptor.FourthId != 1 || 10L * count + (count - 1) / 100 != words)
                throw new InvalidDataException("Invalid CK type 2 record layout.");
            int starts = descriptor.FirstWord + count * recordSize;
            int ends = starts + count;
            ValidateTimes(starts, count);
            for (int i = 0; i < count; i++)
            {
                double end = _file.Word(ends + i);
                double rate = _file.Word(descriptor.FirstWord + i * recordSize + 7);
                if (!double.IsFinite(end) || end < _file.Word(starts + i) ||
                    i + 1 < count && end > _file.Word(starts + i + 1) ||
                    !double.IsFinite(rate) || rate <= 0)
                    throw new InvalidDataException("Invalid CK type 2 time interval or seconds per tick.");
            }
            return new(descriptor, recordSize, starts, count, ends, count);
        }

        int size = descriptor.FourthId == 0 ? 4 : 7;
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

    private RigidQuaternion ReadQuaternion(int at) => new(_file.Word(at), _file.Word(at + 1), _file.Word(at + 2), _file.Word(at + 3));

    private Vector3d ReadRates(int at)
    {
        var value = new Vector3d(_file.Word(at + 4), _file.Word(at + 5), _file.Word(at + 6));
        return value.IsFinite ? value : throw new InvalidDataException("CK angular velocity must be finite.");
    }

    private static RigidQuaternion Turn(RigidQuaternion rotation, Vector3d axis, double radians)
    {
        double length = axis.Length;
        if (length == 0 || radians == 0)
            return rotation;
        var (sine, cosine) = Math.SinCos(radians / 2);
        axis *= sine / length;
        Vector3d vector = new(rotation.X, rotation.Y, rotation.Z);
        Vector3d turned = vector * cosine + axis * rotation.W + Vector3d.Cross(vector, axis);
        return new RigidQuaternion(rotation.W * cosine - Vector3d.Dot(vector, axis), turned.X, turned.Y, turned.Z).Normalize();
    }

    private static RigidQuaternion Interpolate(RigidQuaternion first, RigidQuaternion last, double fraction)
    {
        // CK chooses the half-turn axis from the relative matrix and applies the nonnegative scalar convention.
        var relative = SpiceFixedFrames.Compose(RawMatrix(last).Transpose(), RawMatrix(first));
        var delta = FromCkMatrix(relative);
        Vector3d axis = new(delta.X, delta.Y, delta.Z);
        double angle = delta.W == 0 ? Math.PI : 2 * Math.Atan2(axis.Length, delta.W);
        return Turn(first.Normalize(), axis, -angle * fraction);
    }

    private static Basis3d RawMatrix(RigidQuaternion q)
    {
        double wx = q.W * q.X, wy = q.W * q.Y, wz = q.W * q.Z;
        double xy = q.X * q.Y, xz = q.X * q.Z, yz = q.Y * q.Z;
        double xx = q.X * q.X, yy = q.Y * q.Y, zz = q.Z * q.Z;
        double length = q.W * q.W + xx + yy + zz;
        if (!(length > 0) || !double.IsFinite(length))
            throw new InvalidDataException("Invalid CK orientation quaternion.");
        if (length != 1)
        {
            double scale = 1 / length;
            wx *= scale; wy *= scale; wz *= scale;
            xy *= scale; xz *= scale; yz *= scale;
            xx *= scale; yy *= scale; zz *= scale;
        }
        return new(new(1 - (yy + zz) * 2, (xy + wz) * 2, (xz - wy) * 2),
            new((xy - wz) * 2, 1 - (xx + zz) * 2, (yz + wx) * 2),
            new((xz + wy) * 2, (yz - wx) * 2, 1 - (xx + yy) * 2));
    }

    private static RigidQuaternion FromCkMatrix(Basis3d matrix)
    {
        var (a, b, c) = (matrix.X, matrix.Y, matrix.Z);
        double trace = a.X + b.Y + c.Z;
        double aa = 1 - trace + 2 * a.X, bb = 1 - trace + 2 * b.Y;
        double w, x, y, z, factor;
        if (trace + 1 >= 1)
        {
            w = Math.Sqrt((trace + 1) * .25);
            factor = .25 / w;
            (x, y, z) = ((b.Z - c.Y) * factor, (c.X - a.Z) * factor, (a.Y - b.X) * factor);
        }
        else if (aa >= 1)
        {
            x = Math.Sqrt(aa * .25);
            factor = .25 / x;
            (w, y, z) = ((b.Z - c.Y) * factor, (b.X + a.Y) * factor, (c.X + a.Z) * factor);
        }
        else if (bb >= 1)
        {
            y = Math.Sqrt(bb * .25);
            factor = .25 / y;
            (w, x, z) = ((c.X - a.Z) * factor, (b.X + a.Y) * factor, (c.Y + b.Z) * factor);
        }
        else
        {
            z = Math.Sqrt((1 - trace + 2 * c.Z) * .25);
            factor = .25 / z;
            (w, x, y) = ((a.Y - b.X) * factor, (c.X + a.Z) * factor, (c.Y + b.Z) * factor);
        }
        return (w > 0 ? new RigidQuaternion(w, x, y, z) : new(-w, -x, -y, -z)).Normalize();
    }
}
