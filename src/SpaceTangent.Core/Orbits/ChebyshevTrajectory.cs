// SPDX-License-Identifier: GPL-2.0-or-later
using System.Buffers.Binary;
using SpaceTangent.Contracts;
using SpaceTangent.Core.Addons;
using SpaceTangent.Core.Ephemerides;

namespace SpaceTangent.Core.Orbits;

/// <summary>A local CHEBPOLY trajectory. Coefficients describe kilometers as a function of TDB seconds.</summary>
public sealed class ChebyshevTrajectory
{
    private const int MaximumBytes = 64 * 1024 * 1024;
    private const int HeaderSize = 32;
    private readonly byte[] _coefficients;
    private readonly int _records, _axisBytes;
    private readonly double _start, _recordDuration, _end;

    private ChebyshevTrajectory(byte[] coefficients, int records, int degree, double start, double duration)
    {
        _coefficients = coefficients;
        _records = records;
        _axisBytes = (degree + 1) * sizeof(double);
        _start = start;
        _recordDuration = duration;
        _end = start + records * duration;
    }

    public static ChebyshevTrajectory Load(string path) => AddonDiagnostics.Read(AddonStage.Trajectory, path, () =>
    {
        using FileStream input = AddonDependencies.OpenRead(path, MaximumBytes);
        if (input.Length is < HeaderSize or > MaximumBytes)
            throw new InvalidDataException("Invalid CHEBPOLY file length.");
        Span<byte> header = stackalloc byte[HeaderSize];
        input.ReadExactly(header);
        int records = BinaryPrimitives.ReadInt32LittleEndian(header[8..]);
        int degree = BinaryPrimitives.ReadInt32LittleEndian(header[12..]);
        double start = BinaryPrimitives.ReadDoubleLittleEndian(header[16..]);
        double duration = BinaryPrimitives.ReadDoubleLittleEndian(header[24..]);
        if (!header[..8].SequenceEqual("CHEBPOLY"u8) || records <= 0 || degree is < 0 or > 128 ||
            !(duration > 0) || !double.IsFinite(start) || !double.IsFinite(start + records * duration) ||
            input.Length != HeaderSize + 3L * (degree + 1) * records * sizeof(double))
            throw new InvalidDataException("Invalid CHEBPOLY header or coefficient count.");
        byte[] data = new byte[(int)input.Length - HeaderSize];
        input.ReadExactly(data);
        for (int index = 0; index < data.Length; index += sizeof(double))
            if (!double.IsFinite(BinaryPrimitives.ReadDoubleLittleEndian(data.AsSpan(index, sizeof(double)))))
                throw new InvalidDataException("A CHEBPOLY coefficient is not finite.");
        return new ChebyshevTrajectory(data, records, degree, start, duration);
    });

    public CartesianState Sample(JulianInstant epoch)
    {
        if (epoch.Scale != TimeScale.Tdb)
            throw new ArgumentException("CHEBPOLY trajectories require TDB.", nameof(epoch));
        // Preserve the source's Julian-day projection and hold its endpoint state outside the file.
        double seconds = Math.Clamp((epoch.ToJulianDate() - JulianInstant.J2000Day) * JulianInstant.SecondsPerDay, _start, _end);
        int record = (int)Math.Clamp(Math.Floor((seconds - _start) / _recordDuration), 0, _records - 1);
        double coordinate = Math.Clamp(2 * (seconds - _start - record * _recordDuration) / _recordDuration - 1, -1, 1);
        int offset = record * 3 * _axisBytes;
        var (x, vx) = Chebyshev.Evaluate(_coefficients.AsSpan(offset, _axisBytes), coordinate, littleEndian: true);
        var (y, vy) = Chebyshev.Evaluate(_coefficients.AsSpan(offset + _axisBytes, _axisBytes), coordinate, littleEndian: true);
        var (z, vz) = Chebyshev.Evaluate(_coefficients.AsSpan(offset + 2 * _axisBytes, _axisBytes), coordinate, littleEndian: true);
        return new(new(x, y, z), new Vector3d(vx, vy, vz) * (2 / _recordDuration));
    }
}
