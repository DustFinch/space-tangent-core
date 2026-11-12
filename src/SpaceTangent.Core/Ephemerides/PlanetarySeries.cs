// SPDX-License-Identifier: GPL-2.0-or-later
using System.Security.Cryptography;
using SpaceTangent.Contracts;

namespace SpaceTangent.Core.Ephemerides;

/// <summary>J2000 ecliptic VSOP87 positions and analytic velocities from the pinned truncated series.</summary>
internal sealed class PlanetarySeries
{
    internal const double KilometersPerAu = 149_597_870.7;
    private const double SecondsPerMillennium = 365_250d * JulianInstant.SecondsPerDay;
    private const string Sha256 = "1865ca168437c9574c138af45c169902007c3ec463f4564f1e678b33e43c1217";
    private static readonly JulianInstant J2000 = new(JulianInstant.J2000Day, 0, TimeScale.Tdb);
    private readonly Term[][][] _coordinates;

    private readonly record struct Term(double Amplitude, double Phase, double Frequency);

    private PlanetarySeries(Term[][][] coordinates) => _coordinates = coordinates;

    internal static PlanetarySeries Load(string path)
    {
        const int size = 245_536;
        using var file = File.OpenRead(path);
        if (file.Length != size)
            throw new InvalidDataException("The historical planetary series has an invalid length.");
        var bytes = new byte[size];
        file.ReadExactly(bytes);
        if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(bytes), Convert.FromHexString(Sha256)))
            throw new InvalidDataException("The historical planetary series does not match its SHA-256 pin.");

        using var reader = new BinaryReader(new MemoryStream(bytes, writable: false));
        // The digest pins the version, profile, source manifest and body order. Only the coefficient series are needed here.
        reader.BaseStream.Position = 44;
        var coordinates = new Term[9 * 3][][];
        for (int body = 0; body < 9; body++)
        {
            reader.BaseStream.Position += 4;
            for (int axis = 0; axis < 3; axis++)
            {
                var powers = new Term[reader.ReadByte()][];
                reader.BaseStream.Position += 3;
                for (int power = 0; power < powers.Length; power++)
                {
                    var terms = new Term[reader.ReadInt32()];
                    for (int term = 0; term < terms.Length; term++)
                        terms[term] = new(reader.ReadDouble(), reader.ReadDouble(), reader.ReadDouble());
                    powers[power] = terms;
                }
                coordinates[body * 3 + axis] = powers;
            }
        }
        if (reader.BaseStream.Position != reader.BaseStream.Length)
            throw new InvalidDataException("The historical planetary series has an invalid layout.");
        return new(coordinates);
    }

    internal CartesianState Evaluate(int body, JulianInstant epoch)
    {
        double t = (double)epoch.DifferenceNanoseconds(J2000) / JulianInstant.NanosecondsPerSecond / SecondsPerMillennium;
        int coordinate = body * 3;
        var (x, dx) = EvaluateAxis(_coordinates[coordinate], t);
        var (y, dy) = EvaluateAxis(_coordinates[coordinate + 1], t);
        var (z, dz) = EvaluateAxis(_coordinates[coordinate + 2], t);
        Vector3d position = new(x, y, z);
        Vector3d velocity = new(dx, dy, dz);
        if (body != 0)
        {
            var (sinLongitude, cosLongitude) = Math.SinCos(x);
            var (sinLatitude, cosLatitude) = Math.SinCos(y);
            var radial = new Vector3d(cosLatitude * cosLongitude, cosLatitude * sinLongitude, sinLatitude);
            var longitude = new Vector3d(-cosLatitude * sinLongitude, cosLatitude * cosLongitude, 0);
            var latitude = new Vector3d(-sinLatitude * cosLongitude, -sinLatitude * sinLongitude, cosLatitude);
            position = radial * z;
            velocity = radial * dz + longitude * (z * dx) + latitude * (z * dy);
        }
        return new(position * KilometersPerAu, velocity * (KilometersPerAu / SecondsPerMillennium));
    }

    private static (double Value, double Slope) EvaluateAxis(Term[][] powers, double time)
    {
        double value = 0, slope = 0;
        for (int power = powers.Length - 1; power >= 0; power--)
        {
            double sum = 0, sumCorrection = 0, derivative = 0, derivativeCorrection = 0;
            foreach (Term term in powers[power])
            {
                var (sin, cos) = Math.SinCos(term.Phase + term.Frequency * time);
                Add(ref sum, ref sumCorrection, term.Amplitude * cos);
                Add(ref derivative, ref derivativeCorrection, -term.Amplitude * term.Frequency * sin);
            }
            // Differentiate the Horner recurrence, including the explicit powers of time.
            slope = slope * time + value + derivative + derivativeCorrection;
            value = value * time + sum + sumCorrection;
        }
        return (value, slope);

        static void Add(ref double sum, ref double correction, double value)
        {
            double next = sum + value;
            correction += Math.Abs(sum) >= Math.Abs(value) ? (sum - next) + value : (value - next) + sum;
            sum = next;
        }
    }
}
