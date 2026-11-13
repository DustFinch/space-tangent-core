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
            string dedicatedFile = Path.Combine(missions, "cassini-catalog.json");
            var withDedicated = SscBodyCatalogue.LoadWithMissions([bodies], [], missions, Resolve, _ => { },
                dedicatedMission: dedicatedFile);
            var dedicated = Assert.Single(withDedicated.DedicatedMission!.Spacecraft);
            Assert.Equal(2, withDedicated.DedicatedMission.SensorCount);
            var dedicatedSsc = Assert.Single(withDedicated.Bodies);
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
                Assert.True(dedicated.TrySample(epoch, out var dedicatedState));
                Assert.Equal(state, dedicatedState);
                Assert.Equal(fromSsc, dedicatedSsc.Sample(epoch)!.Value);
                Assert.True(dedicated.TryAttitude(epoch, out var pointing));
                Assert.Equal(attitude.ToWorld, pointing);
            }
        }
        finally
        {
            scratch.Delete(recursive: true);
        }
    }

    [OrderedSpiceFact]
    [Trait("Category", "ExternalData")]
    public void MixedPackReplaysSscBetweenFurnishEventsAndIgnoresPreviewCopies()
    {
        string corpus = Environment.GetEnvironmentVariable("SPACETANGENT_SPICE_GLOBAL")!;
        var scratch = Directory.CreateTempSubdirectory();
        try
        {
            foreach (string name in new[] { "a.bsp", "b.bsp" })
                File.Copy(Path.Combine(corpus, name), Path.Combine(scratch.FullName, name));
            string manifest = Path.Combine(scratch.FullName, "pack.json");
            var epoch = JulianInstant.FromTdbSeconds(5);
            Check(0, 222);
            Check(1, 111);
            Assert.Throws<InvalidDataException>(() => Check(2, 0));

            void Check(int first, double expected)
            {
                var loads = first == 0
                    ? new[] { new { policy = "furnish", files = new[] { "missing-preview.bsp" } },
                        new { policy = "furnish", files = new[] { "b.bsp" } } }
                    : new[] { new { policy = "furnish", files = new[] { "b.bsp" } },
                        new { policy = "furnish", files = new[] { "missing-preview.bsp" } } };
                var catalogue = new
                {
                    name = "priority.ssc",
                    text = """
                        "Test" "Sol" {
                            OrbitFrame { EclipticJ2000 {} }
                            SpiceOrbit { Kernel "a.bsp" Target "1999999" Origin "0" BoundingRadius 300 }
                        }
                        """,
                    firstSpiceLoad = first,
                    spiceLoadCount = 1,
                    resources = new[] { new { kind = "data", source = "a.bsp", file = "a.bsp" } }
                };
                File.WriteAllText(manifest, System.Text.Json.JsonSerializer.Serialize(new
                {
                    formatVersion = 19, spiceLoads = loads, sscCatalogs = new[] { catalogue }
                }));
                var body = Assert.Single(SscBodyCatalogue.Load(scratch.FullName));
                Assert.InRange(Math.Abs(body.Sample(epoch)!.Value.PositionKilometers.X - expected), 0, 1e-12);
            }
        }
        finally
        {
            scratch.Delete(recursive: true);
        }
    }

    [OrderedSpiceFact]
    [Trait("Category", "ExternalData")]
    public void OnlyExplicitSscRepeatsAreSuppressed()
    {
        string corpus = Environment.GetEnvironmentVariable("SPACETANGENT_SPICE_GLOBAL")!;
        var scratch = Directory.CreateTempSubdirectory();
        try
        {
            foreach (string name in new[] { "a.bsp", "b.bsp" })
                File.Copy(Path.Combine(corpus, name), Path.Combine(scratch.FullName, name));
            File.WriteAllText(Path.Combine(scratch.FullName, "a.tm"), """
                KPL/MK
                \begindata
                KERNELS_TO_LOAD = ( 'a.bsp' )
                """);
            string path = Path.Combine(scratch.FullName, "priority.ssc");
            var epoch = JulianInstant.FromTdbSeconds(5);
            Check("a.bsp", 222, 111);
            Check("a.tm", 111, 111);
            Check("a.bsp", 111, 111, "./a.bsp");
            if (File.Exists(Path.Combine(scratch.FullName, "A.BSP")))
                Check("a.bsp", 111, 111, "A.BSP");

            void Check(string first, double expectedSsc, double expectedFurnish, string last = "a.bsp")
            {
                File.WriteAllText(path, $$"""
                    "Test" "Sol" {
                        OrbitFrame { EclipticJ2000 {} }
                        SpiceOrbit { Kernel [ "{{first}}" "b.bsp" "{{last}}" ]
                            Target "1999999" Origin "0" BoundingRadius 300 }
                    }
                    """);
                var ssc = Assert.Single(SscBodyCatalogue.Load(scratch.FullName));
                Assert.Equal(expectedSsc, ssc.Sample(epoch)!.Value.PositionKilometers.X);
                string pack = Path.Combine(scratch.FullName, "pack-" + Guid.NewGuid().ToString("N"));
                string externalPack = pack + "-output";
                // BuildPack keeps the input tree, so stage the source files in their own directory.
                Directory.CreateDirectory(pack);
                foreach (string input in new[] { "priority.ssc", "a.bsp", "b.bsp", "a.tm" })
                    File.Copy(Path.Combine(scratch.FullName, input), Path.Combine(pack, input));
                Assert.Equal(1, SscBodyCatalogue.BuildPack(pack, externalPack, _ => { }));
                var packed = Assert.Single(SscBodyCatalogue.Load(externalPack));
                Assert.Equal(expectedSsc, packed.Sample(epoch)!.Value.PositionKilometers.X);

                File.WriteAllText(Path.Combine(scratch.FullName, "kernels.json"),
                    """{"version":"1.0","spiceKernels":["a.bsp"]}""");
                var combined = SscBodyCatalogue.LoadWithMissions([scratch.FullName], ["kernels.json"], scratch.FullName,
                    _ => null, _ => { });
                Assert.Equal(expectedFurnish, Assert.Single(combined.Bodies).Sample(epoch)!.Value.PositionKilometers.X);
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
