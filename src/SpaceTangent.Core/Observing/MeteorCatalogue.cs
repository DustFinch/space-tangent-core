// SPDX-License-Identifier: GPL-2.0-or-later
using System.Globalization;
using SpaceTangent.Contracts;
using SpaceTangent.Core.Ephemerides;

namespace SpaceTangent.Core.Observing;

public sealed record MeteorShower(string Code, string Name, double BeginLongitude, double EndLongitude,
    double PeakLongitude, double RightAscensionDegrees, double DeclinationDegrees, double RightAscensionDrift,
    double DeclinationDrift, double SpeedKilometersPerSecond, double PopulationIndex, double AnnualZhr,
    double SlopeBefore, double SlopeAfter, double MaximumZhr)
{
    public bool Daytime => Code is "ARI" or "DSX";
    public bool IsActive(double longitude) =>
        MeteorCatalogue.Wrap(longitude - BeginLongitude) <= MeteorCatalogue.Wrap(EndLongitude - BeginLongitude);

    public double ZenithRate(double longitude)
    {
        if (!IsActive(longitude))
            return 0;
        double offset = MeteorCatalogue.SignedLongitude(longitude - PeakLongitude);
        return AnnualZhr * Math.Pow(10, -Math.Abs(offset) * (offset < 0 ? SlopeBefore : SlopeAfter));
    }

    public Vector3d RadiantDirection(double longitude)
    {
        double offset = MeteorCatalogue.SignedLongitude(longitude - PeakLongitude);
        double ra = (RightAscensionDegrees + offset * RightAscensionDrift) * MeteorCatalogue.Radians;
        double dec = Math.Clamp(DeclinationDegrees + offset * DeclinationDrift, -90, 90) * MeteorCatalogue.Radians;
        var (sinRa, cosRa) = Math.SinCos(ra);
        var (sinDec, cosDec) = Math.SinCos(dec);
        return new(cosRa * cosDec, sinRa * cosDec, sinDec);
    }
}

public readonly record struct MeteorRadiant(MeteorShower Shower, Vector3d Direction, double AltitudeDegrees,
    double ZenithRate, double ObservedHourlyRate);

public readonly record struct MeteorPeak(MeteorShower Shower, JulianInstant Epoch);

public sealed class MeteorCatalogue
{
    private const string Header = "code\tname\tbegin\tend\tpeak\tra\tdec\tdrift_ra\tdrift_dec\tspeed\tpopulation\tannual_zhr\tslope_before\tslope_after\tmax_zhr";
    internal const double Radians = Math.PI / 180;
    public IReadOnlyList<MeteorShower> Showers { get; }

    private MeteorCatalogue(MeteorShower[] showers) => Showers = Array.AsReadOnly(showers);

    public static MeteorCatalogue Read(TextReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        if (reader.ReadLine() != Header)
            throw new InvalidDataException("The meteor catalogue header is invalid.");
        var rows = new Dictionary<string, MeteorShower>(StringComparer.Ordinal);
        int lineNumber = 1;
        while (reader.ReadLine() is { } line)
        {
            lineNumber++;
            if (string.IsNullOrWhiteSpace(line))
                continue;
            string[] fields = line.Split('\t');
            if (fields.Length != 15 || fields[0].Length != 3 || !fields[0].All(char.IsAsciiLetterUpper) ||
                string.IsNullOrWhiteSpace(fields[1]) || rows.Count >= 512)
                throw Error();
            var n = new double[13];
            for (int i = 0; i < n.Length; i++)
                if (!double.TryParse(fields[i + 2], NumberStyles.Float, CultureInfo.InvariantCulture, out n[i]) || !double.IsFinite(n[i]))
                    throw Error();
            if (n.Take(4).Any(value => value is < 0 or >= 360) || n[4] is < -90 or > 90 ||
                n[5] is < -90 or > 90 || n[6] is < -90 or > 90 ||
                n[7] is < 0 or > 100 || n[8] is < 1 or > 10 || n[9] is < 0 or > 100_000 ||
                n[10] is < 0 or > 10 || n[11] is < 0 or > 10 || n[12] < n[9] || n[12] > 100_000)
                throw Error();
            var shower = new MeteorShower(fields[0], fields[1], n[0], n[1], n[2], n[3], n[4], n[5], n[6],
                n[7], n[8], n[9], n[10], n[11], n[12]);
            if (!shower.IsActive(shower.PeakLongitude) || !rows.TryAdd(shower.Code, shower))
                throw Error();
        }
        return new(rows.Values.ToArray());

        InvalidDataException Error() => new($"Invalid meteor catalogue record at line {lineNumber}.");
    }

    public IReadOnlyList<MeteorRadiant> Radiants(SolarSystemSample sample, SurfaceLighting observer, double limitingMagnitude)
    {
        double longitude = SolarLongitude(sample);
        var result = new List<MeteorRadiant>();
        foreach (var shower in Showers)
        {
            if (!shower.IsActive(longitude))
                continue;
            var direction = shower.RadiantDirection(longitude);
            if (EarthObserving.HorizontalDirection(observer, direction) is not { } sky)
                continue;
            double zhr = shower.ZenithRate(longitude);
            result.Add(new(shower, direction, sky.AltitudeDegrees, zhr,
                ObservedRate(zhr, sky.AltitudeDegrees, shower.PopulationIndex, limitingMagnitude)));
        }
        return result;
    }

    public MeteorPeak? NextPeak(ISolarSystemEphemeris ephemeris, JulianInstant from)
    {
        if (from.CompareTo(ephemeris.Start) < 0 || from.CompareTo(ephemeris.End) >= 0)
            return null;
        var now = SolarLongitude(ephemeris.Sample(from));
        var candidate = Showers.Where(shower => !shower.Daytime && shower.AnnualZhr >= 10)
            .MinBy(shower => Wrap(shower.PeakLongitude - now));
        if (candidate is null)
            return null;
        double target = Wrap(candidate.PeakLongitude - now);
        if (target < 1e-8)
            return new(candidate, from);
        var left = from;
        double covered = (double)ephemeris.End.DifferenceNanoseconds(from) / JulianInstant.NanosecondsPerSecond;
        double span = Math.Min(covered, 370 * JulianInstant.SecondsPerDay);
        for (double offset = Math.Min(span, JulianInstant.SecondsPerDay); offset > 0;)
        {
            var right = from.AddSeconds(offset);
            double rightLongitude = SolarLongitude(ephemeris.Sample(right));
            if (Wrap(rightLongitude - now) >= target || SignedLongitude(rightLongitude - candidate.PeakLongitude) is >= 0 and < 2)
            {
                double seconds = (double)right.DifferenceNanoseconds(left) / JulianInstant.NanosecondsPerSecond;
                while (seconds > 1)
                {
                    var middle = left.AddSeconds(seconds / 2);
                    if (SignedLongitude(SolarLongitude(ephemeris.Sample(middle)) - candidate.PeakLongitude) >= 0)
                        right = middle;
                    else
                        left = middle;
                    seconds = (double)right.DifferenceNanoseconds(left) / JulianInstant.NanosecondsPerSecond;
                }
                return new(candidate, left.AddSeconds(seconds / 2));
            }
            if (offset >= span)
                break;
            left = right;
            offset = Math.Min(span, offset + JulianInstant.SecondsPerDay);
        }
        return null;
    }

    public static double SolarLongitude(SolarSystemSample sample)
    {
        const double Obliquity = 23.439291111 * Radians;
        Vector3d sun = (sample.Sun - sample.Earth).PositionKilometers;
        double y = Math.Cos(Obliquity) * sun.Y + Math.Sin(Obliquity) * sun.Z;
        return Wrap(Math.Atan2(y, sun.X) / Radians);
    }

    public static double ObservedRate(double zhr, double altitudeDegrees, double populationIndex, double limitingMagnitude) =>
        zhr * Math.Sin(Math.Max(altitudeDegrees, 0) * Radians) / Math.Pow(populationIndex, 6.5 - limitingMagnitude);

    internal static double Wrap(double longitude) => (longitude % 360 + 360) % 360;
    internal static double SignedLongitude(double longitude) => Wrap(longitude + 180) - 180;
}
