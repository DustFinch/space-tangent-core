// SPDX-License-Identifier: GPL-2.0-or-later
using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace SpaceTangent.Core.Surfaces;

/// <summary>A local equirectangular map of signed heights above a spherical datum.</summary>
public sealed class ElevationMap
{
    private const int HeaderBytes = 44;
    private const long MaximumSamples = 64L * 1024 * 1024;
    private readonly short[] _samples;
    private readonly double _metersPerUnit, _offsetMeters;

    private ElevationMap(int width, int height, double radius, double unit, double offset, short[] samples)
    {
        Width = width;
        Height = height;
        ReferenceRadiusKilometers = radius;
        _metersPerUnit = unit;
        _offsetMeters = offset;
        _samples = samples;
    }

    public int Width { get; }
    public int Height { get; }
    public double ReferenceRadiusKilometers { get; }

    /// <summary>Copies samples in north-to-south row order, with each height in meters.</summary>
    public void CopyHeightsMeters(Span<float> destination, CancellationToken cancellation = default)
    {
        if (destination.Length < _samples.Length)
            throw new ArgumentException("The destination is smaller than the terrain grid.", nameof(destination));
        for (int start = 0; start < _samples.Length; start += 32768)
        {
            cancellation.ThrowIfCancellationRequested();
            int end = Math.Min(start + 32768, _samples.Length);
            for (int i = start; i < end; i++)
            {
                float height = (float)(_offsetMeters + _metersPerUnit * _samples[i]);
                if (!float.IsFinite(height))
                    throw new InvalidDataException("Terrain heights exceed the single-precision range.");
                destination[i] = height;
            }
        }
    }

    public static ElevationMap Load(string path, CancellationToken cancellation = default)
    {
        using var file = File.OpenRead(path);
        Span<byte> header = stackalloc byte[HeaderBytes];
        if (file.Length < HeaderBytes)
            throw new InvalidDataException("The terrain header is truncated.");
        file.ReadExactly(header);
        // File signature used by the existing local relief packs.
        if (!header[..8].SequenceEqual("CELWTRN\0"u8) ||
            BinaryPrimitives.ReadUInt16LittleEndian(header[8..]) != 1 ||
            BinaryPrimitives.ReadUInt16LittleEndian(header[10..]) != 0)
            throw new InvalidDataException("Unsupported terrain map header.");
        uint width = BinaryPrimitives.ReadUInt32LittleEndian(header[12..]);
        uint height = BinaryPrimitives.ReadUInt32LittleEndian(header[16..]);
        ulong count = (ulong)width * height;
        double unit = BinaryPrimitives.ReadDoubleLittleEndian(header[20..]);
        double offset = BinaryPrimitives.ReadDoubleLittleEndian(header[28..]);
        double radius = BinaryPrimitives.ReadDoubleLittleEndian(header[36..]);
        if (width < 2 || height < 2 || count > MaximumSamples || file.Length != HeaderBytes + 2L * (long)count ||
            !double.IsFinite(unit) || unit <= 0 || !double.IsFinite(offset) || !double.IsFinite(radius) || radius <= 0)
            throw new InvalidDataException("Invalid terrain dimensions, scale or file length.");

        cancellation.ThrowIfCancellationRequested();
        var samples = new short[(int)count];
        short min = short.MaxValue, max = short.MinValue;
        const int ChunkSize = 32768;
        for (int start = 0; start < samples.Length; start += ChunkSize)
        {
            cancellation.ThrowIfCancellationRequested();
            var chunk = samples.AsSpan(start, Math.Min(ChunkSize, samples.Length - start));
            file.ReadExactly(MemoryMarshal.AsBytes(chunk));
            foreach (ref short value in chunk)
            {
                if (!BitConverter.IsLittleEndian)
                    value = BinaryPrimitives.ReverseEndianness(value);
                min = Math.Min(min, value);
                max = Math.Max(max, value);
            }
        }
        double low = offset + min * unit, high = offset + max * unit;
        if (!double.IsFinite(low) || !double.IsFinite(high) || radius + low / 1000 <= 0 ||
            !double.IsFinite(radius + high / 1000))
            throw new InvalidDataException("Terrain elevations are outside the supported radius range.");
        return new((int)width, (int)height, radius, unit, offset, samples);
    }

    /// <summary>Samples east longitude and north latitude in degrees, using cell centers.</summary>
    public double SampleMeters(double longitudeDegrees, double latitudeDegrees)
    {
        if (!double.IsFinite(longitudeDegrees))
            throw new ArgumentOutOfRangeException(nameof(longitudeDegrees));
        if (!double.IsFinite(latitudeDegrees) || latitudeDegrees is < -90 or > 90)
            throw new ArgumentOutOfRangeException(nameof(latitudeDegrees));

        double x = (.5 + Math.IEEERemainder(longitudeDegrees, 360) / 360) * Width - .5;
        double y = (.5 - latitudeDegrees / 180) * Height - .5;
        int column = (int)Math.Floor(x), row = (int)Math.Floor(y);
        double fx = x - column, fy = y - row;
        int left = (column + Width) % Width, right = (left + 1) % Width;
        int top = Math.Clamp(row, 0, Height - 1) * Width;
        int bottom = Math.Clamp(row + 1, 0, Height - 1) * Width;
        double north = _samples[top + left] * (1 - fx) + _samples[top + right] * fx;
        double south = _samples[bottom + left] * (1 - fx) + _samples[bottom + right] * fx;
        return _offsetMeters + _metersPerUnit * (north * (1 - fy) + south * fy);
    }
}
