// SPDX-License-Identifier: GPL-2.0-or-later
using System.Globalization;
using System.Security.Cryptography;
using SpaceTangent.Contracts;
using SpaceTangent.Core.Addons;
using SpaceTangent.Core.Spice;
using SpaceTangent.Core.World;
using Xunit;

namespace SpaceTangent.Core.Tests;

public sealed class CassiniSpiceCatalogTests
{
    [CassiniFact]
    [Trait("Category", "ExternalData")]
    public void BuiltPackKeepsOriginalCassiniStatesAndPointingAfterRelocation()
    {
        string original = Environment.GetEnvironmentVariable("SPACETANGENT_CASSINI_SSC_ROOT")!;
        DirectoryInfo workspace = Directory.CreateTempSubdirectory("space-tangent-pack-");
        try
        {
            string source = workspace.CreateSubdirectory("source").FullName;
            foreach (string file in Directory.EnumerateFiles(original))
                File.Copy(file, Path.Combine(source, Path.GetFileName(file)));
            string output = Path.Combine(workspace.FullName, "pack");
            string moved = Path.Combine(workspace.FullName, "relocated pack");
            Assert.Equal(1, SscBodyCatalogue.BuildPack(source, output, _ => { }));
            var expected = Assert.Single(SscBodyCatalogue.Load(source));
            Directory.Move(output, moved);
            Directory.Delete(source, recursive: true);
            var actual = Assert.Single(SscBodyCatalogue.Load(moved));
            Assert.Equal(expected.Name, actual.Name);
            Assert.Equal(Path.Combine(moved, "cassini.3ds"), actual.Mesh);
            foreach (double seconds in new[] { 140556964.18453318, 141917558.4203589, 141918830.41202343, 141955264.18408903 })
            {
                var epoch = JulianInstant.FromTdbSeconds(seconds);
                Assert.NotNull(expected.Sample(epoch));
                Assert.Equal(expected.Sample(epoch), actual.Sample(epoch));
                Assert.Equal(expected.SampleRotation!(epoch), actual.SampleRotation!(epoch));
            }
            Assert.NotNull(actual.SampleRotation!(JulianInstant.FromTdbSeconds(141917558.4203589)));
        }
        finally
        {
            workspace.Delete(recursive: true);
        }
    }

    [CassiniFact]
    public void LoadsOriginalSscAndMatchesCassiniStatesThroughCoverageEdges()
    {
        string directory = Environment.GetEnvironmentVariable("SPACETANGENT_CASSINI_SSC_ROOT")!;
        string path = Path.Combine(directory, "cassini.ssc");
        Assert.Equal("14DE7A143DB435FB862D8C7FD34CDF923595F13B717EFCC29603C4A5E06F5A83",
            Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))));
        SpiceSpacecraft cassini = Assert.Single(SscSpiceCatalog.Load(path, directory));
        Assert.Equal("Cassini-SSC", cassini.Name);
        Assert.Equal("Sol/Saturn", cassini.Parent);
        Assert.Equal(-82, cassini.Target);
        Assert.Equal(6, cassini.Center);
        Assert.Equal("CASSINI_SC_COORD", cassini.AttitudeFrame);
        Assert.Equal("J2000", cassini.AttitudeBaseFrame);
        Assert.Equal(".3ds", Path.GetExtension(cassini.MeshPath));

        var reference = Path.Combine(AppContext.BaseDirectory, "Fixtures", "cassini-reference.csv");
        foreach (string line in File.ReadLines(reference).Where(line => !line.StartsWith('#')))
        {
            double[] data = line.Split(',').Select(value => double.Parse(value, CultureInfo.InvariantCulture)).ToArray();
            Assert.True(cassini.TrySample(JulianInstant.FromTdbSeconds(data[0]), out var state));
            var expectedPosition = new Vector3d(data[1], data[2], data[3]);
            var expectedVelocity = new Vector3d(data[4], data[5], data[6]);
            Assert.InRange((state.PositionKilometers - expectedPosition).Length, 0, 1e-6);
            Assert.InRange((state.VelocityKilometersPerSecond - expectedVelocity).Length, 0, 1e-9);
        }
        Assert.False(cassini.TrySample(JulianInstant.FromTdbSeconds(140556964.18453318).AddNanoseconds(-1), out _));
        Assert.False(cassini.TrySample(JulianInstant.FromTdbSeconds(141955264.18408903).AddNanoseconds(1), out _));
    }

    [CassiniFact]
    public void ReadsCassiniCkAndLeavesPointingGapsUnfilled()
    {
        string root = Environment.GetEnvironmentVariable("SPACETANGENT_CASSINI_SSC_ROOT")!;
        SpiceSpacecraft cassini = Assert.Single(SscSpiceCatalog.Load(Path.Combine(root, "cassini.ssc"), root));
        string path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "cassini-ck-reference.csv");
        foreach (string line in File.ReadLines(path).Where(line => !line.StartsWith('#')))
        {
            double[] data = line.Split(',').Select(value => double.Parse(value, CultureInfo.InvariantCulture)).ToArray();
            bool found = cassini.TryAttitude(JulianInstant.FromTdbSeconds(data[0]), out var body);
            Assert.Equal(data[1] == 1, found);
            if (!found)
                continue;
            Assert.InRange((body.X - new Vector3d(data[2], data[3], data[4])).Length, 0, 3e-8);
            Assert.InRange((body.Y - new Vector3d(data[5], data[6], data[7])).Length, 0, 3e-8);
            Assert.InRange((body.Z - new Vector3d(data[8], data[9], data[10])).Length, 0, 3e-8);
        }
    }

    [CassiniFact]
    public void PatchOfOriginalSpiceSscResolvesResourcesFromEachDeclaration()
    {
        string source = Environment.GetEnvironmentVariable("SPACETANGENT_CASSINI_SSC_ROOT")!;
        DirectoryInfo root = Directory.CreateTempSubdirectory("space-tangent-cassini-");
        try
        {
            DirectoryInfo original = root.CreateSubdirectory("original");
            DirectoryInfo overlays = root.CreateSubdirectory("overlays");
            foreach (string file in Directory.EnumerateFiles(source))
                File.Copy(file, Path.Combine(original.FullName, Path.GetFileName(file)));
            const string PointingFile = "04182_04183pa_soi_itl.bc";
            File.Copy(Path.Combine(source, PointingFile), Path.Combine(overlays.FullName, PointingFile));
            string originalPath = Path.Combine(original.FullName, "cassini.ssc");
            string overlayPath = Path.Combine(overlays.FullName, "patch.ssc");
            string patchedMesh = Path.Combine(overlays.FullName, "local-cassini.3ds");
            File.Copy(Path.Combine(source, "cassini.3ds"), patchedMesh);
            File.WriteAllText(overlayPath, """
                Modify "Cassini-SSC" "Sol/Saturn" {
                    Radius 0.009
                    Mesh "local-cassini.3ds"
                    SpiceRotation { Kernel "04182_04183pa_soi_itl.bc" Frame "CASSINI_SC_COORD" BaseFrame "J2000" }
                }
                """);
            SpiceSpacecraft unmodified = Assert.Single(SscSpiceCatalog.Load(originalPath, root.FullName));
            SpiceSpacecraft patched = Assert.Single(SscSpiceCatalog.Load([originalPath, overlayPath], root.FullName));
            Assert.Equal(unmodified.Name, patched.Name);
            Assert.Equal(0.009, patched.RadiusKilometers);
            Assert.Equal(patchedMesh, patched.MeshPath);
            var epoch = JulianInstant.FromTdbSeconds(141911269.46157101);
            Assert.True(unmodified.TrySample(epoch, out var state));
            Assert.True(patched.TrySample(epoch, out var patchedState));
            Assert.Equal(state, patchedState);
            Assert.True(unmodified.TryAttitude(epoch, out var attitude));
            Assert.True(patched.TryAttitude(epoch, out var patchedAttitude));
            Assert.Equal(attitude, patchedAttitude);
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [CassiniFact]
    [Trait("Category", "ExternalData")]
    public void OrdinaryBodyCatalogLoadsOriginalCassiniAcrossPackedAndLooseOwners()
    {
        string source = Environment.GetEnvironmentVariable("SPACETANGENT_CASSINI_SSC_ROOT")!;
        var root = Directory.CreateTempSubdirectory("space-tangent-ssc-");
        try
        {
            var packed = root.CreateSubdirectory("packed");
            var loose = root.CreateSubdirectory("loose");
            var kernels = root.CreateSubdirectory("kernels");
            const string Spk = "040615AP_SK_04167_04186.bsp";
            const string Ck = "04182_04183pa_soi_itl.bc";
            const string Frame = "cas_v43.tf";
            const string Clock = "cas00172.tsc";
            const string Mesh = "cassini.3ds";
            foreach (string file in new[] { Spk, Mesh })
                File.Copy(Path.Combine(source, file), Path.Combine(packed.FullName, file));
            foreach (string file in new[] { Ck, Frame, Clock })
                File.Copy(Path.Combine(source, file), Path.Combine(kernels.FullName, file));

            // Keep the original orbit declaration except for the split kernel list.
            string originalPath = Path.Combine(source, "cassini.ssc");
            string text = File.ReadAllText(originalPath)
                .Replace($"\"{Spk}\" \"{Frame}\" \"{Clock}\" \"{Ck}\"", $"\"{Spk}\"", StringComparison.Ordinal)
                .Replace("Radius 0.006", "Radius 0.006 OrbitFrame { EclipticJ2000 {} }", StringComparison.Ordinal);
            File.WriteAllText(Path.Combine(packed.FullName, "pack.json"), System.Text.Json.JsonSerializer.Serialize(new
            {
                formatVersion = 19,
                sscCatalogs = new[] { new
                {
                    name = "cassini.ssc", text,
                    resources = new[] { new { kind = "data", source = Spk, file = Spk }, new { kind = "model", source = Mesh, file = Mesh } }
                } }
            }));
            File.WriteAllText(Path.Combine(loose.FullName, "support.ssc"),
                """Modify "Cassini-SSC" "Sol/Saturn" { Radius 0.009 }""");
            File.WriteAllText(Path.Combine(kernels.FullName, "pack.json"), """
                {"formatVersion":19,"spiceLoads":[{"policy":"furnish",
                    "files":["cas_v43.tf","cas00172.tsc","04182_04183pa_soi_itl.bc"]}]}
                """);

            Assert.Throws<InvalidDataException>(() => SscBodyCatalogue.Load(packed.FullName));
            var loaded = SscBodyCatalogue.LoadSources([packed.FullName, loose.FullName, kernels.FullName], _ => { });
            var cassini = Assert.Single(loaded, item => item.Name == "Cassini-SSC");
            Assert.Equal(0.009, cassini.RadiusKilometers);
            Assert.Equal(Path.Combine(packed.FullName, Mesh), cassini.Mesh);
            Assert.Equal(packed.FullName, cassini.MeshRoot);
            Assert.Equal("Sol/Saturn", cassini.Center);
            var reference = SscSpiceCatalog.Load(originalPath, source).Single();
            foreach (double second in new[] { 141911269.46157101, 141917558.4203589, 141918830.41202343 })
            {
                var epoch = JulianInstant.FromTdbSeconds(second);
                Assert.True(reference.TrySample(epoch, out var expected));
                var state = Assert.IsType<CartesianState>(cassini.Sample(epoch));
                Assert.InRange((state.PositionKilometers - expected.PositionKilometers).Length, 0, 0.002);
                Assert.InRange((state.VelocityKilometersPerSecond - expected.VelocityKilometersPerSecond).Length, 0, 1e-7);
                Assert.True(reference.TryAttitude(epoch, out var attitude));
                Assert.Equal(attitude, cassini.SampleRotation!(epoch)!.Value.ToWorld);
            }
            Assert.Null(cassini.Sample(JulianInstant.FromTdbSeconds(140556964.18453318).AddNanoseconds(-1)));
            File.Delete(Path.Combine(kernels.FullName, Ck));
            Assert.Throws<FileNotFoundException>(() => SscBodyCatalogue.LoadSources([packed.FullName, loose.FullName, kernels.FullName], _ => { }));
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [PackedCassiniFact]
    [Trait("Category", "ExternalData")]
    public void PackedCassiniKeepsTheOriginalTrajectoryAndBothCameraFields()
    {
        string root = Environment.GetEnvironmentVariable("SPACETANGENT_CASSINI_PACKED_ROOT")!;
        string source = Environment.GetEnvironmentVariable("SPACETANGENT_CASSINI_JSON_ROOT")!;
        var baseline = SpiceJsonCatalog.Load(Path.Combine(source, "cassini.json"), source);
        var saturn = new ObjectId("sol/saturn");
        var diagnostics = new List<string>();
        var content = SampledMissionCatalog.Load("pack.json", root,
            name => name is "Sol/Saturn" or "Saturn" ? saturn : null, diagnostics.Add);
        Assert.True(content.Count == 3, string.Join("\n", diagnostics));
        var cassini = Assert.Single(content, mission => mission.Name == "Cassini SOI");
        Assert.EndsWith("cassini.glb", cassini.MeshPath);
        var builder = new WorldGraphBuilder();
        builder.AddTrajectory(saturn, _ => default(CartesianState));
        foreach (var mission in content)
            builder.AddSampledMission(mission);
        var world = builder.Build();
        foreach (double seconds in new[] { 141911269.46157101, 141917558.4203589 })
        {
            var time = JulianInstant.FromTdbSeconds(seconds);
            var snapshot = world.Evaluate(time);
            Assert.True(snapshot.TryState(cassini.Id, out var position));
            Assert.True(baseline.Spacecraft.Single().TrySample(time, out var expected));
            Assert.InRange((position.PositionKilometers - expected.PositionKilometers).Length, 0, 1e-6);
            Assert.InRange((position.VelocityKilometersPerSecond - expected.VelocityKilometersPerSecond).Length, 0, 1e-9);
            Assert.True(baseline.Spacecraft.Single().TryAttitude(time, out var body));
            Assert.True(snapshot.TryOrientation(cassini.Id, out var attitude));
            Assert.InRange((attitude.ToWorld.X - body.X).Length, 0, 1e-12);
            foreach (var camera in baseline.Sensors)
            {
                var mission = Assert.Single(content, item => item.Name == camera.Name);
                var sensor = Assert.Single(mission.Geometry!).Shape.Sensor!;
                Assert.True(snapshot.TryOrientation(mission.Id, out var frame));
                Assert.Equal(48, sensor.Boundary.Count);
                Assert.Equal(camera.RangeKilometers, sensor.RangeKilometers);
                Assert.InRange((frame.ToWorld.Transform(sensor.Boresight) - body.Transform(camera.BoresightInBody)).Length, 0, 1e-12);
            }
        }
    }

    private sealed class PackedCassiniFactAttribute : FactAttribute
    {
        public PackedCassiniFactAttribute()
        {
            if (new[] { "SPACETANGENT_CASSINI_PACKED_ROOT", "SPACETANGENT_CASSINI_JSON_ROOT" }
                .Any(variable => string.IsNullOrEmpty(Environment.GetEnvironmentVariable(variable))))
                Skip = "Set the original Cassini JSON and archived pack roots.";
        }
    }

    [CassiniJsonFact]
    public void OriginalJsonAndItsMetakernelProduceTheSameCassiniTrajectoryAndAttitude()
    {
        string root = Environment.GetEnvironmentVariable("SPACETANGENT_CASSINI_JSON_ROOT")!;
        string path = Path.Combine(root, "cassini.json");
        Assert.Equal("2E47BFDDEC3733EAB034D607FCE853A8AE2655F2A7B0E4C6C23EB1B26BFA7D12",
            Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))));
        SpiceCatalogResult catalog = SpiceJsonCatalog.Load(path, root);
        Assert.Equal(2, catalog.SensorCount);
        SpiceSpacecraft cassini = Assert.Single(catalog.Spacecraft);
        Assert.Equal("Cassini SOI", cassini.Name);
        Assert.Equal("CASSINI_SC_COORD", cassini.AttitudeFrame);
        Assert.True(cassini.TrySample(JulianInstant.FromTdbSeconds(140556964.18453318), out var state));
        Assert.InRange(Math.Abs(state.PositionKilometers.X + 7408933.280912078), 0, 1e-6);
        Assert.True(cassini.TryAttitude(JulianInstant.FromTdbSeconds(141911269.46157101), out var body));
        Assert.InRange(Math.Abs(body.X.Z - .92616579426881118), 0, 3e-8);
    }

    [CassiniJsonFact]
    public void OriginalCameraRaysMatchCspiceAtTwoPointingEpochs()
    {
        string root = Environment.GetEnvironmentVariable("SPACETANGENT_CASSINI_JSON_ROOT")!;
        SpiceCatalogResult catalog = SpiceJsonCatalog.Load(Path.Combine(root, "cassini.json"), root);
        Assert.Collection(catalog.Sensors, narrow =>
        {
            Assert.Equal("ISS NAC", narrow.Name);
            Assert.Equal(new Vector3d(0, 1, 1), narrow.Color);
            Assert.Equal(.8, narrow.FootprintOpacity);
        }, wide =>
        {
            Assert.Equal("ISS WAC", wide.Name);
            Assert.Equal(new Vector3d(1, .8, .2), wide.Color);
            Assert.Equal(.6, wide.FootprintOpacity);
        });

        string path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "cassini-fov-reference.csv");
        foreach (string line in File.ReadLines(path).Where(line => !line.StartsWith('#')))
        {
            double[] data = line.Split(',').Select(value => double.Parse(value, CultureInfo.InvariantCulture)).ToArray();
            SpiceSensor sensor = Assert.Single(catalog.Sensors, sensor => sensor.InstrumentId == data[0]);
            Assert.Same(catalog.Spacecraft[0], sensor.Spacecraft);
            Assert.Equal("Saturn", sensor.Target);
            Assert.Equal(200_000, sensor.RangeKilometers);
            Assert.Equal(.3, sensor.FrustumOpacity);
            Assert.True(sensor.Spacecraft.TryAttitude(JulianInstant.FromTdbSeconds(data[1]), out Basis3d body));
            Assert.InRange((body.Transform(sensor.BoresightInBody) - Ray(2)).Length, 0, 3e-8);
            var actual = sensor.CornersInBody.Select(body.Transform).ToList();
            for (int index = 5; index < data.Length; index += 3)
            {
                Vector3d expected = Ray(index);
                int match = actual.FindIndex(ray => (ray - expected).Length <= 3e-8);
                Assert.True(match >= 0, $"Missing CSPICE corner at {data[1]} for {sensor.Instrument}.");
                actual.RemoveAt(match);
            }
            Assert.Empty(actual);

            Vector3d Ray(int index) => new(data[index], data[index + 1], data[index + 2]);
        }
    }

    [Fact]
    public void MetakernelSymbolsCannotFormACircularLoad()
    {
        DirectoryInfo root = Directory.CreateTempSubdirectory("SpaceTangent-meta-");
        string catalog = Path.Combine(root.FullName, "catalog.json");
        string meta = Path.Combine(root.FullName, "catalog.tm");
        string nested = Path.Combine(root.CreateSubdirectory("kernels").FullName, "nested.tm");
        try
        {
            File.WriteAllText(catalog, """{"version":"1.0","spiceKernels":["catalog.tm"],"items":[]}""");
            File.WriteAllText(meta, """
                KPL/MK
                \begindata
                PATH_SYMBOLS = ( 'K' )
                PATH_VALUES = ( 'kernels' )
                KERNELS_TO_LOAD = ( '$K/nested.tm' )
                """);
            File.WriteAllText(nested, """
                KPL/MK
                \begindata
                KERNELS_TO_LOAD = ( '../catalog.tm' )
                """);
            Assert.Contains("cycle", Assert.Throws<InvalidDataException>(() =>
                SpiceJsonCatalog.Load(catalog, root.FullName)).Message);
        }
        finally
        {
            File.Delete(catalog);
            File.Delete(meta);
            File.Delete(nested);
            Directory.Delete(Path.GetDirectoryName(nested)!);
            root.Delete();
        }
    }

    [Fact]
    public void RejectsInvalidBinaryKernelAndMalformedCatalog()
    {
        string path = Path.GetTempFileName();
        try
        {
            Assert.Throws<InvalidDataException>(() => new SpkKernel(path));
            File.WriteAllText(path, """
                "Spacecraft" "Sol/Earth" { # missing a value
                  Class }
                """);
            InvalidDataException exception = Assert.Throws<InvalidDataException>(() =>
                SscSpiceCatalog.Load(path, Path.GetDirectoryName(path)!));
            Assert.Contains("SSC line 2", exception.Message);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private sealed class CassiniFactAttribute : FactAttribute
    {
        public CassiniFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SPACETANGENT_CASSINI_SSC_ROOT")))
                Skip = "Set SPACETANGENT_CASSINI_SSC_ROOT to the Cassini SSC and kernel directory.";
        }
    }

    private sealed class CassiniJsonFactAttribute : FactAttribute
    {
        public CassiniJsonFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SPACETANGENT_CASSINI_JSON_ROOT")))
                Skip = "Set SPACETANGENT_CASSINI_JSON_ROOT to the original Cassini SOI add-on.";
        }
    }
}
