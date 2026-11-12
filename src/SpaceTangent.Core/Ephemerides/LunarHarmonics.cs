// SPDX-License-Identifier: GPL-2.0-or-later
// The historical lunar theory uses the original 125 coefficients and binary64 constants.
namespace SpaceTangent.Core.Ephemerides;

internal static class LunarHarmonics
{
    private static LunarDual D(ulong bits) => new(BitConverter.Int64BitsToDouble(unchecked((long)bits)), 0);
    private static LunarDual Rad(LunarDual x) => x / D(0x4066800000000000UL) * D(0x400921fb54442d18UL);
    private static LunarDual Sin(LunarDual x) => LunarDual.Sin(x);
    private static LunarDual Cos(LunarDual x) => LunarDual.Cos(x);
    private static LunarDual Trunc(LunarDual x) => LunarDual.Trunc(x);
    private static LunarDual Pfmod(LunarDual x, LunarDual y) => LunarDual.Pfmod(x,y);

    internal static void Phase(double days1900, double daysJ2000, Span<LunarDual> phase)
    {
        var jd19 = new LunarDual(days1900,1);
        var t = (jd19 / D(0x40e1d5a000000000UL));
        var t2 = (t * t);
        var m1 = (jd19 / D(0x403b525334db53f9UL));
        m1 = (D(0x4076800000000000UL) * (m1 - Trunc(m1)));
        var m2 = (jd19 / D(0x4076d4277d01b35eUL));
        m2 = (D(0x4076800000000000UL) * (m2 - Trunc(m2)));
        var m3 = (jd19 / D(0x403b8df70ce7441cUL));
        m3 = (D(0x4076800000000000UL) * (m3 - Trunc(m3)));
        var m4 = (jd19 / D(0x403d87d4a8e43a52UL));
        m4 = (D(0x4076800000000000UL) * (m4 - Trunc(m4)));
        var m5 = (jd19 / D(0x403b365413529828UL));
        m5 = (D(0x4076800000000000UL) * (m5 - Trunc(m5)));
        var m6 = (jd19 / D(0x40ba8e5d01b00368UL));
        m6 = (D(0x4076800000000000UL) * (m6 - Trunc(m6)));
        var ld = ((D(0x4070e6f255f3519cUL) + m1) - ((D(0x3f5290257c914b16UL) - (D(0x3ebfe07017c01026UL) * t)) * t2));
        var ms = ((D(0x4076679d031055b9UL) + m2) - ((D(0x3f23a92a30553261UL) + (D(0x3ecbaeb22f9294c3UL) * t)) * t2));
        var md = ((D(0x407281ac79702e66UL) + m3) + ((D(0x3f82d3415b1422cdUL) + (D(0x3eee32f0ee144531UL) * t)) * t2));
        var de = ((D(0x4075ebccbe1eb420UL) + m4) - ((D(0x3f578705425f2021UL) - (D(0x3ebfe07017c01026UL) * t)) * t2));
        var f = ((D(0x4026807485e3da30UL) + m5) - ((D(0x3f6a4df47f993d53UL) + (D(0x3e9421f5f40d8376UL) * t)) * t2));
        var n = ((D(0x407032eeb1c432caUL) - m6) + ((D(0x3f6105e1c15097c8UL) + (D(0x3ef711947cfa26a2UL) * t)) * t2));
        var a = Rad((D(0x404999999999999aUL) + (D(0x4034333333333333UL) * t)));
        var sa = Sin(a);
        var sn = Sin(Rad(n));
        var b = (D(0x4075a8f5c28f5c29UL) + ((D(0x40609bd70a3d70a4UL) - (D(0x3f82c958a4060426UL) * t)) * t));
        var sb = (D(0x3f703c8e25c810a5UL) * Sin(Rad(b)));
        var c = Rad(((n + D(0x407130cccccccccdUL)) - (D(0x4002666666666666UL) * t)));
        var sc = Sin(c);
        ld = (((ld + (D(0x3f2e8a2ec28b2a6bUL) * sa)) + sb) + (D(0x3f6016ce789e774fUL) * sn));
        ms = (ms - (D(0x3f5d2178f68be2f8UL) * sa));
        md = (((md + (D(0x3f4ac57e23f24d90UL) * sa)) + sb) + (D(0x3f64d0dcfcc5b8dcUL) * sn));
        f = (((f + sb) - (D(0x3f994898f605ab3bUL) * sn)) - (D(0x3f71ba3ca7503b82UL) * sc));
        de = (((de + (D(0x3f60795f676ea423UL) * sa)) + sb) + (D(0x3f6016ce789e774fUL) * sn));
        var e = (D(0x3ff0000000000000UL) - ((D(0x3f647064ece9a2c6UL) + (D(0x3edf8a89dc374df5UL) * t)) * t));
        var e2 = (e * e);
        ld = Rad(ld);
        ms = Rad(ms);
        n = Rad(n);
        de = Rad(de);
        f = Rad(f);
        md = Rad(md);
        var (zeta,z,theta) = Precession(daysJ2000);
        var epsFixed1900 = Fixed1900Obliquity();
        phase[0]=ld; phase[1]=ms; phase[2]=md; phase[3]=de; phase[4]=f; phase[5]=n; phase[6]=c;
        phase[7]=e; phase[8]=e2; phase[9]=zeta; phase[10]=z; phase[11]=theta; phase[12]=epsFixed1900;
    }

    private static (LunarDual,LunarDual,LunarDual) Precession(double daysJ2000)
    {
        var T = new LunarDual(daysJ2000,1) / D(0x40e1d5a000000000UL);
        var t = -T; // actual EpochConvert(jd,J2000): retain t=-T.
        var zeta = (((((D(0x40a2046faacd9e84UL) + (D(0x3ff6584f4c6e6d9cUL) * T)) - ((D(0x3f223810e8858ff7UL) * T) * T)) * t) + (((D(0x3fd352007dd44135UL) - (D(0x3f368b5cbff47736UL) * T)) * t) * t)) + (((D(0x3f926e115592d98cUL) * t) * t) * t));
        var z = (((((D(0x40a2046faacd9e84UL) + (D(0x3ff6584f4c6e6d9cUL) * T)) - ((D(0x3f223810e8858ff7UL) * T) * T)) * t) + (((D(0x3ff183cf2cf95d4fUL) + (D(0x3f114d2f5dbb9cfaUL) * T)) * t) * t)) + (((D(0x3f92a3cea6c1a049UL) * t) * t) * t));
        var theta = (((((D(0x409f513e5c91d14eUL) - (D(0x3feb4e3bcd35a858UL) * T)) - ((D(0x3f2c714fce746cb9UL) * T) * T)) * t) - (((D(0x3fdb4e3bcd35a858UL) + (D(0x3f2c714fce746cb9UL) * T)) * t) * t)) - (((D(0x3fa56b228dc981bfUL) * t) * t) * t));
        zeta = Rad((zeta / D(0x40ac200000000000UL)));
        z = Rad((z / D(0x40ac200000000000UL)));
        theta = Rad((theta / D(0x40ac200000000000UL)));
        return (zeta,z,theta);
    }

    private static LunarDual Fixed1900Obliquity()
    {
        var t = new LunarDual(0,0);
        var t2 = (t * t);
        var a = (D(0x40590022fe31c9a7UL) * t);
        var b = (D(0x4076800000000000UL) * (a - Trunc(a)));
        var ls = ((D(0x40717b26e978d4feUL) + (D(0x3f33db7f1737542aUL) * t2)) + b);
        a = (D(0x4094e36bc1acde1aUL) * t);
        b = (D(0x4076800000000000UL) * (a - Trunc(a)));
        var ld = ((D(0x4070e6f1a9fbe76dUL) - (D(0x3f5290257c914b16UL) * t2)) + b);
        a = (D(0x4058ffd4c162e15eUL) * t);
        b = (D(0x4076800000000000UL) * (a - Trunc(a)));
        var ms = ((D(0x4076679db22d0e56UL) - (D(0x3f23a92a30553261UL) * t2)) + b);
        a = (D(0x4169486c72e147aeUL) * t);
        b = (D(0x4076800000000000UL) * (a - Trunc(a)));
        var md = ((D(0x407281ae147ae148UL) + (D(0x3f82d3415b1422cdUL) * t2)) + b);
        a = (D(0x40157d8f393ad68dUL) * t);
        b = (D(0x4076800000000000UL) * (a - Trunc(a)));
        var nm = ((D(0x407032ed916872b0UL) + (D(0x3f6105e1c15097c8UL) * t2)) - b);
        var tls = (D(0x4000000000000000UL) * Rad(ls));
        nm = Rad(nm);
        var tnm = (D(0x4000000000000000UL) * Rad(nm));
        ms = Rad(ms);
        var tld = (D(0x4000000000000000UL) * Rad(ld));
        md = Rad(md);
        var dpsi = (((((((((((((((-D(0x40313b923a29c77aUL)) - (D(0x3f91c970f7b9e061UL) * t)) * Sin(nm)) + (((-D(0x3ff45dcc63f14120UL)) - (D(0x3f210a137f38c543UL) * t)) * Sin(tls))) + (D(0x3fcab9f559b3d07dUL) * Sin(tnm))) - (D(0x3fca12d77318fc50UL) * Sin(tld))) + ((D(0x3fc0240b780346dcUL) - (D(0x3f3450efdc9c4da9UL) * t)) * Sin(ms))) + (D(0x3fb147ae147ae148UL) * Sin(md))) - ((D(0x3fa972474538ef35UL) - (D(0x3f1f75104d551d69UL) * t)) * Sin((tls + ms)))) - (D(0x3fa182a9930be0dfUL) * Sin((tld - nm)))) - (D(0x3f9ab9f559b3d07dUL) * Sin((tld + md)))) + (D(0x3f95e9e1b089a027UL) * Sin((tls - ms)))) - (D(0x3f8e83e425aee632UL) * Sin(((tls - tld) + md)))) + (D(0x3f89652bd3c36113UL) * Sin((tls - nm)))) + (D(0x3f8758e219652bd4UL) * Sin((tld - md))));
        var deps = ((((((((((D(0x40226b851eb851ecUL) + (D(0x3f4dd1a21ea35936UL) * t)) * Cos(nm)) + ((D(0x3fe1ab9f559b3d08UL) - (D(0x3f330164840e171aUL) * t)) * Cos(tls))) - (D(0x3fb72474538ef34dUL) * Cos(tnm))) + (D(0x3fb6a161e4f765feUL) * Cos(tld))) + (D(0x3f961e4f765fd8aeUL) * Cos((tls + ms)))) + (D(0x3f92bd3c36113405UL) * Cos((tld - nm)))) + (D(0x3f872474538ef34dUL) * Cos((tld + md)))) - (D(0x3f830be0ded288ceUL) * Cos((tls - ms)))) - (D(0x3f7b089a02752546UL) * Cos((tls - nm))));
        dpsi = Rad((dpsi / D(0x40ac200000000000UL)));
        deps = Rad((deps / D(0x40ac200000000000UL)));
        var obliquity = Rad((D(0x403773c99183f66fUL) - (((((((-D(0x3f5da7b0b3919264UL)) * t) + D(0x3f782a9930be0dedUL)) * t) + D(0x40476c28f5c28f5cUL)) * t) / D(0x40ac200000000000UL))));
        _ = dpsi; // source computes dpsi but its position path uses deps only.
        return obliquity + deps;
    }

    internal static (LunarDual Longitude,LunarDual Latitude,LunarDual Distance,LunarDual Parallax) Orbit(ReadOnlySpan<LunarDual> phase)
    {
        var ld = phase[0];
        var ms = phase[1];
        var md = phase[2];
        var de = phase[3];
        var f = phase[4];
        var n = phase[5];
        var c = phase[6];
        var e = phase[7];
        var e2 = phase[8];
        var TWOPI = D(0x401921fb54442d18UL);
        var l = (((((((((((D(0x401927ae147ae148UL) * Sin(md)) + (D(0x3ff46262cba732dfUL) * Sin(((D(0x4000000000000000UL) * de) - md)))) + (D(0x3fe510de093532e8UL) * Sin((D(0x4000000000000000UL) * de)))) + (D(0x3fcb57c4e2f37fbfUL) * Sin((D(0x4000000000000000UL) * md)))) - ((e * D(0x3fc7c19c17225b75UL)) * Sin(ms))) - (D(0x3fbd451fc4c16590UL) * Sin((D(0x4000000000000000UL) * f)))) + (D(0x3fae1a1db877ab32UL) * Sin((D(0x4000000000000000UL) * (de - md))))) + ((D(0x3fad4ae429e0a41aUL) * e) * Sin((((D(0x4000000000000000UL) * de) - ms) - md)))) + (D(0x3fab4cc25072085bUL) * Sin(((D(0x4000000000000000UL) * de) + md)))) + ((D(0x3fa77ccc03793144UL) * e) * Sin(((D(0x4000000000000000UL) * de) - ms)))) + ((D(0x3fa5011904b3c3e7UL) * e) * Sin((md - ms))));
        l = ((((((((((l - (D(0x3fa1c68ec52a411cUL) * Sin(de))) - ((e * D(0x3f9f32378ab0c88aUL)) * Sin((ms + md)))) + (D(0x3f8f633ce63a5c1cUL) * Sin((D(0x4000000000000000UL) * (de - f))))) - (D(0x3f89a847b24638c9UL) * Sin(((D(0x4000000000000000UL) * f) + md)))) - (D(0x3f867caea747d806UL) * Sin(((D(0x4000000000000000UL) * f) - md)))) + (D(0x3f85dc4007570c56UL) * Sin(((D(0x4010000000000000UL) * de) - md)))) + (D(0x3f848cb4aec8d5c7UL) * Sin((D(0x4008000000000000UL) * md)))) + (D(0x3f81819d2391d580UL) * Sin(((D(0x4010000000000000UL) * de) - (D(0x4000000000000000UL) * md))))) - ((e * D(0x3f80331e3a7daa50UL)) * Sin(((ms - md) + (D(0x4000000000000000UL) * de))))) - ((e * D(0x3f7bc87db2b34613UL)) * Sin(((D(0x4000000000000000UL) * de) + ms))));
        l = ((((((((((l + (D(0x3f7524bfd2e94680UL) * Sin((md - de)))) + ((e * D(0x3f747ae147ae147bUL)) * Sin((ms + de)))) + (D(0x3f6fa333764f11b6UL) * Sin((D(0x4010000000000000UL) * de)))) + ((e * D(0x3f7095af294dd723UL)) * Sin(((md - ms) + (D(0x4000000000000000UL) * de))))) + (D(0x3f705e1c15097c81UL) * Sin((D(0x4000000000000000UL) * (md + de))))) + (D(0x3f6e060fe47991bcUL) * Sin(((D(0x4000000000000000UL) * de) - (D(0x4008000000000000UL) * md))))) + ((e * D(0x3f6613d31b9b66f9UL)) * Sin(((D(0x4000000000000000UL) * md) - ms)))) + (D(0x3f6550ca1cef2410UL) * Sin((md - (D(0x4000000000000000UL) * (f + de)))))) + ((e * D(0x3f63a0c6b484d76bUL)) * Sin(((D(0x4000000000000000UL) * (de - md)) - ms)))) - (D(0x3f633e35c5b4aa97UL) * Sin((md + de))));
        l = (((((((((l + ((e2 * D(0x3f626c7eae5bc87eUL)) * Sin((D(0x4000000000000000UL) * (de - ms))))) - ((e * D(0x3f616872b020c49cUL)) * Sin(((D(0x4000000000000000UL) * md) + ms)))) - ((e2 * D(0x3f6107faa044ae86UL)) * Sin((D(0x4000000000000000UL) * ms)))) + ((e2 * D(0x3f60de093532e7b4UL)) * Sin(((D(0x4000000000000000UL) * (de - ms)) - md)))) - (D(0x3f5d0c804102ff8fUL) * Sin((md + (D(0x4000000000000000UL) * (de - f)))))) - (D(0x3f5a21ea35935fc4UL) * Sin((D(0x4000000000000000UL) * (f + de))))) + ((e * D(0x3f53fd0d0678c005UL)) * Sin((((D(0x4010000000000000UL) * de) - ms) - md)))) - (D(0x3f522fad6cb53501UL) * Sin((D(0x4000000000000000UL) * (md + f))))) + (D(0x3f4d3aa369fcf3dcUL) * Sin((md - (D(0x4008000000000000UL) * de)))));
        l = (((((((((l - ((e * D(0x3f4a93293d102bc7UL)) * Sin(((ms + md) + (D(0x4000000000000000UL) * de))))) + ((e * D(0x3f48efbb0e5e6794UL)) * Sin((((D(0x4010000000000000UL) * de) - ms) - (D(0x4000000000000000UL) * md))))) + ((e2 * D(0x3f4711947cfa26a2UL)) * Sin((md - (D(0x4000000000000000UL) * (ms + de)))))) + ((e * D(0x3f46b54e2b063e08UL)) * Sin((ms - (D(0x4000000000000000UL) * (md - de)))))) + ((e * D(0x3f43986338b47c74UL)) * Sin(((D(0x4000000000000000UL) * (de - f)) - ms)))) + (D(0x3f4205bc01a36e2fUL) * Sin((md + (D(0x4010000000000000UL) * de))))) + (D(0x3f41a11233df2a9dUL) * Sin((D(0x4010000000000000UL) * md)))) + ((e * D(0x3f411276fb09203aUL)) * Sin(((D(0x4010000000000000UL) * de) - ms)))) + (D(0x3f3fd9ba1b1960faUL) * Sin(((D(0x4000000000000000UL) * md) - de))));
        l = (l + ((e2 * D(0x3f477ea1c68ec52aUL)) * Sin((md - (D(0x4000000000000000UL) * ms)))));
        var eclLon = (ld + Rad(l));
        eclLon = Pfmod(eclLon, TWOPI);
        var g = (((((((((((D(0x401483443d46b26cUL) * Sin(f)) + (D(0x3fd1f572de43ed96UL) * Sin((md + f)))) + (D(0x3fd1c5b8dc55000dUL) * Sin((md - f)))) + (D(0x3fc62ca9ac365450UL) * Sin(((D(0x4000000000000000UL) * de) - f)))) + (D(0x3fac5f17bd8be729UL) * Sin((((D(0x4000000000000000UL) * de) + f) - md)))) + (D(0x3fa7b0f6ad70e6f3UL) * Sin((((D(0x4000000000000000UL) * de) - f) - md)))) + (D(0x3fa0ad6883771865UL) * Sin(((D(0x4000000000000000UL) * de) + f)))) + (D(0x3f919c5a3e39f773UL) * Sin(((D(0x4000000000000000UL) * md) + f)))) + (D(0x3f82fa93af74cd31UL) * Sin((((D(0x4000000000000000UL) * de) + md) - f)))) + (D(0x3f8211cb039ef0f1UL) * Sin(((D(0x4000000000000000UL) * md) - f)))) + ((e * D(0x3f80e3cd9a52263eUL)) * Sin((((D(0x4000000000000000UL) * de) - ms) - f))));
        g = ((((((((((g + (D(0x3f71b4fe79ee02a7UL) * Sin(((D(0x4000000000000000UL) * (de - md)) - f)))) + (D(0x3f713404ea4a8c15UL) * Sin((((D(0x4000000000000000UL) * de) + f) + md)))) + ((e * D(0x3f6b9f98b71b8aa0UL)) * Sin(((f - ms) - (D(0x4000000000000000UL) * de))))) + ((e * D(0x3f644028e4fb97bbUL)) * Sin(((((D(0x4000000000000000UL) * de) + f) - ms) - md)))) + ((e * D(0x3f6233df2a9d627cUL)) * Sin((((D(0x4000000000000000UL) * de) + f) - ms)))) + ((e * D(0x3f60f94c87980f56UL)) * Sin(((((D(0x4000000000000000UL) * de) - f) - ms) - md)))) + ((e * D(0x3f5ec0b5675579afUL)) * Sin(((f - ms) + md)))) + (D(0x3f5df3300de4c511UL) * Sin((((D(0x4010000000000000UL) * de) - f) - md)))) - ((e * D(0x3f5d8a5482385404UL)) * Sin((f + ms)))) - (D(0x3f5cac083126e979UL) * Sin((D(0x4008000000000000UL) * f))));
        g = (((((((((((g + ((e * D(0x3f59b90ea9e6eeb7UL)) * Sin(((md - ms) - f)))) - (D(0x3f585cee17a02fb6UL) * Sin((f + de)))) - ((e * D(0x3f5843c3a42f1ed1UL)) * Sin(((f + ms) + md)))) + ((e * D(0x3f5737542a23bff9UL)) * Sin(((f - ms) - md)))) + ((e * D(0x3f561e4f765fd8aeUL)) * Sin((f - ms)))) + (D(0x3f55ca6ca03c4b0aUL) * Sin((f - de)))) + (D(0x3f521ee675147f13UL) * Sin((f + (D(0x4008000000000000UL) * md))))) + (D(0x3f50b630a91537a0UL) * Sin(((D(0x4010000000000000UL) * de) - f)))) + (D(0x3f4b4bb5e0f7fcfcUL) * Sin(((f + (D(0x4010000000000000UL) * de)) - md)))) + (D(0x3f499780baa582dcUL) * Sin((md - (D(0x4008000000000000UL) * f))))) + (D(0x3f45f45e0b4e11dcUL) * Sin(((f + (D(0x4010000000000000UL) * de)) - (D(0x4000000000000000UL) * md)))));
        g = ((((((((((g + (D(0x3f43db7f1737542aUL) * Sin(((D(0x4000000000000000UL) * de) - (D(0x4008000000000000UL) * f))))) + (D(0x3f438fffbce4217dUL) * Sin(((D(0x4000000000000000UL) * (de + md)) - f)))) + ((e * D(0x3f401f31f46ed246UL)) * Sin(((((D(0x4000000000000000UL) * de) + md) - ms) - f)))) + (D(0x3f3d7dbf487fcb92UL) * Sin(((D(0x4000000000000000UL) * (md - de)) - f)))) + (D(0x3f3cc532a497fa5dUL) * Sin(((D(0x4008000000000000UL) * md) - f)))) + (D(0x3f3bb8c32a8c9b84UL) * Sin((f + (D(0x4000000000000000UL) * (de + md)))))) + (D(0x3f3ba7fc32ebe597UL) * Sin((((D(0x4000000000000000UL) * de) - f) - (D(0x4008000000000000UL) * md))))) - ((e * D(0x3f380d3cff64cf8dUL)) * Sin((((ms + f) + (D(0x4000000000000000UL) * de)) - md)))) - ((e * D(0x3f37225b749adc90UL)) * Sin(((ms + f) + (D(0x4000000000000000UL) * de))))) + (D(0x3f35b1422ccb3a26UL) * Sin((f + (D(0x4010000000000000UL) * de)))));
        g = (((g + ((e * D(0x3f34c660a2014728UL)) * Sin(((((D(0x4000000000000000UL) * de) + f) - ms) + md)))) + ((e2 * D(0x3f340dd3fe1975f3UL)) * Sin(((D(0x4000000000000000UL) * (de - ms)) - f)))) - (D(0x3f328bf3bea91d9bUL) * Sin((md + (D(0x4008000000000000UL) * f)))));
        var w1 = (D(0x3f3e90e4bf31d997UL) * Cos(n));
        var w2 = (D(0x3f13c40222efef11UL) * Cos(c));
        var eclLat = (Rad(g) * ((D(0x3ff0000000000000UL) - w1) - w2));
        var hp = (((((((((((D(0x3fee6c54bcf0b6b7UL) + (D(0x3faa87e38eb0318cUL) * Cos(md))) + (D(0x3f8384fd2a62aa19UL) * Cos(((D(0x4000000000000000UL) * de) - md)))) + (D(0x3f800ffda4052d66UL) * Cos((D(0x4000000000000000UL) * de)))) + (D(0x3f67225b749adc90UL) * Cos((D(0x4000000000000000UL) * md)))) + (D(0x3f4c15097c80841fUL) * Cos(((D(0x4000000000000000UL) * de) + md)))) + ((e * D(0x3f417720c8cd63ccUL)) * Cos(((D(0x4000000000000000UL) * de) - ms)))) + ((e * D(0x3f3a47a9e2bcf91aUL)) * Cos((((D(0x4000000000000000UL) * de) - md) - ms)))) + ((e * D(0x3f34f8b588e368f1UL)) * Cos((md - ms)))) - (D(0x3f31c2a023209678UL) * Cos(de))) - ((e * D(0x3f314d2f5dbb9cfaUL)) * Cos((ms + md)))) - (D(0x3f29f3c70c996b76UL) * Cos(((D(0x4000000000000000UL) * f) - md))));
        hp = (((((((((((hp + (D(0x3f26aceaaf35e311UL) * Cos((D(0x4008000000000000UL) * md)))) + (D(0x3f25e39713ad5beeUL) * Cos(((D(0x4010000000000000UL) * de) - md)))) - ((e * D(0x3f1d19157abb8801UL)) * Cos(ms))) + (D(0x3f1b003686a4ca4fUL) * Cos(((D(0x4010000000000000UL) * de) - (D(0x4000000000000000UL) * md))))) - (D(0x3f16052502eec7c9UL) * Cos(((D(0x4000000000000000UL) * md) - (D(0x4000000000000000UL) * de))))) - ((e * D(0x3f15c209246bf013UL)) * Cos(((D(0x4000000000000000UL) * de) + ms)))) + (D(0x3f14b599aa60913aUL) * Cos(((D(0x4000000000000000UL) * de) + (D(0x4000000000000000UL) * md))))) + (D(0x3f12dfd694ccab3fUL) * Cos((D(0x4010000000000000UL) * de)))) + ((e * D(0x3f10c6f7a0b5ed8dUL)) * Cos((((D(0x4000000000000000UL) * de) - ms) + md)))) - ((e * D(0x3f1083dbc23315d7UL)) * Cos((((D(0x4000000000000000UL) * de) + ms) - md)))) + ((e * D(0x3f057eed45e9185dUL)) * Cos((ms + de))));
        hp = ((((((((hp + ((e * D(0x3f02599ed7c6fbd2UL)) * Cos(((D(0x4000000000000000UL) * md) - ms)))) - (D(0x3f014d2f5dbb9cfaUL) * Cos(((D(0x4008000000000000UL) * md) - (D(0x4000000000000000UL) * de))))) - (D(0x3eff75104d551d69UL) * Cos((md + de)))) - (D(0x3efe68a0d349be90UL) * Cos((D(0x4000000000000000UL) * (f - de))))) - ((e * D(0x3efe68a0d349be90UL)) * Cos(((D(0x4000000000000000UL) * md) + ms)))) + ((e2 * D(0x3efb43526527a205UL)) * Cos((D(0x4000000000000000UL) * (de - ms))))) - (D(0x3ef81e03f705857bUL) * Cos(((D(0x4000000000000000UL) * (f - de)) + md)))) + ((e * D(0x3ef3ec460ed80a18UL)) * Cos((((D(0x4010000000000000UL) * de) - ms) - md))));
        var horzPar = Rad(hp);
        var distance = (D(0x40b8ea23d70a3d71UL) / Sin(horzPar));
        return (eclLon,eclLat,distance,horzPar);
    }
}
