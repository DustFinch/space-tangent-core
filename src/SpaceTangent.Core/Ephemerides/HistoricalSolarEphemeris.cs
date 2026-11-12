// SPDX-License-Identifier: GPL-2.0-or-later
using SpaceTangent.Contracts;
using SpaceTangent.Core.Orbits;

namespace SpaceTangent.Core.Ephemerides;

public enum SolarEphemerisSource { De440, Transition, Analytic }

/// <summary>DE440 with the historical analytic theories on either side of its coverage.</summary>
public sealed class HistoricalSolarEphemeris : ISolarSystemEphemeris
{
    private const double TransitionSeconds = 32 * JulianInstant.SecondsPerDay;
    private const double EarthMoonMassRatio = 81.300568221497215;
    private const double J2000Obliquity = 23.4392911 * Math.PI / 180;
    private const double MilliarcsecondsToRadians = Math.PI / (180 * 3600 * 1000);
    private static readonly Vector3d BiasAxis = new Vector3d(6.8192, -16.617, -14.6) * (MilliarcsecondsToRadians * .5);
    private static readonly ConicOrbit Pluto = PlutoOrbit();

    private readonly De440Ephemeris _precise;
    private readonly PlanetarySeries _series;
    private readonly Lazy<(SolarSystemSample Precise, SolarSystemSample Analytic)> _lower, _upper;

    private HistoricalSolarEphemeris(De440Ephemeris precise, PlanetarySeries series)
    {
        _precise = precise;
        _series = series;
        _lower = new(() => (precise.Sample(precise.Start), Approximate(precise.Start)));
        _upper = new(() => (precise.Sample(precise.End), Approximate(precise.End)));
    }

    public JulianInstant Start { get; } = new(1721425, JulianInstant.NanosecondsPerDay / 2, TimeScale.Tdb);
    public JulianInstant End { get; } = new(3182394, JulianInstant.NanosecondsPerDay / 2, TimeScale.Tdb);

    public static HistoricalSolarEphemeris Load(De440Ephemeris precise, string seriesPath) =>
        new(precise ?? throw new ArgumentNullException(nameof(precise)), PlanetarySeries.Load(seriesPath));

    public SolarEphemerisSource SourceAt(JulianInstant epoch)
    {
        if (epoch.Scale != TimeScale.Tdb || epoch.CompareTo(Start) < 0 || epoch.CompareTo(End) > 0)
            throw new ArgumentOutOfRangeException(nameof(epoch), "The historical ephemeris requires a supported TDB epoch.");
        if (epoch.CompareTo(_precise.Start) >= 0 && epoch.CompareTo(_precise.End) <= 0)
            return SolarEphemerisSource.De440;
        return Math.Abs(OffsetFromBoundary(epoch)) < TransitionSeconds ? SolarEphemerisSource.Transition : SolarEphemerisSource.Analytic;
    }

    public SolarSystemSample Sample(JulianInstant tdb)
    {
        SolarEphemerisSource source = SourceAt(tdb);
        if (source == SolarEphemerisSource.De440)
            return _precise.Sample(tdb);
        SolarSystemSample sample = Approximate(tdb);
        if (source == SolarEphemerisSource.Analytic)
            return sample;

        double signed = OffsetFromBoundary(tdb);
        var (exact, approximate) = signed < 0 ? _lower.Value : _upper.Value;
        double direction = Math.Sign(signed);
        double u = Math.Abs(signed) / TransitionSeconds, left = 1 - u;
        double positionWeight = (1 + 2 * u) * left * left;
        double velocityWeight = direction * TransitionSeconds * u * left * left;
        double positionRate = direction * (-6 * u * left) / TransitionSeconds;
        double velocityRate = left * (1 - 3 * u);
        return new(tdb, Match(sample.Sun, exact.Sun, approximate.Sun),
            Match(sample.Earth, exact.Earth, approximate.Earth),
            Match(sample.Moon, exact.Moon, approximate.Moon),
            Match(sample.SaturnBarycenter, exact.SaturnBarycenter, approximate.SaturnBarycenter))
        {
            Mercury = Match(sample.Mercury, exact.Mercury, approximate.Mercury),
            Venus = Match(sample.Venus, exact.Venus, approximate.Venus),
            EarthMoonBarycenter = Match(sample.EarthMoonBarycenter, exact.EarthMoonBarycenter, approximate.EarthMoonBarycenter),
            MarsBarycenter = Match(sample.MarsBarycenter, exact.MarsBarycenter, approximate.MarsBarycenter),
            JupiterBarycenter = Match(sample.JupiterBarycenter, exact.JupiterBarycenter, approximate.JupiterBarycenter),
            UranusBarycenter = Match(sample.UranusBarycenter, exact.UranusBarycenter, approximate.UranusBarycenter),
            NeptuneBarycenter = Match(sample.NeptuneBarycenter, exact.NeptuneBarycenter, approximate.NeptuneBarycenter),
            PlutoBarycenter = Match(sample.PlutoBarycenter, exact.PlutoBarycenter, approximate.PlutoBarycenter)
        };

        // Cubic Hermite correction preserves the precise position and velocity at the coverage boundary.
        CartesianState Match(CartesianState state, CartesianState modern, CartesianState theory)
        {
            CartesianState difference = modern - theory;
            return new(state.PositionKilometers + difference.PositionKilometers * positionWeight +
                difference.VelocityKilometersPerSecond * velocityWeight,
                state.VelocityKilometersPerSecond + difference.PositionKilometers * positionRate +
                difference.VelocityKilometersPerSecond * velocityRate);
        }
    }

    private double OffsetFromBoundary(JulianInstant epoch)
    {
        JulianInstant boundary = epoch.CompareTo(_precise.Start) < 0 ? _precise.Start : _precise.End;
        return (double)epoch.DifferenceNanoseconds(boundary) / JulianInstant.NanosecondsPerSecond;
    }

    private SolarSystemSample Approximate(JulianInstant epoch)
    {
        CartesianState sun = EclipticToIcrf(_series.Evaluate(0, epoch));
        CartesianState earth = Planet(3);
        CartesianState moonRelative = HistoricalLunarOrbit.Evaluate(epoch);
        moonRelative = new(J2000ToIcrf(moonRelative.PositionKilometers), J2000ToIcrf(moonRelative.VelocityKilometersPerSecond));
        return new(epoch, sun, earth, earth + moonRelative, Planet(6))
        {
            Mercury = Planet(1), Venus = Planet(2), EarthMoonBarycenter = earth + moonRelative * (1 / (1 + EarthMoonMassRatio)),
            MarsBarycenter = Planet(4), JupiterBarycenter = Planet(5), UranusBarycenter = Planet(7), NeptuneBarycenter = Planet(8),
            PlutoBarycenter = sun + EclipticToIcrf(Pluto.Evaluate(epoch))
        };
        CartesianState Planet(int body) => sun + EclipticToIcrf(_series.Evaluate(body, epoch));
    }

    private static CartesianState EclipticToIcrf(CartesianState state) =>
        new(Rotate(state.PositionKilometers), Rotate(state.VelocityKilometersPerSecond));

    internal static Vector3d Rotate(Vector3d ecliptic)
    {
        var (sin, cos) = Math.SinCos(J2000Obliquity);
        return J2000ToIcrf(new(ecliptic.X, cos * ecliptic.Y - sin * ecliptic.Z, sin * ecliptic.Y + cos * ecliptic.Z));
    }

    internal static Vector3d J2000ToIcrf(Vector3d vector)
    {
        Vector3d doubled = Vector3d.Cross(BiasAxis, vector) * 2;
        return vector + doubled + Vector3d.Cross(BiasAxis, doubled);
    }

    private static ConicOrbit PlutoOrbit()
    {
        // The historical Pluto-Charon reference ellipse is the catalogue orbit at the New Horizons epoch.
        double period = 248.0553249511832 * 365.25 * JulianInstant.SecondsPerDay;
        double axis = 39.47846297299562 * PlanetarySeries.KilometersPerAu;
        const double eccentricity = .2501751659981197;
        var periapsis = new JulianInstant(2457218, 0, TimeScale.Tdb).AddSeconds(-period * 37.59869705192153 / 360);
        return new(new(axis * (1 - eccentricity), eccentricity, Radians(17.16199308367073), Radians(110.287823984409),
            Radians(113.3692019799804), periapsis, Math.Pow(Math.Tau / period, 2) * axis * axis * axis, new("j2000-ecliptic")));
        static double Radians(double degrees) => degrees * Math.PI / 180;
    }
}
