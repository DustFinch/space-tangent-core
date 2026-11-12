// SPDX-License-Identifier: GPL-2.0-or-later
using SpaceTangent.Contracts;
using SpaceTangent.Core.Addons;
using SpaceTangent.Core.Ephemerides;
using SpaceTangent.Core.World;
using Xunit;

namespace SpaceTangent.Core.Tests;

public sealed class MissionFootprintTests
{
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
