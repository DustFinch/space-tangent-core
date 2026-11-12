// SPDX-License-Identifier: GPL-2.0-or-later
using System.Buffers.Binary;
using SpaceTangent.Contracts;

namespace SpaceTangent.Core.Observing;

/// <summary>
/// A 3D map of the Galaxy's dust around the Sun: visual extinction density (magnitudes per kiloparsec, the Milky Way
/// model's dust density) on a regular grid in Galactic Cartesian axes centred on the Sun (X toward the Galactic Centre,
/// Y along rotation, Z to the north Galactic pole). The archived density cube is based on the map of Vergely, Lallement
/// &amp; Cox (2022), sampled every 80 pc over a 10 x 10 x 0.8 kpc volume.
/// </summary>
public sealed class GalacticDustGrid
{
    private static ReadOnlySpan<byte> Magic => "CELDUST\0"u8;
    private const int HeaderBytes = 8 + 2 + 2 + 3 * 4 + 4 * 8;
    public const int MaximumVoxels = 4_000_000;
    private readonly float[] _density;
    public int SizeX { get; }
    public int SizeY { get; }
    public int SizeZ { get; }
    /// <summary>Grid step and the centre of voxel 0, kiloparsecs from the Sun.</summary>
    public double StepKiloparsecs { get; }
    public Vector3d OriginKiloparsecs { get; }
    /// <summary>How far in from the map's sides (x and y) and its top and bottom (z) it fades into the model, kpc.</summary>
    public const double SideFade = .4, TopFade = .04;

    private GalacticDustGrid(int x, int y, int z, double step, Vector3d origin, float[] density)
    {
        SizeX = x; SizeY = y; SizeZ = z; StepKiloparsecs = step; OriginKiloparsecs = origin; _density = density;
    }

    /// <summary>Reads a map file; throws InvalidDataException on a wrong magic, version, size or value.</summary>
    public static GalacticDustGrid Read(ReadOnlySpan<byte> data)
    {
        if (data.Length < HeaderBytes || !data[..8].SequenceEqual(Magic) || BinaryPrimitives.ReadUInt16LittleEndian(data[8..]) != 1)
            throw new InvalidDataException("Not a dust map.");
        int x = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(data[12..])), y = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(data[16..]));
        int z = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(data[20..]));
        if (x < 2 || y < 2 || z < 2 || (long)x * y > MaximumVoxels || (long)x * y * z > MaximumVoxels || data.Length != HeaderBytes + 2L * x * y * z)
            throw new InvalidDataException("Dust map size is wrong.");
        double step = BinaryPrimitives.ReadDoubleLittleEndian(data[24..]) / 1000;
        var origin = new Vector3d(BinaryPrimitives.ReadDoubleLittleEndian(data[32..]), BinaryPrimitives.ReadDoubleLittleEndian(data[40..]),
            BinaryPrimitives.ReadDoubleLittleEndian(data[48..])) / 1000;
        if (!(step > 0) || !double.IsFinite(step) || !origin.IsFinite) throw new InvalidDataException("Dust map grid is wrong.");
        var density = new float[x * y * z];
        for (int index = 0; index < density.Length; index++)
        {
            float value = (float)BinaryPrimitives.ReadHalfLittleEndian(data[(HeaderBytes + 2 * index)..]);
            if (!float.IsFinite(value) || value < 0) throw new InvalidDataException("Dust map value is wrong.");
            density[index] = value;
        }
        return new(x, y, z, step, origin, density);
    }

    /// <summary>Extinction density (mag per kpc) at a point fromSun kiloparsecs from the Sun, trilinear; 0 off the map.</summary>
    public double Density(Vector3d fromSun)
    {
        double fx = (fromSun.X - OriginKiloparsecs.X) / StepKiloparsecs, fy = (fromSun.Y - OriginKiloparsecs.Y) / StepKiloparsecs;
        double fz = (fromSun.Z - OriginKiloparsecs.Z) / StepKiloparsecs;
        if (!(fx >= 0 && fy >= 0 && fz >= 0 && fx <= SizeX - 1 && fy <= SizeY - 1 && fz <= SizeZ - 1)) return 0;
        int ix = Math.Min((int)fx, SizeX - 2), iy = Math.Min((int)fy, SizeY - 2), iz = Math.Min((int)fz, SizeZ - 2);
        double u = fx - ix, v = fy - iy, w = fz - iz;
        double At(int dx, int dy, int dz) => _density[((iz + dz) * SizeY + iy + dy) * SizeX + ix + dx];
        double Line(int dy, int dz) => At(0, dy, dz) + (At(1, dy, dz) - At(0, dy, dz)) * u;
        double Plane(int dz) => Line(0, dz) + (Line(1, dz) - Line(0, dz)) * v;
        return Plane(0) + (Plane(1) - Plane(0)) * w;
    }

    /// <summary>How much (0..1) the map rather than the model holds the dust at fromSun: 1 inside, fading near its faces.</summary>
    public double Weight(Vector3d fromSun)
    {
        double Inside(double value, double origin, int size, double fade)
        {
            double low = value - origin, high = origin + (size - 1) * StepKiloparsecs - value;
            return Math.Clamp(Math.Min(low, high) / fade, 0, 1);
        }
        return Inside(fromSun.X, OriginKiloparsecs.X, SizeX, SideFade) * Inside(fromSun.Y, OriginKiloparsecs.Y, SizeY, SideFade) *
            Inside(fromSun.Z, OriginKiloparsecs.Z, SizeZ, TopFade);
    }
}
