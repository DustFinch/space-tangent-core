// SPDX-License-Identifier: GPL-2.0-or-later
// Near-Earth equations from Vallado, Crawford, Hujsak and Kelso, AIAA 2006-6753.
// https://celestrak.org/publications/AIAA/2006-6753/
using SpaceTangent.Contracts;
using SpaceTangent.Core.Time;

namespace SpaceTangent.Core.Orbits;

/// <summary>Near-Earth SGP4 and deep-space SDP4. States are Earth-centred TEME in km and km/s.</summary>
public sealed class Sgp4Orbit
{
    private const int MaximumMinutesFromEpoch = 365 * 1440;
    private const double Mu = 398600.8;
    private const double EarthRadius = 6378.135;
    private const double J2 = .001082616, J3 = -.00000253881, J4 = -.00000165597;
    private const double J3OverJ2 = J3 / J2, TwoThirds = 2d / 3;
    private static readonly double Xke = 60 / Math.Sqrt(EarthRadius * EarthRadius * EarthRadius / Mu);

    private readonly TwoLineElements _elements;
    private readonly Sdp4Orbit? _deep;
    private readonly bool _simple;
    private readonly double _meanMotion, _sinInclination, _cosInclination, _con41, _x1mth2, _x7thm1;
    private readonly double _eta, _cc1, _cc4, _cc5, _meanRate, _perigeeRate, _nodeRate;
    private readonly double _perigeeDrag, _meanDrag, _nodeDrag, _aycof, _xlcof, _delmo;
    private readonly double _d2, _d3, _d4, _t2cof, _t3cof, _t4cof, _t5cof;

    public Sgp4Orbit(TwoLineElements elements)
    {
        ArgumentNullException.ThrowIfNull(elements);
        _elements = elements;
        Epoch = TimeConversion.FromUtc(elements.UtcEpoch, TimeScale.Tdb);

        double e = elements.Eccentricity;
        double oneMinusE2 = 1 - e * e;
        double rootOneMinusE2 = Math.Sqrt(oneMinusE2);
        (_sinInclination, _cosInclination) = Math.SinCos(elements.Inclination);
        double cos2 = _cosInclination * _cosInclination;
        double originalA = Math.Pow(Xke / elements.MeanMotion, TwoThirds);
        double d1 = .75 * J2 * (3 * cos2 - 1) / (rootOneMinusE2 * oneMinusE2);
        double delta = d1 / (originalA * originalA);
        double correctedA = originalA * (1 - delta * delta - delta * (1d / 3 + 134 * delta * delta / 81));
        _meanMotion = elements.MeanMotion / (1 + d1 / (correctedA * correctedA));
        if (!double.IsFinite(_meanMotion) || _meanMotion <= 0)
            throw new ArgumentException("TLE does not produce a valid SGP4 orbit.", nameof(elements));
        double a = Math.Pow(Xke / _meanMotion, TwoThirds);
        double p = a * oneMinusE2;
        double perigeeKm = (a * (1 - e) - 1) * EarthRadius;
        if (perigeeKm < 0)
            throw new ArgumentException("TLE perigee is below the Earth reference surface.", nameof(elements));
        if (Math.Tau / _meanMotion >= 225)
        {
            _deep = new(elements);
            return;
        }
        _simple = perigeeKm < 220;
        double sKm = perigeeKm < 156 ? Math.Max(20, perigeeKm - 78) : 78;
        double s = 1 + sKm / EarthRadius;
        double q = Math.Pow((120 - sKm) / EarthRadius, 4);

        double inverseP2 = 1 / (p * p);
        double tsi = 1 / (a - s);
        _eta = a * e * tsi;
        double eta2 = _eta * _eta, eEta = e * _eta, psi2 = Math.Abs(1 - eta2);
        double coefficient = q * Math.Pow(tsi, 4), coefficient1 = coefficient / Math.Pow(psi2, 3.5);
        double con42 = 1 - 5 * cos2;
        _con41 = 3 * cos2 - 1;
        _x1mth2 = 1 - cos2;
        _x7thm1 = 7 * cos2 - 1;
        double cc2 = coefficient1 * _meanMotion *
            (a * (1 + 1.5 * eta2 + eEta * (4 + eta2)) +
             .375 * J2 * tsi / psi2 * _con41 * (8 + 3 * eta2 * (8 + eta2)));
        _cc1 = elements.BStar * cc2;
        double cc3 = e > 1e-4 ? -2 * coefficient * tsi * J3OverJ2 * _meanMotion * _sinInclination / e : 0;
        _cc4 = 2 * _meanMotion * coefficient1 * a * oneMinusE2 *
            (_eta * (2 + .5 * eta2) + e * (.5 + 2 * eta2) - J2 * tsi / (a * psi2) *
             (-3 * _con41 * (1 - 2 * eEta + eta2 * (1.5 - .5 * eEta)) +
              .75 * _x1mth2 * (2 * eta2 - eEta * (1 + eta2)) * Math.Cos(2 * elements.ArgumentOfPerigee)));
        _cc5 = 2 * coefficient1 * a * oneMinusE2 * (1 + 2.75 * (eta2 + eEta) + eEta * eta2);

        double cos4 = cos2 * cos2;
        double j2Rate = 1.5 * J2 * inverseP2 * _meanMotion;
        double j2SquaredRate = .5 * j2Rate * J2 * inverseP2;
        double j4Rate = -.46875 * J4 * inverseP2 * inverseP2 * _meanMotion;
        _meanRate = _meanMotion + .5 * j2Rate * rootOneMinusE2 * _con41 +
            .0625 * j2SquaredRate * rootOneMinusE2 * (13 - 78 * cos2 + 137 * cos4);
        _perigeeRate = -.5 * j2Rate * con42 + .0625 * j2SquaredRate * (7 - 114 * cos2 + 395 * cos4) +
            j4Rate * (3 - 36 * cos2 + 49 * cos4);
        double firstNodeRate = -j2Rate * _cosInclination;
        _nodeRate = firstNodeRate + (.5 * j2SquaredRate * (4 - 19 * cos2) + 2 * j4Rate * (3 - 7 * cos2)) * _cosInclination;
        _perigeeDrag = elements.BStar * cc3 * Math.Cos(elements.ArgumentOfPerigee);
        _meanDrag = e > 1e-4 ? -TwoThirds * coefficient * elements.BStar / eEta : 0;
        _nodeDrag = 3.5 * oneMinusE2 * firstNodeRate * _cc1;
        _t2cof = 1.5 * _cc1;
        _xlcof = -.25 * J3OverJ2 * _sinInclination * (3 + 5 * _cosInclination) / Math.Max(1 + _cosInclination, 1.5e-12);
        _aycof = -.5 * J3OverJ2 * _sinInclination;
        _delmo = Math.Pow(1 + _eta * Math.Cos(elements.MeanAnomaly), 3);

        if (!_simple)
        {
            double cc1Squared = _cc1 * _cc1;
            _d2 = 4 * a * tsi * cc1Squared;
            double intermediate = _d2 * tsi * _cc1 / 3;
            _d3 = (17 * a + s) * intermediate;
            _d4 = .5 * intermediate * a * tsi * (221 * a + 31 * s) * _cc1;
            _t3cof = _d2 + 2 * cc1Squared;
            _t4cof = .25 * (3 * _d3 + _cc1 * (12 * _d2 + 10 * cc1Squared));
            _t5cof = .2 * (3 * _d4 + 12 * _cc1 * _d3 + 6 * _d2 * _d2 + 15 * cc1Squared * (2 * _d2 + cc1Squared));
        }
    }

    public JulianInstant Epoch { get; }
    public bool DeepSpace => _deep is not null;
    public static FrameId Frame { get; } = new("teme-of-date");

    private double MinutesFromEpoch(JulianInstant tdb) =>
        (double)tdb.DifferenceNanoseconds(Epoch) / (60 * JulianInstant.NanosecondsPerSecond);

    /// <summary>Creates an independent stream that retains the deep-space model's query history.</summary>
    public Evaluation CreateEvaluation() => new(this);

    /// <summary>A sequential orbit evaluation stream. Use a separate instance for each observer or parallel caller.</summary>
    public sealed class Evaluation
    {
        private readonly Sgp4Orbit _orbit;
        private readonly Sdp4Orbit.Evaluation? _deep;

        internal Evaluation(Sgp4Orbit orbit)
        {
            _orbit = orbit;
            _deep = orbit._deep?.CreateEvaluation();
        }

        public bool TrySample(JulianInstant tdb, out CartesianState state)
        {
            if (_deep is null)
                return _orbit.TrySample(tdb, out state);

            double minutes = _orbit.MinutesFromEpoch(tdb);
            state = default;
            return Math.Abs(minutes) <= MaximumMinutesFromEpoch && _deep.TrySample(minutes, out state);
        }

        /// <summary>Restores the stream to the orbit's initial state.</summary>
        public void Reset() => _deep?.Reset();
    }

    /// <summary>Samples without retaining previous queries.</summary>
    public bool TrySample(JulianInstant tdb, out CartesianState state)
    {
        double minutes = MinutesFromEpoch(tdb);
        state = default;
        if (Math.Abs(minutes) > MaximumMinutesFromEpoch)
            return false;

        if (_deep is not null)
            return _deep.TrySample(minutes, out state);

        double t2 = minutes * minutes;
        double mean = _elements.MeanAnomaly + _meanRate * minutes;
        double perigee = _elements.ArgumentOfPerigee + _perigeeRate * minutes;
        double node = _elements.AscendingNode + _nodeRate * minutes + _nodeDrag * t2;
        double aFactor = 1 - _cc1 * minutes;
        double eccentricityChange = _elements.BStar * _cc4 * minutes;
        double meanChange = _t2cof * t2;
        if (!_simple)
        {
            double drag = _perigeeDrag * minutes + _meanDrag * (Math.Pow(1 + _eta * Math.Cos(mean), 3) - _delmo);
            mean += drag;
            perigee -= drag;
            double t3 = t2 * minutes, t4 = t3 * minutes;
            aFactor -= _d2 * t2 + _d3 * t3 + _d4 * t4;
            eccentricityChange += _elements.BStar * _cc5 * (Math.Sin(mean) - Math.Sin(_elements.MeanAnomaly));
            meanChange += _t3cof * t3 + t4 * (_t4cof + minutes * _t5cof);
        }

        double a = Math.Pow(Xke / _meanMotion, TwoThirds) * aFactor * aFactor;
        double meanMotion = Xke / Math.Pow(a, 1.5);
        double e = _elements.Eccentricity - eccentricityChange;
        if (!(a > 0 && e >= -.001 && e < 1))
            return false;
        e = Math.Max(e, 1e-6);
        mean += _meanMotion * meanChange;
        double longitude = (mean + perigee + node) % Math.Tau;
        node %= Math.Tau;
        perigee %= Math.Tau;

        double inverseP = 1 / (a * (1 - e * e));
        double ax = e * Math.Cos(perigee);
        double ay = e * Math.Sin(perigee) + inverseP * _aycof;
        double u = (longitude - node + inverseP * _xlcof * ax) % Math.Tau;
        double anomaly = u;
        for (int iteration = 0; iteration < 10; iteration++)
        {
            var (sin, cos) = Math.SinCos(anomaly);
            double step = Math.Clamp((u - ay * cos + ax * sin - anomaly) / (1 - ax * cos - ay * sin), -.95, .95);
            anomaly += step;
            if (Math.Abs(step) < 1e-12)
                break;
        }

        var (sinAnomaly, cosAnomaly) = Math.SinCos(anomaly);
        double eCos = ax * cosAnomaly + ay * sinAnomaly;
        double eSin = ax * sinAnomaly - ay * cosAnomaly;
        double e2 = ax * ax + ay * ay;
        double p = a * (1 - e2);
        if (!(p > 0))
            return false;

        double r = a * (1 - eCos);
        double radialSpeed = Math.Sqrt(a) * eSin / r;
        double transverseSpeed = Math.Sqrt(p) / r;
        double beta = Math.Sqrt(1 - e2);
        double correction = eSin / (1 + beta);
        double sinU = a / r * (sinAnomaly - ay - ax * correction);
        double cosU = a / r * (cosAnomaly - ax + ay * correction);
        double argument = Math.Atan2(sinU, cosU);
        double sin2U = 2 * cosU * sinU, cos2U = 1 - 2 * sinU * sinU;
        double j2Factor = .5 * J2 / p, j2OverP2 = j2Factor / p;

        r = r * (1 - 1.5 * j2OverP2 * beta * _con41) + .5 * j2Factor * _x1mth2 * cos2U;
        if (!(r >= 1))
            return false;
        argument -= .25 * j2OverP2 * _x7thm1 * sin2U;
        node += 1.5 * j2OverP2 * _cosInclination * sin2U;
        double inclination = _elements.Inclination + 1.5 * j2OverP2 * _cosInclination * _sinInclination * cos2U;
        radialSpeed -= meanMotion * j2Factor * _x1mth2 * sin2U / Xke;
        transverseSpeed += meanMotion * j2Factor * (_x1mth2 * cos2U + 1.5 * _con41) / Xke;

        var (sinArgument, cosArgument) = Math.SinCos(argument);
        var (sinNode, cosNode) = Math.SinCos(node);
        var (sinInclination, cosInclination) = Math.SinCos(inclination);
        double mx = -sinNode * cosInclination, my = cosNode * cosInclination;
        var radial = new Vector3d(mx * sinArgument + cosNode * cosArgument,
            my * sinArgument + sinNode * cosArgument, sinInclination * sinArgument);
        var transverse = new Vector3d(mx * cosArgument - cosNode * sinArgument,
            my * cosArgument - sinNode * sinArgument, sinInclination * cosArgument);
        var position = radial * (r * EarthRadius);
        var velocity = (radial * radialSpeed + transverse * transverseSpeed) * (EarthRadius * Xke / 60);
        if (!position.IsFinite || !velocity.IsFinite)
            return false;
        state = new(position, velocity);
        return true;
    }
}
