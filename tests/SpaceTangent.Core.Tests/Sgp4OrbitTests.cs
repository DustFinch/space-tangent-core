// SPDX-License-Identifier: GPL-2.0-or-later
using System.Globalization;
using SpaceTangent.Contracts;
using SpaceTangent.Core.Orbits;
using SpaceTangent.Core.Time;
using Xunit;

namespace SpaceTangent.Core.Tests;

public sealed class Sgp4OrbitTests
{
    private const string JasonFirst = "1 26997U 01055A   11192.81984987 -.00000038  00000-0  10000-3 0  3699";
    private const string JasonSecond = "2 26997  66.0463 303.1838 0007562 261.9351  98.0768 12.80928955448691";
    private const string SorceFirst = "1 27651U 03004A   11193.17002924  .00000232  00000-0  43749-4 0  5408";
    private const string SorceSecond = "2 27651  39.9965 107.7032 0026765 200.0544 159.9185 14.82697678458356";

    public static IEnumerable<object[]> ReferenceStates() =>
        File.ReadLines(Path.Combine(AppContext.BaseDirectory, "Fixtures", "sgp4-reference.csv"))
            .Where(line => !line.StartsWith('#')).Select(line => new object[] { line });

    [Theory]
    [MemberData(nameof(ReferenceStates))]
    public void RealTlesMatchIndependentTemeStates(string row)
    {
        string[] fields = row.Split(',');
        double[] values = fields.Skip(1).Select(value => double.Parse(value, CultureInfo.InvariantCulture)).ToArray();
        var orbit = new Sgp4Orbit(fields[0] == "Jason-1" ? TwoLineElements.Parse(JasonFirst, JasonSecond) :
            TwoLineElements.Parse(SorceFirst, SorceSecond));
        var expectedPosition = new Vector3d(values[1], values[2], values[3]);
        var expectedVelocity = new Vector3d(values[4], values[5], values[6]);

        Assert.True(orbit.TrySample(orbit.Epoch.AddSeconds(values[0]), out var state));
        Assert.InRange((state.PositionKilometers - expectedPosition).Length, 0, 1e-5);
        Assert.InRange((state.VelocityKilometersPerSecond - expectedVelocity).Length, 0, 1e-8);
        Assert.Equal(new FrameId("teme-of-date"), Sgp4Orbit.Frame);
    }

    [Fact]
    public void JasonEpochRetainsTheExactTleDayFraction()
    {
        TwoLineElements elements = TwoLineElements.Parse(JasonFirst, JasonSecond);
        Assert.Equal("26997", elements.CatalogNumber);
        Assert.Equal(new UtcDateTime(new DateOnly(2011, 7, 11), 19, 40, 35, 28_768_000), elements.UtcEpoch);
        var orbit = new Sgp4Orbit(elements);
        Assert.Equal(elements.UtcEpoch, TimeConversion.ToUtc(orbit.Epoch));
        Assert.False(orbit.TrySample(orbit.Epoch.AddSeconds(366 * 86_400), out _));
        Assert.Throws<ArgumentException>(() => orbit.TrySample(new JulianInstant(orbit.Epoch.Day, 0, TimeScale.Tt), out _));
    }

    [Fact]
    public void RejectsInvalidLineChecksumAndMismatchedCatalogNumbers()
    {
        Assert.Throws<FormatException>(() => TwoLineElements.Parse(JasonFirst[..^1] + "0", JasonSecond));
        Assert.Throws<FormatException>(() => TwoLineElements.Parse(JasonFirst, SorceSecond));
    }
}
