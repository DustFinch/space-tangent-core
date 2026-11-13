// SPDX-License-Identifier: GPL-2.0-or-later
using System.Buffers.Binary;
using System.Globalization;
using SpaceTangent.Contracts;

namespace SpaceTangent.Core.Addons;

public enum MissionSwarmFormat { Astorb, Binary, KepBin }

/// <summary>The native orbital attributes consumed by the mission point renderer.</summary>
public sealed class MissionSwarm
{
    public const int FieldsPerObject = 9;
    public const int MaximumInputBytes = 512 * 1024 * 1024;
    private const double AstronomicalUnit = 149597870.691, SecondsPerDay = 86400, J2000 = 2451545;
    private readonly float[] _attributes;

    private MissionSwarm(float[] attributes, double epoch, float radius)
    {
        _attributes = attributes;
        EpochSeconds = epoch;
        BoundingRadiusKilometers = radius;
    }

    public ReadOnlySpan<float> Attributes => _attributes;
    public int Count => _attributes.Length / FieldsPerObject;
    public double EpochSeconds { get; }
    public float BoundingRadiusKilometers { get; }
    public float TimeAt(double julianDate) => (float)((julianDate - J2000) * SecondsPerDay - EpochSeconds);

    public static MissionSwarm Read(Stream stream, MissionSwarmFormat format, CancellationToken cancellation = default)
    {
        if (stream.CanSeek && stream.Length - stream.Position > MaximumInputBytes)
            throw new InvalidDataException("Mission swarm data exceeds its size limit.");
        var attributes = new List<float>();
        double epoch = 0;
        float bound = 0;
        Span<float> fields = stackalloc float[FieldsPerObject];
        foreach (var orbit in ReadOrbits(stream, format))
        {
            cancellation.ThrowIfCancellationRequested();
            if (attributes.Count == 0)
                epoch = orbit.Epoch;
            if (!(orbit.SemiMajor > 0) || orbit.Eccentricity is < 0 or >= 1 ||
                !double.IsFinite(orbit.Epoch))
                throw new InvalidDataException("Invalid swarm orbit.");
            double halfNode = orbit.Node / 2, halfInc = orbit.Inclination / 2, halfPeri = orbit.Periapsis / 2;
            double sn = Math.Sin(halfNode), cn = Math.Cos(halfNode);
            double si = Math.Sin(halfInc), ci = Math.Cos(halfInc);
            double sp = Math.Sin(halfPeri), cp = Math.Cos(halfPeri);
            fields[0] = (float)orbit.SemiMajor;
            fields[1] = (float)orbit.Eccentricity;
            fields[2] = (float)(orbit.Mean + (epoch - orbit.Epoch) * orbit.Motion);
            fields[3] = (float)orbit.Motion;
            fields[4] = (float)(cn * ci * cp - sn * ci * sp);
            fields[5] = (float)(cn * si * cp + sn * si * sp);
            fields[6] = (float)(sn * si * cp - cn * si * sp);
            fields[7] = (float)(sn * ci * cp + cn * ci * sp);
            fields[8] = (float)(orbit.Discovery - epoch);
            bound = Math.Max(bound, (float)(fields[0] * (1 + orbit.Eccentricity)));
            foreach (float value in fields)
            {
                if (!float.IsFinite(value))
                    throw new InvalidDataException("Swarm attributes exceed their finite range.");
                attributes.Add(value);
            }
        }
        if (attributes.Count == 0 || !float.IsFinite(bound))
            throw new InvalidDataException("A swarm must contain finite orbital records.");
        return new(attributes.ToArray(), epoch, bound);
    }

    private readonly record struct Orbit(double Epoch, double SemiMajor, double Eccentricity,
        double Inclination, double Node, double Periapsis, double Mean, double Motion, double Discovery);

    private static IEnumerable<Orbit> ReadOrbits(Stream stream, MissionSwarmFormat format)
    {
        long consumed = 0;
        if (format == MissionSwarmFormat.Astorb)
        {
            using var reader = new StreamReader(stream, leaveOpen: true);
            while (reader.ReadLine() is { } line)
            {
                consumed += line.Length + 1;
                if (consumed > MaximumInputBytes)
                    throw new InvalidDataException("Swarm text exceeds its size limit.");
                if (line.Length == 0)
                    continue;
                if (line.Length < 181)
                    throw new InvalidDataException("Incomplete ASTORB record.");
                double Number(int offset, int length) => double.Parse(line.AsSpan(offset, length), CultureInfo.InvariantCulture);
                double epoch = CalendarEpoch((int)Number(106, 4), (int)Number(110, 2), (int)Number(112, 2));
                double eccentricity = Number(158, 10), au = Number(169, 12);
                string designation = line.Substring(7, 19).Trim();
                double discovered = -365.25 * 100 * SecondsPerDay;
                if (designation.Length >= 7 && designation.AsSpan(0, 4).IndexOfAnyExceptInRange('0', '9') < 0 &&
                    designation[4] == ' ' && designation[5] is >= 'A' and <= 'Z' && designation[6] is >= 'A' and <= 'Z')
                    discovered = ((double.Parse(designation.AsSpan(0, 4), CultureInfo.InvariantCulture) - 2000) * 365.25 +
                        (designation[5] - 'A') * (365.25 / 24)) * SecondsPerDay;
                yield return new(epoch, SemiMajor(au, eccentricity, AstronomicalUnit), eccentricity, Radians(Number(148, 9)),
                    Radians(Number(137, 10)), Radians(Number(126, 10)), Radians(Number(115, 10)), AnnualMotion(au), discovered);
            }
            yield break;
        }

        if (format is not (MissionSwarmFormat.Binary or MissionSwarmFormat.KepBin))
            throw new ArgumentOutOfRangeException(nameof(format));
        bool solar = format == MissionSwarmFormat.Binary;
        var record = new byte[36];
        while (stream.Read(record) is int read && read > 0)
        {
            stream.ReadExactly(record.AsSpan(read));
            consumed += record.Length;
            if (consumed > MaximumInputBytes)
                throw new InvalidDataException("Swarm binary data exceeds its size limit.");
            double Number(int offset) => BinaryPrimitives.ReadSingleBigEndian(record.AsSpan(offset));
            double a = Number(0), eccentricity = Number(4);
            double epoch = (BinaryPrimitives.ReadDoubleBigEndian(record.AsSpan(solar ? 24 : 28)) - J2000) * SecondsPerDay;
            double motion = solar ? AnnualMotion(a) : Radians(Number(24) / SecondsPerDay);
            double discovered = solar ? (float)((Number(32) - J2000) * SecondsPerDay) : 0;
            yield return new(epoch, SemiMajor(a, eccentricity, solar ? AstronomicalUnit : 1), eccentricity,
                Radians(Number(8)), Radians(Number(12)), Radians(Number(16)), Radians(Number(20)), motion, discovered);
        }
    }

    private static double SemiMajor(double a, double e, double scale) => (1 - e) * a * scale / (1 - e);
    private static double AnnualMotion(double a) => 2 * Math.PI / (365.25 * Math.Pow(a, 1.5) * SecondsPerDay);
    private static double Radians(double degrees) => degrees * (Math.PI / 180);

    private static double CalendarEpoch(int year, int month, int day)
    {
        _ = new DateOnly(year, month, day);
        int y = year, m = month;
        if (m <= 2)
        {
            y--;
            m += 12;
        }
        int correction = y > 1582 || y == 1582 && (m > 10 || m == 10 && day >= 15) ? 2 - y / 100 + y / 400 : 0;
        double jd = (int)(365.25 * (y + 4716)) + (int)(30.6001 * (m + 1)) + day + correction - 1524;
        double taiJd = ((jd - J2000) * SecondsPerDay - 32.184) / SecondsPerDay + J2000;
        double tt = (taiJd - J2000) * SecondsPerDay + 32.184;
        double phase = 6.239996 + 1.99096871e-7 * tt;
        return tt + 1.657e-3 * Math.Sin(phase + 1.671e-2 * Math.Sin(phase));
    }
}
