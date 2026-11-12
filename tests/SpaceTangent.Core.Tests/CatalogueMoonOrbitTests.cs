// SPDX-License-Identifier: GPL-2.0-or-later
using System.Text.Json;
using SpaceTangent.Contracts;
using SpaceTangent.Core.Ephemerides;
using Xunit;

namespace SpaceTangent.Core.Tests;

public sealed class CatalogueMoonOrbitTests
{
    [CatalogueMoonFact]
    [Trait("Category", "ExternalData")]
    public void FiveOriginalMoonConicsMatchArchivedHighPrecisionLocalStates()
    {
        string oracle = Environment.GetEnvironmentVariable("SPACETANGENT_CATALOGUE_MOON_ORACLE")!;
        using var reference = JsonDocument.Parse(File.ReadAllBytes(oracle));
        var ids = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["phobos"] = 401, ["deimos"] = 402, ["phoebe"] = 609, ["proteus"] = 808, ["nereid"] = 802
        };
        int count = 0;
        foreach (JsonElement row in reference.RootElement.GetProperty("cases").EnumerateArray())
        {
            if (row.GetProperty("profile").GetString() != "source-binary64-direct-a")
                continue;
            string name = row.GetProperty("body").GetString()!;
            var model = CatalogueMoonOrbit.Find(ids[name])!;
            var time = row.GetProperty("instantTdb");
            var epoch = new JulianInstant(time.GetProperty("WholeDays").GetInt64(), time.GetProperty("NanosecondsOfDay").GetInt64(), TimeScale.Tdb);
            var local = model.SampleLocal(epoch);
            bool position = row.GetProperty("quantity").GetString() == "position-km";
            Vector3d point = position ? local.PositionKilometers : local.VelocityKilometersPerSecond;
            double actual = row.GetProperty("coordinateOneBased").GetInt32() switch { 1 => point.X, 2 => point.Z, _ => -point.Y };
            double expected = row.GetProperty("expectedNearestBinary64").GetDouble();
            double tolerance = position ? 1e-6 : 2e-9;
            Assert.True(Math.Abs(expected - actual) <= tolerance, $"{name} {epoch} expected {expected:R}, got {actual:R}.");
            count++;
        }
        Assert.Equal(270, count);
    }

    private sealed class CatalogueMoonFactAttribute : FactAttribute
    {
        public CatalogueMoonFactAttribute()
        {
            if (Environment.GetEnvironmentVariable("SPACETANGENT_CATALOGUE_MOON_ORACLE") is null)
                Skip = "Set the path to the original local moon orbit oracle.";
        }
    }
}
