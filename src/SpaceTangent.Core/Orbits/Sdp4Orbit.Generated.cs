// SPDX-License-Identifier: GPL-2.0-or-later
// The SDP4 portion of the original NORAD package distributed by T. S. Kelso.
// Upstream snapshot and provenance are documented in docs/SATELLITE_ORBITS.md.
// The scalar operation order is retained for compatibility.
namespace SpaceTangent.Core.Orbits;

internal sealed partial class Sdp4Orbit
{
    private struct DeepArgument
    {
        public double aodp, cosio, sinio, omgdot, xmdot, xnodot, xnodp, eosq;
        public double betao, theta2, sing, cosg, betao2, xll, omgadf, xnode;
        public double em, xinc, xn, t, d2201, d2211, d3210, d3222;
        public double d4410, d4422, d5220, d5232, d5421, d5433, atime, del1;
        public double del2, del3, e3, ee2, omegaq, pe, pgh, ph;
        public double pinc, pl, preep, savtsn, se2, se3, sgh2, sgh3;
        public double sgh4, sh2, sh3, si2, si3, sl2, sl3, sl4;
        public double sse, ssg, ssh, ssi, ssl, thgr, xfact, xgh2;
        public double xgh3, xgh4, xh2, xh3, xi2, xi3, xl2, xl3;
        public double xl4, xlamo, xli, xni, xnq, xqncl, zcosgl, zcoshl;
        public double zcosil, zmol, zmos, zsingl, zsinhl, zsinil;
        public int resonance_flag, synchronous_flag;
    }
    private struct Initialization
    {
        public double coef, coef1, tsi, s4, a3ovk2, eta;
    }
    private static double FMod2p(double x)
    {
       double rval = fmod( x, (3.141592653589793238462643383279502884197*2.0));
       if( rval < 0.0)
          rval += (3.141592653589793238462643383279502884197*2.0);
       return( rval);

    }
    private static void CommonInit(double[] parameters,Tle tle,ref Initialization init,ref DeepArgument deep_arg)
    {
       double
             a1, ao, c2, del1,
             delo, eeta, etasq, perige, pinvsq,
             psisq, qoms24, temp1, temp2, temp3,
             theta4, tsi_squared, x1m5th, xhdot1;
       a1 = Math.Pow(0.074366916133173408/tle.xno,(2.0 / 3.0));
       deep_arg.cosio = Math.Cos(tle.xincl);
       deep_arg.theta2 = deep_arg.cosio*deep_arg.cosio;
       parameters[0] = 3*deep_arg.theta2-1;
       deep_arg.eosq = tle.eo*tle.eo;
       deep_arg.betao2 = 1-deep_arg.eosq;
       deep_arg.betao = Math.Sqrt(deep_arg.betao2);
       del1 = 1.5*5.413079E-4*parameters[0]/(a1*a1*deep_arg.betao*deep_arg.betao2);
       ao = a1*(1-del1*(0.5*(2.0 / 3.0)+del1*(1.0+134.0/81.0*del1)));
       delo = 1.5*5.413079E-4*parameters[0]/(ao*ao*deep_arg.betao*deep_arg.betao2);
       deep_arg.xnodp = tle.xno/(1+delo);
       deep_arg.aodp = ao/(1-delo);
       init.s4 = 1.0122292801892716;
       qoms24 = 1.8802791590152709e-9;
       perige = (deep_arg.aodp*(1-tle.eo)-1.0)*6.378135E3;
       if(perige < 156)
          {
          double temp_val, temp_val_squared;
          if(perige <= 98)
             init.s4 = 20;
          else
             init.s4 = perige-78;
          temp_val = (120.0 - init.s4) * 1.0 / 6.378135E3;
          temp_val_squared = temp_val * temp_val;
          qoms24 = temp_val_squared * temp_val_squared;
          init.s4 = init.s4/6.378135E3 +1.0;
          }
       pinvsq = 1/(deep_arg.aodp*deep_arg.aodp*
                deep_arg.betao2*deep_arg.betao2);
       init.tsi = 1/(deep_arg.aodp-init.s4);
       init.eta = deep_arg.aodp*tle.eo*init.tsi;
       etasq = init.eta*init.eta;
       eeta = tle.eo*init.eta;
       psisq = Math.Abs(1-etasq);
       tsi_squared = init.tsi * init.tsi;
       init.coef = qoms24 * tsi_squared * tsi_squared;
       init.coef1 = init.coef / Math.Pow(psisq,3.5);
       c2 = init.coef1 * deep_arg.xnodp * (deep_arg.aodp*(1+1.5*etasq+eeta*
       (4+etasq))+0.75*5.413079E-4*init.tsi/psisq*parameters[0]*(8+3*etasq*(8+etasq)));
       parameters[2] = tle.bstar*c2;
       deep_arg.sinio = Math.Sin(tle.xincl);
       init.a3ovk2 = - -2.53881E-6/5.413079E-4*1.0*1.0*1.0;
       parameters[1] = 1-deep_arg.theta2;
       parameters[3] = 2*deep_arg.xnodp*init.coef1*deep_arg.aodp*deep_arg.betao2*
            (init.eta*(2+0.5*etasq)+tle.eo*(0.5+2*etasq)-2*5.413079E-4*init.tsi/
            (deep_arg.aodp*psisq)*(-3*parameters[0]*(1-2*eeta+etasq*
            (1.5-0.5*eeta))+0.75*parameters[1]*(2*etasq-eeta*(1+etasq))*
            Math.Cos(2*tle.omegao)));
       theta4 = deep_arg.theta2*deep_arg.theta2;
       temp1 = 3*5.413079E-4*pinvsq*deep_arg.xnodp;
       temp2 = temp1*5.413079E-4*pinvsq;
       temp3 = 1.25*6.2098875E-7*pinvsq*pinvsq*deep_arg.xnodp;
       deep_arg.xmdot = deep_arg.xnodp+0.5*temp1*deep_arg.betao*
                   parameters[0]+0.0625*temp2*deep_arg.betao*
                        (13-78*deep_arg.theta2+137*theta4);
       x1m5th = 1-5*deep_arg.theta2;
       deep_arg.omgdot = -0.5*temp1*x1m5th+0.0625*temp2*
                         (7-114*deep_arg.theta2+395*theta4)+
                    temp3*(3-36*deep_arg.theta2+49*theta4);
       xhdot1 = -temp1*deep_arg.cosio;
       deep_arg.xnodot = xhdot1+(0.5*temp2*(4-19*deep_arg.theta2)+
               2*temp3*(3-7*deep_arg.theta2))*deep_arg.cosio;
       parameters[4] = 3.5*deep_arg.betao2*xhdot1*parameters[2];
       parameters[5] = 1.5*parameters[2];
       parameters[6] = 0.125*init.a3ovk2*deep_arg.sinio*(3+5*deep_arg.cosio)/
               (1+deep_arg.cosio);
       parameters[7] = 0.25*init.a3ovk2*deep_arg.sinio;
       parameters[8] = 7*deep_arg.theta2-1;

    }
    private static void PositionVelocity(double xnode,double a,double e,double[] parameters,double cosio,double sinio,double xincl,double omega,double xl,Span<double> pos,Span<double> vel)
    {
       double axn = e*Math.Cos(omega);
       double temp = 1/(a*(1.0-e*e));
       double xll = temp*parameters[6]*axn;
       double aynl = temp*parameters[7];
       double xlt = xl+xll;
       double ayn = e*Math.Sin(omega)+aynl;
       double elsq = axn*axn+ayn*ayn;
       double capu = FMod2p(xlt-xnode);
       double temp1, temp2, temp3, temp4, temp5, temp6;
       double ecose, esine, pl, r;
       double betal;
       double u, sinu, cosu, sin2u, cos2u;
       double rk, uk, xnodek, xinck;
       double sinuk, cosuk, sinik, cosik, sinnok, cosnok, xmx, xmy;
       double sinepw, cosepw;
       double ux, uy, uz;
       int i;
       if( a <= 0.0 || a * (1.0 - e) <= 0.0 || elsq >= 1.0)
          {
          // Original console diagnostic; zero state is returned below.
          for( i = 0; i < 3; i++)
             {
             pos[i] = 0.0;
             if(!vel.IsEmpty)
                vel[i] = 0.0;
             }
          return;
          }
       i = 0;
       temp2 = capu;
       do
          {
          double epw;
          sinepw = Math.Sin(temp2);
          cosepw = Math.Cos(temp2);
          temp3 = axn*sinepw;
          temp4 = ayn*cosepw;
          temp5 = axn*cosepw;
          temp6 = ayn*sinepw;
          epw = (capu-temp4+temp3-temp2)/(1-temp5-temp6)+temp2;
          if(Math.Abs(epw-temp2) <= 1.0E-6) break;
          temp2 = epw;
          }
       while( i++ < 10 );
       ecose = temp5+temp6;
       esine = temp3-temp4;
       temp = 1-elsq;
       pl = a*temp;
       r = a*(1-ecose);
       temp1 = 1/r;
       temp2 = a*temp1;
       betal = Math.Sqrt(temp);
       temp3 = 1/(1+betal);
       cosu = temp2*(cosepw-axn+ayn*esine*temp3);
       sinu = temp2*(sinepw-ayn-axn*esine*temp3);
       u = Math.Atan2( sinu, cosu);
       sin2u = 2*sinu*cosu;
       cos2u = 2*cosu*cosu-1;
       temp = 1/pl;
       temp1 = 5.413079E-4*temp;
       temp2 = temp1*temp;
       rk = r*(1-1.5*temp2*betal*parameters[0])+0.5*temp1*parameters[1]*cos2u;
       uk = u-0.25*temp2*parameters[8]*sin2u;
       xnodek = xnode+1.5*temp2*cosio*sin2u;
       xinck = xincl+1.5*temp2*cosio*sinio*cos2u;
       sinuk = Math.Sin(uk);
       cosuk = Math.Cos(uk);
       sinik = Math.Sin(xinck);
       cosik = Math.Cos(xinck);
       sinnok = Math.Sin(xnodek);
       cosnok = Math.Cos(xnodek);
       xmx = -sinnok*cosik;
       xmy = cosnok*cosik;
       ux = xmx*sinuk+cosnok*cosuk;
       uy = xmy*sinuk+sinnok*cosuk;
       uz = sinik*sinuk;
       pos[0] = rk*ux*6.378135E3;
       pos[1] = rk*uy*6.378135E3;
       pos[2] = rk*uz*6.378135E3;
       if(!vel.IsEmpty)
          {
          double rdot = 0.074366916133173408*Math.Sqrt(a)*esine/r;
          double rfdot = 0.074366916133173408*Math.Sqrt(pl)/r;
          double xn = 0.074366916133173408/(a * Math.Sqrt(a));
          double rdotk = rdot-xn*temp1*parameters[1]*sin2u;
          double rfdotk = rfdot+xn*temp1*(parameters[1]*cos2u+1.5*parameters[0]);
          double vx = xmx*cosuk-cosnok*sinuk;
          double vy = xmy*cosuk-sinnok*sinuk;
          double vz = sinik*cosuk;
          vel[0] = (rdotk*ux+rfdotk*vx)*6.378135E3;
          vel[1] = (rdotk*uy+rfdotk*vy)*6.378135E3;
          vel[2] = (rdotk*uz+rfdotk*vz)*6.378135E3;
          }

    }
    private static double ThetaG(double jd)
    {
      double omega_E = 1.00273790934;
      double UT = fmod( jd + .5, 1.0);
      double t_cen, GMST, rval;
      t_cen = (jd - UT - 2451545.0) / 36525.0;
      GMST = 24110.54841 + t_cen * (8640184.812866 + t_cen *
                               (0.093104 - t_cen * 6.2E-6));
      GMST = fmod( GMST + 86400.0 * omega_E * UT, 86400.0);
      if( GMST < 0.0)
         GMST += 86400.0;
      rval = (3.141592653589793238462643383279502884197*2.0) * GMST / 86400.0;
      return( rval);

    }
    private static void DeepInit(Tle tle,ref DeepArgument deep_arg)
    {
       double sinq = Math.Sin(tle.xnodeo);
       double cosq = Math.Cos(tle.xnodeo);
       double aqnv = 1/deep_arg.aodp;
       double c1ss = 2.9864797E-6;
       double day = tle.epoch - 2415020.0;
       double zcosi = 0.91744867;
       double zsini = 0.39785416;
       double zsing = -0.98088458;
       double zcosg = 0.1945905;
       double bfact=0, cc = c1ss, se=0;
       double ze = 0.01675, zn = 1.19459E-5;
       double sgh=0,sh=0,si=0;
       double zsinh = sinq, zcosh = cosq;
       double sl=0;
       int iteration;
       deep_arg.thgr = ThetaG( tle.epoch);
       deep_arg.xnq = deep_arg.xnodp;
       deep_arg.xqncl = tle.xincl;
       deep_arg.omegaq = tle.omegao;
          {
          double xnodce = 4.5236020 - 9.2422029E-4 * day;
          double stem = Math.Sin(xnodce);
          double ctem = Math.Cos(xnodce);
          double c_minus_gam = 0.228027132 * day - 1.1151842;
          double gam = 5.8351514 + 0.0019443680 * day;
          double zx, zy;
          deep_arg.preep = day;
          deep_arg.zcosil = 0.91375164 - 0.03568096 * ctem;
          deep_arg.zsinil = Math.Sqrt(1.0 - deep_arg.zcosil * deep_arg.zcosil);
          deep_arg.zsinhl = 0.089683511 * stem / deep_arg.zsinil;
          deep_arg.zcoshl = Math.Sqrt(1.0 - deep_arg.zsinhl*deep_arg.zsinhl);
          deep_arg.zmol = FMod2p( c_minus_gam);
          zx = 0.39785416 * stem / deep_arg.zsinil;
          zy = deep_arg.zcoshl * ctem + 0.91744867 * deep_arg.zsinhl * stem;
          zx = Math.Atan2( zx, zy) + gam - xnodce;
          deep_arg.zcosgl = Math.Cos( zx);
          deep_arg.zsingl = Math.Sin( zx);
          deep_arg.zmos = FMod2p( 6.2565837 + 0.017201977 * day);
          }
       deep_arg.savtsn = 1E20;
       for( iteration = 0; iteration < 2; iteration++)
          {
          double c1l = 4.7968065E-7;
          double a1 = zcosg * zcosh + zsing * zcosi * zsinh;
          double a3 = -zsing * zcosh + zcosg * zcosi * zsinh;
          double a7 = -zcosg * zsinh + zsing * zcosi * zcosh;
          double a8 = zsing * zsini;
          double a9 = zsing * zsinh + zcosg * zcosi * zcosh;
          double a10 = zcosg * zsini;
          double a2 = deep_arg.cosio * a7 + deep_arg.sinio * a8;
          double a4 = deep_arg.cosio * a9 + deep_arg.sinio * a10;
          double a5 = -deep_arg.sinio * a7 + deep_arg.cosio * a8;
          double a6 = -deep_arg.sinio * a9 + deep_arg.cosio * a10;
          double x1 = a1 * deep_arg.cosg + a2 * deep_arg.sing;
          double x2 = a3 * deep_arg.cosg + a4 * deep_arg.sing;
          double x3 = -a1 * deep_arg.sing + a2 * deep_arg.cosg;
          double x4 = -a3 * deep_arg.sing + a4 * deep_arg.cosg;
          double x5 = a5 * deep_arg.sing;
          double x6 = a6 * deep_arg.sing;
          double x7 = a5 * deep_arg.cosg;
          double x8 = a6 * deep_arg.cosg;
          double z31 = 12 * x1 * x1 - 3 * x3 * x3;
          double z32 = 24 * x1 * x2 - 6 * x3 * x4;
          double z33 = 12 * x2 * x2 - 3 * x4 * x4;
          double z11 = -6 * a1 * a5 + deep_arg.eosq * (-24 * x1 * x7 - 6 * x3 * x5);
          double z12 = -6 * (a1 * a6 + a3 * a5) + deep_arg.eosq *
                    (-24 * (x2 * x7 + x1 * x8) - 6 * (x3 * x6 + x4 * x5));
          double z13 = -6 * a3 * a6 + deep_arg.eosq * (-24 * x2 * x8 - 6 * x4 * x6);
          double z21 = 6 * a2 * a5 + deep_arg.eosq * (24 * x1 * x5 - 6 * x3 * x7);
          double z22 = 6 * (a4 * a5 + a2 * a6) + deep_arg.eosq *
                    (24 * (x2 * x5 + x1 * x6) - 6 * (x4 * x7 + x3 * x8));
          double z23 = 6 * a4 * a6 + deep_arg.eosq * (24 * x2 * x6 - 6 * x4 * x8);
          double s3 = cc / deep_arg.xnq;
          double s2 = -0.5 * s3 / deep_arg.betao;
          double s4 = s3 * deep_arg.betao;
          double s1 = -15 * tle.eo * s4;
          double s5 = x1 * x3 + x2 * x4;
          double s6 = x2 * x3 + x1 * x4;
          double s7 = x2 * x4 - x1 * x3;
          double z1 = 3 * (a1 * a1 + a2 * a2) + z31 * deep_arg.eosq;
          double z2 = 6 * (a1 * a3 + a2 * a4) + z32 * deep_arg.eosq;
          double z3 = 3 * (a3 * a3 + a4 * a4) + z33 * deep_arg.eosq;
          z1 = z1 + z1 + deep_arg.betao2 * z31;
          z2 = z2 + z2 + deep_arg.betao2 * z32;
          z3 = z3 + z3 + deep_arg.betao2 * z33;
          se = s1*zn*s5;
          si = s2*zn*(z11+z13);
          sl = -zn*s3*(z1+z3-14-6*deep_arg.eosq);
          sgh = s4*zn*(z31+z33-6);
          if (deep_arg.xqncl < 5.2359877E-2)
             sh = 0;
          else
             sh = -zn*s2*(z21+z23);
          deep_arg.ee2 = 2*s1*s6;
          deep_arg.e3 = 2*s1*s7;
          deep_arg.xi2 = 2*s2*z12;
          deep_arg.xi3 = 2*s2*(z13-z11);
          deep_arg.xl2 = -2*s3*z2;
          deep_arg.xl3 = -2*s3*(z3-z1);
          deep_arg.xl4 = -2*s3*(-21-9*deep_arg.eosq)*ze;
          deep_arg.xgh2 = 2*s4*z32;
          deep_arg.xgh3 = 2*s4*(z33-z31);
          deep_arg.xgh4 = -18*s4*ze;
          deep_arg.xh2 = -2*s2*z22;
          deep_arg.xh3 = -2*s2*(z23-z21);
          if(iteration==0)
             {
             deep_arg.sse = se;
             deep_arg.ssi = si;
             deep_arg.ssl = sl;
             deep_arg.ssh = sh/deep_arg.sinio;
             deep_arg.ssg = sgh-deep_arg.cosio*deep_arg.ssh;
             deep_arg.se2 = deep_arg.ee2;
             deep_arg.si2 = deep_arg.xi2;
             deep_arg.sl2 = deep_arg.xl2;
             deep_arg.sgh2 = deep_arg.xgh2;
             deep_arg.sh2 = deep_arg.xh2;
             deep_arg.se3 = deep_arg.e3;
             deep_arg.si3 = deep_arg.xi3;
             deep_arg.sl3 = deep_arg.xl3;
             deep_arg.sgh3 = deep_arg.xgh3;
             deep_arg.sh3 = deep_arg.xh3;
             deep_arg.sl4 = deep_arg.xl4;
             deep_arg.sgh4 = deep_arg.xgh4;
             zcosg = deep_arg.zcosgl;
             zsing = deep_arg.zsingl;
             zcosi = deep_arg.zcosil;
             zsini = deep_arg.zsinil;
             zcosh = deep_arg.zcoshl*cosq+deep_arg.zsinhl*sinq;
             zsinh = sinq*deep_arg.zcoshl-cosq*deep_arg.zsinhl;
             zn = 1.5835218E-4;
             cc = c1l;
             ze = 0.05490;
             }
          }
       deep_arg.sse += se;
       deep_arg.ssi += si;
       deep_arg.ssl += sl;
       deep_arg.ssg += sgh - deep_arg.cosio / deep_arg.sinio * sh;
       deep_arg.ssh += sh / deep_arg.sinio;
       if( deep_arg.xnq >= 0.00826 && deep_arg.xnq <= 0.00924 && tle.eo >= .5)
          {
          double root22 = 1.7891679E-6;
          double root32 = 3.7393792E-7;
          double root44 = 7.3636953E-9;
          double root52 = 1.1428639E-7;
          double root54 = 2.1765803E-9;
          double g201 = -0.306 - (tle.eo - 0.64) * 0.440;
          double eoc = tle.eo * deep_arg.eosq;
          double sini2 = deep_arg.sinio*deep_arg.sinio;
          double f220 = 0.75*(1+2*deep_arg.cosio+deep_arg.theta2);
          double f221 = 1.5 * sini2;
          double f321 = 1.875 * deep_arg.sinio * (1 - 2 * deep_arg.cosio - 3 * deep_arg.theta2);
          double f322 = -1.875*deep_arg.sinio*(1+2*
                   deep_arg.cosio-3*deep_arg.theta2);
          double f441 = 35 * sini2 * f220;
          double f442 = 39.3750 * sini2 * sini2;
          double f522 = 9.84375*deep_arg.sinio*(sini2*(1-2*deep_arg.cosio-5*
                     deep_arg.theta2)+0.33333333*(-2+4*deep_arg.cosio+
                     6*deep_arg.theta2));
          double f523 = deep_arg.sinio*(4.92187512*sini2*(-2-4*
                     deep_arg.cosio+10*deep_arg.theta2)+6.56250012
                     *(1+2*deep_arg.cosio-3*deep_arg.theta2));
          double f542 = 29.53125*deep_arg.sinio*(2-8*
                     deep_arg.cosio+deep_arg.theta2*
                     (-12+8*deep_arg.cosio+10*deep_arg.theta2));
          double f543 = 29.53125*deep_arg.sinio*(-2-8*deep_arg.cosio+
                     deep_arg.theta2*(12+8*deep_arg.cosio-10*
                     deep_arg.theta2));
          double g410, g422, g520, g521, g532, g533;
          double g211, g310, g322;
          double temp, temp1;
          deep_arg.resonance_flag = 1;
          deep_arg.synchronous_flag = 0;
          if (tle.eo <= 0.65)
             {
             g211 = 3.616-13.247*tle.eo+16.290*deep_arg.eosq;
             g310 = -19.302+117.390*tle.eo-228.419*
                        deep_arg.eosq+156.591*eoc;
             g322 = -18.9068+109.7927*tle.eo-214.6334*
                        deep_arg.eosq+146.5816*eoc;
             g410 = -41.122+242.694*tle.eo-471.094*
                        deep_arg.eosq+313.953*eoc;
             g422 = -146.407+841.880*tle.eo-1629.014*
                        deep_arg.eosq+1083.435*eoc;
             g520 = -532.114+3017.977*tle.eo-5740*
                        deep_arg.eosq+3708.276*eoc;
             }
          else
             {
             g211 = -72.099+331.819*tle.eo-508.738*
                        deep_arg.eosq+266.724*eoc;
             g310 = -346.844+1582.851*tle.eo-2415.925*
                        deep_arg.eosq+1246.113*eoc;
             g322 = -342.585+1554.908*tle.eo-2366.899*
                        deep_arg.eosq+1215.972*eoc;
             g410 = -1052.797+4758.686*tle.eo-7193.992*
                        deep_arg.eosq+3651.957*eoc;
             g422 = -3581.69+16178.11*tle.eo-24462.77*
                        deep_arg.eosq+ 12422.52*eoc;
             if (tle.eo <= 0.715)
                g520 = 1464.74-4664.75*tle.eo+3763.64*deep_arg.eosq;
             else
                g520 = -5149.66+29936.92*tle.eo-54087.36*
                             deep_arg.eosq+31324.56*eoc;
             }
          if (tle.eo < 0.7)
             {
             g533 = -919.2277+4988.61*tle.eo-9064.77*
                        deep_arg.eosq+5542.21*eoc;
             g521 = -822.71072+4568.6173*tle.eo-8491.4146*
                        deep_arg.eosq+5337.524*eoc;
             g532 = -853.666+4690.25*tle.eo-8624.77*
                        deep_arg.eosq+ 5341.4*eoc;
             }
          else
             {
             g533 = -37995.78+161616.52*tle.eo-229838.2*
                        deep_arg.eosq+109377.94*eoc;
             g521 = -51752.104+218913.95*tle.eo-309468.16*
                        deep_arg.eosq+146349.42*eoc;
             g532 = -40023.88+170470.89*tle.eo-242699.48*
                        deep_arg.eosq+115605.82*eoc;
             }
          temp1 = 3 * deep_arg.xnq * deep_arg.xnq * aqnv * aqnv;
          temp = temp1*root22;
          deep_arg.d2201 = temp * f220 * g201;
          deep_arg.d2211 = temp * f221 * g211;
          temp1 *= aqnv;
          temp = temp1*root32;
          deep_arg.d3210 = temp * f321 * g310;
          deep_arg.d3222 = temp * f322 * g322;
          temp1 *= aqnv;
          temp = 2*temp1*root44;
          deep_arg.d4410 = temp * f441 * g410;
          deep_arg.d4422 = temp * f442 * g422;
          temp1 *= aqnv;
          temp = temp1*root52;
          deep_arg.d5220 = temp * f522 * g520;
          deep_arg.d5232 = temp * f523 * g532;
          temp = 2*temp1*root54;
          deep_arg.d5421 = temp * f542 * g521;
          deep_arg.d5433 = temp * f543 * g533;
          deep_arg.xlamo = tle.xmo+tle.xnodeo+tle.xnodeo-deep_arg.thgr-deep_arg.thgr;
          bfact = deep_arg.xmdot + deep_arg.xnodot+
                       deep_arg.xnodot - 4.3752691E-3 - 4.3752691E-3;
          bfact += deep_arg.ssl + deep_arg.ssh + deep_arg.ssh;
          }
       else if( (deep_arg.xnq < 0.0052359877) && (deep_arg.xnq > 0.0034906585))
          {
          double q22 = 1.7891679E-6;
          double q31 = 2.1460748E-6;
          double q33 = 2.2123015E-7;
          double cosio_plus_1 = 1.0 + deep_arg.cosio;
          double g200 = 1+deep_arg.eosq*(-2.5+0.8125*deep_arg.eosq);
          double g300 = 1+deep_arg.eosq*(-6+6.60937*deep_arg.eosq);
          double f311 = 0.9375*deep_arg.sinio*deep_arg.sinio*
                 (1+3*deep_arg.cosio)-0.75*cosio_plus_1;
          double g310 = 1+2*deep_arg.eosq;
          double f220 = 0.75 * cosio_plus_1 * cosio_plus_1;
          double f330 = 2.5 * f220 * cosio_plus_1;
          deep_arg.resonance_flag = deep_arg.synchronous_flag = 1;
          deep_arg.del1 = 3*deep_arg.xnq*deep_arg.xnq*aqnv*aqnv;
          deep_arg.del2 = 2*deep_arg.del1*f220*g200*q22;
          deep_arg.del3 = 3*deep_arg.del1*f330*g300*q33*aqnv;
          deep_arg.del1 = deep_arg.del1*f311*g310*q31*aqnv;
          deep_arg.xlamo = tle.xmo+tle.xnodeo+tle.omegao-deep_arg.thgr;
          bfact = deep_arg.xmdot + deep_arg.omgdot + deep_arg.xnodot - 4.3752691E-3;
          bfact = bfact+deep_arg.ssl+deep_arg.ssg+deep_arg.ssh;
          }
       else
          deep_arg.resonance_flag = deep_arg.synchronous_flag = 0;
       if(deep_arg.resonance_flag!=0)
          {
          deep_arg.xfact = bfact-deep_arg.xnq;
          deep_arg.xli = deep_arg.xlamo;
          deep_arg.xni = deep_arg.xnq;
          deep_arg.atime = 0;
          }

    }
    private static void DeepSec(Tle tle,ref DeepArgument deep_arg)
    {
       double delt, temp;
       int n_steps;
       deep_arg.xll += deep_arg.ssl*deep_arg.t;
       deep_arg.omgadf += deep_arg.ssg*deep_arg.t;
       deep_arg.xnode += deep_arg.ssh*deep_arg.t;
       deep_arg.em = tle.eo+deep_arg.sse*deep_arg.t;
       deep_arg.xinc = tle.xincl+deep_arg.ssi*deep_arg.t;
       if (deep_arg.xinc < 0)
          {
          deep_arg.xinc = -deep_arg.xinc;
          deep_arg.xnode = deep_arg.xnode + 3.141592653589793238462643383279502884197;
          deep_arg.omgadf = deep_arg.omgadf-3.141592653589793238462643383279502884197;
          }
       if(deep_arg.resonance_flag==0) return;
       if( Math.Abs( deep_arg.t) < Math.Abs( deep_arg.t - deep_arg.atime))
          {
          deep_arg.atime = 0;
          deep_arg.xni = deep_arg.xnq;
          deep_arg.xli = deep_arg.xlamo;
          }
       n_steps = (int)Math.Ceiling( Math.Abs( deep_arg.t - deep_arg.atime) / 720.0);
       if(n_steps!=0)
          delt = (deep_arg.t - deep_arg.atime) / (double)n_steps;
       else
          delt = 0.0;
       while(n_steps-- !=0)
          {
          double sin_li = Math.Sin( deep_arg.xli);
          double cos_li = Math.Cos( deep_arg.xli);
          double sin_2li = 2.0 * sin_li * cos_li;
          double cos_2li = 2.0 * cos_li * cos_li - 1.0;
          double xldot, xnddt, xndot;
          if(deep_arg.synchronous_flag!=0)
             {
             double c_fasx2 = 0.99139134268488593;
             double s_fasx2 = 0.13093206501640101;
             double c_2fasx4 = 0.87051638752972937;
             double s_2fasx4 = -0.49213943048915526;
             double c_3fasx6 = 0.43258117585763334;
             double s_3fasx6 = 0.90159499016666422;
             double sin_3li = sin_2li * cos_li + cos_2li * sin_li;
             double cos_3li = cos_2li * cos_li - sin_2li * sin_li;
             xndot = deep_arg.del1 * (sin_li * c_fasx2 - cos_li * s_fasx2)
                   + deep_arg.del2 * (sin_2li * c_2fasx4 - cos_2li * s_2fasx4)
                   + deep_arg.del3 * (sin_3li * c_3fasx6 - cos_3li * s_3fasx6);
             xnddt = deep_arg.del1 * (cos_li * c_fasx2 + sin_li * s_fasx2)
               + 2 * deep_arg.del2 * (cos_2li * c_2fasx4 + sin_2li * s_2fasx4)
               + 3 * deep_arg.del3 * (cos_3li * c_3fasx6 + sin_3li * s_3fasx6);
             }
          else
             {
             double c_g22 = 0.87051638752972937;
             double s_g22 = -0.49213943048915526;
             double c_g32 = 0.57972190187001149;
             double s_g32 = 0.81481440616389245;
             double c_g44 = -0.22866241528815548;
             double s_g44 = 0.97350577801807991;
             double c_g52 = 0.49684831179884198;
             double s_g52 = 0.86783740128127729;
             double c_g54 = -0.29695209575316894;
             double s_g54 = -0.95489237761529999;
             double xomi =
                       deep_arg.omegaq + deep_arg.omgdot * deep_arg.atime;
             double sin_omi = Math.Sin( xomi), cos_omi = Math.Cos( xomi);
             double sin_li_m_omi = sin_li * cos_omi - sin_omi * cos_li;
             double sin_li_p_omi = sin_li * cos_omi + sin_omi * cos_li;
             double cos_li_m_omi = cos_li * cos_omi + sin_omi * sin_li;
             double cos_li_p_omi = cos_li * cos_omi - sin_omi * sin_li;
             double sin_2omi = 2.0 * sin_omi * cos_omi;
             double cos_2omi = 2.0 * cos_omi * cos_omi - 1.0;
             double sin_2li_m_omi = sin_2li * cos_omi - sin_omi * cos_2li;
             double sin_2li_p_omi = sin_2li * cos_omi + sin_omi * cos_2li;
             double cos_2li_m_omi = cos_2li * cos_omi + sin_omi * sin_2li;
             double cos_2li_p_omi = cos_2li * cos_omi - sin_omi * sin_2li;
             double sin_2li_p_2omi = sin_2li * cos_2omi + sin_2omi * cos_2li;
             double cos_2li_p_2omi = cos_2li * cos_2omi - sin_2omi * sin_2li;
             double sin_2omi_p_li = sin_li * cos_2omi + sin_2omi * cos_li;
             double cos_2omi_p_li = cos_li * cos_2omi - sin_2omi * sin_li;
             xndot = deep_arg.d2201 * (sin_2omi_p_li*c_g22 - cos_2omi_p_li*s_g22)
                   + deep_arg.d2211 * (sin_li * c_g22 - cos_li * s_g22)
                   + deep_arg.d3210 * (sin_li_p_omi*c_g32 - cos_li_p_omi*s_g32)
                   + deep_arg.d3222 * (sin_li_m_omi*c_g32 - cos_li_m_omi*s_g32)
                   + deep_arg.d4410 * (sin_2li_p_2omi*c_g44 - cos_2li_p_2omi*s_g44)
                   + deep_arg.d4422 * (sin_2li * c_g44 - cos_2li * s_g44)
                   + deep_arg.d5220 * (sin_li_p_omi*c_g52 - cos_li_p_omi*s_g52)
                   + deep_arg.d5232 * (sin_li_m_omi*c_g52 - cos_li_m_omi*s_g52)
                   + deep_arg.d5421 * (sin_2li_p_omi*c_g54 - cos_2li_p_omi*s_g54)
                   + deep_arg.d5433 * (sin_2li_m_omi*c_g54 - cos_2li_m_omi*s_g54);
             xnddt = deep_arg.d2201 * (cos_2omi_p_li*c_g22 + sin_2omi_p_li*s_g22)
                   + deep_arg.d2211 * (cos_li * c_g22 + sin_li * s_g22)
                   + deep_arg.d3210 * (cos_li_p_omi*c_g32 + sin_li_p_omi*s_g32)
                   + deep_arg.d3222 * (cos_li_m_omi*c_g32 + sin_li_m_omi*s_g32)
                   + deep_arg.d5220 * (cos_li_p_omi*c_g52 + sin_li_p_omi*s_g52)
                   + deep_arg.d5232 * (cos_li_m_omi*c_g52 + sin_li_m_omi*s_g52)
                + 2*(deep_arg.d4410 * (cos_2li_p_2omi*c_g44 + sin_2li_p_2omi*s_g44)
                   + deep_arg.d4422 * (cos_2li * c_g44 + sin_2li * s_g44)
                   + deep_arg.d5421 * (cos_2li_p_omi*c_g54 + sin_2li_p_omi*s_g54)
                   + deep_arg.d5433 * (cos_2li_m_omi*c_g54 + sin_2li_m_omi*s_g54));
             }
          xldot = deep_arg.xni+deep_arg.xfact;
          xnddt *= xldot;
          deep_arg.xli += delt * (xldot + xndot * delt / 2.0);
          deep_arg.xni += delt * (xndot + xnddt * delt / 2.0);
          deep_arg.atime += delt;
          }
       deep_arg.xn = deep_arg.xni;
       temp = -deep_arg.xnode + deep_arg.thgr + deep_arg.t * 4.3752691E-3;
       deep_arg.xll = deep_arg.xli + temp
             + (deep_arg.synchronous_flag!=0 ? -deep_arg.omgadf : temp);

    }
    private static void DeepPer(ref DeepArgument deep_arg)
    {
          if (Math.Abs(deep_arg.savtsn-deep_arg.t) >= 30.0)
             {
             double zf, zm, sinzf, ses, sis, sil, sel, sll, sls;
             double f2, f3, sghl, sghs, shs, sh1;
             deep_arg.savtsn = deep_arg.t;
             zm = deep_arg.zmos+1.19459E-5*deep_arg.t;
             zf = zm+2*0.01675*Math.Sin(zm);
             sinzf = Math.Sin(zf);
             f2 = 0.5*sinzf*sinzf-0.25;
             f3 = -0.5*sinzf*Math.Cos(zf);
             ses = deep_arg.se2*f2+deep_arg.se3*f3;
             sis = deep_arg.si2*f2+deep_arg.si3*f3;
             sls = deep_arg.sl2*f2+deep_arg.sl3*f3+deep_arg.sl4*sinzf;
             sghs = deep_arg.sgh2*f2+deep_arg.sgh3*f3+deep_arg.sgh4*sinzf;
             shs = deep_arg.sh2*f2+deep_arg.sh3*f3;
             zm = deep_arg.zmol+1.5835218E-4*deep_arg.t;
             zf = zm+2*0.05490*Math.Sin(zm);
             sinzf = Math.Sin(zf);
             f2 = 0.5*sinzf*sinzf-0.25;
             f3 = -0.5*sinzf*Math.Cos(zf);
             sel = deep_arg.ee2*f2+deep_arg.e3*f3;
             sil = deep_arg.xi2*f2+deep_arg.xi3*f3;
             sll = deep_arg.xl2*f2+deep_arg.xl3*f3+deep_arg.xl4*sinzf;
             sghl = deep_arg.xgh2*f2+deep_arg.xgh3*f3+deep_arg.xgh4*sinzf;
             sh1 = deep_arg.xh2*f2+deep_arg.xh3*f3;
             deep_arg.pe = ses+sel;
             deep_arg.pinc = sis+sil;
             deep_arg.pl = sls+sll;
             deep_arg.pgh = sghs+sghl;
             deep_arg.ph = shs+sh1;
             }
          deep_arg.xinc += deep_arg.pinc;
          deep_arg.em += deep_arg.pe;
          if (deep_arg.xqncl >= 0.2)
             {
             double temp_val = deep_arg.ph / deep_arg.sinio;
             deep_arg.omgadf += deep_arg.pgh - deep_arg.cosio * temp_val;
             deep_arg.xnode += temp_val;
             deep_arg.xll += deep_arg.pl;
             }
          else
             {
             double sinok = Math.Sin(deep_arg.xnode);
             double cosok = Math.Cos(deep_arg.xnode);
             double sinis = Math.Sin(deep_arg.xinc);
             double cosis = Math.Cos(deep_arg.xinc);
             double alfdp = deep_arg.ph * cosok
                        + (deep_arg.pinc * cosis + sinis) * sinok;
             double betdp = - deep_arg.ph * sinok
                        + (deep_arg.pinc * cosis + sinis) * cosok;
             double xls, xnoh;
             deep_arg.xnode = FMod2p(deep_arg.xnode);
             xls = deep_arg.xll + deep_arg.omgadf + cosis * deep_arg.xnode;
             xls += deep_arg.pl + deep_arg.pgh
                                - deep_arg.pinc * deep_arg.xnode * sinis;
             xnoh = deep_arg.xnode;
             deep_arg.xnode = Math.Atan2(alfdp,betdp);
             if( deep_arg.xnode < xnoh - 3.141592653589793238462643383279502884197)
                deep_arg.xnode += (3.141592653589793238462643383279502884197*2.0);
             else if( deep_arg.xnode > xnoh + 3.141592653589793238462643383279502884197)
                deep_arg.xnode -= (3.141592653589793238462643383279502884197*2.0);
             deep_arg.xll += deep_arg.pl;
             deep_arg.omgadf = xls - deep_arg.xll - Math.Cos(deep_arg.xinc)*
                                 deep_arg.xnode;
             }

    }
    private static void SdpInit(double[] parameters,Tle tle,ref DeepArgument deep_arg)
    {
       Initialization init=default;
       CommonInit(parameters,tle,ref init,ref deep_arg);
       deep_arg.sing = Math.Sin(tle.omegao);
       deep_arg.cosg = Math.Cos(tle.omegao);
       DeepInit(tle,ref deep_arg);

    }
    private static void Sdp(double tsince,Tle tle,double[] parameters,ref DeepArgument deep_arg,Span<double> pos,Span<double> vel)
    {
      double
          a, tempa, tempe, templ, tsq,
          xl, xmam, xmdf, xnoddf;
      xmdf = tle.xmo+deep_arg.xmdot*tsince;
      deep_arg.omgadf = tle.omegao+deep_arg.omgdot*tsince;
      xnoddf = tle.xnodeo+deep_arg.xnodot*tsince;
      tsq = tsince*tsince;
      deep_arg.xnode = xnoddf+parameters[4]*tsq;
      tempa = 1-parameters[2]*tsince;
      tempe = tle.bstar*parameters[3]*tsince;
      templ = parameters[5]*tsq;
      deep_arg.xn = deep_arg.xnodp;
      deep_arg.xll = xmdf;
      deep_arg.t = tsince;
      DeepSec(tle,ref deep_arg);
      xmdf = deep_arg.xll;
      a = Math.Pow(0.074366916133173408/deep_arg.xn,(2.0 / 3.0))*tempa*tempa;
      deep_arg.em = deep_arg.em-tempe;
      xmam = xmdf+deep_arg.xnodp*templ;
      deep_arg.xll = xmam;
      DeepPer(ref deep_arg);
      xmam = deep_arg.xll;
      xl = xmam + deep_arg.omgadf + deep_arg.xnode;
      PositionVelocity( deep_arg.xnode, a, deep_arg.em, parameters, deep_arg.cosio,
                    deep_arg.sinio, deep_arg.xinc, deep_arg.omgadf,
                    xl, pos, vel);

    }
}
