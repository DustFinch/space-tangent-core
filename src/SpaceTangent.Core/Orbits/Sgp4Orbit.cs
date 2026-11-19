// SPDX-License-Identifier: GPL-2.0-or-later
// SGP4/SDP4 after D. A. Vallado, P. Crawford, R. Hujsak and T. S. Kelso, "Revisiting Spacetrack
// Report #3", AIAA 2006-6753 (https://celestrak.org/publications/AIAA/2006-6753/), in the AFSPC
// operations mode that NAIF CSPICE EVSGP4 also implements.
using SpaceTangent.Contracts;
using SpaceTangent.Core.Time;

namespace SpaceTangent.Core.Orbits;

/// <summary>Propagation outcome, numbered as in the published SGP4 error codes.</summary>
public enum Sgp4Status
{
    Ok = 0,
    MeanEccentricityOutOfRange = 1,
    MeanMotionNotPositive = 2,
    PerturbedEccentricityOutOfRange = 3,
    SemilatusRectumNegative = 4,
    Decayed = 6
}

/// <summary>
/// Deterministic SGP4 with the deep-space extension. Every query is independent of earlier ones:
/// the resonance integrator uses a fixed epoch grid with immutable, thread-safe checkpoints.
/// </summary>
public sealed class Sgp4Orbit
{
    private const int MaximumMinutesFromEpoch = 365 * 1440;
    /// <summary>Computational limit; includes the longest published verification case, not an accuracy guarantee.</summary>
    public const double MaximumPropagationMinutes = 2_000_000;
    private const double Mu = 398600.8;
    private const double EarthRadius = 6378.135;
    private const double J2 = .001082616, J3 = -.00000253881, J4 = -.00000165597;
    private const double J3OverJ2 = J3 / J2, TwoThirds = 2d / 3, TwoPi = 2 * Math.PI, Temp4 = 1.5e-12;
    private static readonly double Xke = 60 / Math.Sqrt(EarthRadius * EarthRadius * EarthRadius / Mu);

    private readonly bool _simple;
    private readonly double _ecco, _inclo, _nodeo, _argpo, _mo, _bstar, _noUnkozai;
    private readonly double _con41, _x1mth2, _x7thm1, _cc1, _cc4, _cc5, _d2, _d3, _d4, _delmo, _eta;
    private readonly double _argpdot, _omgcof, _sinmao, _t2cof, _t3cof, _t4cof, _t5cof;
    private readonly double _mdot, _nodedot, _xlcof, _xmcof, _nodecf, _aycof;
    private readonly DeepSpaceTerms? _deep;

    public Sgp4Orbit(TwoLineElements elements)
    {
        ArgumentNullException.ThrowIfNull(elements);
        Epoch = TimeConversion.FromUtc(elements.UtcEpoch, TimeScale.Tdb);
        _ecco = elements.Eccentricity;
        _inclo = elements.Inclination;
        _nodeo = elements.AscendingNode;
        _argpo = elements.ArgumentOfPerigee;
        _mo = elements.MeanAnomaly;
        _bstar = elements.BStar;
        // Days from 1949 December 31 0h UTC. The published code first forms the full Julian date in binary64,
        // and the lunar-solar terms of highly eccentric orbits are sensitive to that rounding.
        double julianDate = elements.UtcEpoch.Date.DayNumber + 1_721_425.5 +
            elements.UtcEpoch.NanosecondsSinceMidnight / (double)JulianInstant.NanosecondsPerDay;
        double epoch = julianDate - 2_433_281.5;

        // initl: recover the original mean motion and semimajor axis.
        double eccsq = _ecco * _ecco, omeosq = 1 - eccsq, rteosq = Math.Sqrt(omeosq);
        double cosio = Math.Cos(_inclo), cosio2 = cosio * cosio;
        double ak = Math.Pow(Xke / elements.MeanMotion, TwoThirds);
        double d1 = .75 * J2 * (3 * cosio2 - 1) / (rteosq * omeosq);
        double del = d1 / (ak * ak);
        double adel = ak * (1 - del * del - del * (1d / 3 + 134 * del * del / 81));
        del = d1 / (adel * adel);
        _noUnkozai = elements.MeanMotion / (1 + del);
        if (!double.IsFinite(_noUnkozai) || _noUnkozai <= 0 || !(omeosq > 0))
            throw new ArgumentException("TLE does not produce a valid SGP4 orbit.", nameof(elements));
        double ao = Math.Pow(Xke / _noUnkozai, TwoThirds);
        double sinio = Math.Sin(_inclo), po = ao * omeosq, con42 = 1 - 5 * cosio2;
        _con41 = -con42 - cosio2 - cosio2;
        double posq = po * po, rp = ao * (1 - _ecco);

        // sgp4init: secular and drag coefficients.
        double ss = 78 / EarthRadius + 1;
        double qzms2t = Math.Pow((120 - 78) / EarthRadius, 4);
        _simple = rp < 220 / EarthRadius + 1;
        double sfour = ss, qzms24 = qzms2t, perigee = (rp - 1) * EarthRadius;
        if (perigee < 156)
        {
            sfour = perigee < 98 ? 20 : perigee - 78;
            qzms24 = Math.Pow((120 - sfour) / EarthRadius, 4);
            sfour = sfour / EarthRadius + 1;
        }
        double pinvsq = 1 / posq, tsi = 1 / (ao - sfour);
        _eta = ao * _ecco * tsi;
        double etasq = _eta * _eta, eeta = _ecco * _eta, psisq = Math.Abs(1 - etasq);
        double coef = qzms24 * Math.Pow(tsi, 4), coef1 = coef / Math.Pow(psisq, 3.5);
        double cc2 = coef1 * _noUnkozai * (ao * (1 + 1.5 * etasq + eeta * (4 + etasq)) +
            .375 * J2 * tsi / psisq * _con41 * (8 + 3 * etasq * (8 + etasq)));
        _cc1 = _bstar * cc2;
        double cc3 = _ecco > 1e-4 ? -2 * coef * tsi * J3OverJ2 * _noUnkozai * sinio / _ecco : 0;
        _x1mth2 = 1 - cosio2;
        _cc4 = 2 * _noUnkozai * coef1 * ao * omeosq * (_eta * (2 + .5 * etasq) + _ecco * (.5 + 2 * etasq) -
            J2 * tsi / (ao * psisq) * (-3 * _con41 * (1 - 2 * eeta + etasq * (1.5 - .5 * eeta)) +
            .75 * _x1mth2 * (2 * etasq - eeta * (1 + etasq)) * Math.Cos(2 * _argpo)));
        _cc5 = 2 * coef1 * ao * omeosq * (1 + 2.75 * (etasq + eeta) + eeta * etasq);
        double cosio4 = cosio2 * cosio2;
        double temp1 = 1.5 * J2 * pinvsq * _noUnkozai;
        double temp2 = .5 * temp1 * J2 * pinvsq;
        double temp3 = -.46875 * J4 * pinvsq * pinvsq * _noUnkozai;
        _mdot = _noUnkozai + .5 * temp1 * rteosq * _con41 + .0625 * temp2 * rteosq * (13 - 78 * cosio2 + 137 * cosio4);
        _argpdot = -.5 * temp1 * con42 + .0625 * temp2 * (7 - 114 * cosio2 + 395 * cosio4) +
            temp3 * (3 - 36 * cosio2 + 49 * cosio4);
        double xhdot1 = -temp1 * cosio;
        _nodedot = xhdot1 + (.5 * temp2 * (4 - 19 * cosio2) + 2 * temp3 * (3 - 7 * cosio2)) * cosio;
        double xpidot = _argpdot + _nodedot;
        _omgcof = _bstar * cc3 * Math.Cos(_argpo);
        _xmcof = _ecco > 1e-4 ? -TwoThirds * coef * _bstar / eeta : 0;
        _nodecf = 3.5 * omeosq * xhdot1 * _cc1;
        _t2cof = 1.5 * _cc1;
        _xlcof = -.25 * J3OverJ2 * sinio * (3 + 5 * cosio) / (Math.Abs(cosio + 1) > Temp4 ? 1 + cosio : Temp4);
        _aycof = -.5 * J3OverJ2 * sinio;
        _delmo = Math.Pow(1 + _eta * Math.Cos(_mo), 3);
        _sinmao = Math.Sin(_mo);
        _x7thm1 = 7 * cosio2 - 1;

        if (TwoPi / _noUnkozai >= 225)
        {
            _simple = true;
            _deep = new DeepSpaceTerms(epoch, AfspcSiderealAngle(epoch), Xke, _ecco, _inclo, _nodeo, _argpo,
                _mo, _noUnkozai, _mdot, _nodedot, _argpdot, xpidot);
        }

        if (!_simple)
        {
            double cc1sq = _cc1 * _cc1;
            _d2 = 4 * ao * tsi * cc1sq;
            double temp = _d2 * tsi * _cc1 / 3;
            _d3 = (17 * ao + sfour) * temp;
            _d4 = .5 * temp * ao * tsi * (221 * ao + 31 * sfour) * _cc1;
            _t3cof = _d2 + 2 * cc1sq;
            _t4cof = .25 * (3 * _d3 + _cc1 * (12 * _d2 + 10 * cc1sq));
            _t5cof = .2 * (3 * _d4 + 12 * _cc1 * _d3 + 6 * _d2 * _d2 + 15 * cc1sq * (2 * _d2 + cc1sq));
        }

        Sgp4Status status = Propagate(0, out _);
        if (status != Sgp4Status.Ok)
            throw new ArgumentException($"TLE does not produce a valid SGP4 state at its epoch ({status}).", nameof(elements));
    }

    public JulianInstant Epoch { get; }
    public bool DeepSpace => _deep is not null;
    public static FrameId Frame { get; } = new("teme-of-date");

    /// <summary>Samples within one year of the element epoch.</summary>
    public bool TrySample(JulianInstant tdb, out CartesianState state)
    {
        double minutes = (double)tdb.DifferenceNanoseconds(Epoch) / (60 * JulianInstant.NanosecondsPerSecond);
        state = default;
        return Math.Abs(minutes) <= MaximumMinutesFromEpoch && Propagate(minutes, out state) == Sgp4Status.Ok;
    }

    /// <summary>
    /// Evaluates the TEME state within <see cref="MaximumPropagationMinutes"/> of the element epoch. A decayed state is
    /// still returned with <see cref="Sgp4Status.Decayed"/>; other errors return a default state.
    /// </summary>
    public Sgp4Status Propagate(double minutesSinceEpoch, out CartesianState state)
    {
        state = default;
        double t = minutesSinceEpoch;
        if (!double.IsFinite(t) || Math.Abs(t) > MaximumPropagationMinutes)
            throw new ArgumentOutOfRangeException(nameof(minutesSinceEpoch));

        double xmdf = _mo + _mdot * t;
        double argpdf = _argpo + _argpdot * t;
        double nodedf = _nodeo + _nodedot * t;
        double argpm = argpdf, mm = xmdf, t2 = t * t;
        double nodem = nodedf + _nodecf * t2;
        double tempa = 1 - _cc1 * t;
        double tempe = _bstar * _cc4 * t;
        double templ = _t2cof * t2;
        if (!_simple)
        {
            double delomg = _omgcof * t;
            double delmtemp = 1 + _eta * Math.Cos(xmdf);
            double delm = _xmcof * (delmtemp * delmtemp * delmtemp - _delmo);
            double temp = delomg + delm;
            mm = xmdf + temp;
            argpm = argpdf - temp;
            double t3 = t2 * t, t4 = t3 * t;
            tempa = tempa - _d2 * t2 - _d3 * t3 - _d4 * t4;
            tempe += _bstar * _cc5 * (Math.Sin(mm) - _sinmao);
            templ = templ + _t3cof * t3 + t4 * (_t4cof + t * _t5cof);
        }

        double nm = _noUnkozai, em = _ecco, inclm = _inclo;
        _deep?.Secular(t, ref em, ref argpm, ref inclm, ref mm, ref nodem, ref nm);

        if (nm <= 0)
            return Sgp4Status.MeanMotionNotPositive;
        double am = Math.Pow(Xke / nm, TwoThirds) * tempa * tempa;
        nm = Xke / Math.Pow(am, 1.5);
        em -= tempe;
        if (em >= 1 || em < -.001)
            return Sgp4Status.MeanEccentricityOutOfRange;
        if (em < 1e-6)
            em = 1e-6;
        mm += _noUnkozai * templ;
        double xlm = mm + argpm + nodem;
        nodem %= TwoPi;
        argpm %= TwoPi;
        xlm %= TwoPi;
        mm = (xlm - argpm - nodem) % TwoPi;

        double ep = em, xincp = inclm, argpp = argpm, nodep = nodem, mp = mm;
        double sinip = Math.Sin(inclm), cosip = Math.Cos(inclm);
        double aycof = _aycof, xlcof = _xlcof, con41 = _con41, x1mth2 = _x1mth2, x7thm1 = _x7thm1;
        if (_deep is not null)
        {
            _deep.Periodics(t, ref ep, ref xincp, ref nodep, ref argpp, ref mp);
            if (xincp < 0)
            {
                xincp = -xincp;
                nodep += Math.PI;
                argpp -= Math.PI;
            }
            if (ep < 0 || ep > 1)
                return Sgp4Status.PerturbedEccentricityOutOfRange;
            sinip = Math.Sin(xincp);
            cosip = Math.Cos(xincp);
            aycof = -.5 * J3OverJ2 * sinip;
            xlcof = -.25 * J3OverJ2 * sinip * (3 + 5 * cosip) / (Math.Abs(cosip + 1) > Temp4 ? 1 + cosip : Temp4);
        }

        double axnl = ep * Math.Cos(argpp);
        double inverseSemilatus = 1 / (am * (1 - ep * ep));
        double aynl = ep * Math.Sin(argpp) + inverseSemilatus * aycof;
        double xl = mp + argpp + nodep + inverseSemilatus * xlcof * axnl;
        double u = (xl - nodep) % TwoPi;
        double eo1 = u, tem5 = 9999.9, sineo1 = 0, coseo1 = 0;
        for (int iteration = 1; Math.Abs(tem5) >= 1e-12 && iteration <= 10; iteration++)
        {
            sineo1 = Math.Sin(eo1);
            coseo1 = Math.Cos(eo1);
            tem5 = (u - aynl * coseo1 + axnl * sineo1 - eo1) / (1 - coseo1 * axnl - sineo1 * aynl);
            if (Math.Abs(tem5) >= .95)
                tem5 = tem5 > 0 ? .95 : -.95;
            eo1 += tem5;
        }

        double ecose = axnl * coseo1 + aynl * sineo1;
        double esine = axnl * sineo1 - aynl * coseo1;
        double el2 = axnl * axnl + aynl * aynl;
        double pl = am * (1 - el2);
        if (pl < 0)
            return Sgp4Status.SemilatusRectumNegative;

        double rl = am * (1 - ecose);
        double rdotl = Math.Sqrt(am) * esine / rl;
        double rvdotl = Math.Sqrt(pl) / rl;
        double betal = Math.Sqrt(1 - el2);
        double correction = esine / (1 + betal);
        double sinu = am / rl * (sineo1 - aynl - axnl * correction);
        double cosu = am / rl * (coseo1 - axnl + aynl * correction);
        double su = Math.Atan2(sinu, cosu);
        double sin2u = (cosu + cosu) * sinu, cos2u = 1 - 2 * sinu * sinu;
        double temp1 = .5 * J2 / pl, temp2 = temp1 / pl;
        if (_deep is not null)
        {
            double cosisq = cosip * cosip;
            con41 = 3 * cosisq - 1;
            x1mth2 = 1 - cosisq;
            x7thm1 = 7 * cosisq - 1;
        }

        double mrt = rl * (1 - 1.5 * temp2 * betal * con41) + .5 * temp1 * x1mth2 * cos2u;
        su -= .25 * temp2 * x7thm1 * sin2u;
        double xnode = nodep + 1.5 * temp2 * cosip * sin2u;
        double xinc = xincp + 1.5 * temp2 * cosip * sinip * cos2u;
        double mvt = rdotl - nm * temp1 * x1mth2 * sin2u / Xke;
        double rvdot = rvdotl + nm * temp1 * (x1mth2 * cos2u + 1.5 * con41) / Xke;

        double sinsu = Math.Sin(su), cossu = Math.Cos(su);
        double snod = Math.Sin(xnode), cnod = Math.Cos(xnode);
        double sini = Math.Sin(xinc), cosi = Math.Cos(xinc);
        double xmx = -snod * cosi, xmy = cnod * cosi;
        var radial = new Vector3d(xmx * sinsu + cnod * cossu, xmy * sinsu + snod * cossu, sini * sinsu);
        var transverse = new Vector3d(xmx * cossu - cnod * sinsu, xmy * cossu - snod * sinsu, sini * cossu);
        double kilometersPerSecond = EarthRadius * Xke / 60;
        var position = radial * (mrt * EarthRadius);
        var velocity = (radial * mvt + transverse * rvdot) * kilometersPerSecond;
        if (!position.IsFinite || !velocity.IsFinite)
            return Sgp4Status.SemilatusRectumNegative;
        state = new(position, velocity);
        return mrt < 1 ? Sgp4Status.Decayed : Sgp4Status.Ok;
    }

    /// <summary>The AFSPC 1970-based Greenwich sidereal angle used to initialize the resonance terms.</summary>
    private static double AfspcSiderealAngle(double epoch)
    {
        double ts70 = epoch - 7305;
        double ds70 = Math.Floor(ts70 + 1e-8);
        double tfrac = ts70 - ds70;
        const double c1 = 1.72027916940703639e-2, thgr70 = 1.7321343856509374, fk5r = 5.07551419432269442e-15;
        double gsto = (thgr70 + c1 * ds70 + (c1 + TwoPi) * tfrac + ts70 * ts70 * fk5r) % TwoPi;
        return gsto < 0 ? gsto + TwoPi : gsto;
    }
}
