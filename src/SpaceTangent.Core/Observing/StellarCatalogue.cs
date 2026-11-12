// SPDX-License-Identifier: GPL-2.0-or-later
using System.Globalization;
using System.Text;
using SpaceTangent.Contracts;
using SpaceTangent.Core.Addons;
using SpaceTangent.Core.Ephemerides;
using SpaceTangent.Core.Orbits;

namespace SpaceTangent.Core.Observing;

public sealed record StellarEntry(ObjectId Id, string Name, double DistanceParsecs, Vector3d PositionParsecs,
    Vector3d MotionParsecsPerYear, double VisualMagnitude, double ColorIndex, double RadiusKilometers)
{
    public const double KilometersPerParsec = 30_856_775_814_913.673;
    public const double KilometersPerLightYear = 9_460_730_472_580.8;
    private const double SecondsPerJulianYear = 31_557_600;
    private static readonly JulianInstant J2000 = JulianInstant.FromJulianDate(2451545, TimeScale.Tdb);

    public IReadOnlyList<string> Aliases { get; init; } = [];
    public double? AbsoluteMagnitude { get; init; }
    public Vector3d? ColorSrgb { get; init; }
    public bool IsBarycenter { get; init; }
    public double SystemRadiusKilometers { get; init; }
    public ObjectId? BarycenterId => Barycenter?.Id;
    internal StellarEntry? Barycenter { get; init; }
    internal StellarConic? Orbit { get; init; }

    /// <summary>Geometric position and velocity in ICRS axes, relative to the Sun's J2000 position.</summary>
    public CartesianState StateAt(JulianInstant tdb)
    {
        if (Barycenter is { } parent)
        {
            CartesianState origin = parent.StateAt(tdb);
            if (Orbit is not { } orbit)
                return origin;
            CartesianState relative = orbit.Evaluate(tdb);
            return origin + new CartesianState(HistoricalSolarEphemeris.Rotate(relative.PositionKilometers),
                HistoricalSolarEphemeris.Rotate(relative.VelocityKilometersPerSecond));
        }
        double years = (double)tdb.DifferenceNanoseconds(J2000) / (JulianInstant.NanosecondsPerSecond * SecondsPerJulianYear);
        return new((PositionParsecs + MotionParsecsPerYear * years) * KilometersPerParsec,
            MotionParsecsPerYear * (KilometersPerParsec / SecondsPerJulianYear));
    }
}

/// <summary>Positions with measured distances, separate from direction-only catalogue entries.</summary>
public sealed partial class StellarCatalogue
{
    private const string Header = "id\tname\tdist_pc\tx_pc\ty_pc\tz_pc\tvx_pc_y\tvy_pc_y\tvz_pc_y\tv_mag\tb_v\tr_km";
    private const int MaximumEntries = 160_000;
    private readonly Dictionary<ObjectId, StellarEntry> _index;
    private readonly Lazy<Dictionary<string, StellarEntry>> _names;

    private StellarCatalogue(Dictionary<ObjectId, StellarEntry> index)
    {
        _index = index;
        Stars = Array.AsReadOnly(index.Values.ToArray());
        _names = new(() =>
        {
            var names = new Dictionary<string, StellarEntry>(StringComparer.OrdinalIgnoreCase);
            foreach (StellarEntry star in Stars)
            {
                names.TryAdd(star.Name, star);
                foreach (string alias in star.Aliases)
                    names.TryAdd(alias, star);
            }
            return names;
        });
    }

    public static StellarCatalogue Empty { get; } = new([]);
    public IReadOnlyList<StellarEntry> Stars { get; }
    public StellarEntry? Find(ObjectId id) => _index.GetValueOrDefault(id);
    public StellarEntry? FindByName(string name) => _names.Value.GetValueOrDefault(name.Trim());

    public static StellarCatalogue Load(string path)
    {
        using FileStream file = AddonDependencies.OpenRead(path, 64L * 1024 * 1024);
        using var reader = new StreamReader(file, new UTF8Encoding(false, true));
        return Read(reader);
    }

    public static StellarCatalogue Read(TextReader reader)
    {
        if (reader.ReadLine() != Header)
            throw new InvalidDataException("The spatial star catalogue has an invalid header.");
        var index = new Dictionary<ObjectId, StellarEntry>();
        var numbers = new double[10];
        for (int line = 2; reader.ReadLine() is { } text; line++)
        {
            string[] fields = text.Split('\t');
            if (fields.Length != 12 || fields[1].Length is < 1 or > 100 ||
                !ValidId(fields[0]) || index.Count >= MaximumEntries)
                throw new InvalidDataException($"Invalid spatial star at line {line}.");
            for (int i = 0; i < numbers.Length; i++)
                if (!double.TryParse(fields[i + 2], NumberStyles.Float, CultureInfo.InvariantCulture, out numbers[i]) ||
                    !double.IsFinite(numbers[i]))
                    throw new InvalidDataException($"Invalid spatial star number at line {line}.");
            var position = new Vector3d(numbers[1], numbers[2], numbers[3]);
            var motion = new Vector3d(numbers[4], numbers[5], numbers[6]);
            if (numbers[0] is <= 0 or >= 100_000 || Math.Abs(position.Length - numbers[0]) > .001 ||
                motion.Length > .25 || numbers[7] is < -5 or > 35 || numbers[8] is < -1 or > 6 ||
                numbers[9] is < 100 or > 3e9)
                throw new InvalidDataException($"Spatial star values are out of range at line {line}.");
            var id = new ObjectId(fields[0]);
            if (!index.TryAdd(id, new(id, fields[1], numbers[0], position, motion, numbers[7], numbers[8], numbers[9])))
                throw new InvalidDataException($"Duplicate spatial star ID at line {line}.");
        }
        return new(index);

        static bool ValidId(string value) => value.Length is >= 5 and <= 12 &&
            (value.StartsWith("hip/", StringComparison.Ordinal) || value.StartsWith("hyg/", StringComparison.Ordinal)) &&
            int.TryParse(value.AsSpan(4), NumberStyles.None, CultureInfo.InvariantCulture, out int number) && number > 0;
    }
}
