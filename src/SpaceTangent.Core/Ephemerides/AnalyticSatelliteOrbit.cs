// SPDX-License-Identifier: GPL-2.0-or-later
using SpaceTangent.Contracts;

namespace SpaceTangent.Core.Ephemerides;

public enum SatelliteTheory
{
    Io, Europa, Ganymede, Callisto,
    Mimas, Enceladus, Tethys, Dione, Rhea, Titan, Hyperion, Iapetus,
    Miranda, Ariel, Umbriel, Titania, Oberon,
    Triton
}

/// <summary>Parent-relative states from E5, TASS 1.7, GUST86 and the analytical Triton theory.</summary>
public static partial class AnalyticSatelliteOrbit
{
    private readonly record struct Epoch(JulianInstant Instant)
    {
        public static double operator -(Epoch instant, double epoch) =>
            (double)instant.Instant.DifferenceNanoseconds(JulianInstant.FromJulianDate(epoch, TimeScale.Tdb)) /
                JulianInstant.NanosecondsPerDay;
    }

    public static CartesianState Sample(SatelliteTheory theory, JulianInstant tdb)
    {
        if (tdb.Scale != TimeScale.Tdb)
            throw new ArgumentException("An analytical satellite orbit requires TDB.", nameof(tdb));
        if (!Enum.IsDefined(theory))
            throw new ArgumentOutOfRangeException(nameof(theory));
        return new(Position(tdb), (Position(tdb.AddSeconds(30)) - Position(tdb.AddSeconds(-30))) / 60);

        Vector3d Position(JulianInstant at)
        {
            Vector3d raw = PositionCore(theory, new(at));
            if (theory is <= SatelliteTheory.Callisto or >= SatelliteTheory.Miranda and <= SatelliteTheory.Oberon)
                raw = MeanEquatorToEcliptic(raw, theory <= SatelliteTheory.Callisto);
            Vector3d zUp = new(raw.X, -raw.Z, raw.Y);
            return theory == SatelliteTheory.Triton ? HistoricalSolarEphemeris.J2000ToIcrf(zUp) : HistoricalSolarEphemeris.Rotate(zUp);
        }
    }

    private static Vector3d MeanEquatorToEcliptic(Vector3d position, bool jupiter)
    {
        // The analytic theory's equator uses the archived catalogue's node and tilt, independent of the current body spin.
        double node = (float)DegToRad(jupiter ? 337.818458 : 167.646661);
        double tilt = (float)DegToRad(jupiter ? 2.216451 : 97.724832);
        return RotateY(RotateX(position, tilt), node);
    }

    private static Vector3d RotateX(Vector3d vector, double radians)
    {
        var (sin, cos) = Math.SinCos(radians);
        return new(vector.X, cos * vector.Y - sin * vector.Z, sin * vector.Y + cos * vector.Z);
    }

    private static Vector3d RotateY(Vector3d vector, double radians)
    {
        var (sin, cos) = Math.SinCos(radians);
        return new(cos * vector.X + sin * vector.Z, vector.Y, -sin * vector.X + cos * vector.Z);
    }

    private const double LPEJ = 0.23509484;          // Longitude of perihelion of Jupiter
    private const double JupiterRadius = 71398.0;
    private static readonly double JupAscendingNode = DegToRad(22.203);
    private const double SatAscendingNode = 169.5291, SatTilt = 28.0512; // tass17.cpp
    private static readonly double Gk1 = Square(0.01720209895 * 365.25) / 3498.790;
    private const double KilometersPerAu = 149597870.7;

    private static Vector3d PositionCore(SatelliteTheory theory, Epoch jdTdb) => theory switch
    {
        SatelliteTheory.Io => IoPosition(jdTdb),
        SatelliteTheory.Europa => EuropaPosition(jdTdb),
        SatelliteTheory.Ganymede => GanymedePosition(jdTdb),
        SatelliteTheory.Callisto => CallistoPosition(jdTdb),
        SatelliteTheory.Mimas => TassPosition(jdTdb, MimasTermIndices, MimasTerms, MimasAPF, MimasExtra),
        SatelliteTheory.Enceladus => TassPosition(jdTdb, EnceladusTermIndices, EnceladusTerms, EnceladusAPF, EnceladusExtra),
        SatelliteTheory.Tethys => TassPosition(jdTdb, TethysTermIndices, TethysTerms, TethysAPF, TethysExtra),
        SatelliteTheory.Dione => TassPosition(jdTdb, DioneTermIndices, DioneTerms, DioneAPF, DioneExtra),
        SatelliteTheory.Rhea => TassPosition(jdTdb, RheaTermIndices, RheaTerms, RheaAPF, RheaExtra),
        SatelliteTheory.Titan => TassPosition(jdTdb, TitanTermIndices, TitanTerms, TitanAPF, TitanExtra),
        SatelliteTheory.Hyperion => HyperionPosition(jdTdb),
        SatelliteTheory.Iapetus => TassPosition(jdTdb, IapetusTermIndices, IapetusTerms, IapetusAPF, IapetusExtra),
        SatelliteTheory.Triton => TritonPosition(jdTdb),
        _ => UranianPosition(theory - SatelliteTheory.Miranda, jdTdb)
    };

    private readonly record struct TassApf(double Ampl, double Phas, double Freq);
    private readonly record struct TassExtra(int Id, double Al0, double An0, double Tam, double Am);

    private static double DegToRad(double degrees) => degrees * (Math.PI / 180);
    private static double RadToDeg(double radians) => radians * (180 / Math.PI);
    private static double Square(double value) => value * value;
    /// <summary>celmath pfmod: positive floating modulus.</summary>
    private static double Pfmod(double x, double y)
    {
        double quotient = Math.Floor(Math.Abs(x / y));
        return x < 0 ? x + (quotient + 1) * y : x - quotient * y;
    }
    private static Vector3d FromPolar(double lon, double lat, double distance)
    {
        var (sLon, cLon) = Math.SinCos(lon);
        var (sLat, cLat) = Math.SinCos(lat);
        return new(cLon * sLat * distance, cLat * distance, -sLon * sLat * distance);
    }

    private static void ComputeGalileanElements(double t,
        out double l1, out double l2, out double l3, out double l4,
        out double p1, out double p2, out double p3, out double p4,
        out double w1, out double w2, out double w3, out double w4,
        out double gamma, out double phi, out double psi, out double G, out double Gp)
    {
        // Parameter t is Julian days, epoch 1976 Aug 10.
        l1 = 1.8513962 + 3.551552269981 * t;
        l2 = 3.0670952 + 1.769322724929 * t;
        l3 = 2.1041485 + 0.87820795239 * t;
        l4 = 1.473836 + 0.37648621522 * t;

        p1 = 1.69451 + 2.8167146e-3 * t;
        p2 = 2.702927 + 8.248962e-4 * t;
        p3 = 3.28443 + 1.24396e-4 * t;
        p4 = 5.851859 + 3.21e-5 * t;

        w1 = 5.451267 - 2.3176901e-3 * t;
        w2 = 1.753028 - 5.695121e-4 * t;
        w3 = 2.080331 - 1.25263e-4 * t;
        w4 = 5.630757 - 3.07063e-5 * t;

        gamma = 5.7653e-3 * Math.Sin(2.85674 + 1.8347e-5 * t) + 6.002e-4 * Math.Sin(0.60189 - 2.82274e-4 * t);
        phi = 3.485014 + 3.033241e-3 * t;
        psi = 5.524285 - 3.63e-8 * t;
        G = 0.527745 + 1.45023893e-3 * t + gamma;
        Gp = 0.5581306 + 5.83982523e-4 * t;
    }

    /// <summary>GUST86, customorbit.cpp UranianSatelliteOrbit (index 0 = Miranda).</summary>
    private static Vector3d UranianPosition(int n, Epoch jd)
    {
        double t = jd - 2444239.5;
        double L = UranL0[n] + UranL1[n] * t;
        for (int i = 0; i < UranLK[n].Length; i++)
            L += UranLK[n][i] * Math.Sin(UranLTheta[n][i] * t + UranLPhi[n][i]);

        double a0 = 0, a1 = 0;
        for (int i = 0; i < UranZK[n].Length; i++)
        {
            var (sw, cw) = Math.SinCos(UranZTheta[n][i] * t + UranZPhi[n][i]);
            a0 += UranZK[n][i] * cw; a1 += UranZK[n][i] * sw;
        }
        double b0 = 0, b1 = 0;
        for (int i = 0; i < UranZetaK[n].Length; i++)
        {
            var (sw, cw) = Math.SinCos(UranZetaTheta[n][i] * t + UranZetaPhi[n][i]);
            b0 += UranZetaK[n][i] * cw; b1 += UranZetaK[n][i] * sw;
        }

        double e = Math.Sqrt(Square(a0) + Square(a1));
        double p = Math.Atan2(a1, a0);
        double gamma = 2.0 * Math.Asin(Math.Sqrt(Square(b0) + Square(b1)));
        double theta = Math.Atan2(b1, b0);

        L += DegToRad(174.99);
        double M = L - p;
        // Iterate a few times to compute the eccentric anomaly from the mean anomaly.
        double ecc = M;
        for (int i = 0; i < 4; ++i) ecc = M + e * Math.Sin(ecc);

        double x = UranA[n] * (Math.Cos(ecc) - e);
        double z = UranA[n] * Math.Sqrt(1 - Square(e)) * -Math.Sin(ecc);
        return RotateY(RotateX(RotateY(new Vector3d(x, 0, z), p - theta), gamma), theta);
    }

    /// <summary>tass17.cpp CalcLongitude: the long-period longitude terms of every satellite.</summary>
    private static void CalcLongitude(double t, Span<double> dlo)
    {
        dlo.Clear();
        static double Sum(double t, TassApf[] apf, int first)
        {
            double sum = 0;
            for (int i = first; i < apf.Length; i++) sum += apf[i].Ampl * Math.Sin(apf[i].Phas + t * apf[i].Freq);
            return sum;
        }
        dlo[0] = Sum(t, MimasAPF, 84); dlo[1] = Sum(t, EnceladusAPF, 30); dlo[2] = Sum(t, TethysAPF, 73); dlo[3] = Sum(t, DioneAPF, 56);
        dlo[4] = Sum(t, RheaAPF, 57); dlo[5] = Sum(t, TitanAPF, 82); dlo[7] = Sum(t, IapetusAPF, 583);
    }

    private static Vector3d EllipticToRectangular(ReadOnlySpan<double> elem, double tam, double am)
    {
        double tmas = 1.0 / tam;
        double aam = am * 365.25;
        double amo = aam * (1 + elem[0]);
        double rmu = Gk1 * (1 + tmas);
        double dga = Math.Cbrt(rmu / Square(amo));
        double rl = elem[1], rk = elem[2], rh = elem[3];

        // Solve Kepler's equation, a few iterations should be enough
        var (srl, crl) = Math.SinCos(rl);
        double fle = rl - rk * srl + rh * crl;
        var (sf, cf) = Math.SinCos(fle);
        double corf = (rl - fle + rk * sf - rh * cf) / (1 - rk * cf - rh * sf);
        fle += corf;
        for (int i = 0; i < 5; i++)
        {
            (sf, cf) = Math.SinCos(fle);
            corf = (rl - fle + rk * sf - rh * cf) / (1 - rk * cf - rh * sf);
            fle += corf;
        }
        (sf, cf) = Math.SinCos(fle);
        double dlf = -rk * sf + rh * cf;
        double phi = Math.Sqrt(1 - rk * rk - rh * rh);
        double psi = 1.0 / (1 + phi);
        double x1 = dga * (cf - rk - psi * rh * dlf);
        double y1 = dga * (sf - rh + psi * rk * dlf);
        double dwho = 2 * Math.Sqrt(1 - elem[5] * elem[5] - elem[4] * elem[4]);
        double rtp = 1 - 2 * Square(elem[5]);
        double rtq = 1 - 2 * Square(elem[4]);
        double rdg = 2 * elem[5] * elem[4];
        double tx = x1 * rtp + y1 * rdg;
        double ty = x1 * rdg + y1 * rtq;
        double tz = (-x1 * elem[5] + y1 * elem[4]) * dwho;
        var (si, ci) = Math.SinCos(DegToRad(SatTilt));
        var (so, co) = Math.SinCos(DegToRad(SatAscendingNode));
        double x = co * tx - so * ci * ty + so * si * tz;
        double y = so * tx + co * ci * ty - co * si * tz;
        double z = si * ty + ci * tz;
        // Convert to the native Y-up axes
        return new Vector3d(x, z, -y) * KilometersPerAu;
    }

    /// <summary>tass17.cpp TASS17MajMoonOrbit::computePosition.</summary>
    private static Vector3d TassPosition(Epoch jd, int[] termIndices, int[][] terms, TassApf[] apf, TassExtra extra)
    {
        double t = (jd - 2444240.0) / 365.25; // Julian years since J1980
        // Stack temporaries: the theory is evaluated three times per body and frame.
        Span<double> elem = stackalloc double[6];
        Span<double> dlo = stackalloc double[8];
        CalcLongitude(t, dlo);
        static double Phase(int i, TassApf[] apf, int[][] terms, ReadOnlySpan<double> dlo, double t)
        {
            double phas = apf[i].Phas;
            for (int jk = 0; jk < 8; jk++) phas += terms[i][jk] * dlo[jk];
            return phas + t * apf[i].Freq;
        }

        double s = 0; // p (mean motion)
        for (int i = 0; i < termIndices[0]; i++) s += apf[i].Ampl * Math.Cos(Phase(i, apf, terms, dlo, t));
        elem[0] = s;

        s = dlo[extra.Id - 1] + extra.Al0; // lambda (mean longitude)
        for (int i = termIndices[0]; i < termIndices[1]; i++) s += apf[i].Ampl * Math.Sin(Phase(i, apf, terms, dlo, t));
        s += extra.An0 * t;
        var (sn, cs) = Math.SinCos(s);
        elem[1] = Math.Atan2(sn, cs);

        double s1 = 0, s2 = 0; // z (eccentricity and pericenter)
        for (int i = termIndices[1]; i < termIndices[2]; i++)
        {
            var (swt, cwt) = Math.SinCos(Phase(i, apf, terms, dlo, t));
            s1 += apf[i].Ampl * cwt; s2 += apf[i].Ampl * swt;
        }
        elem[2] = s1; elem[3] = s2;

        s1 = 0; s2 = 0; // zeta (inclination and node)
        for (int i = termIndices[2]; i < termIndices[3]; i++)
        {
            var (swt, cwt) = Math.SinCos(Phase(i, apf, terms, dlo, t));
            s1 += apf[i].Ampl * cwt; s2 += apf[i].Ampl * swt;
        }
        elem[4] = s1; elem[5] = s2;
        return EllipticToRectangular(elem, extra.Tam, extra.Am);
    }

    /// <summary>tass17.cpp TASS17HyperionOrbit::computePosition.</summary>
    private static Vector3d HyperionPosition(Epoch jd)
    {
        int[] termIndices = HyperionTermIndices; var apf = HyperionAPF;
        double t = jd - 2451545.0;
        double p = -0.1574686065780747E-02;
        for (int i = 0; i < termIndices[0]; i++) p += apf[i].Ampl * Math.Cos(t * apf[i].Freq + apf[i].Phas);
        double q = 0.4348683610500939E+01;
        for (int i = termIndices[0]; i < termIndices[1]; i++) q += apf[i].Ampl * Math.Sin(t * apf[i].Freq + apf[i].Phas);
        double zr = 0, zi = 0;
        for (int i = termIndices[1]; i < termIndices[2]; i++)
        {
            var (swt, cwt) = Math.SinCos(t * apf[i].Freq + apf[i].Phas);
            zr += apf[i].Ampl * cwt; zi += apf[i].Ampl * swt;
        }
        double ztr = 0, zti = 0;
        for (int i = termIndices[2]; i < termIndices[3]; i++)
        {
            var (swt, cwt) = Math.SinCos(t * apf[i].Freq + apf[i].Phas);
            ztr += apf[i].Ampl * cwt; zti += apf[i].Ampl * swt;
        }
        double vl = (0.2953088138695055E+00 * t + q) % (Math.PI * 2); // C# % on doubles is std::fmod
        if (vl < 0) vl += Math.PI * 2;
        return EllipticToRectangular([p, vl, zr, zi, ztr, zti], 0.3333333333333E+08, 0.2953088138695E+00);
    }
}
