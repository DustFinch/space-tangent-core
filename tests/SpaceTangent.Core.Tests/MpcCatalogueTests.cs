// SPDX-License-Identifier: GPL-2.0-or-later
using SpaceTangent.Contracts;
using SpaceTangent.Core.Addons;
using Xunit;

namespace SpaceTangent.Core.Tests;

public sealed class MpcCatalogueTests
{
    [OriginalMpcFact]
    [Trait("Category", "ExternalData")]
    public void OriginalAsteroidsAndCometsMatchIndependentEclipticStates()
    {
        var result = MpcCatalogue.Load(Input!);
        Assert.Empty(result.Warnings);
        Assert.Equal(6, result.Orbits.Count);
        Assert.Equal("(433) Eros", result.Orbits[0].Designation);
        Assert.Equal("(99942) Apophis", result.Orbits[1].Designation);
        (string Name, double Tt, Vector3d Position, Vector3d Velocity)[] expected =
        [
            ("Eros", 2461200.5, new(-158432558.30217618, -137747352.03322664, -39879532.61736883), new(11.881823343928911, -22.18738555293222, -.5114022500850117)),
            ("Apophis", 2461200.5, new(-154265926.80610278, 54628826.0804215, -6563454.489497245), new(-7.618220701242135, -24.483955812774578, 1.1266863273635392)),
            ("1P/Halley", 2474037.0655, new(-21016902.40091891, -99378017.58890963, 10523019.111313716), new(-47.765077379121934, -11.914665913900912, -11.225805373784725)),
            ("1I/`Oumuamua", 2458005.9886, new(70282229.83111906, 54169353.83918995, -31173599.882352192), new(55.52540046609192, 17.150260428415926, 11.723935987912814)),
            ("2I/Borisov", 2458826.5572, new(-253679242.72974372, 91449996.13985957, -140715565.64306483), new(-6.724803211612587, -34.63940834570032, -25.870532822772606)),
            ("3I/ATLAS", 2460977.9825, new(-217763958.8130688, 48294723.20159188, 8163217.82496035), new(-12.404051785295088, 66.19737087110428, -3.8188194910859172))
        ];
        foreach (var (name, tt, position, velocity) in expected)
        {
            MpcOrbit orbit = Assert.Single(result.Orbits, candidate => candidate.Name == name);
            Assert.InRange(Math.Abs((double)orbit.ElementEpoch.DifferenceNanoseconds(
                JulianInstant.FromJulianDate(tt, TimeScale.Tdb))) / 1e9, 0, .002);
            JulianInstant epoch = orbit.ElementEpoch.AddSeconds(17 * 86400);
            CartesianState state = orbit.EclipticOrbit.Evaluate(epoch);
            Assert.InRange((state.PositionKilometers - position).Length, 0, .001);
            Assert.InRange((state.VelocityKilometersPerSecond - velocity).Length, 0, 1e-9);
            Assert.InRange(Math.Abs(orbit.Sample(epoch).PositionKilometers.Length - position.Length), 0, .001);
        }
    }

    [Fact]
    public void MalformedLinesAndDuplicateDesignationsAreReported()
    {
        const string valid = "00433   10.39  0.15 K2669  62.51145  178.91814  304.26797   10.82855  0.2228780  0.55970463   1.4582437";
        string path = Path.GetTempFileName();
        try
        {
            File.WriteAllLines(path, ["# local selection", valid, "broken record", valid, ""]);
            var result = MpcCatalogue.Load(path);
            Assert.Equal("00433", Assert.Single(result.Orbits).Designation);
            Assert.Equal(2, result.Warnings.Count);
            Assert.StartsWith("Line 3:", result.Warnings[0]);
            Assert.Contains("Repeated designation", result.Warnings[1]);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static string? Input => Environment.GetEnvironmentVariable("SPACETANGENT_MPC_SELECTION");

    private sealed class OriginalMpcFactAttribute : FactAttribute
    {
        public OriginalMpcFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Input))
                Skip = "Set SPACETANGENT_MPC_SELECTION to the verified MPCORB and CometEls selection.";
        }
    }
}
