// SPDX-License-Identifier: GPL-2.0-or-later
using System.Text.Json;
using SpaceTangent.Contracts;
using SpaceTangent.Core.Addons;
using SpaceTangent.Core.Orbits;
using Xunit;

namespace SpaceTangent.Core.Tests;

public sealed class LocalTleCatalogueTests
{
    private const string GpsFirst = "1 26407U 00040A   26300.16000851  .00000034  00000+0  00000+0 0  9990";
    private const string GpsSecond = "2 26407  54.8288 209.9260 0115918 304.7530 227.8988  2.00558680192595";

    [Fact]
    public void ARealGpsOrbitIsIndependentOfPropagationOrder()
    {
        var orbit = new Sgp4Orbit(TwoLineElements.Parse(GpsFirst, GpsSecond));
        Assert.True(orbit.DeepSpace);
        Assert.True(orbit.TrySample(orbit.Epoch, out var initial));
        Assert.InRange(initial.PositionKilometers.Length, 26_000, 27_000);
        Assert.True(orbit.TrySample(orbit.Epoch.AddSeconds(86400 * 30), out var later));
        Assert.True((later.PositionKilometers - initial.PositionKilometers).Length > 10);
        Assert.True(orbit.TrySample(orbit.Epoch, out var again));
        Assert.Equal(initial, again);
        Assert.False(orbit.TrySample(orbit.Epoch.AddSeconds(86400 * 366), out _));
    }

    [Fact]
    public void CatalogueMergeKeepsTheNewestElementsAndGroupAliases()
    {
        var folder = Directory.CreateTempSubdirectory();
        try
        {
            string older = ReplaceChecksum(GpsFirst[..20] + "299" + GpsFirst[23..]);
            File.WriteAllText(Path.Combine(folder.FullName, "old.txt"), $"GPS old\n{older}\n{GpsSecond}\n");
            File.WriteAllText(Path.Combine(folder.FullName, "gps.tle"), $"0 GPS BIIR-5\n{GpsFirst}\n{GpsSecond}\n");
            var satellite = Assert.Single(LocalTleCatalogue.Load(folder.FullName));
            Assert.Equal(new ObjectId("norad/26407"), satellite.Id);
            Assert.Equal("GPS BIIR-5", satellite.Name);
            Assert.Equal(new Sgp4Orbit(TwoLineElements.Parse(GpsFirst, GpsSecond)).Epoch, satellite.Epoch);
            Assert.Contains("old", satellite.Aliases);
            Assert.Contains("gps", satellite.Aliases);
            Assert.Contains("GPS old", satellite.Aliases);

            File.WriteAllText(Path.Combine(folder.FullName, "gps.tle"), $"0 GPS BIIR-5\n{GpsFirst}\n{GpsSecond[..^1]}0\n");
            Assert.Throws<InvalidDataException>(() => LocalTleCatalogue.Load(folder.FullName));
        }
        finally { folder.Delete(recursive: true); }
    }

    [TleFixturesFact]
    [Trait("Category", "ExternalData")]
    public void ArchivedGroupsAndTheOriginalNativeDeepSpaceStatesMatch()
    {
        var warnings = new List<string>();
        var all = LocalTleCatalogue.Load(Environment.GetEnvironmentVariable("SPACETANGENT_TLE_ROOT")!, warnings.Add);
        Assert.Empty(warnings);
        Assert.Equal(243, all.Count);
        Assert.Equal(32, all.Count(satellite => satellite.Aliases.Contains("gps-ops")));
        var iss = Assert.Single(all, satellite => satellite.Id == new ObjectId("norad/25544"));
        Assert.False(iss.DeepSpace);
        Assert.Contains("stations", iss.Aliases);
        var gps = Assert.Single(all, satellite => satellite.Id == new ObjectId("norad/26407"));
        Assert.True(gps.DeepSpace);
        Assert.Contains("gps-ops", gps.Aliases);

        using var stream = File.OpenRead(Environment.GetEnvironmentVariable("SPACETANGENT_TLE_REFERENCE")!);
        using var doc = JsonDocument.Parse(stream);
        int count = 0;
        foreach (var item in doc.RootElement.GetProperty("cases").EnumerateArray())
        {
            string first = item.GetProperty("line1").GetString()!;
            if (first.Length < 69 || first.Substring(2, 5) is not ("08195" or "14128" or "24208" or "28129"))
                continue;
            string second = item.GetProperty("line2").GetString()!;
            var orbit = new Sgp4Orbit(TwoLineElements.Parse(first, second));
            Assert.True(orbit.DeepSpace);
            foreach (var sample in item.GetProperty("samples").EnumerateArray())
            {
                double minutes = sample.GetProperty("minutes").GetDouble();
                Vector3d position = Vector(sample, "positionKm"), velocity = Vector(sample, "velocityKmS");
                Assert.True(orbit.TrySample(orbit.Epoch.AddSeconds(minutes * 60), out var state));
                Assert.InRange((state.PositionKilometers - position).Length, 0, .001);
                Assert.InRange((state.VelocityKilometersPerSecond - velocity).Length, 0, 1e-7);
                count++;
            }
        }
        Assert.Equal(48, count);

        static Vector3d Vector(JsonElement state, string key)
        {
            var value = state.GetProperty(key);
            return new(value[0].GetDouble(), value[1].GetDouble(), value[2].GetDouble());
        }
    }

    private static string ReplaceChecksum(string line)
    {
        int sum = line[..68].Sum(character => char.IsAsciiDigit(character) ? character - '0' : character == '-' ? 1 : 0);
        return line[..68] + (char)('0' + sum % 10);
    }

    private sealed class TleFixturesFactAttribute : FactAttribute
    {
        public TleFixturesFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SPACETANGENT_TLE_ROOT")) ||
                string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SPACETANGENT_TLE_REFERENCE")))
                Skip = "Set the archived TLE group directory and the independent native state reference.";
        }
    }
}
