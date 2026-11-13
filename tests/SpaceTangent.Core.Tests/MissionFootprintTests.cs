// SPDX-License-Identifier: GPL-2.0-or-later
using SpaceTangent.Contracts;
using SpaceTangent.Core.Addons;
using SpaceTangent.Core.Ephemerides;
using SpaceTangent.Core.World;
using Xunit;

namespace SpaceTangent.Core.Tests;

public sealed class MissionFootprintTests
{
    [ArchivedFootprintFact]
    [Trait("Category", "ExternalData")]
    public void ArchivedSchedulesKeepRatesTargetsAndNativeFootprints()
    {
        string root = Environment.GetEnvironmentVariable("SPACETANGENT_ARCHIVED_OBSERVATIONS")!;
        string source = Environment.GetEnvironmentVariable("SPACETANGENT_PROBE_MISSIONS_ROOT")!;
        var earth = SolarSystemSample.EarthId;
        var satellites = PackedSatelliteCatalog.Load(root);
        ObjectId? Resolve(string name) => name == "Earth" ? earth :
            satellites.FirstOrDefault(satellite => satellite.Name == name)?.Id;
        var loose = SampledMissionCatalog.Load("observation-verification.json", source, Resolve);
        var packed = SampledMissionCatalog.Load("pack.json", root, Resolve);
        var builder = new WorldGraphBuilder();
        builder.AddTrajectory(earth, _ => default(CartesianState));
        builder.AddMeanEarthRotation();
        foreach (var satellite in satellites)
            builder.AddScienceSatellite(satellite);
        foreach (var mission in packed)
            builder.AddSampledMission(mission);
        var world = builder.Build();
        foreach (var original in loose.Where(mission => mission.Observation is not null))
        {
            var actual = Assert.Single(packed, mission => mission.Id == original.Id);
            var expected = original.Observation!;
            var style = Assert.IsType<MissionObservation>(actual.Observation);
            Assert.Equal(expected with { Groups = style.Groups }, style);
            Assert.Equal(expected.Groups.Count, style.Groups.Count);
            for (int group = 0; group < style.Groups.Count; group++)
            {
                Assert.Equal(expected.Groups[group].RateSeconds, style.Groups[group].RateSeconds);
                Assert.InRange((double)Int128.Abs(expected.Groups[group].Start.DifferenceNanoseconds(style.Groups[group].Start)), 0, 22000);
                Assert.InRange((double)Int128.Abs(expected.Groups[group].End.DifferenceNanoseconds(style.Groups[group].End)), 0, 22000);
            }
            var sensor = Assert.Single(packed, mission => mission.Id == style.Sensor);
            var axes = new Vector3d(6378.137, 6378.137, 6356.752);
            var footprints = MissionFootprints.Calculate(style, sensor, axes, world);
            if (original.Name == "Jason Footprints")
            {
                Assert.Equal(7, footprints.Count);
                Assert.All(footprints, item => Assert.Equal(32, item.Points.Count));
                Assert.Equal(3, footprints.Count(item => !item.Swath));
                var coarse = MissionFootprints.Calculate(style with { SideDivisions = 3 }, sensor, axes, world);
                Assert.All(coarse, item => Assert.Equal(12, item.Points.Count));
                Assert.Equal(footprints.Count, coarse.Count);
            }
            else
                Assert.Empty(footprints);
        }
    }

    private sealed class ArchivedFootprintFactAttribute : FactAttribute
    {
        public ArchivedFootprintFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SPACETANGENT_ARCHIVED_OBSERVATIONS")) ||
                string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SPACETANGENT_PROBE_MISSIONS_ROOT")))
                Skip = "Set the archived science pack and its original observation catalogue.";
        }
    }

    [Fact]
    public void SamplesDiscreteFootprintsAndSwathsOnlyWhenTheWholeFieldLands()
    {
        var directory = Directory.CreateTempSubdirectory();
        try
        {
            File.WriteAllText(Path.Combine(directory.FullName, "observation.json"), """
                {"version":"1.0","items":[
                  {"name":"Surface Map","center":"Earth","geometry":{"type":"Observations","sensor":"Instrument",
                    "alongTrackDivisions":4,"showResWithColor":true,"groups":[
                      {"startTime":"2000-01-01T12:00:00","endTime":"2000-01-01T12:00:30","obsRate":10},
                      {"startTime":"2000-01-01T12:00:40","endTime":"2000-01-01T12:01:00"}]}},
                  {"name":"Instrument","center":"Earth","trajectory":{"type":"FixedPoint","position":[0,0,3]},
                    "geometry":{"type":"Sensor","target":"Earth","range":3,"orientation":[0,1,0,0],
                    "horizontalFov":10,"verticalFov":6}}
                ]}
                """);
            var earth = SolarSystemSample.EarthId;
            var tracks = SampledMissionCatalog.Load("observation.json", directory.FullName,
                name => name == "Earth" ? earth : null);
            var style = Assert.IsType<MissionObservation>(tracks[0].Observation);
            Assert.Equal(earth, style.Target);
            Assert.True(style.ByResolution);
            var builder = new WorldGraphBuilder();
            builder.AddTrajectory(earth, _ => new(default, default));
            builder.AddOrientation(earth, _ => new(Basis3d.Identity, default(Vector3d)));
            foreach (var track in tracks)
                builder.AddSampledMission(track);
            var graph = builder.Build();
            var axes = new Vector3d(2, 1, 1);
            var points = MissionFootprints.Calculate(style, tracks[1], axes, graph);
            Assert.Equal(8, points.Count);
            Assert.All(points.Take(4), footprint => Assert.False(footprint.Swath));
            Assert.All(points.Skip(4), footprint => Assert.True(footprint.Swath));
            Assert.Equal(style.Groups[0].Start.AddSeconds(30), points[3].Epoch);
            Assert.Equal(style.Groups[1].End, points[7].Epoch);
            Assert.All(points, footprint =>
            {
                Assert.Equal(48, footprint.Points.Count);
                Assert.InRange(footprint.MeanRangeKilometers, 2, 2.02);
                Assert.All(footprint.Points, point =>
                    Assert.InRange(Math.Abs(point.X * point.X / 4 + point.Y * point.Y + point.Z * point.Z - 1), 0, 1e-13));
            });

            var span = Assert.Single(tracks[1].Geometry!);
            var clipped = span with { Shape = span.Shape with { Sensor = span.Shape.Sensor! with { RangeKilometers = 2 } } };
            Assert.Empty(MissionFootprints.Calculate(style, tracks[1] with { Geometry = [clipped] }, axes, graph));
        }
        finally { directory.Delete(recursive: true); }
    }
}
