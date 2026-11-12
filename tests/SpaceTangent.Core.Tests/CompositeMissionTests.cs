// SPDX-License-Identifier: GPL-2.0-or-later
using System.Text.Json;
using System.Text.Json.Nodes;
using SpaceTangent.Contracts;
using SpaceTangent.Core.Addons;
using SpaceTangent.Core.Ephemerides;
using SpaceTangent.Core.Orbits;
using Xunit;

namespace SpaceTangent.Core.Tests;

public sealed class CompositeMissionTests
{
    [Fact]
    public void CompositeUsesHalfOpenIntervalsAndKeepsExplicitGaps()
    {
        var folder = Directory.CreateTempSubdirectory();
        try
        {
            File.WriteAllText(Path.Combine(folder.FullName, "orbits.json"), """
                {"version":"1.0","items":[{"name":"Example","center":"Earth","startTime":2451545,"endTime":2451550,
                "trajectory":{"type":"Composite","segments":[
                  {"endTime":2451546,"trajectory":{"type":"FixedSpherical","radius":"1000 m","latitude":0,"longitude":90}},
                  {"startTime":2451547,"trajectory":{"type":"Keplerian","semiMajorAxis":"1000 km","period":"4h",
                    "eccentricity":0,"epoch":2451547}}
                ]}}]}
                """);
            var track = Assert.Single(SampledMissionCatalog.Load("orbits.json", folder.FullName, _ => new ObjectId("sol/earth")));
            var sample = track.Arcs[0].LocalState;
            var fixedPoint = Assert.IsType<CartesianState>(sample(new(2451545, 0, TimeScale.Tdb)));
            Assert.InRange((fixedPoint.PositionKilometers - new Vector3d(0, 1, 0)).Length, 0, 1e-12);
            Assert.Null(sample(new(2451546, 0, TimeScale.Tdb)));
            var start = new JulianInstant(2451547, 0, TimeScale.Tdb);
            var quarter = Assert.IsType<CartesianState>(sample(start.AddSeconds(3600)));
            Assert.InRange((quarter.PositionKilometers - new Vector3d(0, 1000, 0)).Length, 0, 1e-8);
            Assert.InRange((quarter.VelocityKilometersPerSecond - new Vector3d(-1000 * Math.Tau / 14400, 0, 0)).Length, 0, 1e-10);
            Assert.Null(sample(new(2451550, 0, TimeScale.Tdb)));
        }
        finally { folder.Delete(recursive: true); }
    }

    [OriginalSeriesFact]
    [Trait("Category", "ExternalData")]
    public void OriginalSaturnTrajectoryDefinitionsMatchTheIndependentOracle()
    {
        string archive = Environment.GetEnvironmentVariable("SPACETANGENT_CHEBYSHEV_ROOT")!;
        var document = JsonNode.Parse(File.ReadAllText(Path.Combine(archive, "saturn.json")))!;
        var records = document["items"]!.AsArray().Where(item =>
            item!["name"]?.GetValue<string>() is "Enceladus" or "Dione" or "Phoebe").ToArray();
        var sampleOnly = new JsonArray();
        foreach (JsonNode? record in records)
        {
            var copy = record!.DeepClone().AsObject();
            copy.Remove("geometry");
            copy.Remove("rotationModel");
            sampleOnly.Add(copy);
        }
        Assert.Equal(3, sampleOnly.Count);
        var folder = Directory.CreateTempSubdirectory();
        try
        {
            File.WriteAllText(Path.Combine(folder.FullName, "orbits.json"),
                new JsonObject { ["version"] = "1.0", ["items"] = sampleOnly }.ToJsonString());
            using var reference = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(archive, "reference.json")));
            foreach (var input in reference.RootElement.GetProperty("files").EnumerateArray())
            {
                string file = input.GetProperty("file").GetString()!;
                if (Path.GetExtension(file) == ".cheb")
                    File.Copy(Path.Combine(archive, file), Path.Combine(folder.FullName, file));
            }
            var tracks = SampledMissionCatalog.Load("orbits.json", folder.FullName, name => name == "Saturn" ? new("sol/saturn") : null);
            foreach (var row in reference.RootElement.GetProperty("samples").EnumerateArray())
            {
                var body = Assert.Single(tracks, mission => mission.Name == row.GetProperty("body").GetString());
                var epoch = JulianInstant.FromJulianDate(row.GetProperty("jd").GetDouble(), TimeScale.Tdb);
                CartesianState actual = Assert.IsType<CartesianState>(body.Arcs[0].LocalState(epoch));
                Assert.InRange((actual.PositionKilometers - Vector(row, "positionKm")).Length, 0, 2e-5);
                Assert.InRange((actual.VelocityKilometersPerSecond - Vector(row, "velocityKmS")).Length, 0, 2e-10);
            }
            var first = Assert.Single(tracks, mission => mission.Name == "Enceladus").Arcs[0];
            var outsideSeries = JulianInstant.FromJulianDate(2447892.5, TimeScale.Tdb);
            Assert.Equal(AnalyticSatelliteOrbit.Sample(SatelliteTheory.Enceladus, outsideSeries), first.LocalState(outsideSeries));
            var endpoint = ChebyshevTrajectory.Load(Path.Combine(folder.FullName, "phoebe.cheb"));
            foreach (var row in reference.RootElement.GetProperty("phoebeEndpoints").EnumerateArray())
            {
                var epoch = JulianInstant.FromJulianDate(row.GetProperty("jd").GetDouble(), TimeScale.Tdb);
                var value = endpoint.Sample(epoch);
                Assert.InRange((value.PositionKilometers - Vector(row, "positionKm")).Length, 0, 2e-5);
                Assert.InRange((value.VelocityKilometersPerSecond - Vector(row, "velocityKmS")).Length, 0, 2e-10);
            }
        }
        finally { folder.Delete(recursive: true); }
    }

    private static Vector3d Vector(JsonElement value, string name)
    {
        var components = value.GetProperty(name);
        return new(components[0].GetDouble(), components[1].GetDouble(), components[2].GetDouble());
    }

    private sealed class OriginalSeriesFactAttribute : FactAttribute
    {
        public OriginalSeriesFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SPACETANGENT_CHEBYSHEV_ROOT")))
                Skip = "Set the original Saturn catalogue and CHEBPOLY reference directory.";
        }
    }
}
