// SPDX-License-Identifier: GPL-2.0-or-later
using SpaceTangent.Contracts;
using SpaceTangent.Core.Addons;
using SpaceTangent.Core.World;
using Xunit;

namespace SpaceTangent.Core.Tests;

public sealed class OrderedSpiceCatalogueTests
{
    [OrderedSpiceFact]
    [Trait("Category", "ExternalData")]
    public void LaterKernelOnlyCatalogueCompletesTheOriginalCassiniInstrumentFrames()
    {
        string global = Environment.GetEnvironmentVariable("SPACETANGENT_SPICE_GLOBAL")!;
        string mission = Environment.GetEnvironmentVariable("SPACETANGENT_CASSINI_JSON_ROOT")!;
        string archive = Environment.GetEnvironmentVariable("SPACETANGENT_SPK_ARCHIVE")!;
        string oracle = Environment.GetEnvironmentVariable("SPACETANGENT_SPK_ORACLE")!;
        var folder = Directory.CreateTempSubdirectory();
        try
        {
            Copy(global, "cassini-catalog.json");
            Copy(global, "late.dat");
            Copy(archive, "naif0012.tls");
            foreach (string name in new[] { "040615AP_SK_04167_04186.bsp", "04182_04183pa_soi_itl.bc", "cas_iss_v10.ti", "cassini.3ds" })
                Copy(mission, name);
            File.WriteAllText(Path.Combine(folder.FullName, "late-catalogue.json"), """{"version":"1.0","spiceKernels":["late.dat"]}""");
            Assert.Throws<InvalidDataException>(() => SpiceJsonCatalog.Load("cassini-catalog.json", folder.FullName));
            var combined = SpiceJsonCatalog.Load(["cassini-catalog.json", "late-catalogue.json"], folder.FullName);
            var reference = SpiceJsonCatalog.Load("cassini.json", mission);
            Assert.Equal(2, combined.SensorCount);
            var track = Assert.Single(combined.Spacecraft);
            var original = Assert.Single(reference.Spacecraft);
            Assert.Equal("META_CASSINI", track.AttitudeFrame);
            double cos = .9063077870366499, sin = .42261826174069944;
            var toOriginal = new Basis3d(new(cos, -sin, 0), new(sin, cos, 0), new(0, 0, 1));
            foreach (double second in new[] { 141917558.4203589, 141918830.41202343 })
            {
                var epoch = JulianInstant.FromTdbSeconds(second);
                Assert.True(track.TrySample(epoch, out var state));
                Assert.True(original.TrySample(epoch, out var expected));
                Assert.Equal(expected, state);
                Assert.True(track.TryAttitude(epoch, out var attitude));
                Assert.True(original.TryAttitude(epoch, out var expectedAttitude));
                Assert.InRange((expectedAttitude.Transform(toOriginal.X) - attitude.X).Length, 0, 3e-8);
                Assert.InRange((expectedAttitude.Transform(toOriginal.Y) - attitude.Y).Length, 0, 3e-8);
                Assert.InRange((expectedAttitude.Transform(toOriginal.Z) - attitude.Z).Length, 0, 3e-8);
            }
            foreach (var sensor in combined.Sensors)
            {
                var expected = Assert.Single(reference.Sensors, item => item.Name == sensor.Name);
                Assert.InRange((expected.BoresightInBody - toOriginal.Transform(sensor.BoresightInBody)).Length, 0, 1e-14);
                Assert.Equal(expected.CornersInBody.Count, sensor.CornersInBody.Count);
                for (int index = 0; index < expected.CornersInBody.Count; index++)
                    Assert.InRange((expected.CornersInBody[index] - toOriginal.Transform(sensor.CornersInBody[index])).Length, 0, 1e-14);
            }

            ObjectId saturn = new("sol/saturn");
            var sampled = SampledMissionCatalog.Load(["cassini-catalog.json", "late-catalogue.json"], folder.FullName,
                name => name.Equals("Saturn", StringComparison.OrdinalIgnoreCase) ? saturn : null);
            Assert.Equal(3, sampled.Count);
            var graph = new WorldGraphBuilder();
            graph.AddTrajectory(saturn, _ => new(default, default));
            foreach (var missionTrack in sampled)
                graph.AddSampledMission(missionTrack);
            var epochOfInterest = JulianInstant.FromTdbSeconds(141917558.4203589);
            var snapshot = graph.Build().Evaluate(epochOfInterest);
            var cassini = Assert.Single(sampled, item => item.Name == track.Name);
            Assert.True(snapshot.TryState(cassini.Id, out var actualState));
            Assert.True(track.TrySample(epochOfInterest, out var expectedState));
            Assert.Equal(expectedState, actualState);
            Assert.True(snapshot.TryOrientation(cassini.Id, out var actualFrame));
            Assert.True(track.TryAttitude(epochOfInterest, out var expectedFrame));
            Assert.Equal(expectedFrame, actualFrame.ToWorld);
            foreach (var instrument in sampled.Where(item => item.Id != cassini.Id))
            {
                var geometry = Assert.IsType<MissionSensor>(Assert.Single(instrument.Geometry!).Shape.Sensor);
                var referenceSensor = Assert.Single(combined.Sensors, item => item.Name == instrument.Name);
                Assert.Equal(referenceSensor.BoresightInBody, geometry.Boresight);
                Assert.Equal(48, geometry.Boundary.Count);
                Assert.True(snapshot.TryState(instrument.Id, out var position));
                Assert.Equal(actualState, position);
            }

            Copy(oracle, "fixed-frames.tf");
            File.WriteAllText(Path.Combine(folder.FullName, "fixed-catalogue.json"), """
                {"version":"1.0","spiceKernels":["040615AP_SK_04167_04186.bsp","fixed-frames.tf"],"items":[
                  {"name":"Matrix Attitude","class":"spacecraft","center":"Saturn","trajectory":{"type":"Spice","target":"-82","center":"6"},
                   "bodyFrame":{"type":"Spice","name":"MATRIX_TEST"},"geometry":{"type":"Mesh","source":"cassini.3ds","size":0.006}},
                  {"name":"Quaternion Attitude","class":"spacecraft","center":"Saturn","trajectory":{"type":"Spice","target":"-82","center":"6"},
                   "bodyFrame":{"type":"Spice","name":"QUATERNION_TEST"},"geometry":{"type":"Mesh","source":"cassini.3ds","size":0.006}}
                ]}
                """);
            var fixedFrames = SpiceJsonCatalog.Load("fixed-catalogue.json", folder.FullName);
            Assert.Equal(2, fixedFrames.Spacecraft.Count);
            foreach (var fixedTrack in fixedFrames.Spacecraft)
            {
                Assert.True(fixedTrack.TryAttitude(JulianInstant.FromTdbSeconds(141917558.4203589), out var attitude));
                Assert.InRange((new Vector3d(0, 1, 0) - attitude.X).Length, 0, 1e-14);
                Assert.InRange((new Vector3d(-1, 0, 0) - attitude.Y).Length, 0, 1e-14);
            }
        }
        finally { folder.Delete(recursive: true); }

        void Copy(string source, string name) => File.Copy(Path.Combine(source, name), Path.Combine(folder.FullName, name));
    }

    [OrderedSpiceFact]
    [Trait("Category", "ExternalData")]
    public void SscAndJsonShareTheFinalPoolWithoutSharingPhysicalResourcePaths()
    {
        string global = Environment.GetEnvironmentVariable("SPACETANGENT_SPICE_GLOBAL")!;
        string mission = Environment.GetEnvironmentVariable("SPACETANGENT_CASSINI_JSON_ROOT")!;
        string archive = Environment.GetEnvironmentVariable("SPACETANGENT_SPK_ARCHIVE")!;
        var scratch = Directory.CreateTempSubdirectory();
        try
        {
            string bodies = scratch.CreateSubdirectory("bodies").FullName;
            string missions = scratch.CreateSubdirectory("missions").FullName;
            foreach (var name in new[] { "cassini-catalog.json", "late.dat" })
                File.Copy(Path.Combine(global, name), Path.Combine(missions, name));
            foreach (var name in new[] { "040615AP_SK_04167_04186.bsp", "04182_04183pa_soi_itl.bc", "cas_iss_v10.ti", "cassini.3ds" })
                File.Copy(Path.Combine(mission, name), Path.Combine(missions, name));
            File.Copy(Path.Combine(archive, "naif0012.tls"), Path.Combine(missions, "naif0012.tls"));
            File.Move(Path.Combine(missions, "late.dat"), Path.Combine(bodies, "late.dat"));
            File.WriteAllText(Path.Combine(bodies, "pack.json"), """
                {"formatVersion":19,"spiceLoads":[{"policy":"furnish","files":["late.dat"]}],
                 "sscCatalogs":[{"name":"body.ssc","text":"\"SSC Cassini\" \"Sol/Saturn\" { OrbitFrame { EclipticJ2000 {} } SpiceOrbit { Target \"CASSINI\" Origin \"6\" BoundingRadius 10000000 } BodyFrame { EquatorJ2000 {} } SpiceRotation { Frame \"META_CASSINI\" BaseFrame \"J2000\" } }"}]}
                """);
            ObjectId saturn = new("sol/saturn");
            ObjectId? Resolve(string name) => name.Equals("Saturn", StringComparison.OrdinalIgnoreCase) ? saturn : null;
            string[] catalogues = ["cassini-catalog.json"];
            Assert.Throws<InvalidDataException>(() => SampledMissionCatalog.Load(catalogues, missions, Resolve));
            var combined = SscBodyCatalogue.LoadWithMissions([bodies], catalogues, missions, Resolve, _ => { });
            var ssc = Assert.Single(combined.Bodies);
            var craft = Assert.Single(combined.Missions.Missions, item => item.Name == "Cassini SOI");
            Assert.Equal(3, combined.Missions.Missions.Count);
            var graph = new WorldGraphBuilder();
            graph.AddTrajectory(saturn, _ => new(default, default));
            graph.AddSampledMission(craft);
            var world = graph.Build();
            foreach (double second in new[] { 141917558.4203589, 141918830.41202343 })
            {
                var epoch = JulianInstant.FromTdbSeconds(second);
                var snapshot = world.Evaluate(epoch);
                Assert.True(snapshot.TryState(craft.Id, out var state));
                Assert.True(snapshot.TryOrientation(craft.Id, out var attitude));
                var fromSsc = ssc.Sample(epoch)!.Value;
                Assert.InRange((fromSsc.PositionKilometers - state.PositionKilometers).Length, 0, .001);
                Assert.InRange((fromSsc.VelocityKilometersPerSecond - state.VelocityKilometersPerSecond).Length, 0, 1e-8);
                Assert.Equal(attitude, ssc.SampleRotation!(epoch)!.Value);
            }
        }
        finally
        {
            scratch.Delete(recursive: true);
        }
    }

    private sealed class OrderedSpiceFactAttribute : FactAttribute
    {
        public OrderedSpiceFactAttribute()
        {
            if (new[] { "SPACETANGENT_SPICE_GLOBAL", "SPACETANGENT_CASSINI_JSON_ROOT", "SPACETANGENT_SPK_ARCHIVE", "SPACETANGENT_SPK_ORACLE" }
                .Any(key => string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(key))))
                Skip = "Set the archived SPICE pool and original Cassini kernel roots.";
        }
    }
}
