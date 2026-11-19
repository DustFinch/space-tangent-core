// SPDX-License-Identifier: GPL-2.0-or-later
// The precession, nutation and rotation formulas follow the ERFA library (BSD-3-Clause,
// Copyright (C) 2013-2021 NumFOCUS Foundation), derived from the IAU SOFA library:
// eraLtpequ, eraLtpecl, eraLtp, eraLtpb (J. Vondrák, N. Capitaine and P. Wallace 2011, A&A 534, A22),
// eraNut00b (IAU 2000B, McCarthy and Luzum 2003), eraObl06, eraEra00, eraC2ixys and the polynomial
// part of eraS06.
// Full upstream copyright, conditions and disclaimer: LICENSES/ERFA.txt in the repository/distribution.
using SpaceTangent.Contracts;
using SpaceTangent.Core.Time;
using SpaceTangent.Core.World;

namespace SpaceTangent.Core.Coordinates;

/// <summary>
/// Orientation of the Earth in the world ICRF axes: long-term precession with frame bias, IAU 2000B nutation and
/// the Earth rotation angle from UT1 = TT − ΔT. Polar motion is neglected (below 0.5″).
/// </summary>
public static class EarthOrientation
{
    private const double ArcsecondsToRadians = Math.PI / (180 * 3600);
    private const double TwoPi = 2 * Math.PI;
    private const double DaysPerJulianCentury = 36_525;
    private const double EarthRotationRadiansPerUt1Second = TwoPi * 1.00273781191135448 / 86_400;
    private static readonly JulianInstant J2000Tt = new(JulianInstant.J2000Day, 0, TimeScale.Tt);

    /// <summary>Rotation from terrestrial (Earth-fixed) axes to the world axes, with Earth's angular velocity.</summary>
    public static FrameKinematics TerrestrialToWorld(JulianInstant epoch, DeltaTModel deltaT)
    {
        JulianInstant tt = ToTt(epoch);
        return TerrestrialToWorld(tt, DeltaT.Seconds(tt, deltaT));
    }

    /// <summary>Rotation from terrestrial axes to the world axes for a TT epoch and an explicit TT − UT1 in seconds.</summary>
    public static FrameKinematics TerrestrialToWorld(JulianInstant tt, double ttMinusUt1Seconds)
    {
        Matrix3 celestialToTerrestrial = CelestialToTerrestrial(tt, ttMinusUt1Seconds);
        Basis3d toWorld = celestialToTerrestrial.TransposeToBasis();
        return new(toWorld, toWorld.Z * EarthRotationRadiansPerUt1Second);
    }

    /// <summary>The Earth rotation angle (IAU 2000) in radians, in [0, 2π).</summary>
    public static double EarthRotationAngle(JulianInstant epoch, DeltaTModel deltaT)
    {
        JulianInstant tt = ToTt(epoch);
        return EarthRotationAngle(TtDays(tt) - DeltaT.Seconds(tt, deltaT) / JulianInstant.SecondsPerDay);
    }

    /// <summary>Rotation from the mean equator and equinox of date to the world axes (precession and frame bias).</summary>
    public static Basis3d MeanEquatorOfDateToWorld(JulianInstant epoch) =>
        BiasPrecession(JulianEpoch(ToTt(epoch))).TransposeToBasis();

    /// <summary>
    /// Rotation from the TEME frame of SGP4 to the world axes: TEME to pseudo-Earth-fixed through the 1982 Greenwich
    /// mean sidereal time, as in Vallado's teme2ecef, then Earth-fixed to world. Velocities rotate with the same basis
    /// because both frames are quasi-inertial.
    /// </summary>
    public static Basis3d TemeToWorld(JulianInstant epoch, DeltaTModel deltaT)
    {
        JulianInstant tt = ToTt(epoch);
        return TemeToWorld(tt, DeltaT.Seconds(tt, deltaT));
    }

    public static Basis3d TemeToWorld(JulianInstant tt, double ttMinusUt1Seconds)
    {
        Matrix3 celestialToTerrestrial = CelestialToTerrestrial(tt, ttMinusUt1Seconds);
        double gmst = Gmst1982(TtDays(tt) - ttMinusUt1Seconds / JulianInstant.SecondsPerDay);
        Matrix3 temeToPseudoFixed = Matrix3.RotationZ(gmst);
        return (celestialToTerrestrial.Transpose() * temeToPseudoFixed).ToBasis();
    }

    private static JulianInstant ToTt(JulianInstant epoch) => epoch.Scale switch
    {
        TimeScale.Tt => epoch,
        TimeScale.Tdb => TimeConversion.ApproximateTtForEarth(epoch),
        _ => TimeConversion.Convert(epoch, TimeScale.Tt)
    };

    private static double TtDays(JulianInstant tt) =>
        (double)tt.DifferenceNanoseconds(J2000Tt) / JulianInstant.NanosecondsPerDay;

    private static double JulianEpoch(JulianInstant tt) => 2000 + TtDays(tt) / 365.25;

    private static Matrix3 CelestialToTerrestrial(JulianInstant tt, double ttMinusUt1Seconds)
    {
        if (tt.Scale != TimeScale.Tt)
            throw new ArgumentException("Earth orientation requires a TT epoch.", nameof(tt));
        if (!double.IsFinite(ttMinusUt1Seconds))
            throw new ArgumentOutOfRangeException(nameof(ttMinusUt1Seconds));

        double days = TtDays(tt);
        double centuries = days / DaysPerJulianCentury;
        Matrix3 biasPrecession = BiasPrecession(2000 + days / 365.25);
        var (dpsi, deps) = Nutation2000B(centuries);
        Matrix3 nutation = NutationMatrix(MeanObliquity2006(centuries), dpsi, deps);
        Matrix3 npb = nutation * biasPrecession;
        double x = npb[2, 0], y = npb[2, 1];
        double s = CioLocatorPolynomial(centuries) - x * y / 2;
        Matrix3 celestialToIntermediate = CelestialToIntermediate(x, y, s);
        double era = EarthRotationAngle(days - ttMinusUt1Seconds / JulianInstant.SecondsPerDay);
        return Matrix3.RotationZ(era) * celestialToIntermediate;
    }

    /// <summary>eraEra00 with UT1 days from JD 2451545.0.</summary>
    private static double EarthRotationAngle(double ut1Days)
    {
        double fraction = ut1Days % 1;
        double angle = TwoPi * (fraction + .7790572732640 + .00273781191135448 * ut1Days) % TwoPi;
        return angle < 0 ? angle + TwoPi : angle;
    }

    /// <summary>Greenwich mean sidereal time (IAU 1982) as used with TEME.</summary>
    private static double Gmst1982(double ut1Days)
    {
        double t = ut1Days / DaysPerJulianCentury;
        double seconds = 67310.54841 + (876600 * 3600.0 + 8640184.812866) * t + (.093104 - 6.2e-6 * t) * t * t;
        double angle = seconds * (Math.PI / 43_200) % TwoPi;
        return angle < 0 ? angle + TwoPi : angle;
    }

    /// <summary>eraLtpb: long-term precession matrix including the ICRS frame bias (GCRS to mean of date).</summary>
    private static Matrix3 BiasPrecession(double julianEpoch)
    {
        Matrix3 rp = Precession(julianEpoch);
        const double dx = -.016617 * ArcsecondsToRadians, de = -.0068192 * ArcsecondsToRadians, dr = -.0146 * ArcsecondsToRadians;
        var result = new Matrix3();
        for (int i = 0; i < 3; i++)
        {
            result[i, 0] = rp[i, 0] - rp[i, 1] * dr + rp[i, 2] * dx;
            result[i, 1] = rp[i, 0] * dr + rp[i, 1] + rp[i, 2] * de;
            result[i, 2] = -rp[i, 0] * dx - rp[i, 1] * de + rp[i, 2];
        }
        return result;
    }

    /// <summary>eraLtp: long-term precession matrix from the equator and ecliptic poles of date.</summary>
    private static Matrix3 Precession(double julianEpoch)
    {
        Vector3d equator = EquatorPole(julianEpoch);
        Vector3d ecliptic = EclipticPole(julianEpoch);
        Vector3d node = Vector3d.Cross(equator, ecliptic);
        node /= node.Length;
        Vector3d middle = Vector3d.Cross(equator, node);
        return Matrix3.FromRows(node, middle, equator);
    }

    private static readonly double[,] EquatorPolynomial =
    {
        { 5453.282155, .4252841, -.00037173, -.000000152 },
        { -73750.930350, -.7675452, -.00018725, .000000231 }
    };

    private static readonly double[,] EquatorPeriodic =
    {
        { 256.75, -819.940624, 75004.344875, 81491.287984, 1558.515853 },
        { 708.15, -8444.676815, 624.033993, 787.163481, 7774.939698 },
        { 274.20, 2600.009459, 1251.136893, 1251.296102, -2219.534038 },
        { 241.45, 2755.175630, -1102.212834, -1257.950837, -2523.969396 },
        { 2309.00, -167.659835, -2660.664980, -2966.799730, 247.850422 },
        { 492.20, 871.855056, 699.291817, 639.744522, -846.485643 },
        { 396.10, 44.769698, 153.167220, 131.600209, -1393.124055 },
        { 288.90, -512.313065, -950.865637, -445.040117, 368.526116 },
        { 231.10, -819.415595, 499.754645, 584.522874, 749.045012 },
        { 1610.00, -538.071099, -145.188210, -89.756563, 444.704518 },
        { 620.00, -189.793622, 558.116553, 524.429630, 235.934465 },
        { 157.87, -402.922932, -23.923029, -13.549067, 374.049623 },
        { 220.30, 179.516345, -165.405086, -210.157124, -171.330180 },
        { 1200.00, -9.814756, 9.344131, -44.919798, -22.899655 }
    };

    private static readonly double[,] EclipticPolynomial =
    {
        { 5851.607687, -.1189000, -.00028913, .000000101 },
        { -1600.886300, 1.1689818, -.00000020, -.000000437 }
    };

    private static readonly double[,] EclipticPeriodic =
    {
        { 708.15, -5486.751211, -684.661560, 667.666730, -5523.863691 },
        { 2309.00, -17.127623, 2446.283880, -2354.886252, -549.747450 },
        { 1620.00, -617.517403, 399.671049, -428.152441, -310.998056 },
        { 492.20, 413.442940, -356.652376, 376.202861, 421.535876 },
        { 1183.00, 78.614193, -186.387003, 184.778874, -36.776172 },
        { 622.00, -180.732815, -316.800070, 335.321713, -145.278396 },
        { 882.00, -87.676083, 198.296701, -185.138669, -34.744450 },
        { 547.00, 46.140315, 101.135679, -120.972830, 22.885731 }
    };

    /// <summary>eraLtpequ: the mean equator pole of date in the J2000 frame.</summary>
    private static Vector3d EquatorPole(double julianEpoch)
    {
        var (x, y) = PoleSeries(julianEpoch, EquatorPolynomial, EquatorPeriodic);
        double w = 1 - x * x - y * y;
        return new(x, y, w < 0 ? 0 : Math.Sqrt(w));
    }

    /// <summary>eraLtpecl: the ecliptic pole of date in the J2000 equatorial frame.</summary>
    private static Vector3d EclipticPole(double julianEpoch)
    {
        const double eps0 = 84381.406 * ArcsecondsToRadians;
        var (p, q) = PoleSeries(julianEpoch, EclipticPolynomial, EclipticPeriodic);
        double w = 1 - p * p - q * q;
        w = w < 0 ? 0 : Math.Sqrt(w);
        var (s, c) = (Math.Sin(eps0), Math.Cos(eps0));
        return new(p, -q * c - w * s, -q * s + w * c);
    }

    private static (double First, double Second) PoleSeries(double julianEpoch, double[,] polynomial, double[,] periodic)
    {
        double t = (julianEpoch - 2000) / 100;
        double first = 0, second = 0, w = TwoPi * t;
        for (int i = 0; i < periodic.GetLength(0); i++)
        {
            double a = w / periodic[i, 0];
            double s = Math.Sin(a), c = Math.Cos(a);
            first += c * periodic[i, 1] + s * periodic[i, 3];
            second += c * periodic[i, 2] + s * periodic[i, 4];
        }
        w = 1;
        for (int i = 0; i < polynomial.GetLength(1); i++)
        {
            first += polynomial[0, i] * w;
            second += polynomial[1, i] * w;
            w *= t;
        }
        return (first * ArcsecondsToRadians, second * ArcsecondsToRadians);
    }

    /// <summary>eraObl06: mean obliquity of the ecliptic, IAU 2006.</summary>
    private static double MeanObliquity2006(double t) =>
        (84381.406 + (-46.836769 + (-.0001831 + (.00200340 + (-.000000576 + -.0000000434 * t) * t) * t) * t) * t) *
        ArcsecondsToRadians;

    /// <summary>eraNumat: mean of date to true of date.</summary>
    private static Matrix3 NutationMatrix(double epsa, double dpsi, double deps) =>
        Matrix3.RotationX(-(epsa + deps)) * Matrix3.RotationZ(-dpsi) * Matrix3.RotationX(epsa);

    /// <summary>The polynomial part of eraS06 in radians, before the −XY/2 term. Periodic terms are below 3 mas.</summary>
    private static double CioLocatorPolynomial(double t) =>
        (94.00e-6 + (3808.65e-6 + (-122.68e-6 + (-72574.11e-6 + (27.98e-6 + 15.62e-6 * t) * t) * t) * t) * t) *
        ArcsecondsToRadians;

    /// <summary>eraC2ixys: celestial to intermediate matrix from the CIP coordinates and the CIO locator.</summary>
    private static Matrix3 CelestialToIntermediate(double x, double y, double s)
    {
        double r2 = x * x + y * y;
        double e = r2 > 0 ? Math.Atan2(y, x) : 0;
        double d = Math.Atan(Math.Sqrt(r2 / (1 - r2)));
        return Matrix3.RotationZ(-(e + s)) * Matrix3.RotationY(d) * Matrix3.RotationZ(e);
    }

    // IAU 2000B luni-solar nutation: multipliers of l, l', F, D, Ω; then ψ sin, t·sin, cos and ε cos, t·cos, sin
    // coefficients in 0.1 µas.
    private static readonly double[,] Nutation =
    {
        { 0, 0, 0, 0, 1, -172064161, -174666, 33386, 92052331, 9086, 15377 },
        { 0, 0, 2, -2, 2, -13170906, -1675, -13696, 5730336, -3015, -4587 },
        { 0, 0, 2, 0, 2, -2276413, -234, 2796, 978459, -485, 1374 },
        { 0, 0, 0, 0, 2, 2074554, 207, -698, -897492, 470, -291 },
        { 0, 1, 0, 0, 0, 1475877, -3633, 11817, 73871, -184, -1924 },
        { 0, 1, 2, -2, 2, -516821, 1226, -524, 224386, -677, -174 },
        { 1, 0, 0, 0, 0, 711159, 73, -872, -6750, 0, 358 },
        { 0, 0, 2, 0, 1, -387298, -367, 380, 200728, 18, 318 },
        { 1, 0, 2, 0, 2, -301461, -36, 816, 129025, -63, 367 },
        { 0, -1, 2, -2, 2, 215829, -494, 111, -95929, 299, 132 },
        { 0, 0, 2, -2, 1, 128227, 137, 181, -68982, -9, 39 },
        { -1, 0, 2, 0, 2, 123457, 11, 19, -53311, 32, -4 },
        { -1, 0, 0, 2, 0, 156994, 10, -168, -1235, 0, 82 },
        { 1, 0, 0, 0, 1, 63110, 63, 27, -33228, 0, -9 },
        { -1, 0, 0, 0, 1, -57976, -63, -189, 31429, 0, -75 },
        { -1, 0, 2, 2, 2, -59641, -11, 149, 25543, -11, 66 },
        { 1, 0, 2, 0, 1, -51613, -42, 129, 26366, 0, 78 },
        { -2, 0, 2, 0, 1, 45893, 50, 31, -24236, -10, 20 },
        { 0, 0, 0, 2, 0, 63384, 11, -150, -1220, 0, 29 },
        { 0, 0, 2, 2, 2, -38571, -1, 158, 16452, -11, 68 },
        { 0, -2, 2, -2, 2, 32481, 0, 0, -13870, 0, 0 },
        { -2, 0, 0, 2, 0, -47722, 0, -18, 477, 0, -25 },
        { 2, 0, 2, 0, 2, -31046, -1, 131, 13238, -11, 59 },
        { 1, 0, 2, -2, 2, 28593, 0, -1, -12338, 10, -3 },
        { -1, 0, 2, 0, 1, 20441, 21, 10, -10758, 0, -3 },
        { 2, 0, 0, 0, 0, 29243, 0, -74, -609, 0, 13 },
        { 0, 0, 2, 0, 0, 25887, 0, -66, -550, 0, 11 },
        { 0, 1, 0, 0, 1, -14053, -25, 79, 8551, -2, -45 },
        { -1, 0, 0, 2, 1, 15164, 10, 11, -8001, 0, -1 },
        { 0, 2, 2, -2, 2, -15794, 72, -16, 6850, -42, -5 },
        { 0, 0, -2, 2, 0, 21783, 0, 13, -167, 0, 13 },
        { 1, 0, 0, -2, 1, -12873, -10, -37, 6953, 0, -14 },
        { 0, -1, 0, 0, 1, -12654, 11, 63, 6415, 0, 26 },
        { -1, 0, 2, 2, 1, -10204, 0, 25, 5222, 0, 15 },
        { 0, 2, 0, 0, 0, 16707, -85, -10, 168, -1, 10 },
        { 1, 0, 2, 2, 2, -7691, 0, 44, 3268, 0, 19 },
        { -2, 0, 2, 0, 0, -11024, 0, -14, 104, 0, 2 },
        { 0, 1, 2, 0, 2, 7566, -21, -11, -3250, 0, -5 },
        { 0, 0, 2, 2, 1, -6637, -11, 25, 3353, 0, 14 },
        { 0, -1, 2, 0, 2, -7141, 21, 8, 3070, 0, 4 },
        { 0, 0, 0, 2, 1, -6302, -11, 2, 3272, 0, 4 },
        { 1, 0, 2, -2, 1, 5800, 10, 2, -3045, 0, -1 },
        { 2, 0, 2, -2, 2, 6443, 0, -7, -2768, 0, -4 },
        { -2, 0, 0, 2, 1, -5774, -11, -15, 3041, 0, -5 },
        { 2, 0, 2, 0, 1, -5350, 0, 21, 2695, 0, 12 },
        { 0, -1, 2, -2, 1, -4752, -11, -3, 2719, 0, -3 },
        { 0, 0, 0, -2, 1, -4940, -11, -21, 2720, 0, -9 },
        { -1, -1, 0, 2, 0, 7350, 0, -8, -51, 0, 4 },
        { 2, 0, 0, -2, 1, 4065, 0, 6, -2206, 0, 1 },
        { 1, 0, 0, 2, 0, 6579, 0, -24, -199, 0, 2 },
        { 0, 1, 2, -2, 1, 3579, 0, 5, -1900, 0, 1 },
        { 1, -1, 0, 0, 0, 4725, 0, -6, -41, 0, 3 },
        { -2, 0, 2, 0, 2, -3075, 0, -2, 1313, 0, -1 },
        { 3, 0, 2, 0, 2, -2904, 0, 15, 1233, 0, 7 },
        { 0, -1, 0, 2, 0, 4348, 0, -10, -81, 0, 2 },
        { 1, -1, 2, 0, 2, -2878, 0, 8, 1232, 0, 4 },
        { 0, 0, 0, 1, 0, -4230, 0, 5, -20, 0, -2 },
        { -1, -1, 2, 2, 2, -2819, 0, 7, 1207, 0, 3 },
        { -1, 0, 2, 0, 0, -4056, 0, 5, 40, 0, -2 },
        { 0, -1, 2, 2, 2, -2647, 0, 11, 1129, 0, 5 },
        { -2, 0, 0, 0, 1, -2294, 0, -10, 1266, 0, -4 },
        { 1, 1, 2, 0, 2, 2481, 0, -7, -1062, 0, -3 },
        { 2, 0, 0, 0, 1, 2179, 0, -2, -1129, 0, -2 },
        { -1, 1, 0, 1, 0, 3276, 0, 1, -9, 0, 0 },
        { 1, 1, 0, 0, 0, -3389, 0, 5, 35, 0, -2 },
        { 1, 0, 2, 0, 0, 3339, 0, -13, -107, 0, 1 },
        { -1, 0, 2, -2, 1, -1987, 0, -6, 1073, 0, -2 },
        { 1, 0, 0, 0, 2, -1981, 0, 0, 854, 0, 0 },
        { -1, 0, 0, 1, 0, 4026, 0, -353, -553, 0, -139 },
        { 0, 0, 2, 1, 2, 1660, 0, -5, -710, 0, -2 },
        { -1, 0, 2, 4, 2, -1521, 0, 9, 647, 0, 4 },
        { -1, 1, 0, 1, 1, 1314, 0, 0, -700, 0, 0 },
        { 0, -2, 2, -2, 1, -1283, 0, 0, 672, 0, 0 },
        { 1, 0, 2, 2, 1, -1331, 0, 8, 663, 0, 4 },
        { -2, 0, 2, 2, 2, 1383, 0, -2, -594, 0, -2 },
        { -1, 0, 0, 0, 2, 1405, 0, 4, -610, 0, 2 },
        { 1, 1, 2, -2, 2, 1290, 0, 0, -556, 0, 0 }
    };

    /// <summary>eraNut00b: nutation in longitude and obliquity in radians, TT Julian centuries from J2000.</summary>
    private static (double Dpsi, double Deps) Nutation2000B(double t)
    {
        const double turnArcseconds = 1_296_000, unitToRadians = ArcsecondsToRadians / 1e7;
        const double planetaryPsi = -.135e-3 * ArcsecondsToRadians, planetaryEps = .388e-3 * ArcsecondsToRadians;
        double el = (485868.249036 + 1717915923.2178 * t) % turnArcseconds * ArcsecondsToRadians;
        double elp = (1287104.79305 + 129596581.0481 * t) % turnArcseconds * ArcsecondsToRadians;
        double f = (335779.526232 + 1739527262.8478 * t) % turnArcseconds * ArcsecondsToRadians;
        double d = (1072260.70369 + 1602961601.2090 * t) % turnArcseconds * ArcsecondsToRadians;
        double om = (450160.398036 + -6962890.5431 * t) % turnArcseconds * ArcsecondsToRadians;
        double dp = 0, de = 0;
        for (int i = Nutation.GetLength(0) - 1; i >= 0; i--)
        {
            double arg = (Nutation[i, 0] * el + Nutation[i, 1] * elp + Nutation[i, 2] * f + Nutation[i, 3] * d +
                Nutation[i, 4] * om) % TwoPi;
            double sarg = Math.Sin(arg), carg = Math.Cos(arg);
            dp += (Nutation[i, 5] + Nutation[i, 6] * t) * sarg + Nutation[i, 7] * carg;
            de += (Nutation[i, 8] + Nutation[i, 9] * t) * carg + Nutation[i, 10] * sarg;
        }
        return (dp * unitToRadians + planetaryPsi, de * unitToRadians + planetaryEps);
    }
}

/// <summary>A row-major 3×3 matrix with the ERFA rotation conventions.</summary>
internal struct Matrix3
{
    private double _m00, _m01, _m02, _m10, _m11, _m12, _m20, _m21, _m22;

    public double this[int row, int column]
    {
        readonly get => (row * 3 + column) switch
        {
            0 => _m00, 1 => _m01, 2 => _m02, 3 => _m10, 4 => _m11, 5 => _m12, 6 => _m20, 7 => _m21, 8 => _m22,
            _ => throw new ArgumentOutOfRangeException(nameof(row))
        };
        set
        {
            switch (row * 3 + column)
            {
                case 0: _m00 = value; break;
                case 1: _m01 = value; break;
                case 2: _m02 = value; break;
                case 3: _m10 = value; break;
                case 4: _m11 = value; break;
                case 5: _m12 = value; break;
                case 6: _m20 = value; break;
                case 7: _m21 = value; break;
                case 8: _m22 = value; break;
                default: throw new ArgumentOutOfRangeException(nameof(row));
            }
        }
    }

    public static Matrix3 FromRows(Vector3d first, Vector3d second, Vector3d third) => new()
    {
        _m00 = first.X, _m01 = first.Y, _m02 = first.Z,
        _m10 = second.X, _m11 = second.Y, _m12 = second.Z,
        _m20 = third.X, _m21 = third.Y, _m22 = third.Z
    };

    /// <summary>eraRx: rotation of the axes by the angle about X (a passive rotation).</summary>
    public static Matrix3 RotationX(double angle)
    {
        var (s, c) = Math.SinCos(angle);
        return FromRows(new(1, 0, 0), new(0, c, s), new(0, -s, c));
    }

    public static Matrix3 RotationY(double angle)
    {
        var (s, c) = Math.SinCos(angle);
        return FromRows(new(c, 0, -s), new(0, 1, 0), new(s, 0, c));
    }

    public static Matrix3 RotationZ(double angle)
    {
        var (s, c) = Math.SinCos(angle);
        return FromRows(new(c, s, 0), new(-s, c, 0), new(0, 0, 1));
    }

    public static Matrix3 operator *(Matrix3 left, Matrix3 right)
    {
        var result = new Matrix3();
        for (int i = 0; i < 3; i++)
        for (int j = 0; j < 3; j++)
            result[i, j] = left[i, 0] * right[0, j] + left[i, 1] * right[1, j] + left[i, 2] * right[2, j];
        return result;
    }

    public readonly Matrix3 Transpose() => FromRows(new(_m00, _m10, _m20), new(_m01, _m11, _m21), new(_m02, _m12, _m22));

    /// <summary>The basis whose Transform applies this matrix.</summary>
    public readonly Basis3d ToBasis() => new(new(_m00, _m10, _m20), new(_m01, _m11, _m21), new(_m02, _m12, _m22));

    /// <summary>The basis whose Transform applies the transpose of this matrix.</summary>
    public readonly Basis3d TransposeToBasis() => new(new(_m00, _m01, _m02), new(_m10, _m11, _m12), new(_m20, _m21, _m22));
}
