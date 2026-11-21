// SPDX-License-Identifier: GPL-2.0-or-later
using SpaceTangent.Contracts;
using Xunit;

namespace SpaceTangent.Core.Tests;

public sealed class InstantBoundaryContractTests
{
    [Theory]
    [InlineData(-1)]
    [InlineData(JulianInstant.MaximumDay + 1)]
    public void ConstructorRejectsDaysOutsideTheSupportedCalendar(long day)
    {
        Assert.Equal("day", Assert.Throws<ArgumentOutOfRangeException>(() =>
            new JulianInstant(day, 0, TimeScale.Tdb)).ParamName);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(3)]
    public void ConstructorRejectsUndefinedTimeScales(int scale)
    {
        Assert.Equal("scale", Assert.Throws<ArgumentOutOfRangeException>(() =>
            new JulianInstant(2451545, 0, (TimeScale)scale)).ParamName);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    [InlineData(-1)]
    [InlineData(JulianInstant.MaximumDay + 1d)]
    public void FloatingJulianDatesRejectNonfiniteAndOutOfRangeInputs(double day)
    {
        Assert.Equal("date", Assert.Throws<ArgumentOutOfRangeException>(() =>
            JulianInstant.FromJulianDate(day, TimeScale.Tt)).ParamName);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    [InlineData(1e13)]
    [InlineData(-1e13)]
    public void SecondsApisRejectNonfiniteAndExcessiveOffsets(double seconds)
    {
        Assert.Equal("seconds", Assert.Throws<ArgumentOutOfRangeException>(() =>
            JulianInstant.FromTdbSeconds(seconds)).ParamName);
        Assert.Equal("seconds", Assert.Throws<ArgumentOutOfRangeException>(() =>
            new JulianInstant(2451545, 0, TimeScale.Tt).AddSeconds(seconds)).ParamName);
    }

    [Fact]
    public void RoundingTheLastRepresentableOffsetBeforeNoonCarriesIntoTheNextDay()
    {
        // The adjacent binary64 value is within half a nanosecond of the next integer day.
        var atNoon = JulianInstant.FromTdbSeconds(Math.BitDecrement(86400d));
        Assert.Equal(new JulianInstant(JulianInstant.J2000Day + 1, 0, TimeScale.Tdb), atNoon);
        Assert.Equal(new JulianInstant(JulianInstant.J2000Day - 1, 0, TimeScale.Tdb),
            JulianInstant.FromTdbSeconds(-86400d));
        Assert.Equal(new JulianInstant(JulianInstant.J2000Day, 1, TimeScale.Tdb),
            JulianInstant.FromTdbSeconds(1e-9));
    }

    [Theory]
    [InlineData("name with spaces")]
    [InlineData("объект")]
    [InlineData("name?query")]
    [InlineData("name.with.dot")]
    public void ObjectAndFrameIdentifiersRejectUnsupportedCharacters(string text)
    {
        Assert.Equal("value", Assert.Throws<ArgumentException>(() => new ObjectId(text)).ParamName);
        Assert.Equal("value", Assert.Throws<ArgumentException>(() => new FrameId(text)).ParamName);
    }

    [Fact]
    public void BothIdentifierTypesPreserveTheSameValidAsciiSpelling()
    {
        const string text = "SSC/Sol/Earth_probe-42";
        Assert.Equal(text, new ObjectId(text).Value);
        Assert.Equal(text, new FrameId(text).Value);
    }
}
