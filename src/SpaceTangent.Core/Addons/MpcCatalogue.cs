// SPDX-License-Identifier: GPL-2.0-or-later
using System.Globalization;
using System.Text;
using SpaceTangent.Contracts;
using SpaceTangent.Core.Ephemerides;
using SpaceTangent.Core.Orbits;
using SpaceTangent.Core.Time;

namespace SpaceTangent.Core.Addons;

public sealed record MpcOrbit(string Name, string Designation, bool IsComet, double RadiusKilometers,
    double? AbsoluteMagnitude, JulianInstant ElementEpoch, ConicOrbit EclipticOrbit)
{
    public CartesianState Sample(JulianInstant epoch)
    {
        CartesianState state = EclipticOrbit.Evaluate(epoch);
        return new(HistoricalSolarEphemeris.Rotate(state.PositionKilometers),
            HistoricalSolarEphemeris.Rotate(state.VelocityKilometersPerSecond));
    }
}

public sealed record MpcCatalogueResult(IReadOnlyList<MpcOrbit> Orbits, IReadOnlyList<string> Warnings);

/// <summary>Reads local MPCORB asteroid and CometEls orbital element records.</summary>
public static class MpcCatalogue
{
    public const int MaximumOrbits = 5000;
    private const long MaximumBytes = 16 * 1024 * 1024;
    private const double AuKilometers = 149_597_870.7, SolarMu = 132_712_440_041.2794, Radians = Math.PI / 180;
    private static readonly FrameId Ecliptic = new("j2000-ecliptic");

    public static MpcCatalogueResult Load(string path, CancellationToken cancellation = default)
    {
        using FileStream stream = AddonDependencies.OpenRead(path, MaximumBytes);
        if (stream.Length > MaximumBytes)
            throw new InvalidDataException("MPC selection exceeds 16 MiB.");
        using var reader = new StreamReader(stream, new UTF8Encoding(false, true));
        var orbits = new List<MpcOrbit>();
        var warnings = new List<string>();
        var designations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int number = 0, invalid = 0;
        while (reader.ReadLine() is { } line)
        {
            cancellation.ThrowIfCancellationRequested();
            number++;
            if (string.IsNullOrWhiteSpace(line) || line.TrimStart().StartsWith('#') ||
                line.StartsWith("-----", StringComparison.Ordinal) || line.StartsWith("Des'n", StringComparison.Ordinal))
                continue;
            try
            {
                MpcOrbit orbit = line.Length >= 79 && "CPDXIA".Contains(line[4]) &&
                    int.TryParse(line.AsSpan(14, 4), NumberStyles.None, CultureInfo.InvariantCulture, out int year)
                    ? ReadComet(line, year) : ReadAsteroid(line);
                if (!designations.Add(orbit.Designation))
                    throw new FormatException($"Repeated designation {orbit.Designation}.");
                if (orbits.Count == MaximumOrbits)
                {
                    warnings.Add($"The selection reached {MaximumOrbits} orbits. Later records were left out.");
                    break;
                }
                orbits.Add(orbit);
            }
            catch (Exception error) when (error is FormatException or ArgumentOutOfRangeException or OverflowException)
            {
                if (invalid++ < 20)
                    warnings.Add($"Line {number}: {error.Message}");
            }
        }
        if (invalid > 20)
            warnings.Add($"{invalid - 20} additional invalid or duplicate lines were left out.");
        return new(orbits.AsReadOnly(), warnings.AsReadOnly());
    }

    private static MpcOrbit ReadAsteroid(string line)
    {
        if (line.Length < 103)
            throw new FormatException("Incomplete MPCORB record.");
        string packed = Field(line, 0, 7);
        if (packed.Length == 0)
            throw new FormatException("MPCORB record needs a designation.");
        string designation = Field(line, 166, 194);
        if (designation.Length == 0)
            designation = packed;
        int end = designation.IndexOf(')');
        string name = designation.StartsWith('(') && end >= 0 && end + 1 < designation.Length
            ? designation[(end + 1)..].TrimStart() : designation;
        double a = Number(line, 92, 103) * AuKilometers, e = Number(line, 70, 79);
        if (!(a > 0 && a <= 1e5 * AuKilometers && e is >= 0 and < 1))
            throw new FormatException("Invalid MPCORB ellipse.");
        JulianInstant epoch = ReadPackedEpoch(line.AsSpan(20, 5));
        double mean = Math.IEEERemainder(Number(line, 26, 35) * Radians, Math.Tau);
        JulianInstant periapsis = epoch.AddSeconds(-mean * a * Math.Sqrt(a / SolarMu));
        double? h = OptionalNumber(line, 8, 13);
        double radius = h is { } magnitude ? 1329 / (2 * Math.Sqrt(.15)) * Math.Pow(10, -magnitude / 5) : 1;
        if (!(radius > 0 && radius <= 2_000_000))
            throw new FormatException("MPCORB magnitude implies an invalid radius.");
        return new(name, designation, false, radius, h, epoch, Orbit(a * (1 - e), e, Number(line, 59, 68),
            Number(line, 48, 57), Number(line, 37, 46), periapsis));
    }

    private static MpcOrbit ReadComet(string line, int year)
    {
        string designation = Field(line, 102, 158);
        if (designation.Length == 0)
            designation = Field(line, 0, 12);
        double day = Number(line, 22, 29);
        JulianInstant periapsis = Epoch(year, (int)Number(line, 19, 21), (int)day, day % 1);
        double q = Number(line, 30, 39) * AuKilometers, e = Number(line, 41, 49);
        if (!(q > 0 && q <= 1e5 * AuKilometers && e is >= 0 and <= 100))
            throw new FormatException("Invalid comet perihelion distance or eccentricity.");
        return new(designation, designation, true, 1, OptionalNumber(line, 91, 95), periapsis,
            Orbit(q, e, Number(line, 71, 79), Number(line, 61, 69), Number(line, 51, 59), periapsis));
    }

    private static ConicOrbit Orbit(double q, double e, double inclination, double node, double argument,
        JulianInstant periapsis) => new(new(q, e, inclination * Radians, node * Radians,
            argument * Radians, periapsis, SolarMu, Ecliptic));

    private static JulianInstant ReadPackedEpoch(ReadOnlySpan<char> packed)
    {
        int century = packed[0] switch { 'I' => 1800, 'J' => 1900, 'K' => 2000, 'L' => 2100, _ => 0 };
        if (century == 0 || !int.TryParse(packed[1..3], NumberStyles.None, CultureInfo.InvariantCulture, out int year))
            throw new FormatException("Invalid packed MPC epoch.");
        return Epoch(century + year, Digit(packed[3]), Digit(packed[4]), 0);

        static int Digit(char c) => c switch { >= '0' and <= '9' => c - '0', >= 'A' and <= 'V' => c - 'A' + 10, _ => -1 };
    }

    private static JulianInstant Epoch(int year, int month, int day, double fraction)
    {
        var date = new DateOnly(year, month, day);
        JulianInstant tt = new(1_721_425L + date.DayNumber, JulianInstant.NanosecondsPerDay / 2, TimeScale.Tt);
        tt = tt.AddSeconds(fraction * JulianInstant.SecondsPerDay);
        // Outside the time conversion profile, the catalogue precision is coarser than the periodic TT/TDB offset.
        return year is >= 1900 and < 2100 ? TimeConversion.Convert(tt, TimeScale.Tdb) :
            new(tt.Day, tt.NanosecondsOfDay, TimeScale.Tdb);
    }

    private static string Field(string line, int start, int end) =>
        start >= line.Length ? "" : line[start..Math.Min(line.Length, end)].Trim();

    private static double Number(string line, int start, int end) =>
        double.TryParse(Field(line, start, end), NumberStyles.Float, CultureInfo.InvariantCulture, out double value) &&
        double.IsFinite(value) ? value : throw new FormatException($"Invalid number in MPC columns {start + 1}–{end}.");

    private static double? OptionalNumber(string line, int start, int end) =>
        Field(line, start, end).Length == 0 ? null : Number(line, start, end);
}
