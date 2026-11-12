// SPDX-License-Identifier: GPL-2.0-or-later
using System.Buffers.Binary;
using SpaceTangent.Contracts;

namespace SpaceTangent.Core.Addons;

/// <summary>Physical coefficients and native transmission and inscatter tables from an atmscat file.</summary>
public sealed class AtmosphereLookup
{
    public const int MaximumBytes = 64 * 1024 * 1024;
    private const int HeaderBytes = 76;
    private readonly float[] _transmission, _scattering;

    private AtmosphereLookup(float[] header, int[] dimensions, float[] transmission, float[] scattering)
    {
        RayleighScaleHeightKilometers = header[0];
        RayleighPerKilometer = new(1000f * header[1], 1000f * header[2], 1000f * header[3]);
        MieScaleHeightKilometers = header[4];
        MiePerKilometer = 1000f * header[5];
        MieAsymmetry = header[6];
        AbsorptionPerKilometer = new(1000f * header[7], 1000f * header[8], 1000f * header[9]);
        RadiusKilometers = header[10];
        (TransmissionWidth, TransmissionHeight) = (dimensions[0], dimensions[1]);
        (ScatteringWidth, ScatteringHeight, ScatteringDepth) = (dimensions[2], dimensions[3], dimensions[4]);
        _transmission = transmission;
        _scattering = scattering;
    }

    public float RadiusKilometers { get; }
    public float RayleighScaleHeightKilometers { get; }
    public float MieScaleHeightKilometers { get; }
    public float TransparentHeightKilometers => 8 * Math.Max(RayleighScaleHeightKilometers, MieScaleHeightKilometers);
    public Vector3d RayleighPerKilometer { get; }
    public float MiePerKilometer { get; }
    public float MieAsymmetry { get; }
    public Vector3d AbsorptionPerKilometer { get; }
    public int TransmissionWidth { get; }
    public int TransmissionHeight { get; }
    public int ScatteringWidth { get; }
    public int ScatteringHeight { get; }
    public int ScatteringDepth { get; }
    public ReadOnlySpan<float> Transmission => _transmission;
    public ReadOnlySpan<float> Scattering => _scattering;

    public static AtmosphereLookup Load(string path)
    {
        using var input = File.OpenRead(path);
        if (input.Length is < HeaderBytes or > MaximumBytes)
            throw new InvalidDataException("The atmosphere lookup file has an invalid size.");
        byte[] data = new byte[checked((int)input.Length)];
        input.ReadExactly(data);
        return Read(data);
    }

    public static AtmosphereLookup Read(ReadOnlySpan<byte> source)
    {
        if (source.Length is < HeaderBytes or > MaximumBytes || !source[..8].SequenceEqual("atmscatr"u8) ||
            BinaryPrimitives.ReadUInt32LittleEndian(source[8..]) != 1)
            throw new InvalidDataException("The atmosphere lookup requires an atmscat version 1 header.");
        float[] header = Floats(source.Slice(12, 44));
        if (header.Any(value => !float.IsFinite(value)) || header[0] <= 0 || header[4] <= 0 || header[10] <= 0 ||
            Math.Abs(header[6]) >= 1 || !float.IsFinite(8 * Math.Max(header[0], header[4])) ||
            !Coefficient(header[1]) || !Coefficient(header[2]) || !Coefficient(header[3]) || !Coefficient(header[5]) ||
            !Coefficient(header[7]) || !Coefficient(header[8]) || !Coefficient(header[9]))
            throw new InvalidDataException("The atmosphere lookup has invalid coefficients.");
        var dimensions = new int[5];
        for (int index = 0; index < dimensions.Length; index++)
        {
            uint dimension = BinaryPrimitives.ReadUInt32LittleEndian(source[(56 + index * 4)..]);
            if (dimension == 0 || dimension > (index < 2 ? 16_384 : 2048))
                throw new InvalidDataException("The atmosphere lookup has invalid dimensions.");
            dimensions[index] = (int)dimension;
        }
        long transmissionBytes = 12L * dimensions[0] * dimensions[1];
        long scatteringBytes = 16L * dimensions[2] * dimensions[3] * dimensions[4];
        if (HeaderBytes + transmissionBytes + scatteringBytes > source.Length)
            throw new InvalidDataException("The atmosphere lookup tables are incomplete.");
        int scatteringStart = HeaderBytes + (int)transmissionBytes;
        return new(header, dimensions, Floats(source[HeaderBytes..scatteringStart]),
            Floats(source.Slice(scatteringStart, (int)scatteringBytes)));

        static bool Coefficient(float value) => value >= 0 && float.IsFinite(1000 * value);
    }

    /// <summary>Returns little-endian RGB16F ordered by view direction then height.</summary>
    public byte[] TransmissionHalfBytes() => HalfBytes(_transmission);

    /// <summary>Returns little-endian RGBA16F ordered by solar direction, view direction, then height.</summary>
    public byte[] ScatteringHalfBytes() => HalfBytes(_scattering);

    private static float[] Floats(ReadOnlySpan<byte> source)
    {
        var result = new float[source.Length / sizeof(float)];
        for (int index = 0; index < result.Length; index++)
            result[index] = BinaryPrimitives.ReadSingleLittleEndian(source[(index * sizeof(float))..]);
        return result;
    }

    private static byte[] HalfBytes(ReadOnlySpan<float> values)
    {
        byte[] result = new byte[values.Length * sizeof(ushort)];
        for (int index = 0; index < values.Length; index++)
        {
            // Match the native upload: unordered values take the upper clamp, including NaN.
            float value = values[index] < 256f ? values[index] : 256f;
            var half = (Half)Math.Max(.00001f, value);
            BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(index * sizeof(ushort)), BitConverter.HalfToUInt16Bits(half));
        }
        return result;
    }
}
