// SPDX-License-Identifier: GPL-2.0-or-later
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
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
    [Trait("Category", "ExternalData")]
    public void OriginalCassiniKernelsWorkInsidePackedPhasesAndKeepPointingGaps()
    {
        string original = Environment.GetEnvironmentVariable("SPACETANGENT_CASSINI_SSC_ROOT")!;
        var workspace = Directory.CreateTempSubdirectory("space-tangent-phase-spice-");
        try
        {
            string source = workspace.CreateSubdirectory("source").FullName;
            foreach (string file in Directory.EnumerateFiles(original))
                File.Copy(file, Path.Combine(source, Path.GetFileName(file)));
            var expected = Assert.Single(SscBodyCatalogue.Load(source));
            var ephemeris = Assert.Single(SscSpiceCatalog.Load(Path.Combine(source, "cassini.ssc"), source));
            File.WriteAllText(Path.Combine(source, "cassini.ssc"), """
                "Cassini-SSC" "Sol/Saturn" {
                    Class "spacecraft" Radius 0.006 Mesh "cassini.3ds"
                    Timeline [
                        {
                            Ending 2453186.9
                            SpiceOrbit { Kernel "missing.bsp" Target "CASSINI" Origin "6" BoundingRadius 10000000 }
                            FixedPosition [1 2 3]
                            SpiceRotation { Frame "UNKNOWN_FRAME" }
                            FixedRotation { MeridianAngle 0 }
                        }
                        {
                            OrbitFrame { EclipticJ2000 {} }
                            SpiceOrbit { Kernel [ "040615AP_SK_04167_04186.bsp" "cas_v43.tf" "cas00172.tsc" "04182_04183pa_soi_itl.bc" ] Target "CASSINI" Origin "6" BoundingRadius 10000000 }
                            BodyFrame { EquatorJ2000 {} }
                            SpiceRotation { Frame "CASSINI_SC_COORD" BaseFrame "J2000" }
                        }
                    ]
                }
                """);
            string pack = Path.Combine(workspace.FullName, "pack");
            var warnings = new List<string>();
            Assert.Equal(1, SscBodyCatalogue.BuildPack(source, pack, warnings.Add));
            Assert.Equal(2, warnings.Count);
            warnings.Clear();
            var definition = Assert.Single(SscBodyCatalogue.Load(pack, warning: warnings.Add));
            Assert.Equal(2, warnings.Count);
            Assert.All(warnings, warning => Assert.Contains("Timeline phase 1", warning));
            var center = new ObjectId("saturn");
            var id = new ObjectId("cassini");
            var builder = new WorldGraphBuilder();
            builder.AddTrajectory(center, _ => new CartesianState(default, default));
            builder.AddSampledMission(definition.BindTimeline!(id, _ => center));
            var graph = builder.Build();
            var fallback = graph.Evaluate(JulianInstant.FromJulianDate(2453186, TimeScale.Tdb));
            Assert.True(fallback.TryState(id, out var fixedPosition));
            Assert.Equal(new Vector3d(1, 2, 3), fixedPosition.PositionKilometers);
            Assert.True(fallback.TryOrientation(id, out _));
            // The SSC model clamps to the native millisecond inset at the end of the kernel window.
            var end = JulianInstant.FromJulianDate(
                JulianInstant.FromTdbSeconds(141955264.18408903).ToJulianDate() - .001 / JulianInstant.SecondsPerDay, TimeScale.Tdb);
            foreach (double seconds in new[] { 141917558.4203589, 141918830.41202343, 141919293.40898937, 141955264.18408903 })
            {
                var epoch = JulianInstant.FromTdbSeconds(seconds);
                var snapshot = graph.Evaluate(epoch);
                Assert.True(snapshot.TryState(id, out var state));
                Assert.True(ephemeris.TrySample(epoch.CompareTo(end) > 0 ? end : epoch, out var reference));
                Assert.InRange((state.PositionKilometers - reference.PositionKilometers).Length, 0, 1e-5);
                Assert.InRange((state.VelocityKilometersPerSecond - reference.VelocityKilometersPerSecond).Length, 0, 1e-8);
                var rotation = expected.SampleRotation!(epoch);
                Assert.Equal(rotation.HasValue, snapshot.TryOrientation(id, out var actual));
                if (rotation.HasValue)
                    Assert.Equal(rotation.Value, actual);
            }
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
            var first = JulianInstant.FromTdbSeconds(140556964.18453318);
            var inset = JulianInstant.FromJulianDate(first.ToJulianDate() + .001 / JulianInstant.SecondsPerDay, TimeScale.Tdb);
            Assert.True(reference.TrySample(inset, out var endpoint));
            var held = Assert.IsType<CartesianState>(cassini.Sample(first.AddSeconds(-60)));
            Assert.InRange((held.PositionKilometers - endpoint.PositionKilometers).Length, 0, .002);
            Assert.InRange((held.VelocityKilometersPerSecond - endpoint.VelocityKilometersPerSecond).Length, 0, 1e-7);
            File.Delete(Path.Combine(kernels.FullName, Ck));
            Assert.Throws<FileNotFoundException>(() => SscBodyCatalogue.LoadSources([packed.FullName, loose.FullName, kernels.FullName], _ => { }));
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [CassiniKernelTheory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Category", "ExternalData")]
    public void BadChildOfOptionalMetakernelKeepsCassiniPointing(bool damaged)
    {
        string original = Environment.GetEnvironmentVariable("SPACETANGENT_CASSINI_SSC_ROOT")!;
        var workspace = Directory.CreateTempSubdirectory("st-cassini-optional-kernel-");
        try
        {
            string root = workspace.FullName;
            foreach (string input in Directory.EnumerateFiles(original))
                File.Copy(input, Path.Combine(root, Path.GetFileName(input)));
            var reference = Assert.Single(SscBodyCatalogue.Load(root));
            File.WriteAllText(Path.Combine(root, "optional.tm"), """
                KPL/MK
                \begindata
                KERNELS_TO_LOAD = ( 'cas_v43.tf' 'child.bsp' )
                \begintext
                """);
            if (damaged)
                File.WriteAllText(Path.Combine(root, "child.bsp"), "DAF/SPK");
            string alternate = Path.Combine(root, "z-alternative.ssc");
            const string catalogue = """
                "Backup test" "Sol/Saturn" {
                    Radius 1
                    SpiceOrbit { Kernel "optional.tm" Target "CASSINI" Origin "6" BoundingRadius 10000 }
                    FixedPosition [ 8000 0 0 ]
                }
                """;
            File.WriteAllText(alternate, catalogue);
            var warnings = new List<string>();
            var loaded = SscBodyCatalogue.Load(root, warning: warnings.Add);
            Assert.Equal(2, loaded.Count);
            Assert.Contains("SpiceOrbit initialization failed", Assert.Single(warnings));
            var epoch = JulianInstant.FromTdbSeconds(141917558.4203589);
            var preserved = Assert.Single(loaded, body => body.Name == reference.Name);
            Assert.NotNull(reference.SampleRotation!(epoch));
            Assert.Equal(reference.SampleRotation(epoch), preserved.SampleRotation!(epoch));
            Assert.Equal(reference.Sample(epoch), preserved.Sample(epoch));
            Assert.NotNull(Assert.Single(loaded, body => body.Name == "Backup test").Sample(epoch));

            File.WriteAllText(alternate, catalogue.Replace("FixedPosition [ 8000 0 0 ]", "", StringComparison.Ordinal));
            if (damaged)
                Assert.Throws<InvalidDataException>(() => SscBodyCatalogue.Load(root));
            else
                Assert.EndsWith("child.bsp", Assert.Throws<FileNotFoundException>(() => SscBodyCatalogue.Load(root)).FileName);
        }
        finally
        {
            workspace.Delete(recursive: true);
        }
    }

    [CassiniFact]
    [Trait("Category", "ExternalData")]
    public void RepeatedMalformedOptionalTextKernelCannotChangeOriginalCassini()
    {
        string original = Environment.GetEnvironmentVariable("SPACETANGENT_CASSINI_SSC_ROOT")!;
        var scratch = Directory.CreateTempSubdirectory("st-invalid-fk-");
        try
        {
            string root = scratch.FullName;
            foreach (string file in Directory.EnumerateFiles(original))
                File.Copy(file, Path.Combine(root, Path.GetFileName(file)));
            var expected = Assert.Single(SscBodyCatalogue.Load(root));
            File.WriteAllBytes(Path.Combine(root, "malformed.tf"), [0x4B, 0x50, 0x4C, 0x2F, 0x46, 0x4B, 0x0A, 0xFF]);
            File.WriteAllText(Path.Combine(root, "z-fallback.ssc"), """
                "First fallback" "Sol/Saturn" {
                    FixedPosition [ 1 0 0 ]
                    SpiceRotation { Kernel "malformed.tf" Frame "CASSINI_SC_COORD" BaseFrame "J2000" }
                    FixedRotation { MeridianAngle 1 }
                }
                "Second fallback" "Sol/Saturn" {
                    FixedPosition [ 2 0 0 ]
                    SpiceRotation { Kernel "malformed.tf" Frame "CASSINI_SC_COORD" BaseFrame "J2000" }
                    FixedRotation { MeridianAngle 2 }
                }
                """);
            var warnings = new List<string>();
            var bodies = SscBodyCatalogue.Load(root, warning: warnings.Add);
            Assert.Equal(2, warnings.Count);
            Assert.All(warnings, warning => Assert.Contains("malformed.tf", warning));
            var epoch = JulianInstant.FromTdbSeconds(141917558.4203589);
            Assert.Equal(expected.SampleRotation!(epoch), bodies[0].SampleRotation!(epoch));
            Assert.Equal(expected.Sample(epoch), bodies[0].Sample(epoch));
            Assert.NotEqual(bodies[1].SampleRotation!(epoch), bodies[2].SampleRotation!(epoch));

            File.Copy(Path.Combine(root, "cas_v43.tf"), Path.Combine(root, "malformed.tf"), overwrite: true);
            warnings.Clear();
            bodies = SscBodyCatalogue.Load(root, warning: warnings.Add);
            Assert.Empty(warnings);
            Assert.Equal(bodies[1].SampleRotation!(epoch), bodies[2].SampleRotation!(epoch));
        }
        finally
        {
            scratch.Delete(recursive: true);
        }
    }

    private sealed class CassiniKernelTheoryAttribute : TheoryAttribute
    {
        public CassiniKernelTheoryAttribute()
        {
            if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SPACETANGENT_CASSINI_SSC_ROOT")))
                Skip = "Set the original Cassini SSC input.";
        }
    }

    [ArchivedSscSelectionFact]
    [Trait("Category", "ExternalData")]
    public void ArchivedSscSelectionLoadsItsSelectedCassiniKernelsInOrder()
    {
        string root = Environment.GetEnvironmentVariable("SPACETANGENT_ARCHIVED_SSC_SELECTION")!;
        var saturn = new ObjectId("sol/saturn");
        var diagnostics = new List<string>();
        var cassini = Assert.Single(SampledMissionCatalog.Load("ssc-selection/pack.json", Path.Combine(root, "packed"),
            name => name is "Sol/Saturn" or "Saturn" ? saturn : null, diagnostics.Add));
        Assert.Empty(diagnostics);
        Assert.Equal("Cassini SOI", cassini.Name);

        var builder = new WorldGraphBuilder();
        builder.AddTrajectory(saturn, _ => default(CartesianState));
        builder.AddSampledMission(cassini);
        var world = builder.Build();
        using var oracle = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(root, "reference.json")));
        foreach (var row in oracle.RootElement.GetProperty("rows").EnumerateArray())
        {
            var epoch = JulianInstant.FromTdbSeconds(row.GetProperty("seconds").GetDouble());
            var snapshot = world.Evaluate(epoch);
            var state = row.GetProperty("state").EnumerateArray().Select(value => value.GetDouble()).ToArray();
            var matrix = row.GetProperty("matrix").EnumerateArray().Select(value => value.GetDouble()).ToArray();
            Assert.True(snapshot.TryState(cassini.Id, out var actual));
            Assert.InRange((actual.PositionKilometers - new Vector3d(state[0], state[1], state[2])).Length, 0, 1e-6);
            Assert.InRange((actual.VelocityKilometersPerSecond - new Vector3d(state[3], state[4], state[5])).Length, 0, 1e-9);
            Assert.True(snapshot.TryOrientation(cassini.Id, out var attitude));
            Assert.InRange((attitude.ToWorld.X - new Vector3d(matrix[0], matrix[1], matrix[2])).Length, 0, 3e-8);
            Assert.InRange((attitude.ToWorld.Y - new Vector3d(matrix[3], matrix[4], matrix[5])).Length, 0, 3e-8);
            Assert.InRange((attitude.ToWorld.Z - new Vector3d(matrix[6], matrix[7], matrix[8])).Length, 0, 3e-8);
        }
    }

    private sealed class ArchivedSscSelectionFactAttribute : FactAttribute
    {
        public ArchivedSscSelectionFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SPACETANGENT_ARCHIVED_SSC_SELECTION")))
                Skip = "Set the archived SSC selection pack and its CSPICE reference.";
        }
    }


    [PackedCassiniTheory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Category", "ExternalData")]
    public void PackedCassiniKeepsTheOriginalTrajectoryAndBothCameraFields(bool split)
    {
        string root = Environment.GetEnvironmentVariable(split ? "SPACETANGENT_CASSINI_SPLIT_ROOT" : "SPACETANGENT_CASSINI_PACKED_ROOT")!;
        string[] manifests = split ? ["00-orbits/pack.json", "99-frames/pack.json"] : ["pack.json"];
        string source = Environment.GetEnvironmentVariable("SPACETANGENT_CASSINI_JSON_ROOT")!;
        var baseline = SpiceJsonCatalog.Load(Path.Combine(source, "cassini.json"), source);
        var saturn = new ObjectId("sol/saturn");
        var diagnostics = new List<string>();
        var content = SampledMissionCatalog.Load(manifests, root,
            name => name is "Sol/Saturn" or "Saturn" ? saturn : null, diagnostics.Add);
        Assert.True(content.Count == 3, string.Join("\n", diagnostics));
        var cassini = Assert.Single(content, mission => mission.Name == "Cassini SOI");
        Assert.EndsWith("cassini.glb", cassini.MeshPath);
        var builder = new WorldGraphBuilder();
        builder.AddTrajectory(saturn, _ => default(CartesianState));
        foreach (var mission in content)
            builder.AddSampledMission(mission);
        var world = builder.Build();
        if (split)
        {
            // CSPICE reports the J2000-to-instrument matrix row-major. Each row is a body axis in J2000.
            string oracle = Environment.GetEnvironmentVariable("SPACETANGENT_CASSINI_SPLIT_ORACLE")!;
            using var document = JsonDocument.Parse(File.ReadAllBytes(oracle));
            foreach (var row in document.RootElement.GetProperty("rows").EnumerateArray())
            {
                var epoch = JulianInstant.FromTdbSeconds(row.GetProperty("seconds").GetDouble());
                var snapshot = world.Evaluate(epoch);
                double[] state = row.GetProperty("state").EnumerateArray().Select(value => value.GetDouble()).ToArray();
                double[] matrix = row.GetProperty("matrix").EnumerateArray().Select(value => value.GetDouble()).ToArray();
                Assert.True(snapshot.TryState(cassini.Id, out var actual));
                Assert.InRange((actual.PositionKilometers - new Vector3d(state[0], state[1], state[2])).Length, 0, 1e-6);
                Assert.InRange((actual.VelocityKilometersPerSecond - new Vector3d(state[3], state[4], state[5])).Length, 0, 1e-9);
                Assert.True(snapshot.TryOrientation(cassini.Id, out var orientation));
                Assert.InRange((orientation.ToWorld.X - new Vector3d(matrix[0], matrix[1], matrix[2])).Length, 0, 3e-8);
                Assert.InRange((orientation.ToWorld.Y - new Vector3d(matrix[3], matrix[4], matrix[5])).Length, 0, 3e-8);
                Assert.InRange((orientation.ToWorld.Z - new Vector3d(matrix[6], matrix[7], matrix[8])).Length, 0, 3e-8);
            }
            Assert.Throws<InvalidDataException>(() => SampledMissionCatalog.Load(manifests.Take(1), root,
                name => name is "Sol/Saturn" or "Saturn" ? saturn : null));
            return;
        }

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
                Assert.Equal(camera.FootprintOpacity, sensor.FootprintOpacity);
                Assert.InRange((frame.ToWorld.Transform(sensor.Boresight) - body.Transform(camera.BoresightInBody)).Length, 0, 1e-12);
            }
        }
    }

    [CassiniJsonFact]
    [Trait("Category", "ExternalData")]
    public void MissionCataloguePackRelocatesOriginalKernelsMeshAndCameraFields()
    {
        string source = Environment.GetEnvironmentVariable("SPACETANGENT_CASSINI_JSON_ROOT")!;
        var saturn = new ObjectId("sol/saturn");
        ObjectId? Resolve(string name) => name == "Saturn" ? saturn : null;
        var temporary = Directory.CreateTempSubdirectory("st-mission-pack-");
        try
        {
            string output = Path.Combine(temporary.FullName, "built");
            Assert.Equal((3, 0, 0), MissionCataloguePack.Build(source, output, Resolve));
            string install = Path.Combine(temporary.FullName, "перенесённая миссия");
            Directory.Move(output, install);
            var sourceGraph = Build(SampledMissionCatalog.Load("cassini.json", source, Resolve));
            var installed = SampledMissionCatalog.Load("перенесённая миссия/pack.json", temporary.FullName, Resolve);
            var installedGraph = Build(installed);
            var craft = Assert.Single(installed, mission => mission.Name == "Cassini SOI");
            Assert.Equal(Path.Combine(install, "content", "cassini.3ds"), craft.MeshPath);
            Assert.Equal(File.ReadAllBytes(Path.Combine(source, "cassini.3ds")), File.ReadAllBytes(craft.MeshPath!));
            Assert.Equal(2, installed.Count(mission => mission.Geometry?.Any(span => span.Shape.Sensor is not null) == true));

            foreach (double seconds in new[] { 141911269.46157101, 141917558.4203589 })
            {
                var epoch = JulianInstant.FromTdbSeconds(seconds);
                var baseline = sourceGraph.Evaluate(epoch);
                var relocated = installedGraph.Evaluate(epoch);
                foreach (var mission in installed)
                {
                    Assert.True(baseline.TryState(mission.Id, out var state));
                    Assert.True(relocated.TryState(mission.Id, out var relocatedState));
                    Assert.Equal(state, relocatedState);
                    Assert.True(baseline.TryOrientation(mission.Id, out var orientation));
                    Assert.True(relocated.TryOrientation(mission.Id, out var relocatedOrientation));
                    Assert.Equal(orientation, relocatedOrientation);
                }
            }

            File.Delete(Path.Combine(install, "content", "soi.tm"));
            File.Copy(Path.Combine(source, "soi.tm"), Path.Combine(install, "soi.tm"));
            Assert.Throws<FileNotFoundException>(() => SampledMissionCatalog.Load("перенесённая миссия/pack.json", temporary.FullName, Resolve));
        }
        finally
        {
            temporary.Delete(recursive: true);
        }

        WorldGraph Build(IReadOnlyList<SampledMission> missions)
        {
            var builder = new WorldGraphBuilder();
            builder.AddTrajectory(saturn, _ => default(CartesianState));
            foreach (var mission in missions)
                builder.AddSampledMission(mission);
            return builder.Build();
        }
    }

    private sealed class PackedCassiniTheoryAttribute : TheoryAttribute
    {
        public PackedCassiniTheoryAttribute()
        {
            if (new[] { "SPACETANGENT_CASSINI_PACKED_ROOT", "SPACETANGENT_CASSINI_SPLIT_ROOT", "SPACETANGENT_CASSINI_SPLIT_ORACLE", "SPACETANGENT_CASSINI_JSON_ROOT" }
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

    [CassiniJsonFact]
    [Trait("Category", "ExternalData")]
    public void CurvedAndPolygonInstrumentFieldsSurviveMissionPacking()
    {
        string original = Environment.GetEnvironmentVariable("SPACETANGENT_CASSINI_JSON_ROOT")!;
        var temporary = Directory.CreateTempSubdirectory("st-ik-shapes-");
        try
        {
            string source = Directory.CreateDirectory(Path.Combine(temporary.FullName, "source")).FullName;
            foreach (string file in Directory.EnumerateFiles(original))
                File.Copy(file, Path.Combine(source, Path.GetFileName(file)));
            string catalog = Path.Combine(source, "cassini.json");
            var document = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(catalog))!.AsObject();
            var items = document["items"]!.AsArray();
            foreach (var (name, instrument) in new[] { ("NAC radiator", "-82368"), ("WAC radiator", "-82369"),
                ("Corner circle", "-82991"), ("Corner ellipse", "-82992"), ("Angle ellipse", "-82993"), ("Polygon", "-82994") })
            {
                var item = items[1]!.DeepClone();
                item["name"] = name;
                item["geometry"]!["instrName"] = instrument;
                items.Add(item);
            }
            document["spiceKernels"]!.AsArray().Add("shapes.ti");
            File.WriteAllText(catalog, document.ToJsonString());
            File.WriteAllText(Path.Combine(source, "shapes.ti"), """
                KPL/IK
                \begindata
                INS-82991_FOV_FRAME = 'CASSINI_SC_COORD'
                INS-82991_FOV_SHAPE = 'circle'
                INS-82991_BORESIGHT = (0 0 1)
                INS-82991_FOV_BOUNDARY_CORNERS = (.2 0 1)
                INS-82992_FOV_FRAME = 'CASSINI_SC_COORD'
                INS-82992_FOV_SHAPE = 'ELLIPSE'
                INS-82992_BORESIGHT = (0 0 1)
                INS-82992_FOV_BOUNDARY_CORNERS = (.2 0 1  0 .4 1)
                INS-82993_FOV_FRAME = 'CASSINI_SC_COORD'
                INS-82993_FOV_SHAPE = 'ELLIPSE'
                INS-82993_BORESIGHT = (0 0 1)
                INS-82993_FOV_CLASS_SPEC = 'angles'
                INS-82993_FOV_REF_VECTOR = (1 0 0)
                INS-82993_FOV_REF_ANGLE = (11.309932474020215)
                INS-82993_FOV_CROSS_ANGLE = (21.80140948635181)
                INS-82993_FOV_ANGLE_UNITS = 'degrees'
                INS-82994_FOV_FRAME = 'CASSINI_SC_COORD'
                INS-82994_FOV_SHAPE = 'POLYGON'
                INS-82994_BORESIGHT = (0 0 1)
                INS-82994_FOV_BOUNDARY_CORNERS = (0 .3 1  -.2 -.1 1  .2 -.1 1)
                \begintext
                """);
            var dedicated = SpiceJsonCatalog.Load(catalog, source);
            Assert.Equal(8, dedicated.SensorCount);
            var sensors = dedicated.Sensors.ToDictionary(sensor => sensor.Name);
            foreach (string name in new[] { "NAC radiator", "WAC radiator" })
            {
                var sensor = sensors[name];
                Assert.Equal(32, sensor.CornersInBody.Count);
                Assert.All(sensor.CornersInBody, ray =>
                    Assert.InRange(Math.Abs(Vector3d.Dot(ray, sensor.BoresightInBody)), 0, 1e-13));
            }
            Assert.Equal(32, sensors["Corner circle"].CornersInBody.Count);
            Compare(sensors["Corner circle"].CornersInBody[0], new(.2, 0, 1));
            Compare(sensors["Corner circle"].CornersInBody[8], new(0, .2, 1));
            foreach (string name in new[] { "Corner ellipse", "Angle ellipse" })
            {
                var rim = sensors[name].CornersInBody;
                Assert.Equal(32, rim.Count);
                Compare(rim[0], new(.2, 0, 1));
                Compare(rim[8], new(0, .4, 1));
                Compare(rim[4], new(.2 / Math.Sqrt(2), .4 / Math.Sqrt(2), 1));
            }
            Assert.Equal(3, sensors["Polygon"].CornersInBody.Count);
            Compare(sensors["Polygon"].CornersInBody[1], new(-.2, -.1, 1));

            ObjectId? Resolve(string name) => name == "Saturn" ? new("sol/saturn") : null;
            var loose = SampledMissionCatalog.Load("cassini.json", source, Resolve);
            string packed = Path.Combine(temporary.FullName, "packed");
            Assert.Equal((9, 0, 0), MissionCataloguePack.Build(source, packed, Resolve));
            var restored = SampledMissionCatalog.Load("packed/pack.json", temporary.FullName, Resolve);
            foreach (var (name, expected) in new[] { ("NAC radiator", 32), ("Corner circle", 32),
                ("Angle ellipse", 32), ("Polygon", 36) })
            {
                var fromSource = Sensor(loose, name);
                var fromPack = Sensor(restored, name);
                Assert.Equal(expected, fromSource.Boundary.Count);
                Assert.Equal(fromSource.Boundary, fromPack.Boundary);
            }
        }
        finally
        {
            temporary.Delete(recursive: true);
        }

        static MissionSensor Sensor(IReadOnlyList<SampledMission> missions, string name) =>
            Assert.Single(Assert.Single(missions, mission => mission.Name == name).Geometry!).Shape.Sensor!;

        static void Compare(Vector3d actual, Vector3d expected) =>
            Assert.InRange((actual - expected / expected.Length).Length, 0, 1e-13);
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
            Assert.Contains("SPICE(RECURSIVELOADING)", Assert.Throws<InvalidDataException>(() =>
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
