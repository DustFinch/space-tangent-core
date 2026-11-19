// SPDX-License-Identifier: GPL-2.0-or-later
// Deep-space lunar-solar terms and resonance integration (dscom, dsinit, dspace and dpper) after
// Vallado, Crawford, Hujsak and Kelso, AIAA 2006-6753, in the AFSPC operations mode.
namespace SpaceTangent.Core.Orbits;

internal sealed class DeepSpaceTerms
{
    private const double TwoPi = 2 * Math.PI, TwoThirds = 2d / 3;
    private const double Zes = .01675, Zel = .05490, Zns = 1.19459e-5, Znl = 1.5835218e-4;
    private const double Rptim = 4.37526908801129966e-3, StepPositive = 720, StepNegative = -720, StepSquared = 259200;
    private const double Fasx2 = .13130908, Fasx4 = 2.8843198, Fasx6 = .37448087;
    private const double G22 = 5.7686396, G32 = .95240898, G44 = 1.8014998, G52 = 1.0508330, G54 = 4.4108898;

    private const int CheckpointSteps = 64;
    private readonly record struct ResonancePoint(double Time, double Motion, double Longitude);
    private readonly Lazy<ResonancePoint[]> _positiveGrid, _negativeGrid;
    private readonly double _argpo, _argpdot;

    // Lunar-solar periodic coefficients (dscom).
    private readonly double _e3, _ee2, _se2, _se3, _sgh2, _sgh3, _sgh4, _sh2, _sh3, _si2, _si3, _sl2, _sl3, _sl4;
    private readonly double _xgh2, _xgh3, _xgh4, _xh2, _xh3, _xi2, _xi3, _xl2, _xl3, _xl4, _zmol, _zmos;
    // Secular rates and resonance terms (dsinit).
    private readonly int _irez;
    private readonly double _gsto, _no, _dedt, _didt, _dmdt, _dnodt, _domdt, _del1, _del2, _del3, _xfact, _xlamo;
    private readonly double _d2201, _d2211, _d3210, _d3222, _d4410, _d4422, _d5220, _d5232, _d5421, _d5433;

    internal DeepSpaceTerms(double epoch, double gsto, double xke, double ecco, double inclo, double nodeo,
        double argpo, double mo, double no, double mdot, double nodedot, double argpdot, double xpidot)
    {
        _argpo = argpo;
        _argpdot = argpdot;
        _positiveGrid = new(() => BuildResonanceGrid(StepPositive));
        _negativeGrid = new(() => BuildResonanceGrid(StepNegative));
        _gsto = gsto;
        _no = no;

        // dscom
        const double c1ss = 2.9864797e-6, c1l = 4.7968065e-7, zsinis = .39785416, zcosis = .91744867;
        const double zcosgs = .1945905, zsings = -.98088458;
        double snodm = Math.Sin(nodeo), cnodm = Math.Cos(nodeo);
        double sinomm = Math.Sin(argpo), cosomm = Math.Cos(argpo);
        double sinim = Math.Sin(inclo), cosim = Math.Cos(inclo);
        double em = ecco, emsq = em * em, betasq = 1 - emsq, rtemsq = Math.Sqrt(betasq);
        double day = epoch + 18261.5;
        double xnodce = (4.5236020 - 9.2422029e-4 * day) % TwoPi;
        double stem = Math.Sin(xnodce), ctem = Math.Cos(xnodce);
        double zcosil = .91375164 - .03568096 * ctem;
        double zsinil = Math.Sqrt(1 - zcosil * zcosil);
        double zsinhl = .089683511 * stem / zsinil;
        double zcoshl = Math.Sqrt(1 - zsinhl * zsinhl);
        double gam = 5.8351514 + .0019443680 * day;
        double zx = .39785416 * stem / zsinil;
        double zy = zcoshl * ctem + .91744867 * zsinhl * stem;
        zx = gam + Math.Atan2(zx, zy) - xnodce;
        double zcosgl = Math.Cos(zx), zsingl = Math.Sin(zx);
        double zcosg = zcosgs, zsing = zsings, zcosi = zcosis, zsini = zsinis, zcosh = cnodm, zsinh = snodm;
        double cc = c1ss, xnoi = 1 / no;
        double s1 = 0, s2 = 0, s3 = 0, s4 = 0, s5 = 0, s6 = 0, s7 = 0;
        double z1 = 0, z2 = 0, z3 = 0, z11 = 0, z12 = 0, z13 = 0, z21 = 0, z22 = 0, z23 = 0, z31 = 0, z32 = 0, z33 = 0;
        double ss1 = 0, ss2 = 0, ss3 = 0, ss4 = 0, ss5 = 0, ss6 = 0, ss7 = 0;
        double sz1 = 0, sz2 = 0, sz3 = 0, sz11 = 0, sz12 = 0, sz13 = 0, sz21 = 0, sz22 = 0, sz23 = 0;
        double sz31 = 0, sz32 = 0, sz33 = 0;
        for (int lsflg = 1; lsflg <= 2; lsflg++)
        {
            double a1 = zcosg * zcosh + zsing * zcosi * zsinh;
            double a3 = -zsing * zcosh + zcosg * zcosi * zsinh;
            double a7 = -zcosg * zsinh + zsing * zcosi * zcosh;
            double a8 = zsing * zsini;
            double a9 = zsing * zsinh + zcosg * zcosi * zcosh;
            double a10 = zcosg * zsini;
            double a2 = cosim * a7 + sinim * a8;
            double a4 = cosim * a9 + sinim * a10;
            double a5 = -sinim * a7 + cosim * a8;
            double a6 = -sinim * a9 + cosim * a10;
            double x1 = a1 * cosomm + a2 * sinomm;
            double x2 = a3 * cosomm + a4 * sinomm;
            double x3 = -a1 * sinomm + a2 * cosomm;
            double x4 = -a3 * sinomm + a4 * cosomm;
            double x5 = a5 * sinomm, x6 = a6 * sinomm, x7 = a5 * cosomm, x8 = a6 * cosomm;
            z31 = 12 * x1 * x1 - 3 * x3 * x3;
            z32 = 24 * x1 * x2 - 6 * x3 * x4;
            z33 = 12 * x2 * x2 - 3 * x4 * x4;
            z1 = 3 * (a1 * a1 + a2 * a2) + z31 * emsq;
            z2 = 6 * (a1 * a3 + a2 * a4) + z32 * emsq;
            z3 = 3 * (a3 * a3 + a4 * a4) + z33 * emsq;
            z11 = -6 * a1 * a5 + emsq * (-24 * x1 * x7 - 6 * x3 * x5);
            z12 = -6 * (a1 * a6 + a3 * a5) + emsq * (-24 * (x2 * x7 + x1 * x8) - 6 * (x3 * x6 + x4 * x5));
            z13 = -6 * a3 * a6 + emsq * (-24 * x2 * x8 - 6 * x4 * x6);
            z21 = 6 * a2 * a5 + emsq * (24 * x1 * x5 - 6 * x3 * x7);
            z22 = 6 * (a4 * a5 + a2 * a6) + emsq * (24 * (x2 * x5 + x1 * x6) - 6 * (x4 * x7 + x3 * x8));
            z23 = 6 * a4 * a6 + emsq * (24 * x2 * x6 - 6 * x4 * x8);
            z1 = z1 + z1 + betasq * z31;
            z2 = z2 + z2 + betasq * z32;
            z3 = z3 + z3 + betasq * z33;
            s3 = cc * xnoi;
            s2 = -.5 * s3 / rtemsq;
            s4 = s3 * rtemsq;
            s1 = -15 * em * s4;
            s5 = x1 * x3 + x2 * x4;
            s6 = x2 * x3 + x1 * x4;
            s7 = x2 * x4 - x1 * x3;
            if (lsflg == 1)
            {
                (ss1, ss2, ss3, ss4, ss5, ss6, ss7) = (s1, s2, s3, s4, s5, s6, s7);
                (sz1, sz2, sz3, sz11, sz12, sz13) = (z1, z2, z3, z11, z12, z13);
                (sz21, sz22, sz23, sz31, sz32, sz33) = (z21, z22, z23, z31, z32, z33);
                zcosg = zcosgl;
                zsing = zsingl;
                zcosi = zcosil;
                zsini = zsinil;
                zcosh = zcoshl * cnodm + zsinhl * snodm;
                zsinh = snodm * zcoshl - cnodm * zsinhl;
                cc = c1l;
            }
        }
        _zmol = (4.7199672 + .22997150 * day - gam) % TwoPi;
        _zmos = (6.2565837 + .017201977 * day) % TwoPi;
        _se2 = 2 * ss1 * ss6;
        _se3 = 2 * ss1 * ss7;
        _si2 = 2 * ss2 * sz12;
        _si3 = 2 * ss2 * (sz13 - sz11);
        _sl2 = -2 * ss3 * sz2;
        _sl3 = -2 * ss3 * (sz3 - sz1);
        _sl4 = -2 * ss3 * (-21 - 9 * emsq) * Zes;
        _sgh2 = 2 * ss4 * sz32;
        _sgh3 = 2 * ss4 * (sz33 - sz31);
        _sgh4 = -18 * ss4 * Zes;
        _sh2 = -2 * ss2 * sz22;
        _sh3 = -2 * ss2 * (sz23 - sz21);
        _ee2 = 2 * s1 * s6;
        _e3 = 2 * s1 * s7;
        _xi2 = 2 * s2 * z12;
        _xi3 = 2 * s2 * (z13 - z11);
        _xl2 = -2 * s3 * z2;
        _xl3 = -2 * s3 * (z3 - z1);
        _xl4 = -2 * s3 * (-21 - 9 * emsq) * Zel;
        _xgh2 = 2 * s4 * z32;
        _xgh3 = 2 * s4 * (z33 - z31);
        _xgh4 = -18 * s4 * Zel;
        _xh2 = -2 * s2 * z22;
        _xh3 = -2 * s2 * (z23 - z21);

        // dsinit
        const double q22 = 1.7891679e-6, q31 = 2.1460748e-6, q33 = 2.2123015e-7, root22 = 1.7891679e-6;
        const double root44 = 7.3636953e-9, root54 = 2.1765803e-9, root32 = 3.7393792e-7, root52 = 1.1428639e-7;
        double nm = no;
        if (nm > .0034906585 && nm < .0052359877)
            _irez = 1;
        if (nm >= 8.26e-3 && nm <= 9.24e-3 && em >= .5)
            _irez = 2;
        double ses = ss1 * Zns * ss5;
        double sis = ss2 * Zns * (sz11 + sz13);
        double sls = -Zns * ss3 * (sz1 + sz3 - 14 - 6 * emsq);
        double sghs = ss4 * Zns * (sz31 + sz33 - 6);
        double shs = -Zns * ss2 * (sz21 + sz23);
        bool equatorial = inclo < 5.2359877e-2 || inclo > Math.PI - 5.2359877e-2;
        if (equatorial)
            shs = 0;
        if (sinim != 0)
            shs /= sinim;
        double sgs = sghs - cosim * shs;
        _dedt = ses + s1 * Znl * s5;
        _didt = sis + s2 * Znl * (z11 + z13);
        _dmdt = sls - Znl * s3 * (z1 + z3 - 14 - 6 * emsq);
        double sghl = s4 * Znl * (z31 + z33 - 6);
        double shll = equatorial ? 0 : -Znl * s2 * (z21 + z23);
        _domdt = sgs + sghl;
        _dnodt = shs;
        if (sinim != 0)
        {
            _domdt -= cosim / sinim * shll;
            _dnodt += shll / sinim;
        }

        if (_irez == 0)
            return;
        double theta = gsto % TwoPi;
        double aonv = Math.Pow(nm / xke, TwoThirds);
        if (_irez == 2)
        {
            double cosisq = cosim * cosim;
            em = ecco;
            emsq = em * em;
            double eoc = em * emsq;
            double g201 = -.306 - (em - .64) * .440;
            double g211, g310, g322, g410, g422, g520, g533, g521, g532;
            if (em <= .65)
            {
                g211 = 3.616 - 13.2470 * em + 16.2900 * emsq;
                g310 = -19.302 + 117.3900 * em - 228.4190 * emsq + 156.5910 * eoc;
                g322 = -18.9068 + 109.7927 * em - 214.6334 * emsq + 146.5816 * eoc;
                g410 = -41.122 + 242.6940 * em - 471.0940 * emsq + 313.9530 * eoc;
                g422 = -146.407 + 841.8800 * em - 1629.014 * emsq + 1083.4350 * eoc;
                g520 = -532.114 + 3017.977 * em - 5740.032 * emsq + 3708.2760 * eoc;
            }
            else
            {
                g211 = -72.099 + 331.819 * em - 508.738 * emsq + 266.724 * eoc;
                g310 = -346.844 + 1582.851 * em - 2415.925 * emsq + 1246.113 * eoc;
                g322 = -342.585 + 1554.908 * em - 2366.899 * emsq + 1215.972 * eoc;
                g410 = -1052.797 + 4758.686 * em - 7193.992 * emsq + 3651.957 * eoc;
                g422 = -3581.690 + 16178.110 * em - 24462.770 * emsq + 12422.520 * eoc;
                g520 = em > .715
                    ? -5149.66 + 29936.92 * em - 54087.36 * emsq + 31324.56 * eoc
                    : 1464.74 - 4664.75 * em + 3763.64 * emsq;
            }
            if (em < .7)
            {
                g533 = -919.22770 + 4988.6100 * em - 9064.7700 * emsq + 5542.21 * eoc;
                g521 = -822.71072 + 4568.6173 * em - 8491.4146 * emsq + 5337.524 * eoc;
                g532 = -853.66600 + 4690.2500 * em - 8624.7700 * emsq + 5341.4 * eoc;
            }
            else
            {
                g533 = -37995.780 + 161616.52 * em - 229838.20 * emsq + 109377.94 * eoc;
                g521 = -51752.104 + 218913.95 * em - 309468.16 * emsq + 146349.42 * eoc;
                g532 = -40023.880 + 170470.89 * em - 242699.48 * emsq + 115605.82 * eoc;
            }
            double sini2 = sinim * sinim;
            double f220 = .75 * (1 + 2 * cosim + cosisq);
            double f221 = 1.5 * sini2;
            double f321 = 1.875 * sinim * (1 - 2 * cosim - 3 * cosisq);
            double f322 = -1.875 * sinim * (1 + 2 * cosim - 3 * cosisq);
            double f441 = 35 * sini2 * f220;
            double f442 = 39.3750 * sini2 * sini2;
            double f522 = 9.84375 * sinim * (sini2 * (1 - 2 * cosim - 5 * cosisq) +
                .33333333 * (-2 + 4 * cosim + 6 * cosisq));
            double f523 = sinim * (4.92187512 * sini2 * (-2 - 4 * cosim + 10 * cosisq) +
                6.56250012 * (1 + 2 * cosim - 3 * cosisq));
            double f542 = 29.53125 * sinim * (2 - 8 * cosim + cosisq * (-12 + 8 * cosim + 10 * cosisq));
            double f543 = 29.53125 * sinim * (-2 - 8 * cosim + cosisq * (12 + 8 * cosim - 10 * cosisq));
            double xno2 = nm * nm, ainv2 = aonv * aonv;
            double temp1 = 3 * xno2 * ainv2;
            double temp = temp1 * root22;
            _d2201 = temp * f220 * g201;
            _d2211 = temp * f221 * g211;
            temp1 *= aonv;
            temp = temp1 * root32;
            _d3210 = temp * f321 * g310;
            _d3222 = temp * f322 * g322;
            temp1 *= aonv;
            temp = 2 * temp1 * root44;
            _d4410 = temp * f441 * g410;
            _d4422 = temp * f442 * g422;
            temp1 *= aonv;
            temp = temp1 * root52;
            _d5220 = temp * f522 * g520;
            _d5232 = temp * f523 * g532;
            temp = 2 * temp1 * root54;
            _d5421 = temp * f542 * g521;
            _d5433 = temp * f543 * g533;
            _xlamo = (mo + nodeo + nodeo - theta - theta) % TwoPi;
            _xfact = mdot + _dmdt + 2 * (nodedot + _dnodt - Rptim) - no;
        }
        else
        {
            double g200 = 1 + emsq * (-2.5 + .8125 * emsq);
            double g310 = 1 + 2 * emsq;
            double g300 = 1 + emsq * (-6 + 6.60937 * emsq);
            double f220 = .75 * (1 + cosim) * (1 + cosim);
            double f311 = .9375 * sinim * sinim * (1 + 3 * cosim) - .75 * (1 + cosim);
            double f330 = 1 + cosim;
            f330 = 1.875 * f330 * f330 * f330;
            double del1 = 3 * nm * nm * aonv * aonv;
            _del2 = 2 * del1 * f220 * g200 * q22;
            _del3 = 3 * del1 * f330 * g300 * q33 * aonv;
            _del1 = del1 * f311 * g310 * q31 * aonv;
            _xlamo = (mo + nodeo + argpo - theta) % TwoPi;
            _xfact = mdot + xpidot - Rptim + _dmdt + _domdt + _dnodt - no;
        }
    }

    /// <summary>dspace: secular lunar-solar rates and resonance integration from the epoch.</summary>
    internal void Secular(double t, ref double em, ref double argpm, ref double inclm,
        ref double mm, ref double nodem, ref double nm)
    {
        double theta = (_gsto + t * Rptim) % TwoPi;
        em += _dedt * t;
        inclm += _didt * t;
        argpm += _domdt * t;
        nodem += _dnodt * t;
        mm += _dmdt * t;
        if (_irez == 0)
            return;

        int checkpoint = (int)(Math.Abs(t) / StepPositive) / CheckpointSteps;
        var initial = checkpoint == 0 ? new ResonancePoint(0, _no, _xlamo)
            : (t > 0 ? _positiveGrid.Value : _negativeGrid.Value)[checkpoint];
        double atime = initial.Time, xni = initial.Motion, xli = initial.Longitude, delt = t > 0 ? StepPositive : StepNegative;
        double ft, xndt, xldot, xnddt;
        while (true)
        {
            ResonanceRates(atime, xni, xli, out xndt, out xldot, out xnddt);

            if (Math.Abs(t - atime) < StepPositive)
            {
                ft = t - atime;
                break;
            }
            xli += xldot * delt + xndt * StepSquared;
            xni += xndt * delt + xnddt * StepSquared;
            atime += delt;
        }

        nm = xni + xndt * ft + xnddt * ft * ft * .5;
        double xl = xli + xldot * ft + xndt * ft * ft * .5;
        mm = _irez != 1 ? xl - 2 * nodem + 2 * theta : xl - nodem - argpm + theta;
    }

    // These immutable checkpoints always come from the epoch grid, never from a query's fractional tail.
    // Lazy publication is thread-safe; at most 44 points per direction cover the public computational limit.
    private ResonancePoint[] BuildResonanceGrid(double step)
    {
        int count = (int)(Sgp4Orbit.MaximumPropagationMinutes / StepPositive) / CheckpointSteps + 1;
        var points = new ResonancePoint[count];
        double atime = 0, xni = _no, xli = _xlamo;
        points[0] = new(atime, xni, xli);
        for (int block = 1; block < count; block++)
        {
            for (int i = 0; i < CheckpointSteps; i++)
            {
                ResonanceRates(atime, xni, xli, out double xndt, out double xldot, out double xnddt);
                xli += xldot * step + xndt * StepSquared;
                xni += xndt * step + xnddt * StepSquared;
                atime += step;
            }
            points[block] = new(atime, xni, xli);
        }
        return points;
    }

    private void ResonanceRates(double atime, double xni, double xli, out double xndt, out double xldot, out double xnddt)
    {
        if (_irez != 2)
        {
            xndt = _del1 * Math.Sin(xli - Fasx2) + _del2 * Math.Sin(2 * (xli - Fasx4)) +
                _del3 * Math.Sin(3 * (xli - Fasx6));
            xldot = xni + _xfact;
            xnddt = _del1 * Math.Cos(xli - Fasx2) + 2 * _del2 * Math.Cos(2 * (xli - Fasx4)) +
                3 * _del3 * Math.Cos(3 * (xli - Fasx6));
            xnddt *= xldot;
        }
        else
        {
            double xomi = _argpo + _argpdot * atime;
            double x2omi = xomi + xomi, x2li = xli + xli;
            xndt = _d2201 * Math.Sin(x2omi + xli - G22) + _d2211 * Math.Sin(xli - G22) +
                _d3210 * Math.Sin(xomi + xli - G32) + _d3222 * Math.Sin(-xomi + xli - G32) +
                _d4410 * Math.Sin(x2omi + x2li - G44) + _d4422 * Math.Sin(x2li - G44) +
                _d5220 * Math.Sin(xomi + xli - G52) + _d5232 * Math.Sin(-xomi + xli - G52) +
                _d5421 * Math.Sin(xomi + x2li - G54) + _d5433 * Math.Sin(-xomi + x2li - G54);
            xldot = xni + _xfact;
            xnddt = _d2201 * Math.Cos(x2omi + xli - G22) + _d2211 * Math.Cos(xli - G22) +
                _d3210 * Math.Cos(xomi + xli - G32) + _d3222 * Math.Cos(-xomi + xli - G32) +
                _d5220 * Math.Cos(xomi + xli - G52) + _d5232 * Math.Cos(-xomi + xli - G52) +
                2 * (_d4410 * Math.Cos(x2omi + x2li - G44) + _d4422 * Math.Cos(x2li - G44) +
                _d5421 * Math.Cos(xomi + x2li - G54) + _d5433 * Math.Cos(-xomi + x2li - G54));
            xnddt *= xldot;
        }
    }

    /// <summary>dpper: lunar-solar periodics, with the Lyddane form below 0.2 rad of inclination.</summary>
    internal void Periodics(double t, ref double ep, ref double inclp, ref double nodep, ref double argpp, ref double mp)
    {
        double zm = _zmos + Zns * t;
        double zf = zm + 2 * Zes * Math.Sin(zm);
        double sinzf = Math.Sin(zf);
        double f2 = .5 * sinzf * sinzf - .25;
        double f3 = -.5 * sinzf * Math.Cos(zf);
        double ses = _se2 * f2 + _se3 * f3;
        double sis = _si2 * f2 + _si3 * f3;
        double sls = _sl2 * f2 + _sl3 * f3 + _sl4 * sinzf;
        double sghs = _sgh2 * f2 + _sgh3 * f3 + _sgh4 * sinzf;
        double shs = _sh2 * f2 + _sh3 * f3;
        zm = _zmol + Znl * t;
        zf = zm + 2 * Zel * Math.Sin(zm);
        sinzf = Math.Sin(zf);
        f2 = .5 * sinzf * sinzf - .25;
        f3 = -.5 * sinzf * Math.Cos(zf);
        double sel = _ee2 * f2 + _e3 * f3;
        double sil = _xi2 * f2 + _xi3 * f3;
        double sll = _xl2 * f2 + _xl3 * f3 + _xl4 * sinzf;
        double sghl = _xgh2 * f2 + _xgh3 * f3 + _xgh4 * sinzf;
        double shll = _xh2 * f2 + _xh3 * f3;
        double pe = ses + sel, pinc = sis + sil, pl = sls + sll, pgh = sghs + sghl, ph = shs + shll;

        inclp += pinc;
        ep += pe;
        double sinip = Math.Sin(inclp), cosip = Math.Cos(inclp);
        if (inclp >= .2)
        {
            ph /= sinip;
            pgh -= cosip * ph;
            argpp += pgh;
            nodep += ph;
            mp += pl;
            return;
        }

        double sinop = Math.Sin(nodep), cosop = Math.Cos(nodep);
        double alfdp = sinip * sinop + (ph * cosop + pinc * cosip * sinop);
        double betdp = sinip * cosop + (-ph * sinop + pinc * cosip * cosop);
        nodep %= TwoPi;
        if (nodep < 0)
            nodep += TwoPi;
        double xls = mp + argpp + pl + pgh + (cosip - pinc * sinip) * nodep;
        double xnoh = nodep;
        nodep = Math.Atan2(alfdp, betdp);
        if (nodep < 0)
            nodep += TwoPi;
        if (Math.Abs(xnoh - nodep) > Math.PI)
            nodep += nodep < xnoh ? TwoPi : -TwoPi;
        mp += pl;
        argpp = xls - mp - cosip * nodep;
    }
}
