// SPDX-License-Identifier: GPL-2.0-or-later
using SpaceTangent.Contracts;

namespace SpaceTangent.Core.Ephemerides;

internal static class HistoricalLunarOrbit
{
    private static readonly JulianInstant Epoch1900 = new(2415020, 0, TimeScale.Tdb);
    private static readonly JulianInstant EpochJ2000 = new(2451545, 0, TimeScale.Tdb);

    internal static CartesianState Evaluate(JulianInstant epoch)
    {
        double days1900 = (double)epoch.DifferenceNanoseconds(Epoch1900) / JulianInstant.NanosecondsPerDay;
        double daysJ2000 = (double)epoch.DifferenceNanoseconds(EpochJ2000) / JulianInstant.NanosecondsPerDay;
        Span<LunarDual> phase = stackalloc LunarDual[13];
        LunarHarmonics.Phase(days1900, daysJ2000, phase);
        var orbit = LunarHarmonics.Orbit(phase);
        var (sinLongitude, cosLongitude) = LunarDual.SinCos(orbit.Longitude);
        var (sinLatitude, cosLatitude) = LunarDual.SinCos(orbit.Latitude);
        var point = new LunarDualVector(orbit.Distance * cosLatitude * cosLongitude,
            orbit.Distance * cosLatitude * sinLongitude, orbit.Distance * sinLatitude);
        point = point.RotateX(phase[12]).RotateZ(phase[9]).RotateY(-phase[11]).RotateZ(phase[10]);
        var position = new Vector3d(point.X.Value, point.Y.Value, point.Z.Value);
        var velocity = new Vector3d(point.X.Tangent, point.Y.Tangent, point.Z.Tangent) / JulianInstant.SecondsPerDay;
        if (!position.IsFinite || !velocity.IsFinite)
            throw new ArithmeticException("The historical lunar state is not finite.");
        return new(position, velocity);
    }
}

// Each operation carries the derivative of the same expression with respect to TDB days.
internal readonly record struct LunarDual(double Value,double Tangent)
{
    internal bool IsFinite => double.IsFinite(Value)&&double.IsFinite(Tangent);
    public static LunarDual operator +(LunarDual a,LunarDual b) => new(a.Value+b.Value,a.Tangent+b.Tangent);
    public static LunarDual operator -(LunarDual a,LunarDual b) => new(a.Value-b.Value,a.Tangent-b.Tangent);
    public static LunarDual operator -(LunarDual a) => new(-a.Value,-a.Tangent);
    public static LunarDual operator +(LunarDual a) => a;
    public static LunarDual operator *(LunarDual a,LunarDual b) => new(a.Value*b.Value,a.Tangent*b.Value+a.Value*b.Tangent);
    public static LunarDual operator /(LunarDual a,LunarDual b) => new(a.Value/b.Value,
        b.Tangent==0?a.Tangent/b.Value:(a.Tangent*b.Value-a.Value*b.Tangent)/(b.Value*b.Value));
    internal static (LunarDual Sin,LunarDual Cos) SinCos(LunarDual x)
    { var(s,c)=Math.SinCos(x.Value); return(new(s,c*x.Tangent),new(c,-s*x.Tangent)); }
    internal static LunarDual Sin(LunarDual x) => SinCos(x).Sin;
    internal static LunarDual Cos(LunarDual x) => SinCos(x).Cos;
    internal static LunarDual Trunc(LunarDual x)
    {
        if(!double.IsFinite(x.Value)||Math.Abs(x.Value)>=int.MaxValue) throw new ArithmeticException("Source cycle integer exceeds its bound.");
        return new(Math.Truncate(x.Value),0);
    }
    internal static LunarDual Pfmod(LunarDual x,LunarDual y)
    {
        if(!x.IsFinite||!y.IsFinite||y.Value<=0) throw new ArithmeticException("Positive finite source modulus required.");
        double quotient=Math.Truncate(Math.Abs(x.Value/y.Value));
        if(!double.IsFinite(quotient)||quotient>=int.MaxValue) throw new ArithmeticException("Bounded source quotient required.");
        var q=new LunarDual(quotient,0);
        return x.Value<0?x+(q+new LunarDual(1,0))*y:x-q*y;
    }
}

internal readonly record struct LunarDualVector(LunarDual X,LunarDual Y,LunarDual Z)
{
    internal LunarDualVector RotateX(LunarDual angle)
    { var(s,c)=LunarDual.SinCos(angle); return new(X,c*Y-s*Z,s*Y+c*Z); }
    internal LunarDualVector RotateY(LunarDual angle)
    { var(s,c)=LunarDual.SinCos(angle); return new(c*X+s*Z,Y,-s*X+c*Z); }
    internal LunarDualVector RotateZ(LunarDual angle)
    { var(s,c)=LunarDual.SinCos(angle); return new(c*X-s*Y,s*X+c*Y,Z); }
}
