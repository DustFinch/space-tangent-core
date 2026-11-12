// SPDX-License-Identifier: GPL-2.0-or-later
using System.Globalization;
using SpaceTangent.Contracts;
using SpaceTangent.Core.Ephemerides;
using Xunit;

namespace SpaceTangent.Core.Tests;

public sealed class AnalyticSatelliteOrbitTests
{
    [Fact]
    public void MatchesTheOriginalParentRelativeSatellitePositions()
    {
        string fixture = Path.Combine(AppContext.BaseDirectory, "Fixtures", "analytic-satellites-reference.csv");
        var worst = new Dictionary<SatelliteTheory, double>();
        int count = 0;
        foreach (string row in File.ReadLines(fixture).Skip(1))
        {
            string[] fields = row.Split(',');
            SatelliteTheory theory = Enum.Parse<SatelliteTheory>(fields[0]);
            var epoch = JulianInstant.FromJulianDate(Number(1), TimeScale.Tdb);
            Vector3d reference = new(Number(2), Number(3), Number(4));
            Vector3d position = AnalyticSatelliteOrbit.Sample(theory, epoch).PositionKilometers;
            double error = (position - reference).Length;
            worst[theory] = Math.Max(worst.GetValueOrDefault(theory), error);
            count++;
            double Number(int index) => double.Parse(fields[index], CultureInfo.InvariantCulture);
        }
        Assert.Equal(18, worst.Count);
        Assert.Equal(18 * 41, count);
        Assert.True(worst.Values.Max() < 1, string.Join(", ", worst.Select(pair => $"{pair.Key} {pair.Value:F3} km")));
    }

    [Fact]
    public void SuppliesFiniteHistoricalPositionsAndConsistentVelocities()
    {
        var epoch = new JulianInstant(2106215, 0, TimeScale.Tdb);
        foreach (var model in Enum.GetValues<SatelliteTheory>())
        {
            CartesianState state = AnalyticSatelliteOrbit.Sample(model, epoch);
            var later = AnalyticSatelliteOrbit.Sample(model, epoch.AddSeconds(1));
            var earlier = AnalyticSatelliteOrbit.Sample(model, epoch.AddSeconds(-1));
            Vector3d numerical = (later.PositionKilometers - earlier.PositionKilometers) / 2;
            Assert.InRange(state.PositionKilometers.Length, 50_000, 5_000_000);
            Assert.True((state.VelocityKilometersPerSecond - numerical).Length < .0002, model.ToString());
        }
    }

}
