// SPDX-License-Identifier: GPL-2.0-or-later
using SpaceTangent.Contracts;

namespace SpaceTangent.Core.Coordinates;

/// <summary>
/// Absolute coordinates in a common frame. Each axis uses signed 64.64 micro-light-years
/// within the inclusive physical range of ±2^62 micro-light-years.
/// </summary>
public readonly record struct AbsolutePosition
{
    private const int FractionBits = 64;
    private const double LimitMicroLightYears = 4_611_686_018_427_387_904;
    private static readonly Int128 TickLimit = (Int128)1 << 126;

    public AbsolutePosition(Int128 xTicks, Int128 yTicks, Int128 zTicks)
    {
        XTicks = ValidateAxis(xTicks, nameof(xTicks));
        YTicks = ValidateAxis(yTicks, nameof(yTicks));
        ZTicks = ValidateAxis(zTicks, nameof(zTicks));
    }

    public Int128 XTicks { get; }
    public Int128 YTicks { get; }
    public Int128 ZTicks { get; }

    public static AbsolutePosition FromKilometers(Vector3d coordinates) =>
        FromMicroLightYears(coordinates / AstronomicalUnits.KilometersPerMicroLightYear);

    public static AbsolutePosition FromAstronomicalUnits(Vector3d coordinates) =>
        FromKilometers(coordinates * AstronomicalUnits.KilometersPerAstronomicalUnit);

    public static AbsolutePosition FromMicroLightYears(Vector3d coordinates) =>
        new(ToTicks(coordinates.X), ToTicks(coordinates.Y), ToTicks(coordinates.Z));

    public AbsolutePosition OffsetKilometers(Vector3d offset)
    {
        AbsolutePosition delta = FromKilometers(offset);
        return new(AddOffset(XTicks, delta.XTicks), AddOffset(YTicks, delta.YTicks), AddOffset(ZTicks, delta.ZTicks));
    }

    public Vector3d RelativeKilometersTo(AbsolutePosition origin) =>
        new Vector3d(Difference(XTicks, origin.XTicks), Difference(YTicks, origin.YTicks),
                     Difference(ZTicks, origin.ZTicks)) * AstronomicalUnits.KilometersPerMicroLightYear;

    private static Int128 ValidateAxis(Int128 ticks, string parameter)
    {
        if (ticks < -TickLimit || ticks > TickLimit)
            throw new ArgumentOutOfRangeException(parameter, "Absolute position is outside the supported range.");
        return ticks;
    }

    private static Int128 ToTicks(double microLightYears)
    {
        if (!double.IsFinite(microLightYears) || Math.Abs(microLightYears) > LimitMicroLightYears)
            throw new ArgumentOutOfRangeException(nameof(microLightYears));

        return (Int128)Math.ScaleB(microLightYears, FractionBits);
    }

    private static Int128 AddOffset(Int128 origin, Int128 offset)
    {
        if (offset > 0 ? origin > TickLimit - offset : origin < -TickLimit - offset)
            throw new ArgumentOutOfRangeException(nameof(offset), "Offset exceeds the supported position range.");
        return origin + offset;
    }

    private static double Difference(Int128 axis, Int128 origin)
    {
        // The magnitude can reach 2^127 across the two supported endpoints.
        bool negative = axis < origin;
        UInt128 magnitude = negative ? unchecked((UInt128)(origin - axis)) : unchecked((UInt128)(axis - origin));
        double microLightYears = Math.ScaleB((double)magnitude, -FractionBits);
        return negative ? -microLightYears : microLightYears;
    }
}
