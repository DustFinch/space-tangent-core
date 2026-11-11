// SPDX-License-Identifier: GPL-2.0-or-later
using System.Globalization;
using SpaceTangent.Contracts;

namespace SpaceTangent.Core.Observing;

public sealed class BrightStar
{
    private static readonly JulianInstant J2000 = new(JulianInstant.J2000Day, 0, TimeScale.Tdb);
    private const long NanosecondsPerJulianYear = 31_557_600L * JulianInstant.NanosecondsPerSecond;
    private readonly Vector3d _direction, _motion;

    internal BrightStar(int hip, string name, double rightAscension, double declination, double eastMotion,
        double northMotion, double magnitude)
    {
        Hipparcos = hip;
        Id = new(FormattableString.Invariant($"hip/{hip}"));
        Name = name;
        VisualMagnitude = magnitude;
        var (sinRa, cosRa) = Math.SinCos(rightAscension);
        var (sinDec, cosDec) = Math.SinCos(declination);
        _direction = new(cosDec * cosRa, cosDec * sinRa, sinDec);
        var east = new Vector3d(-sinRa, cosRa, 0);
        var north = new Vector3d(-sinDec * cosRa, -sinDec * sinRa, cosDec);
        _motion = east * eastMotion + north * northMotion;
    }

    public int Hipparcos { get; }
    public ObjectId Id { get; }
    public string Name { get; }
    public double VisualMagnitude { get; }

    /// <summary>ICRS unit direction from the J2000 position and linear tangential proper motion.</summary>
    public Vector3d DirectionAt(JulianInstant tdb)
    {
        double years = (double)tdb.DifferenceNanoseconds(J2000) / NanosecondsPerJulianYear;
        Vector3d position = _direction + _motion * years;
        return position / position.Length;
    }
}

public sealed class BrightStarCatalogue
{
    private const string Header = "hip\tname\tra_rad\tdec_rad\tmu_alpha_cos_delta_rad_y\tmu_delta_rad_y\tv_mag";
    private readonly Dictionary<ObjectId, BrightStar> _index;
    public IReadOnlyList<BrightStar> Stars { get; }
    public static BrightStarCatalogue Empty { get; } = new([]);

    private BrightStarCatalogue(Dictionary<ObjectId, BrightStar> index)
    {
        _index = index;
        Stars = Array.AsReadOnly(index.Values.OrderBy(star => star.VisualMagnitude).ThenBy(star => star.Hipparcos).ToArray());
    }

    public BrightStar? Find(ObjectId id) => _index.GetValueOrDefault(id);

    public static BrightStarCatalogue Read(TextReader input)
    {
        if (input.ReadLine() != Header)
            throw new InvalidDataException("The bright star catalogue has an invalid header.");

        var index = new Dictionary<ObjectId, BrightStar>();
        int lineNumber = 1;
        while (input.ReadLine() is { } line)
        {
            lineNumber++;
            string[] fields = line.Split('\t');
            if (fields.Length != 7 ||
                !int.TryParse(fields[0], NumberStyles.None, CultureInfo.InvariantCulture, out int hip) || hip <= 0 ||
                fields[1].Length > 100 ||
                !Number(2, out double ra) || ra is < 0 or >= Math.Tau ||
                !Number(3, out double dec) || Math.Abs(dec) > Math.PI / 2 ||
                !Number(4, out double east) || Math.Abs(east) > .001 ||
                !Number(5, out double north) || Math.Abs(north) > .001 ||
                !Number(6, out double magnitude) || magnitude is < -5 or > 15 ||
                index.Count >= 20_000)
                throw new InvalidDataException($"Invalid bright star at line {lineNumber}.");

            var star = new BrightStar(hip, fields[1], ra, dec, east, north, magnitude);
            if (!index.TryAdd(star.Id, star))
                throw new InvalidDataException($"Duplicate Hipparcos ID at line {lineNumber}.");

            bool Number(int field, out double value) =>
                double.TryParse(fields[field], NumberStyles.Float, CultureInfo.InvariantCulture, out value) && double.IsFinite(value);
        }
        return new(index);
    }
}
