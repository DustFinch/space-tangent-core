// SPDX-License-Identifier: GPL-2.0-or-later
using SpaceTangent.Contracts;
using SpaceTangent.Core.Addons;
using SpaceTangent.Core.Time;
using SpaceTangent.Core.World;
using Xunit;

namespace SpaceTangent.Core.Tests;

public sealed class OrderedSpiceCatalogueTests
{
    [ArchivedV16Fact]
    [Trait("Category", "ExternalData")]
    public void RelocatedCassiniV16SkipsSpiceForAvailableCustomModels()
    {
        string archive = Environment.GetEnvironmentVariable("SPACETANGENT_CASSINI_V16")!;
        string series = Environment.GetEnvironmentVariable("SPACETANGENT_VSOP87")!;
        var ephemeris = SpaceTangent.Core.Ephemerides.HistoricalSolarEphemeris.Load(
            SpaceTangent.Core.Ephemerides.De440Ephemeris.Load(Environment.GetEnvironmentVariable("SPACETANGENT_DE440")!), series);
        var scratch = Directory.CreateTempSubdirectory("st-v16-");
        try
        {
            string root = scratch.CreateSubdirectory("Перенесённый пак").FullName;
            foreach (string file in Directory.EnumerateFiles(Path.Combine(archive, "packed"), "*", SearchOption.AllDirectories))
            {
                string target = Path.Combine(root, Path.GetRelativePath(Path.Combine(archive, "packed"), file));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(file, target);
            }
            string packed = Path.Combine(root, "00-ssc");
            var warnings = new List<string>();
            Assert.Throws<FileNotFoundException>(() => SscBodyCatalogue.Load(packed));
            var custom = SscCustomModels.FromSolarSystem(ephemeris,
                naif => naif == 499 ? _ => new FrameKinematics(Basis3d.Identity, default(Vector3d)) : null);

            var before = SscBodyCatalogue.Load(packed, warning: warnings.Add, custom: custom);
            string rebuilt = Path.Combine(scratch.FullName, "Повторная упаковка");
            Assert.Equal(2, SscBodyCatalogue.BuildPack(Path.Combine(archive, "source", "00-ssc"), rebuilt,
                warnings.Add, custom: custom));
            Assert.Equal(2, warnings.Count);
            Assert.All(warnings, warning => Assert.Contains("resource was not indexed", warning));
            warnings.Clear();
            var roundtrip = SscBodyCatalogue.Load(rebuilt, warning: warnings.Add, custom: custom);
            Assert.Equal(before.Select(body => body.Name), roundtrip.Select(body => body.Name));
            var after = SscBodyCatalogue.LoadSources([packed, Path.Combine(root, "99-override")], warnings.Add, custom: custom);
            Assert.Empty(warnings);
            Assert.Equal(["Cassini SOI", "Unselected"], after.Select(body => body.Name));
            Assert.EndsWith("cassini.glb", after[0].Mesh);
            var epoch = JulianInstant.FromJulianDate(2453187.57, TimeScale.Tdb);
            Assert.Equal(ephemeris.SamplePlanetarySeries(4, epoch), after[1].Sample(epoch));
            Assert.Equal(Basis3d.Identity, after[1].SampleRotation!(epoch)!.Value.ToWorld);
            foreach (double day in new[] { 2453187.55, 2453187.57, 2453187.6 })
            {
                epoch = JulianInstant.FromJulianDate(day, TimeScale.Tdb);
                Assert.NotNull(before[0].Sample(epoch));
                Assert.Equal(before[0].Sample(epoch), after[0].Sample(epoch));
                Assert.Equal(before[0].Sample(epoch), roundtrip[0].Sample(epoch));
                var original = before[0].SampleRotation!(epoch)!.Value.ToWorld;
                var updated = after[0].SampleRotation!(epoch)!.Value.ToWorld;
                Assert.InRange(Vector3d.Dot(original.X, updated.X), Math.Cos(Math.PI / 18) - 1e-10,
                    Math.Cos(Math.PI / 18) + 1e-10);
            }
        }
        finally { scratch.Delete(recursive: true); }
    }

    [ArchivedV16Fact]
    [Trait("Category", "ExternalData")]
    public void CassiniArchiveCustomModelsTakePrecedenceInsideTimelinePhases()
    {
        string archive = Environment.GetEnvironmentVariable("SPACETANGENT_CASSINI_V16")!;
        string source = Path.Combine(archive, "source", "00-ssc");
        var ephemeris = SpaceTangent.Core.Ephemerides.HistoricalSolarEphemeris.Load(
            SpaceTangent.Core.Ephemerides.De440Ephemeris.Load(Environment.GetEnvironmentVariable("SPACETANGENT_DE440")!),
            Environment.GetEnvironmentVariable("SPACETANGENT_VSOP87")!);
        var scratch = Directory.CreateTempSubdirectory("st-v16-timeline-");
        try
        {
            string loose = scratch.CreateSubdirectory("source").FullName;
            foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
            {
                string target = Path.Combine(loose, Path.GetRelativePath(source, file));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(file, target);
            }

            // Use the archived Cassini data and move only the competing models in the test entry into a phase.
            string catalogue = Path.Combine(loose, "cassini.ssc");
            string text = File.ReadAllText(catalogue);
            int start = text.IndexOf("    CustomOrbit", StringComparison.Ordinal);
            int end = text.LastIndexOf('}');
            Assert.InRange(start, 1, end - 1);
            File.WriteAllText(catalogue, text[..start] + "    Timeline [ {\n" + text[start..end] + "    } ]\n}\n");
            var fixedSpin = new FrameKinematics(new(new(0, 1, 0), new(-1, 0, 0), new(0, 0, 1)), default(Vector3d));
            var custom = SscCustomModels.FromSolarSystem(ephemeris, naif => naif == 499 ? _ => fixedSpin : null);
            var diagnostics = new List<string>();
            var packed = Path.Combine(scratch.FullName, "pack");
            Assert.Equal(2, SscBodyCatalogue.BuildPack(loose, packed, diagnostics.Add, custom: custom));
            Assert.DoesNotContain(diagnostics, message => message.Contains("poison.dat", StringComparison.Ordinal));
            Check(SscBodyCatalogue.Load(loose, warning: diagnostics.Add, custom: custom));
            Check(SscBodyCatalogue.Load(packed, warning: diagnostics.Add, custom: custom));

            void Check(IReadOnlyList<SscBodyDefinition> bodies)
            {
                var body = Assert.Single(bodies, entry => entry.Name == "Unselected");
                var sun = new ObjectId("sol/sun");
                var id = new ObjectId("ssc/sol/unselected");
                var builder = new WorldGraphBuilder();
                builder.AddTrajectory(sun, _ => new CartesianState(default, default));
                builder.AddSampledMission(body.BindTimeline!(id, path => path == "Sol" ? sun : throw new InvalidDataException(path)));
                var epoch = JulianInstant.FromJulianDate(2453187.57, TimeScale.Tdb);
                var world = builder.Build().Evaluate(epoch);
                Assert.True(world.TryState(id, out var state));
                Assert.Equal(ephemeris.SamplePlanetarySeries(4, epoch), state);
                Assert.True(world.TryOrientation(id, out var orientation));
                Assert.Equal(fixedSpin, orientation);
            }
        }
        finally { scratch.Delete(recursive: true); }
    }

    private sealed class ArchivedV16FactAttribute : FactAttribute
    {
        public ArchivedV16FactAttribute()
        {
            if (!Directory.Exists(Environment.GetEnvironmentVariable("SPACETANGENT_CASSINI_V16")) ||
                !File.Exists(Environment.GetEnvironmentVariable("SPACETANGENT_VSOP87")) ||
                !File.Exists(Environment.GetEnvironmentVariable("SPACETANGENT_DE440")))
                Skip = "Set the original v16 pack, VSOP87 series and DE440.";
        }
    }

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

    [OrderedSpiceFact]
    [Trait("Category", "ExternalData")]
    public void MissingExplicitKernelDoesNotStopTheNextSscModelFromLoadingItsOtherKernels()
    {
        string corpus = Environment.GetEnvironmentVariable("SPACETANGENT_SPICE_GLOBAL")!;
        var scratch = Directory.CreateTempSubdirectory();
        try
        {
            File.Copy(Path.Combine(corpus, "b.bsp"), Path.Combine(scratch.FullName, "b.bsp"));
            string path = Path.Combine(scratch.FullName, "priority.ssc");
            File.WriteAllText(path, """
                "First" "Sol" {
                    OrbitFrame { EclipticJ2000 {} }
                    SpiceOrbit { Kernel "absent.bsp" Target "1999999" Origin "0" BoundingRadius 300 }
                    FixedPosition [ 1 0 0 ]
                }
                "Second" "Sol" {
                    OrbitFrame { EclipticJ2000 {} }
                    SpiceOrbit { Kernel [ "absent.bsp" "b.bsp" ] Target "1999999" Origin "0" BoundingRadius 300 }
                }
                "Third" "Sol" {
                    OrbitFrame { EclipticJ2000 {} }
                    SpiceOrbit { Kernel "absent.bsp" Target "1999999" Origin "0" BoundingRadius 300 }
                    FixedPosition [ 3 0 0 ]
                }
                """);
            var epoch = JulianInstant.FromTdbSeconds(5);
            var warnings = new List<string>();
            var bodies = SscBodyCatalogue.Load(scratch.FullName, warning: warnings.Add);
            Assert.Equal(new[] { 1d, 222d, 3d }, bodies.Select(body => body.Sample(epoch)!.Value.PositionKilometers.X));
            Assert.Equal(2, warnings.Count);
            string pack = Path.Combine(scratch.FullName, "packed");
            string loose = Path.Combine(scratch.FullName, "loose");
            Directory.CreateDirectory(loose);
            File.Copy(path, Path.Combine(loose, "priority.ssc"));
            File.Copy(Path.Combine(scratch.FullName, "b.bsp"), Path.Combine(loose, "b.bsp"));
            warnings.Clear();
            Assert.Equal(3, SscBodyCatalogue.BuildPack(loose, pack, warnings.Add));
            Assert.Equal(2, warnings.Count);
            warnings.Clear();
            var packed = SscBodyCatalogue.Load(pack, warning: warnings.Add);
            Assert.Equal(new[] { 1d, 222d, 3d }, packed.Select(body => body.Sample(epoch)!.Value.PositionKilometers.X));
            Assert.Equal(2, warnings.Count);

            File.Copy(Path.Combine(corpus, "a.bsp"), Path.Combine(scratch.FullName, "absent.bsp"));
            warnings.Clear();
            bodies = SscBodyCatalogue.Load(scratch.FullName, warning: warnings.Add);
            Assert.Equal(new[] { 222d, 222d, 222d }, bodies.Select(body => body.Sample(epoch)!.Value.PositionKilometers.X));
            Assert.Empty(warnings);
        }
        finally
        {
            scratch.Delete(recursive: true);
        }
    }

    [OrderedSpiceFact]
    [Trait("Category", "ExternalData")]
    public void SourceAndPackedMissionsShareOrderedKernelsAndKeepTheirResourceOwners()
    {
        string corpus = Environment.GetEnvironmentVariable("SPACETANGENT_SPICE_GLOBAL")!;
        var scratch = Directory.CreateTempSubdirectory();
        try
        {
            string loose = scratch.CreateSubdirectory("loose").FullName;
            string packed = scratch.CreateSubdirectory("packed").FullName;
            File.Copy(Path.Combine(corpus, "a.bsp"), Path.Combine(loose, "track.bsp"));
            File.Copy(Path.Combine(corpus, "b.bsp"), Path.Combine(packed, "track.bsp"));
            File.WriteAllText(Path.Combine(loose, "source.json"), """
                {"version":"1.0","spiceKernels":["track.bsp"],"items":[
                    {"name":"Source track","center":"Sun","trajectory":{"type":"Spice","target":1999999,"center":0}}]}
                """);
            File.WriteAllText(Path.Combine(packed, "pack.json"), """
                {"formatVersion":19,
                 "spiceLoads":[{"policy":"furnish","files":["unused-preview.bsp"]},{"policy":"furnish","files":["track.bsp"]}],
                 "sscCatalogs":[{"name":"preview.ssc","text":"","firstSpiceLoad":0,"spiceLoadCount":1}],
                 "bodies":[{"path":"Sol/Packed Track","name":"Packed track","parent":"Sol","radiusKm":1,
                     "rotation":{"kind":"quaternion","frame":{"kind":"equatorJ2000"},"quaternion":[1,0,0,0]},
                     "orbit":{"kind":"spice","target":1999999,"spiceCenter":0}}]}
                """);
            ObjectId sun = new("sol/sun");
            ObjectId? Resolve(string name) => name is "Sun" or "Sol" ? sun : null;
            Check(["loose/source.json", "packed/pack.json"], 222);
            Check(["packed/pack.json", "loose/source.json"], 111);

            void Check(string[] catalogues, double x)
            {
                var loaded = SscBodyCatalogue.LoadWithMissions([], catalogues, scratch.FullName, Resolve, _ => { });
                Assert.Equal(2, loaded.Missions.Missions.Count);
                var builder = new WorldGraphBuilder();
                builder.AddTrajectory(sun, _ => new(default, default));
                foreach (var mission in loaded.Missions.Missions)
                    builder.AddSampledMission(mission);
                var snapshot = builder.Build().Evaluate(JulianInstant.FromTdbSeconds(5));
                foreach (var mission in loaded.Missions.Missions)
                {
                    Assert.True(snapshot.TryState(mission.Id, out var state));
                    Assert.Equal(x, state.PositionKilometers.X, 10);
                }
            }
        }
        finally
        {
            scratch.Delete(recursive: true);
        }
    }

    [OrderedSpiceFact]
    [Trait("Category", "ExternalData")]
    public void DisconnectedInitialSpkStateSelectsTheAuthoredAlternate()
    {
        string corpus = Environment.GetEnvironmentVariable("SPACETANGENT_SPICE_GLOBAL")!;
        var scratch = Directory.CreateTempSubdirectory();
        try
        {
            string source = scratch.CreateSubdirectory("source").FullName;
            File.Copy(Path.Combine(corpus, "a.bsp"), Path.Combine(source, "a.bsp"));
            File.WriteAllText(Path.Combine(source, "fallback.ssc"), """
                "Disconnected" "Sol" {
                    SpiceOrbit { Kernel "a.bsp" Target "1999999" Origin "1999998" BoundingRadius 300 }
                    FixedPosition [ 3 0 0 ]
                }
                "Connected" "Sol" {
                    SpiceOrbit { Kernel "a.bsp" Target "1999999" Origin "0" BoundingRadius 300 }
                    FixedPosition [ 4 0 0 ]
                }
                """);
            Check(source);
            string packed = Path.Combine(scratch.FullName, "packed");
            Assert.Equal(2, SscBodyCatalogue.BuildPack(source, packed, _ => { }));
            Check(packed);

            void Check(string root)
            {
                var warnings = new List<string>();
                var bodies = SscBodyCatalogue.Load(root, warning: warnings.Add);
                var epoch = JulianInstant.FromTdbSeconds(5);
                Assert.Equal(new[] { 3d, 111d }, bodies.Select(body => body.Sample(epoch)!.Value.PositionKilometers.X));
                Assert.Contains("No connected SPICE state", Assert.Single(warnings));
            }
        }
        finally
        {
            scratch.Delete(recursive: true);
        }
    }


    [OrderedSpiceFact]
    [Trait("Category", "ExternalData")]
    public void ExplicitSscIntervalRequiresContinuousTargetCoverage()
    {
        string corpus = Environment.GetEnvironmentVariable("SPACETANGENT_SPICE_GLOBAL")!;
        var scratch = Directory.CreateTempSubdirectory();
        try
        {
            string source = scratch.CreateSubdirectory("source").FullName;
            File.Copy(Path.Combine(corpus, "a.bsp"), Path.Combine(source, "a.bsp"));
            File.WriteAllText(Path.Combine(source, "intervals.ssc"), """
                "Covered" "Sol" {
                    SpiceOrbit { Kernel "a.bsp" Target "1999999" Origin "0" BoundingRadius 300
                        Beginning 2451545 Ending 2451545.0001 }
                    FixedPosition [ 3 0 0 ]
                }
                "Ends outside coverage" "Sol" {
                    Timeline [
                        { SpiceOrbit { Kernel "a.bsp" Target "1999999" Origin "0" BoundingRadius 300
                              Beginning 2451545 Ending 2451545.0002 }
                          FixedPosition [ 4 0 0 ] }
                    ]
                }
                """);
            Check(source);
            string packed = Path.Combine(scratch.FullName, "packed");
            Assert.Equal(2, SscBodyCatalogue.BuildPack(source, packed, _ => { }));
            Check(packed);

            void Check(string root)
            {
                var warnings = new List<string>();
                var bodies = SscBodyCatalogue.Load(root, warning: warnings.Add);
                var epoch = JulianInstant.FromTdbSeconds(5);
                Assert.Equal(111d, bodies[0].Sample(epoch)!.Value.PositionKilometers.X);
                var center = new ObjectId("sol");
                var id = new ObjectId("interval-fallback");
                var graph = new WorldGraphBuilder();
                graph.AddTrajectory(center, _ => new CartesianState(default, default));
                graph.AddSampledMission(bodies[1].BindTimeline!(id, _ => center));
                Assert.True(graph.Build().Evaluate(epoch).TryState(id, out var state));
                Assert.Equal(4d, state.PositionKilometers.X);
                Assert.Contains("interval is not entirely covered", Assert.Single(warnings));
            }
        }
        finally
        {
            scratch.Delete(recursive: true);
        }
    }

    [OrderedSpiceFact]
    [Trait("Category", "ExternalData")]
    public void AuthoredBodyNamesAndNumericPrefixesMatchNativeSscStates()
    {
        string corpus = Path.Combine(Path.GetDirectoryName(Environment.GetEnvironmentVariable("SPACETANGENT_SPICE_GLOBAL")!)!, "spice-init");
        string original = Environment.GetEnvironmentVariable("SPACETANGENT_CASSINI_SSC_ROOT")!;
        var scratch = Directory.CreateTempSubdirectory();
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(corpus, "reference.json")));
            var rows = document.RootElement.GetProperty("rows").EnumerateArray().ToArray();
            var aliases = rows.Single(row => row.GetProperty("case").GetString() == "target-alias-frozen").GetProperty("results");
            var prefix = rows.Single(row => row.GetProperty("case").GetString() == "numeric-id-prefix-fallback").GetProperty("results");
            foreach (var (file, expected) in new[] { ("alias-a.tf", aliases[1]), ("alias-b.tf", aliases[5]) })
            {
                string source = scratch.CreateSubdirectory(Path.GetFileNameWithoutExtension(file)).FullName;
                foreach (string asset in new[] { file, "aliases.bsp", "linear.bsp" })
                    File.Copy(Path.Combine(corpus, asset), Path.Combine(source, asset));
                File.WriteAllText(Path.Combine(source, "names.ssc"), $$"""
                    "Named" "Sol" {
                        OrbitFrame { EquatorJ2000 {} }
                        SpiceOrbit { Kernel [ "{{file}}" "aliases.bsp" ] Target "prefix_body" Origin "SSB" BoundingRadius 300 }
                        FixedPosition [ 3 0 0 ]
                    }
                    "Numeric prefix" "Sol" {
                        OrbitFrame { EquatorJ2000 {} }
                        SpiceOrbit { Kernel "linear.bsp" Target "1999999tail" Origin "+0 tail" BoundingRadius 300 }
                        FixedPosition [ 4 0 0 ]
                    }
                    """);
                Check(source);
                string packed = Path.Combine(scratch.FullName, Path.GetFileNameWithoutExtension(file) + "-packed");
                Assert.Equal(2, SscBodyCatalogue.BuildPack(source, packed, _ => { }));
                Check(packed);

                void Check(string root)
                {
                    var warnings = new List<string>();
                    var bodies = SscBodyCatalogue.Load(root, warning: warnings.Add);
                    Assert.Empty(warnings);
                    CheckState(bodies[0], expected);
                    CheckState(bodies[1], prefix[1]);
                }
            }

            string mission = scratch.CreateSubdirectory("mission").FullName;
            foreach (string file in Directory.EnumerateFiles(original))
                File.Copy(file, Path.Combine(mission, Path.GetFileName(file)));
            string catalogue = Path.Combine(mission, "cassini.ssc");
            string ssc = File.ReadAllText(catalogue);
            Assert.Contains("Target \"CASSINI\"", ssc);
            ssc = ssc.Replace("Target \"CASSINI\"", "Target \"Cassini spacecraft\"", StringComparison.Ordinal);
            ssc = ssc.Replace("Origin \"6\"", "Origin \"Saturn system\"", StringComparison.Ordinal);
            int kernelList = ssc.IndexOf("Kernel [", StringComparison.Ordinal);
            Assert.True(kernelList >= 0);
            ssc = ssc.Insert(kernelList + "Kernel [".Length, " \"body-names.tf\"");
            File.WriteAllText(catalogue, ssc);
            File.WriteAllText(Path.Combine(mission, "body-names.tf"), """
                KPL/FK
                \begindata
                NAIF_BODY_NAME = ( 'CASSINI SPACECRAFT', 'SATURN SYSTEM' )
                NAIF_BODY_CODE = ( -82, 6 )
                \begintext
                """);
            var epoch = JulianInstant.FromTdbSeconds(141917558.4203589);
            var legacy = Assert.Single(SscSpiceCatalog.Load(Path.Combine(original, "cassini.ssc"), original));
            var aliased = Assert.Single(SscSpiceCatalog.Load(catalogue, mission));
            Assert.True(legacy.TrySample(epoch, out var baseline));
            Assert.True(aliased.TrySample(epoch, out var actual));
            Assert.Equal(baseline, actual);
            File.WriteAllText(Path.Combine(mission, "aliased-cassini.json"), """
                {"version":"1.0",
                 "spiceKernels":["body-names.tf","040615AP_SK_04167_04186.bsp"],
                 "items":[{"name":"Cassini","class":"spacecraft","center":"Saturn",
                   "trajectory":{"type":"Spice","target":"Cassini spacecraft","center":"Saturn system"},
                   "bodyFrame":{"type":"Spice","name":"J2000"},
                   "geometry":{"type":"Mesh","source":"cassini.3ds","size":0.006}}]}
                """);
            var json = Assert.Single(SpiceJsonCatalog.Load("aliased-cassini.json", mission).Spacecraft);
            Assert.True(json.TrySample(epoch, out var jsonState));
            Assert.Equal(baseline, jsonState);
            var generic = Assert.Single(SscBodyCatalogue.Load(mission));
            var packedMission = Path.Combine(scratch.FullName, "mission-packed");
            Assert.Equal(1, SscBodyCatalogue.BuildPack(mission, packedMission, _ => { }));
            var originalBody = Assert.Single(SscBodyCatalogue.Load(original));
            Assert.NotNull(originalBody.Sample(epoch));
            Assert.Equal(originalBody.Sample(epoch), generic.Sample(epoch));
            Assert.Equal(originalBody.Sample(epoch), Assert.Single(SscBodyCatalogue.Load(packedMission)).Sample(epoch));
        }
        finally
        {
            scratch.Delete(recursive: true);
        }

    }

    [OrderedSpiceFact]
    [Trait("Category", "ExternalData")]
    public void SscNavigationHoldsNativeOrbitEndpointsAndSurvivesADisconnectedOrigin()
    {
        string corpus = Path.Combine(Path.GetDirectoryName(Environment.GetEnvironmentVariable("SPACETANGENT_SPICE_GLOBAL")!)!, "spice-init");
        var scratch = Directory.CreateTempSubdirectory();
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(corpus, "reference.json")));
            var rows = document.RootElement.GetProperty("rows").EnumerateArray().ToArray();
            string[] cases = ["default-window", "first-coverage-interval", "explicit-second-interval", "explicit-complete-window"];
            string source = scratch.CreateSubdirectory("source").FullName;
            foreach (string name in new[] { "linear.bsp", "gaps.bsp" })
                File.Copy(Path.Combine(corpus, name), Path.Combine(source, name));
            File.WriteAllText(Path.Combine(source, "windows.ssc"), """
                "Default" "Sol" {
                    OrbitFrame { EquatorJ2000 {} }
                    SpiceOrbit { Kernel "linear.bsp" Target "1999999" Origin "0" BoundingRadius 500 }
                    FixedPosition [ 3 0 0 ]
                }
                "First" "Sol" {
                    OrbitFrame { EquatorJ2000 {} }
                    SpiceOrbit { Kernel "gaps.bsp" Target "1999999" Origin "0" BoundingRadius 500 }
                }
                "Second" "Sol" {
                    OrbitFrame { EquatorJ2000 {} }
                    SpiceOrbit { Kernel "gaps.bsp" Target "1999999" Origin "0" BoundingRadius 500
                        Beginning 2451545.0002314814 Ending 2451545.000347222 }
                }
                "Explicit" "Sol" {
                    OrbitFrame { EquatorJ2000 {} }
                    SpiceOrbit { Kernel "linear.bsp" Target "1999999" Origin "0" BoundingRadius 500
                        Beginning 2451545 Ending 2451545.0001157406 }
                }
                "Solar system barycenter" "Sol" {
                    SpiceOrbit { Target "0" Origin "0" BoundingRadius 500 }
                }
                "Limited lifetime" "Sol" {
                    Beginning 2451545 Ending 2451545.0001157406
                    SpiceOrbit { Kernel "linear.bsp" Target "1999999" Origin "0" BoundingRadius 500 }
                }
                """);
            Check(source);
            string packed = Path.Combine(scratch.FullName, "packed");
            Assert.Equal(6, SscBodyCatalogue.BuildPack(source, packed, _ => { }));
            Check(packed);

            string gap = scratch.CreateSubdirectory("origin-gap").FullName;
            File.Copy(Path.Combine(corpus, "origin-gap.bsp"), Path.Combine(gap, "origin-gap.bsp"));
            File.WriteAllText(Path.Combine(gap, "origin-gap.ssc"), """
                "Origin gap" "Sol" {
                    OrbitFrame { EquatorJ2000 {} }
                    SpiceOrbit { Kernel "origin-gap.bsp" Target "1999999" Origin "1999988" BoundingRadius 500 }
                    FixedPosition [ 3 0 0 ]
                }
                """);
            string packedGap = Path.Combine(scratch.FullName, "origin-gap-packed");
            Assert.Equal(1, SscBodyCatalogue.BuildPack(gap, packedGap, _ => { }));
            foreach (string root in new[] { gap, packedGap })
            {
                var body = Assert.Single(SscBodyCatalogue.Load(root));
                Assert.InRange(body.Sample(JulianInstant.FromTdbSeconds(2))!.Value.PositionKilometers.X, 101.9999999, 102.0000001);
                var held = body.Sample(JulianInstant.FromTdbSeconds(7))!.Value;
                Assert.InRange(held.PositionKilometers.X, 99.9999999, 100.0000001);
                Assert.InRange(held.VelocityKilometersPerSecond.X, .999999999, 1.000000001);
            }

            void Check(string root)
            {
                var warnings = new List<string>();
                var bodies = SscBodyCatalogue.Load(root, warning: warnings.Add);
                Assert.Empty(warnings);
                for (int index = 0; index < cases.Length; index++)
                {
                    var row = rows.Single(row => row.GetProperty("case").GetString() == cases[index]);
                    foreach (var reference in row.GetProperty("results").EnumerateArray().Skip(1))
                        CheckState(bodies[index], reference);
                }
                var epoch = JulianInstant.FromTdbSeconds(-5);
                Assert.Equal(default(CartesianState), bodies[4].Sample(epoch));
                Assert.Null(bodies[5].Sample(epoch));
            }
        }
        finally
        {
            scratch.Delete(recursive: true);
        }
    }

    [OrderedSpiceFact]
    [Trait("Category", "ExternalData")]
    public void BadSpkSampleFallsBackToAConnectedBoundaryWithoutStoppingTimeline()
    {
        string corpus = Path.Combine(Path.GetDirectoryName(Environment.GetEnvironmentVariable("SPACETANGENT_SPICE_GLOBAL")!)!, "spice-init");
        var scratch = Directory.CreateTempSubdirectory();
        try
        {
            string source = scratch.CreateSubdirectory("source").FullName;
            byte[] kernel = File.ReadAllBytes(Path.Combine(corpus, "gaps.bsp"));
            // Change only a payload value in the second segment. Its descriptor, epochs and directory remain valid.
            const int secondSegmentXOffset = (401 - 1) * sizeof(double);
            System.Buffers.Binary.BinaryPrimitives.WriteDoubleLittleEndian(kernel.AsSpan(secondSegmentXOffset), double.NaN);
            string kernelFile = Path.Combine(source, "damaged-record.bsp");
            File.WriteAllBytes(kernelFile, kernel);
            var spk = new SpaceTangent.Core.Spice.SpkKernel(kernelFile);
            var badEpoch = JulianInstant.FromTdbSeconds(25);
            Assert.Throws<InvalidDataException>(() => spk.TryState(1999999, 0, badEpoch, out _));
            File.WriteAllText(Path.Combine(source, "damaged.ssc"), """
                "Damaged" "Sol" {
                    Timeline [
                        { OrbitFrame { EquatorJ2000 {} }
                          SpiceOrbit { Kernel "damaged-record.bsp" Target "1999999" Origin "0" BoundingRadius 500
                              Beginning 2451545.0002314814 Ending 2451545.000347222 } }
                    ]
                }
                "Healthy" "Sol" {
                    OrbitFrame { EquatorJ2000 {} }
                    SpiceOrbit { Kernel "damaged-record.bsp" Target "1999999" Origin "0" BoundingRadius 500 }
                }
                """);
            string packed = Path.Combine(scratch.FullName, "packed");
            Assert.Equal(2, SscBodyCatalogue.BuildPack(source, packed, _ => { }));
            foreach (string root in new[] { source, packed })
            {
                var warnings = new List<string>();
                var bodies = SscBodyCatalogue.Load(root, warning: warnings.Add);
                var id = new ObjectId("damaged");
                var center = new ObjectId("sol");
                var graph = new WorldGraphBuilder();
                graph.AddTrajectory(center, _ => new CartesianState(default, default));
                graph.AddSampledMission(bodies[0].BindTimeline!(id, _ => center));
                var world = graph.Build();
                Assert.Empty(warnings);
                Assert.True(world.Evaluate(badEpoch).TryState(id, out var held));
                Assert.InRange(held.PositionKilometers.X, 109.9999999, 110.0000001);
                Assert.InRange(held.VelocityKilometersPerSecond.X, .999999999, 1.000000001);
                Assert.True(world.Evaluate(badEpoch.AddSeconds(1)).TryState(id, out _));
                Assert.Contains("Timeline phase 1: SpiceOrbit state failed", Assert.Single(warnings));
                Assert.InRange(bodies[1].Sample(JulianInstant.FromTdbSeconds(5))!.Value.PositionKilometers.X, 104.9999999, 105.0000001);
            }
        }
        finally
        {
            scratch.Delete(recursive: true);
        }
    }

    [OrderedSpiceFact]
    [Trait("Category", "ExternalData")]
    public void SscInitializationFreezesAliasesAndCoverageBeforeLaterLoads()
    {
        string corpus = Path.Combine(Path.GetDirectoryName(Environment.GetEnvironmentVariable("SPACETANGENT_SPICE_GLOBAL")!)!, "spice-init");
        using var reference = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(corpus, "reference.json")));
        var rows = reference.RootElement.GetProperty("rows").EnumerateArray().ToArray();
        var scratch = Directory.CreateTempSubdirectory();
        try
        {
            string source = scratch.CreateSubdirectory("source").FullName;
            foreach (string file in new[] { "linear.bsp", "wide.bsp", "aliases.bsp", "alias-a.tf", "alias-b.tf", "fixed-frame.tf" })
                File.Copy(Path.Combine(corpus, file), Path.Combine(source, file));
            File.WriteAllText(Path.Combine(source, "01-early.ssc"), """
                "Missing SPK" "Sol" {
                    SpiceOrbit { Target "1999999" Origin "0" BoundingRadius 500 }
                    FixedPosition [ 3 0 0 ]
                }
                "Missing alias" "Sol" {
                    SpiceOrbit { Target "PREFIX_BODY" Origin "0" BoundingRadius 500 }
                    FixedPosition [ 4 0 0 ]
                }
                "Missing frame" "Sol" {
                    FixedPosition [ 5 0 0 ]
                    BodyFrame { EquatorJ2000 {} }
                    SpiceRotation { Frame "PREFIX_FRAME" BaseFrame "J2000" }
                    FixedRotation { MeridianAngle 10 }
                }
                "Early" "Sol" {
                    OrbitFrame { EquatorJ2000 {} }
                    SpiceOrbit { Kernel "linear.bsp" Target "1999999" Origin "0" BoundingRadius 500 }
                }
                """);
            File.WriteAllText(Path.Combine(source, "02-wide.ssc"), """
                "Wide" "Sol" {
                    OrbitFrame { EquatorJ2000 {} }
                    SpiceOrbit { Kernel [ "wide.bsp" "alias-b.tf" ] Target "1999999" Origin "0" BoundingRadius 500 }
                }
                "Late frame" "Sol" {
                    FixedPosition [ 6 0 0 ]
                    BodyFrame { EquatorJ2000 {} }
                    SpiceRotation { Kernel "fixed-frame.tf" Frame "PREFIX_FRAME" BaseFrame "J2000" }
                }
                """);
            Check(source, (bodies, warnings) =>
            {
                Assert.Equal(3, warnings.Count);
                Assert.Contains("No SPK coverage", warnings[0]);
                Assert.Contains("Unknown NAIF object PREFIX_BODY", warnings[1]);
                Assert.Contains("Unknown SSC SPICE frame PREFIX_FRAME", warnings[2]);
                var epoch = JulianInstant.FromTdbSeconds(5);
                Assert.Equal(3d, bodies[0].Sample(epoch)!.Value.PositionKilometers.X);
                Assert.Equal(4d, bodies[1].Sample(epoch)!.Value.PositionKilometers.X);
                var expected = Results("late-window-freeze");
                CheckState(bodies[3], expected[3]);
                CheckState(bodies[4], expected[5]);
                var early = bodies[2].SampleRotation!(epoch)!.Value.ToWorld;
                var late = bodies[5].SampleRotation!(epoch)!.Value.ToWorld;
                Assert.InRange(Math.Abs(late.X.X - Math.Cos(25 * Math.PI / 180)), 0, 1e-13);
                Assert.NotEqual(early, late);
            });

            string aliases = scratch.CreateSubdirectory("aliases").FullName;
            foreach (string file in new[] { "aliases.bsp", "alias-a.tf", "alias-b.tf" })
                File.Copy(Path.Combine(corpus, file), Path.Combine(aliases, file));
            File.WriteAllText(Path.Combine(aliases, "aliases.ssc"), """
                "Required external alias" "Sol" {
                    OrbitFrame { EquatorJ2000 {} }
                    SpiceOrbit { Target "PREFIX_BODY" Origin "0" BoundingRadius 500 }
                }
                "Early alias" "Sol" {
                    OrbitFrame { EquatorJ2000 {} }
                    SpiceOrbit { Kernel [ "alias-a.tf" "aliases.bsp" ] Target "PREFIX_BODY" Origin "0" BoundingRadius 500 }
                }
                "Late alias" "Sol" {
                    OrbitFrame { EquatorJ2000 {} }
                    SpiceOrbit { Kernel "alias-b.tf" Target "PREFIX_BODY" Origin "0" BoundingRadius 500 }
                }
                """);
            Check(aliases, (bodies, warnings) =>
            {
                Assert.Empty(warnings);
                var expected = Results("target-alias-frozen");
                CheckState(bodies[0], expected[5]);
                CheckState(bodies[1], expected[3]);
                CheckState(bodies[2], expected[5]);
            });
        }
        finally
        {
            scratch.Delete(recursive: true);
        }

        System.Text.Json.JsonElement Results(string name) =>
            rows.Single(row => row.GetProperty("case").GetString() == name).GetProperty("results");

        void Check(string source, Action<IReadOnlyList<SscBodyDefinition>, List<string>> verify)
        {
            string packed = source + "-packed";
            SscBodyCatalogue.BuildPack(source, packed, _ => { });
            foreach (string folder in new[] { source, packed })
            {
                var warnings = new List<string>();
                var bodies = SscBodyCatalogue.Load(folder, warning: warnings.Add);
                verify(bodies, warnings);
            }
        }
    }

    [OrderedSpiceFact]
    [Trait("Category", "ExternalData")]
    public void ReplacedAndModifiedBodiesLeaveTheirPreviouslyLoadedKernelsAvailable()
    {
        string corpus = Path.Combine(Path.GetDirectoryName(Environment.GetEnvironmentVariable("SPACETANGENT_SPICE_GLOBAL")!)!, "spice-init");
        var scratch = Directory.CreateTempSubdirectory();
        try
        {
            string source = scratch.CreateSubdirectory("source").FullName;
            string data = Directory.CreateDirectory(Path.Combine(source, "data")).FullName;
            foreach (string file in new[] { "linear.bsp", "alias-a.tf" })
                File.Copy(Path.Combine(corpus, file), Path.Combine(source, file));
            foreach (string file in new[] { "wide.bsp", "alias-b.tf", "aliases.bsp" })
                File.Copy(Path.Combine(corpus, file), Path.Combine(data, file));
            File.WriteAllText(Path.Combine(source, "01-initial.ssc"), """
                "Loader" "Sol" {
                    SpiceOrbit { Kernel [ "linear.bsp" "alias-a.tf" ] Target "1999999" Origin "0" BoundingRadius 500 }
                }
                "Observer" "Sol" {
                    OrbitFrame { EquatorJ2000 {} }
                    SpiceOrbit { Target "PREFIX_BODY" Origin "0" BoundingRadius 500 }
                    FixedPosition [ 3 0 0 ]
                }
                "Missing" "Sol" {
                    SpiceOrbit { Kernel "absent.bsp" Target "1999999" Origin "0" BoundingRadius 500 }
                }
                """);
            File.WriteAllText(Path.Combine(data, "02-edits.ssc"), """
                Modify "Loader" "Sol" {
                    SpiceOrbit { Kernel [ "alias-b.tf" "aliases.bsp" "wide.bsp" ] Target "1999999" Origin "0" BoundingRadius 500 }
                }
                Replace "Loader" "Sol" { FixedPosition [ 4 0 0 ] }
                Replace "Missing" "Sol" { FixedPosition [ 5 0 0 ] }
                """);
            string packed = Path.Combine(scratch.FullName, "packed");
            Assert.Equal(3, SscBodyCatalogue.BuildPack(source, packed, _ => { }));
            foreach (string folder in new[] { source, packed })
            {
                var warnings = new List<string>();
                var bodies = SscBodyCatalogue.Load(folder, warning: warnings.Add);
                Assert.Empty(warnings);
                var epoch = JulianInstant.FromTdbSeconds(25);
                Assert.Equal(4d, bodies[0].Sample(epoch)!.Value.PositionKilometers.X);
                Assert.Equal(5d, bodies[2].Sample(epoch)!.Value.PositionKilometers.X);
                var state = bodies[1].Sample(epoch)!.Value;
                Assert.InRange(state.PositionKilometers.X, 1019.99795, 1019.99797);
                Assert.InRange(state.VelocityKilometersPerSecond.X, 1.99999999, 2.00000001);
            }
        }
        finally
        {
            scratch.Delete(recursive: true);
        }
    }

    [OrderedSpiceFact]
    [Trait("Category", "ExternalData")]
    public void AFailedSscModelRetainsTheKernelsItFurnishedBeforeTheFailure()
    {
        string corpus = Path.Combine(Path.GetDirectoryName(Environment.GetEnvironmentVariable("SPACETANGENT_SPICE_GLOBAL")!)!, "spice-init");
        var scratch = Directory.CreateTempSubdirectory();
        try
        {
            string source = scratch.CreateSubdirectory("source").FullName;
            foreach (string file in new[] { "fixed-frame.tf", "linear.bsp", "wide.bsp" })
                File.Copy(Path.Combine(corpus, file), Path.Combine(source, file));
            File.WriteAllText(Path.Combine(source, "damaged.bsp"), "DAF/SPK");
            File.WriteAllText(Path.Combine(source, "partial-load.ssc"), """
                "Rotation fallback" "Sol" {
                    FixedPosition [ 1 0 0 ]
                    BodyFrame { EquatorJ2000 {} }
                    SpiceRotation { Kernel [ "fixed-frame.tf" "missing.tpc" ] Frame "PREFIX_FRAME" BaseFrame "J2000" }
                    FixedRotation { MeridianAngle 10 }
                }
                "Later rotation" "Sol" {
                    FixedPosition [ 2 0 0 ]
                    BodyFrame { EquatorJ2000 {} }
                    SpiceRotation { Frame "PREFIX_FRAME" BaseFrame "J2000" }
                    FixedRotation { MeridianAngle 15 }
                }
                "Orbit fallback" "Sol" {
                    SpiceOrbit { Kernel [ "linear.bsp" "damaged.bsp" "wide.bsp" ] Target "1999999" Origin "0" BoundingRadius 500 }
                    FixedPosition [ 3 0 0 ]
                }
                "Later orbit" "Sol" {
                    OrbitFrame { EquatorJ2000 {} }
                    SpiceOrbit { Target "1999999" Origin "0" BoundingRadius 500 }
                    FixedPosition [ 4 0 0 ]
                }
                """);
            string packed = Path.Combine(scratch.FullName, "packed");
            Assert.Equal(4, SscBodyCatalogue.BuildPack(source, packed, _ => { }));
            foreach (string folder in new[] { source, packed })
            {
                var warnings = new List<string>();
                var bodies = SscBodyCatalogue.Load(folder, warning: warnings.Add);
                Assert.Equal(2, warnings.Count);
                Assert.Contains("Rotation fallback: SpiceRotation initialization failed", warnings[0]);
                Assert.Contains("Orbit fallback: SpiceOrbit initialization failed", warnings[1]);
                var epoch = JulianInstant.FromTdbSeconds(5);
                var fallback = bodies[0].SampleRotation!(epoch)!.Value.ToWorld;
                var rotation = bodies[1].SampleRotation!(epoch)!.Value.ToWorld;
                Assert.NotEqual(fallback, rotation);
                Assert.InRange(Math.Abs(rotation.X.X - Math.Cos(25 * Math.PI / 180)), 0, 1e-13);
                Assert.Equal(3d, bodies[2].Sample(epoch)!.Value.PositionKilometers.X);
                Assert.InRange(bodies[3].Sample(epoch)!.Value.PositionKilometers.X, 104.9999999, 105.0000001);
            }
        }
        finally
        {
            scratch.Delete(recursive: true);
        }
    }

    [OrderedSpiceFact]
    [Trait("Category", "ExternalData")]
    public void AFailedSscMetakernelRetainsItsSuccessfullyLoadedChildren()
    {
        string corpus = Path.Combine(Path.GetDirectoryName(Environment.GetEnvironmentVariable("SPACETANGENT_SPICE_GLOBAL")!)!, "spice-init");
        var scratch = Directory.CreateTempSubdirectory();
        try
        {
            string source = scratch.CreateSubdirectory("source").FullName;
            foreach (string file in new[] { "linear.bsp", "wide.bsp" })
                File.Copy(Path.Combine(corpus, file), Path.Combine(source, file));
            File.WriteAllText(Path.Combine(source, "damaged.bsp"), "DAF/SPK");
            File.WriteAllText(Path.Combine(source, "optional.tm"), """
                KPL/MK
                \begindata
                KERNELS_TO_LOAD = ( 'linear.bsp' 'damaged.bsp' 'wide.bsp' )
                """);
            File.WriteAllText(Path.Combine(source, "partial-load.ssc"), """
                "Orbit fallback" "Sol" {
                    SpiceOrbit { Kernel "optional.tm" Target "1999999" Origin "0" BoundingRadius 500 }
                    FixedPosition [ 3 0 0 ]
                }
                "Later orbit" "Sol" {
                    OrbitFrame { EquatorJ2000 {} }
                    SpiceOrbit { Target "1999999" Origin "0" BoundingRadius 500 }
                    FixedPosition [ 4 0 0 ]
                }
                """);
            string packed = Path.Combine(scratch.FullName, "packed");
            Assert.Equal(2, SscBodyCatalogue.BuildPack(source, packed, _ => { }));
            foreach (string folder in new[] { source, packed })
            {
                var warnings = new List<string>();
                var bodies = SscBodyCatalogue.Load(folder, warning: warnings.Add);
                string warning = Assert.Single(warnings);
                Assert.Contains("Orbit fallback: SpiceOrbit initialization failed", warning);
                Assert.Contains("damaged.bsp", warning);
                var epoch = JulianInstant.FromTdbSeconds(5);
                Assert.Equal(3d, bodies[0].Sample(epoch)!.Value.PositionKilometers.X);
                Assert.InRange(bodies[1].Sample(epoch)!.Value.PositionKilometers.X, 104.9999999, 105.0000001);
            }
        }
        finally
        {
            scratch.Delete(recursive: true);
        }
    }

    [OrderedSpiceFact]
    [Trait("Category", "ExternalData")]
    public void SscCoverageCapacityAndShortWindowsMatchNativeInitialization()
    {
        string corpus = Path.Combine(Path.GetDirectoryName(Environment.GetEnvironmentVariable("SPACETANGENT_SPICE_GLOBAL")!)!, "spice-init");
        using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(corpus, "reference.json")));
        using var definitions = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(corpus, "cases.json")));
        var scratch = Directory.CreateTempSubdirectory();
        try
        {
            foreach (string name in new[] { "ten-coverage-intervals", "eleven-coverage-intervals",
                "coverage-overflow-before-wide-bridge", "ssb-ignores-coverage-cell-overflow",
                "explicit-inverted-window", "explicit-point-window", "explicit-narrow-window" })
            {
                var input = definitions.RootElement.EnumerateArray().Single(row => row.GetProperty("name").GetString() == name)
                    .GetProperty("operations")[0].GetProperty("orbit");
                var expected = document.RootElement.GetProperty("rows").EnumerateArray()
                    .Single(row => row.GetProperty("case").GetString() == name).GetProperty("results");
                string source = scratch.CreateSubdirectory(name).FullName;
                string[] files = input.GetProperty("kernels").EnumerateArray().Select(value => value.GetString()!).ToArray();
                foreach (string file in files)
                    File.Copy(Path.Combine(corpus, file), Path.Combine(source, file));
                string kernels = string.Join(" ", files.Select(file => $"\"{file}\""));
                string target = input.TryGetProperty("target", out var specified) ? specified.GetString()! : "1999999";
                string window = "";
                if (input.TryGetProperty("boundsSeconds", out var bounds))
                    window = FormattableString.Invariant($"Beginning {2451545 + bounds[0].GetDouble() / 86400:R} Ending {2451545 + bounds[1].GetDouble() / 86400:R}");
                File.WriteAllText(Path.Combine(source, "test.ssc"), $$"""
                    "Coverage" "Sol" {
                        OrbitFrame { EquatorJ2000 {} }
                        SpiceOrbit { Kernel [ {{kernels}} ] Target "{{target}}" Origin "0" BoundingRadius 500 {{window}} }
                        FixedPosition [ 4 0 0 ]
                    }
                    """);
                string packed = source + "-packed";
                Assert.Equal(1, SscBodyCatalogue.BuildPack(source, packed, _ => { }));
                foreach (string root in new[] { source, packed })
                {
                    var warnings = new List<string>();
                    var body = Assert.Single(SscBodyCatalogue.Load(root, warning: warnings.Add));
                    if (expected[0].GetProperty("ok").GetBoolean())
                    {
                        Assert.Empty(warnings);
                        CheckState(body, expected[1]);
                    }
                    else
                    {
                        Assert.Contains("coverage exceeds ten intervals", Assert.Single(warnings));
                        Assert.Equal(4d, body.Sample(JulianInstant.FromTdbSeconds(5))!.Value.PositionKilometers.X);
                    }
                }
            }
        }
        finally
        {
            scratch.Delete(recursive: true);
        }
    }

    [OrderedSpiceFact]
    [Trait("Category", "ExternalData")]
    public void BoundedCassiniPointingHoldsTheNativeEndpoints()
    {
        string corpus = Path.Combine(Path.GetDirectoryName(Environment.GetEnvironmentVariable("SPACETANGENT_SPICE_GLOBAL")!)!, "spice-init");
        string mission = Environment.GetEnvironmentVariable("SPACETANGENT_CASSINI_JSON_ROOT")!;
        string archive = Environment.GetEnvironmentVariable("SPACETANGENT_SPK_ARCHIVE")!;
        using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(corpus, "reference.json")));
        var expected = document.RootElement.GetProperty("rows").EnumerateArray()
            .Single(row => row.GetProperty("case").GetString() == "real-cassini-clamped-rotation")
            .GetProperty("results");
        var scratch = Directory.CreateTempSubdirectory();
        try
        {
            string source = scratch.CreateSubdirectory("source").FullName;
            File.Copy(Path.Combine(corpus, "late.dat"), Path.Combine(source, "late.dat"));
            File.Copy(Path.Combine(archive, "naif0012.tls"), Path.Combine(source, "naif0012.tls"));
            const string ck = "04182_04183pa_soi_itl.bc";
            File.Copy(Path.Combine(mission, ck), Path.Combine(source, ck));
            File.WriteAllText(Path.Combine(source, "cassini.ssc"), """
                "Bounded Cassini" "Sol" {
                    FixedPosition [ 1 0 0 ]
                    BodyFrame { EquatorJ2000 {} }
                    SpiceRotation { Kernel [ "04182_04183pa_soi_itl.bc" "naif0012.tls" "late.dat" ]
                        Frame "META_CASSINI" BaseFrame "J2000" Beginning 2453187.55 Ending 2453187.6 }
                }
                """);
            string packed = Path.Combine(scratch.FullName, "packed");
            Assert.Equal(1, SscBodyCatalogue.BuildPack(source, packed, _ => { }));
            foreach (string folder in new[] { source, packed })
            {
                var body = Assert.Single(SscBodyCatalogue.Load(folder));
                foreach (var row in expected.EnumerateArray().Skip(1))
                {
                    double day = row.GetProperty("jd").GetDouble();
                    var orientation = body.SampleRotation!(JulianInstant.FromJulianDate(day, TimeScale.Tdb))!.Value;
                    var values = row.GetProperty("values").EnumerateArray().Select(value => value.GetDouble()).ToArray();
                    Assert.InRange((orientation.ToWorld.X - ToEquatorial(new(1, 0, 0), values)).Length, 0, 2e-9);
                    Assert.InRange((orientation.ToWorld.Y - ToEquatorial(new(0, 1, 0), values)).Length, 0, 2e-9);
                    Assert.InRange((orientation.ToWorld.Z - ToEquatorial(new(0, 0, 1), values)).Length, 0, 2e-9);
                    if (day is <= 2453187.55 or >= 2453187.6)
                        Assert.Equal(default(Vector3d), orientation.AngularVelocityRadiansPerSecond);
                }
            }
            // The same physical bounds can be expressed as calendar UTC or a numeric TDB day.
            string path = Path.Combine(source, "cassini.ssc");
            string declaration = File.ReadAllText(path);
            File.WriteAllText(path, declaration.Replace("Beginning 2453187.55 Ending 2453187.6",
                $"Beginning \"{Calendar(2453187.55)}\" Ending \"{Calendar(2453187.6)}\"", StringComparison.Ordinal));
            string calendarPack = Path.Combine(scratch.FullName, "calendar-pack");
            Assert.Equal(1, SscBodyCatalogue.BuildPack(source, calendarPack, _ => { }));
            var numeric = Assert.Single(SscBodyCatalogue.Load(packed));
            foreach (string folder in new[] { source, calendarPack })
            {
                var calendar = Assert.Single(SscBodyCatalogue.Load(folder));
                foreach (double day in new[] { 2453187.54, 2453187.57, 2453187.61 })
                {
                    var epoch = JulianInstant.FromJulianDate(day, TimeScale.Tdb);
                    Assert.Equal(numeric.SampleRotation!(epoch), calendar.SampleRotation!(epoch));
                }
            }

            static string Calendar(double day)
            {
                var utc = TimeConversion.ToUtc(JulianInstant.FromJulianDate(day, TimeScale.Tdb));
                var time = utc.Date.ToDateTime(new TimeOnly(utc.Hour, utc.Minute, utc.Second))
                    .AddTicks((utc.Nanosecond + 50) / 100);
                return time.ToString("yyyy MM dd HH:mm:ss.fffffff", System.Globalization.CultureInfo.InvariantCulture);
            }
        }
        finally
        {
            scratch.Delete(recursive: true);
        }

        static Vector3d ToEquatorial(Vector3d axis, double[] quaternion)
        {
            // Undo the reference axis ordering, passive rotation and prime-meridian half-turn.
            var native = new Vector3d(-axis.X, axis.Z, axis.Y);
            var imaginary = new Vector3d(-quaternion[1], -quaternion[2], -quaternion[3]);
            var twiceCross = Vector3d.Cross(imaginary, native) * 2;
            var rotated = native + twiceCross * quaternion[0] + Vector3d.Cross(imaginary, twiceCross);
            return new(rotated.X, -rotated.Z, rotated.Y);
        }
    }

    [OrderedSpiceFact]
    [Trait("Category", "ExternalData")]
    public void NestedMetakernelRetainsAssignmentsAndRejectsItsLoadInstructions()
    {
        string corpus = Path.Combine(Path.GetDirectoryName(Environment.GetEnvironmentVariable("SPACETANGENT_SPICE_GLOBAL")!)!, "spice-init");
        var scratch = Directory.CreateTempSubdirectory();
        try
        {
            string source = scratch.CreateSubdirectory("source").FullName;
            foreach (string file in new[] { "linear.bsp", "wide.bsp" })
                File.Copy(Path.Combine(corpus, file), Path.Combine(source, file));
            File.WriteAllText(Path.Combine(source, "outer.tm"), """
                KPL/MK
                \begindata
                KERNELS_TO_LOAD = ( 'linear.bsp' 'nested.tpc' 'wide.bsp' )
                """);
            File.WriteAllText(Path.Combine(source, "nested.tpc"), File.ReadAllText(Path.Combine(corpus, "fixed-frame.tf"))
                .Replace("KPL/FK", "KPL/PCK", StringComparison.Ordinal)
                .Replace("\\begintext", "KERNELS_TO_LOAD = ( 'wide.bsp' )", StringComparison.Ordinal));
            File.WriteAllText(Path.Combine(source, "meta.ssc"), """
                "Fallback" "Sol" {
                    FixedPosition [ 3 0 0 ]
                    SpiceRotation { Kernel "outer.tm" Frame "PREFIX_FRAME" BaseFrame "J2000" }
                    FixedRotation { MeridianAngle 10 }
                }
                "Later" "Sol" {
                    OrbitFrame { EquatorJ2000 {} }
                    BodyFrame { EquatorJ2000 {} }
                    SpiceOrbit { Target "1999999" Origin "0" BoundingRadius 500 }
                    SpiceRotation { Frame "PREFIX_FRAME" BaseFrame "J2000" }
                    FixedRotation { MeridianAngle 15 }
                }
                """);
            string packed = Path.Combine(scratch.FullName, "packed");
            Assert.Equal(2, SscBodyCatalogue.BuildPack(source, packed, _ => { }));
            foreach (string root in new[] { source, packed })
            {
                var warnings = new List<string>();
                var bodies = SscBodyCatalogue.Load(root, warning: warnings.Add);
                string warning = Assert.Single(warnings);
                Assert.Contains("SPICE(RECURSIVELOADING)", warning);
                Assert.Contains("nested.tpc", warning);
                var epoch = JulianInstant.FromTdbSeconds(5);
                Assert.InRange(bodies[1].Sample(epoch)!.Value.PositionKilometers.X, 104.9999999, 105.0000001);
                var rotation = bodies[1].SampleRotation!(epoch)!.Value.ToWorld;
                Assert.InRange(Math.Abs(rotation.X.X - Math.Cos(25 * Math.PI / 180)), 0, 1e-13);
            }
        }
        finally
        {
            scratch.Delete(recursive: true);
        }
    }

    [OrderedSpiceFact]
    [Trait("Category", "ExternalData")]
    public void MetakernelJoinsContinuedPathsAndRejectsOverlongNamesInOrder()
    {
        string corpus = Environment.GetEnvironmentVariable("SPACETANGENT_SPICE_GLOBAL")!;
        var scratch = Directory.CreateTempSubdirectory();
        try
        {
            string source = scratch.CreateSubdirectory("source").FullName;
            string kernels = Directory.CreateDirectory(Path.Combine(source, "kernels")).FullName;
            File.Copy(Path.Combine(corpus, "a.bsp"), Path.Combine(kernels, "a.bsp"));
            File.Copy(Path.Combine(corpus, "b.bsp"), Path.Combine(kernels, "b.bsp"));
            string tooLong = new('x', 256);
            File.WriteAllText(Path.Combine(source, "paths.tm"), $$"""
                KPL/MK
                \begindata
                PATH_SYMBOLS = ( 'K' 'KERNEL' )
                PATH_VALUES = ( 'absent' 'ker+ ' 'nels' )
                KERNELS_TO_LOAD = ( '$KERNEL/a.+' 'bsp' '{{tooLong}}' '$KERNEL/b.bsp' )
                """);
            File.WriteAllText(Path.Combine(source, "last.tm"), """
                KPL/MK
                \begindata
                KERNELS_TO_LOAD = ( 'kernels/b.bsp+' )
                """);
            File.WriteAllText(Path.Combine(source, "paths.ssc"), """
                "Too long" "Sol" {
                    SpiceOrbit { Kernel "paths.tm" Target "1999999" Origin "0" BoundingRadius 500 }
                    FixedPosition [ 3 0 0 ]
                }
                "First dependency" "Sol" {
                    SpiceOrbit { Target "1999999" Origin "0" BoundingRadius 500 }
                    FixedPosition [ 4 0 0 ]
                }
                "Trailing continuation" "Sol" {
                    SpiceOrbit { Kernel "last.tm" Target "1999999" Origin "0" BoundingRadius 500 }
                    FixedPosition [ 5 0 0 ]
                }
                """);
            string packed = Path.Combine(scratch.FullName, "packed");
            Assert.Equal(3, SscBodyCatalogue.BuildPack(source, packed, _ => { }));
            foreach (string root in new[] { source, packed })
            {
                var warnings = new List<string>();
                var bodies = SscBodyCatalogue.Load(root, warning: warnings.Add);
                Assert.Contains("SPICE(FILENAMETOOLONG)", Assert.Single(warnings));
                var epoch = JulianInstant.FromTdbSeconds(5);
                Assert.Equal(new[] { 3d, 222d, 222d }, bodies.Select(body => body.Sample(epoch)!.Value.PositionKilometers.X));
            }
            // Without the independent second load the failed list must leave only its first dependency active.
            File.WriteAllText(Path.Combine(source, "last.tm"), "KPL/MK\n\\begindata\nKERNELS_TO_LOAD = ()\n");
            var earlier = SscBodyCatalogue.Load(source);
            Assert.Equal(111d, earlier[1].Sample(JulianInstant.FromTdbSeconds(5))!.Value.PositionKilometers.X);
        }
        finally
        {
            scratch.Delete(recursive: true);
        }
    }

    [OrderedSpiceFact]
    [Trait("Category", "ExternalData")]
    public void NonzeroSscSelfOrbitRequiresCoverageEvenWithAnotherTargetInThePool()
    {
        string corpus = Path.Combine(Path.GetDirectoryName(Environment.GetEnvironmentVariable("SPACETANGENT_SPICE_GLOBAL")!)!, "spice-init");
        var scratch = Directory.CreateTempSubdirectory();
        try
        {
            string source = scratch.CreateSubdirectory("source").FullName;
            File.Copy(Path.Combine(corpus, "linear.bsp"), Path.Combine(source, "linear.bsp"));
            const string valid = """
                "Covered" "Sol" {
                    SpiceOrbit { Kernel "linear.bsp" Target "1999999" Origin "1999999" BoundingRadius 500 }
                }
                "Barycenter" "Sol" {
                    SpiceOrbit { Target "0" Origin "0" BoundingRadius 500 }
                }
                """;
            const string unsupported = """
                "Uncovered" "Sol" {
                    SpiceOrbit { Target "399" Origin "399" BoundingRadius 500 }
                    FixedPosition [ 3 0 0 ]
                }
                "Outside window" "Sol" {
                    SpiceOrbit { Target "1999999" Origin "1999999" BoundingRadius 500
                        Beginning 2451545.001 Ending 2451545.002 }
                    FixedPosition [ 4 0 0 ]
                }
                """;
            string catalogue = Path.Combine(source, "self.ssc");
            File.WriteAllText(catalogue, valid + "\n" + unsupported);
            string packed = Path.Combine(scratch.FullName, "packed");
            Assert.Equal(4, SscBodyCatalogue.BuildPack(source, packed, _ => { }));
            foreach (string root in new[] { source, packed })
            {
                var warnings = new List<string>();
                var bodies = SscBodyCatalogue.Load(root, warning: warnings.Add);
                var epoch = JulianInstant.FromTdbSeconds(5);
                Assert.Equal(new[] { 0d, 0d, 3d, 4d }, bodies.Select(body => body.Sample(epoch)!.Value.PositionKilometers.X));
                Assert.Equal(2, warnings.Count);
                Assert.Contains("No SPK coverage", warnings[0]);
                Assert.Contains("not entirely covered", warnings[1]);
            }

            File.WriteAllText(catalogue, valid + "\n" + unsupported.Replace("FixedPosition [ 3 0 0 ]", "", StringComparison.Ordinal));
            Assert.Contains("No SPK coverage", Assert.Throws<InvalidDataException>(() => SscBodyCatalogue.Load(source)).Message);
        }
        finally
        {
            scratch.Delete(recursive: true);
        }
    }

    [OrderedSpiceFact]
    [Trait("Category", "ExternalData")]
    public void SscNoninertialOrbitUsesLateCkAndMatchesNativeStateAndVelocity()
    {
        string corpus = Path.GetDirectoryName(Environment.GetEnvironmentVariable("SPACETANGENT_SPICE_GLOBAL")!)!;
        string init = Path.Combine(corpus, "spice-init");
        string archive = Environment.GetEnvironmentVariable("SPACETANGENT_SPK_ARCHIVE")!;
        using var reference = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(init, "reference.json")));
        var rows = reference.RootElement.GetProperty("rows").EnumerateArray().ToArray();
        var original = Results("noninertial-source-clamp");
        var changed = Results("late-ck-invalidates-initialization-frame-cache");
        var scratch = Directory.CreateTempSubdirectory();
        try
        {
            string source = scratch.CreateSubdirectory("source").FullName;
            foreach (string file in new[] { "state.tf", "native-state-spk.bsp", "native-state-rate.bc", "native-state-chain.bc" })
                File.Copy(Path.Combine(corpus, "spice-state", file), Path.Combine(source, file));
            foreach (string file in new[] { "oracle.tsc", "native-ck2.bc" })
                File.Copy(Path.Combine(corpus, "spice-ck", file), Path.Combine(source, file));
            foreach (string file in new[] { "pck00011.tpc", "naif0012.tls" })
                File.Copy(Path.Combine(archive, file), Path.Combine(source, file));
            File.Copy(Path.Combine(init, "late-rate.bc"), Path.Combine(source, "late-rate.bc"));
            File.WriteAllText(Path.Combine(source, "01-orbit.ssc"), """
                "Rotating frame orbit" "Sol" {
                    OrbitFrame { EquatorJ2000 {} }
                    SpiceOrbit {
                        Kernel [ "state.tf" "oracle.tsc" "native-state-spk.bsp" "native-state-rate.bc"
                            "native-state-chain.bc" "native-ck2.bc" "pck00011.tpc" "naif0012.tls" ]
                        Target "1900010" Origin "1900004" BoundingRadius 500
                    }
                    FixedPosition [ 3 0 0 ]
                }
                """);
            Check(source + "-original", original.EnumerateArray().Skip(1));
            File.WriteAllText(Path.Combine(source, "02-override.ssc"), """
                "Frame update" "Sol" {
                    FixedPosition [ 4 0 0 ]
                    SpiceRotation { Kernel "late-rate.bc" Frame "J2000" BaseFrame "J2000" }
                }
                """);
            Check(source + "-late", new[] { changed[3] });

            void Check(string packed, IEnumerable<System.Text.Json.JsonElement> samples)
            {
                SscBodyCatalogue.BuildPack(source, packed, _ => { });
                foreach (string root in new[] { source, packed })
                {
                    var warnings = new List<string>();
                    var body = SscBodyCatalogue.Load(root, warning: warnings.Add)[0];
                    Assert.Empty(warnings);
                    foreach (var sample in samples)
                        CheckState(body, sample);
                }
            }
        }
        finally
        {
            scratch.Delete(recursive: true);
        }

        System.Text.Json.JsonElement Results(string name) =>
            rows.Single(row => row.GetProperty("case").GetString() == name).GetProperty("results");
    }

    private static void CheckState(SscBodyDefinition body, System.Text.Json.JsonElement reference)
    {
        var epoch = JulianInstant.FromJulianDate(reference.GetProperty("jd").GetDouble(), TimeScale.Tdb);
        var state = body.Sample(epoch)!.Value;
        var values = reference.GetProperty("values").EnumerateArray().Select(value => value.GetDouble()).ToArray();
        Assert.InRange((state.PositionKilometers - new Vector3d(values[0], -values[2], values[1])).Length, 0, 2e-7);
        Assert.InRange((state.VelocityKilometersPerSecond -
            new Vector3d(values[3], -values[5], values[4]) / 86400).Length, 0, 2e-11);
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
