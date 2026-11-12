// SPDX-License-Identifier: GPL-2.0-or-later
using SpaceTangent.Contracts;
using SpaceTangent.Core.Orbits;

namespace SpaceTangent.Core.Ephemerides;

/// <summary>The five fixed-element moon orbits retained from the original solar catalogue.</summary>
public sealed class CatalogueMoonOrbit
{
    private const double Degrees = Math.PI / 180;
    private static readonly FrameId MarsEquator = new("mars-mean-equator"), Ecliptic = new("ecliptic-j2000");
    private static readonly CatalogueMoonOrbit[] Models =
    [
        new(401, true, 2460000.0, .31891023, 9378.938, .01468, 2.097806, 38.701507, 66.811489, 173.124729),
        new(402, true, 2460000.0, 1.2624407, 23459.739, .000283, 2.217941, 286.210554, 125.429446, 331.164065),
        new(609, false, 2453167.5, 548.167596, 12917060.944, .152921, 173.212514, 263.558611, 2.526169, 31.167678),
        new(808, false, 2447763.5, 1.12231477, 117647, .00053, 28.908433, 48.083589, 81.195458, 34.162384),
        new(802, false, 2447763.5, 359.396374, 5506217.008, .753882, 5.055101, 319.892917, 296.451886, 36.05586)
    ];

    private readonly ConicOrbit _orbit;
    private readonly double _phaseSeconds;

    private CatalogueMoonOrbit(int target, bool marsEquator, double epoch, double periodDays, double semimajorAxis,
        double eccentricity, double inclination, double node, double periapsis, double meanAnomaly)
    {
        NaifId = target;
        UsesMarsMeanEquator = marsEquator;
        double period = periodDays * JulianInstant.SecondsPerDay;
        _phaseSeconds = meanAnomaly / 360 * period;
        double speed = Math.Tau * semimajorAxis / period;
        _orbit = new(new(semimajorAxis * (1 - eccentricity), eccentricity, inclination * Degrees, node * Degrees,
            periapsis * Degrees, JulianInstant.FromJulianDate(epoch, TimeScale.Tdb), speed * speed * semimajorAxis,
            marsEquator ? MarsEquator : Ecliptic));
    }

    public int NaifId { get; }
    public bool UsesMarsMeanEquator { get; }
    public static CatalogueMoonOrbit? Find(int naifId) => Models.FirstOrDefault(model => model.NaifId == naifId);

    /// <summary>Samples relative to the parent in the catalogue's Z-up local axes.</summary>
    public CartesianState SampleLocal(JulianInstant tdb)
    {
        if (tdb.Scale != TimeScale.Tdb)
            throw new ArgumentException("A catalogue moon orbit requires TDB.", nameof(tdb));
        double elapsed = (double)tdb.DifferenceNanoseconds(_orbit.Elements.PeriapsisEpoch) / JulianInstant.NanosecondsPerSecond;
        return _orbit.EvaluateElapsedSeconds(elapsed + _phaseSeconds);
    }
}
