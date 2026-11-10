// SPDX-License-Identifier: GPL-2.0-or-later
using SpaceTangent.Contracts;
using SpaceTangent.Core.Coordinates;
using Xunit;

namespace SpaceTangent.Core.Tests;

public sealed class AbsolutePositionTests
{
    [Fact]
    public void QuantizationTruncatesTowardZeroOnThe64BitFraction()
    {
        double quantum = Math.ScaleB(1, -64);
        var position = AbsolutePosition.FromMicroLightYears(new Vector3d(1.5 * quantum, -1.5 * quantum, 0.5));

        Assert.Equal((Int128)1, position.XTicks);
        Assert.Equal((Int128)(-1), position.YTicks);
        Assert.Equal((Int128)1 << 63, position.ZTicks);
    }

    [Fact]
    public void SubtractsCoordinatesBeforeConvertingToDouble()
    {
        Int128 far = (Int128)1 << 125;
        var origin = new AbsolutePosition(far, -far, far);
        var offset = new Vector3d(0.001, -0.001, 0.002);
        var moved = origin.OffsetKilometers(offset);
        var local = moved.RelativeKilometersTo(origin);

        Assert.NotEqual(origin, moved);
        Assert.InRange(Math.Abs(local.X - offset.X), 0, 6e-13);
        Assert.InRange(Math.Abs(local.Y - offset.Y), 0, 6e-13);
        Assert.InRange(Math.Abs(local.Z - offset.Z), 0, 6e-13);
    }

    [Fact]
    public void HandlesOppositeEndpointsWithoutWraparound()
    {
        Int128 limit = (Int128)1 << 126;
        var positive = new AbsolutePosition(limit, 0, 0);
        var negative = new AbsolutePosition(-limit, 0, 0);
        double separation = Math.ScaleB(9_460_730.4725808, 63);

        Assert.Equal(separation, positive.RelativeKilometersTo(negative).X);
        Assert.Equal(-separation, negative.RelativeKilometersTo(positive).X);
        Assert.Equal(positive, positive.OffsetKilometers(default));
        Assert.Equal(negative, negative.OffsetKilometers(default));
        Assert.Throws<ArgumentOutOfRangeException>(() => positive.OffsetKilometers(new(1, 0, 0)));
        Assert.Throws<ArgumentOutOfRangeException>(() => negative.OffsetKilometers(new(-1, 0, 0)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new AbsolutePosition(limit + 1, 0, 0));
    }

    [Fact]
    public void MatchesPublishedAuAndJulianLightYear()
    {
        // IAU 2012 Resolution B2 and 299,792.458 km/s over 365.25 Julian days.
        var oneAu = AbsolutePosition.FromAstronomicalUnits(new(1, 0, 0));
        var oneLightYear = AbsolutePosition.FromMicroLightYears(new(0, 1_000_000, 0));

        Assert.Equal(AbsolutePosition.FromKilometers(new(149_597_870.7, 0, 0)), oneAu);
        Assert.InRange(Math.Abs(oneAu.RelativeKilometersTo(default).X - 149_597_870.7), 0, 3e-8);
        Assert.Equal(9_460_730_472_580.8, oneLightYear.RelativeKilometersTo(default).Y, 0.002);
        Assert.Throws<ArgumentOutOfRangeException>(() => AbsolutePosition.FromKilometers(new(double.NaN, 0, 0)));
    }

    [Fact]
    public void VectorLengthAvoidsOverflowAndUnderflowInIntermediateSquares()
    {
        Assert.Equal(5e200, new Vector3d(3e200, 4e200, 0).Length, 1e185);
        Assert.Equal(5e-200, new Vector3d(3e-200, 4e-200, 0).Length, 1e-215);
    }
}
