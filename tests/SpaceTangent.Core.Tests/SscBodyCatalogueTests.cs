// SPDX-License-Identifier: GPL-2.0-or-later
using SpaceTangent.Contracts;
using SpaceTangent.Core.Addons;
using SpaceTangent.Core.Time;
using SpaceTangent.Core.World;
using Xunit;

namespace SpaceTangent.Core.Tests;

public sealed class SscBodyCatalogueTests
{
    [Fact]
    public void RectangularFixedPositionSurvivesSourceAndPack()
    {
        var root = Directory.CreateTempSubdirectory("st-ssc-rectangular-");
        try
        {
            string source = root.CreateSubdirectory("source").FullName;
            string pack = Path.Combine(root.FullName, "pack");
            File.WriteAllText(Path.Combine(source, "probe.ssc"), """
                "Probe" "Sol/Earth" {
                    OrbitFrame { EquatorJ2000 {} }
                    FixedPosition { Rectangular [ 0 0 -0.00155 ] }
                }
                """);

            var expected = new CartesianState(new(0, 0, -.00155), default);
            var epoch = Epoch(2451545);
            Assert.Equal(expected, Assert.Single(SscBodyCatalogue.Load(source)).Sample(epoch));
            Assert.Equal(1, SscBodyCatalogue.BuildPack(source, pack, _ => { }));
            Assert.Equal(expected, Assert.Single(SscBodyCatalogue.Load(pack)).Sample(epoch));
        }
        finally { root.Delete(recursive: true); }
    }

    [Fact]
    public void OrderedPatchesKeepAndReplaceTheEffectiveFields()
    {
        DirectoryInfo root = Directory.CreateTempSubdirectory("st-ssc-");
        try
        {
            File.WriteAllText(Path.Combine(root.FullName, "01-base.ssc"), """
                Add "Demo:Alias" "Sol/Earth"
                {
                    Radius 3
                    Color [ 0.2 0.4 0.6 ]
                    EllipticalOrbit { SemiMajorAxis 7000 Period 0.067 }
                }
                """);
            File.WriteAllText(Path.Combine(root.FullName, "02-change.ssc"), """
                Modify "Alias" "Sol/Earth" { Radius 4 }
                """);
            var body = Assert.Single(SscBodyCatalogue.Load(root.FullName));
            Assert.Equal(4, body.RadiusKilometers);
            Assert.Equal(new Vector3d(.2, .4, .6), body.Color);
            Assert.Equal(new Vector3d(7000, 0, 0), body.Sample(Epoch(2451545))!.Value.PositionKilometers);

            File.WriteAllText(Path.Combine(root.FullName, "03-replace.ssc"), """
                Replace "Demo" "Sol/Earth"
                {
                    Radius 5
                    SampledTrajectory { Source "states.xyzv" Interpolation "cubic" }
                }
                """);
            File.WriteAllText(Path.Combine(root.FullName, "states.xyzv"),
                "2451545 8000 0 0 0 1 0\n2451546 8000 86400 0 0 1 0\n");
            body = Assert.Single(SscBodyCatalogue.Load(root.FullName));
            Assert.Equal(5, body.RadiusKilometers);
            Assert.Equal(new Vector3d(.8, .8, .8), body.Color);
            Assert.Equal(new Vector3d(8000, 43200, 0), body.Sample(Epoch(2451545.5))!.Value.PositionKilometers);
            Assert.Equal(body.Sample(Epoch(2451545)), body.Sample(Epoch(2451544)));
            Assert.Equal(body.Sample(Epoch(2451546)), body.Sample(Epoch(2451547)));

            File.WriteAllText(Path.Combine(root.FullName, "04-lifetime.ssc"), """
                Modify "Demo" "Sol/Earth" { Beginning "2451543" Ending "2451548" }
                """);
            body = Assert.Single(SscBodyCatalogue.Load(root.FullName));
            Assert.Null(body.Sample(Epoch(2451542)));
            Assert.Equal(body.Sample(Epoch(2451545)), body.Sample(Epoch(2451543)));
            Assert.Equal(body.Sample(Epoch(2451546)), body.Sample(Epoch(2451548)));
            Assert.Null(body.Sample(Epoch(2451549)));
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FailedSpiceModelsUseDeclaredOrbitAndRotationAlternatives(bool missingKernel)
    {
        var root = Directory.CreateTempSubdirectory("st-ssc-fallback-");
        try
        {
            string file = Path.Combine(root.FullName, "fallback.ssc");
            const string source = """
                "Test probe" "Sol/Earth" {
                    Radius 2
                    SpiceOrbit { Target "NOT_A_NAIF_TARGET" Origin "Earth" BoundingRadius 10000 }
                    EllipticalOrbit { SemiMajorAxis 7000 Period 1 }
                    SpiceRotation { Frame "UNKNOWN_FRAME" BaseFrame "J2000" }
                    FixedRotation { Inclination 12 }
                }
                """;
            File.WriteAllText(file, missingKernel
                ? source.Replace("SpiceOrbit {", "SpiceOrbit { Kernel \"missing.bsp\"", StringComparison.Ordinal)
                    .Replace("SpiceRotation {", "SpiceRotation { Kernel \"missing.bc\"", StringComparison.Ordinal)
                : source);
            var warnings = new List<string>();
            var fallback = Assert.Single(SscBodyCatalogue.Load(root.FullName, warning: warnings.Add));
            Assert.Equal(2, warnings.Count);
            Assert.StartsWith("Sol/Earth/Test probe: SpiceOrbit initialization failed.", warnings[0]);
            Assert.StartsWith("Sol/Earth/Test probe: SpiceRotation initialization failed.", warnings[1]);
            if (missingKernel)
                Assert.All(warnings, message => Assert.Contains("asset is missing", message));
            File.WriteAllText(file, string.Join('\n', source.Split('\n').Where(line => !line.TrimStart().StartsWith("Spice", StringComparison.Ordinal))));
            var expected = Assert.Single(SscBodyCatalogue.Load(root.FullName));
            var epoch = Epoch(2451545);
            Assert.Equal(expected.Sample(epoch), fallback.Sample(epoch));
            Assert.Equal(expected.SampleRotation!(epoch), fallback.SampleRotation!(epoch));
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [LocalBodyPackFact]
    [Trait("Category", "ExternalData")]
    public void OriginalStationAndAsteroidArcHaveRealStates()
    {
        var bodies = SscBodyCatalogue.Load(Environment.GetEnvironmentVariable("SPACETANGENT_SSC_ADDON_PACK")!);
        var station = Assert.Single(bodies, item => item.Name == "ISS");
        Assert.EndsWith("iss.cmod", station.Mesh);
        Assert.Equal(FrameAxis.NegativeY, station.Orientation!.Primary.Axis);
        Assert.Equal(DirectionQuantity.Velocity, station.Orientation.Secondary.Quantity);
        Assert.InRange(station.Sample(Epoch(2460634.5))!.Value.PositionKilometers.Length, 6792, 6794);
        var asteroid = Assert.Single(bodies, item => item.Name == "2008 TC3");
        var state = asteroid.Sample(Epoch(2454013.5))!.Value;
        Assert.InRange(state.PositionKilometers.Length, 240_000_000, 250_000_000);
        Assert.InRange(state.VelocityKilometersPerSecond.Length, 19, 21);
    }

    [LocalBodyPackFact]
    [Trait("Category", "ExternalData")]
    public void OriginalAsteroidSamplesTakePriorityOverOtherOrbitsInSourceAndPack()
    {
        string fixture = Environment.GetEnvironmentVariable("SPACETANGENT_SSC_ADDON_PACK")!;
        var root = Directory.CreateTempSubdirectory("st-ssc-sample-priority-");
        try
        {
            var original = Assert.Single(SscBodyCatalogue.Load(fixture), item => item.Name == "2008 TC3");
            string folder = Directory.CreateDirectory(Path.Combine(root.FullName, "source")).FullName;
            string catalog = Directory.EnumerateFiles(fixture, "2008-tc3.ssc", SearchOption.AllDirectories).Single();
            string samples = Directory.EnumerateFiles(fixture, "2008TC3.xyzv", SearchOption.AllDirectories).Single();
            string text = File.ReadAllText(catalog);
            int field = text.IndexOf("SampledTrajectory", StringComparison.Ordinal);
            Assert.True(field >= 0);
            File.WriteAllText(Path.Combine(folder, "2008-tc3.ssc"), text.Insert(field,
                "EllipticalOrbit { SemiMajorAxis 2 Period 3 }\n FixedPosition [ 1 2 3 ]\n "));
            File.Copy(samples, Path.Combine(folder, Path.GetFileName(samples)));
            var loose = Assert.Single(SscBodyCatalogue.Load(folder));
            string packed = Path.Combine(root.FullName, "packed");
            Assert.Equal(1, SscBodyCatalogue.BuildPack(folder, packed, _ => { }));
            var restored = Assert.Single(SscBodyCatalogue.Load(packed));

            foreach (double day in new[] { 2454012.5, 2454013.5, 2454016.5, 2454030.5, 2454747.5 })
            {
                var epoch = Epoch(day);
                Assert.NotNull(original.Sample(epoch));
                Assert.Equal(original.Sample(epoch), loose.Sample(epoch));
                Assert.Equal(original.Sample(epoch), restored.Sample(epoch));
            }
            Assert.Equal(original.Sample(Epoch(2454013.5)), loose.Sample(Epoch(2454012.5)));
            Assert.Equal(original.Sample(Epoch(2454746.617361111)), restored.Sample(Epoch(2454747.5)));
            Assert.Null(loose.OrbitApoapsisKilometers);
            Assert.Null(restored.OrbitApoapsisKilometers);
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [LocalBodyPackFact]
    [Trait("Category", "ExternalData")]
    public void InvalidSampleTableSkipsOnlyItsBranchOfAnOriginalCatalogue()
    {
        string original = Environment.GetEnvironmentVariable("SPACETANGENT_SSC_ADDON_PACK")!;
        var temporary = Directory.CreateTempSubdirectory("st-ssc-rejected-samples-");
        try
        {
            string root = Directory.CreateDirectory(Path.Combine(temporary.FullName, "source")).FullName;
            string data = Directory.CreateDirectory(Path.Combine(root, "data")).FullName;
            foreach (string asset in new[] { "2008-tc3.ssc", "2008TC3.xyzv" })
                File.Copy(Path.Combine(original, "data", asset), Path.Combine(data, asset));
            File.WriteAllText(Path.Combine(data, "rejected.ssc"), """
                "Corrupt" "Sol/Earth" {
                    SampledTrajectory { Source "broken.xyzv" }
                    FixedPosition [ 5 6 7 ]
                }
                "Child" "Sol/Earth/Corrupt" { FixedPosition [ 1 2 3 ] }
                "DuplicateEpoch" "Sol/Earth" {
                    Timeline [ { SampledTrajectory { Source "duplicate.xyzv" } } ]
                }
                "Missing" "Sol/Earth" {
                    Timeline [ { SampledTrajectory { Source "missing.xyzv" } } ]
                }
                """);
            File.WriteAllText(Path.Combine(data, "broken.xyzv"), "2451545 1 2");
            var originalRows = File.ReadLines(Path.Combine(data, "2008TC3.xyzv"))
                .Where(line => !string.IsNullOrWhiteSpace(line)).Take(3).ToArray();
            File.WriteAllLines(Path.Combine(data, "duplicate.xyzv"), originalRows.Concat(originalRows));
            var warnings = new List<string>();
            var source = SscBodyCatalogue.Load(root, warning: warnings.Add);
            Assert.Contains(warnings, warning => warning.Contains("Sol/Earth/Corrupt: SampledTrajectory initialization failed: broken.xyzv", StringComparison.Ordinal));
            Assert.Contains(warnings, warning => warning.Contains("Sol/Earth/DuplicateEpoch: Timeline phase 1: SampledTrajectory initialization failed: duplicate.xyzv: SSC trajectory sample epochs must increase.", StringComparison.Ordinal));
            Assert.Contains(warnings, warning => warning.Contains("Sol/Earth/Missing: Timeline phase 1: SampledTrajectory initialization failed: missing.xyzv", StringComparison.Ordinal));
            string packed = Path.Combine(temporary.FullName, "packed");
            Assert.Equal(1, SscBodyCatalogue.BuildPack(root, packed, warnings.Add));
            var restored = SscBodyCatalogue.Load(packed);
            var epoch = Epoch(2454013.5);
            var expected = Assert.Single(SscBodyCatalogue.Load(original), item => item.Name == "2008 TC3");
            Assert.Equal("2008 TC3", Assert.Single(source).Name);
            Assert.Equal(expected.Sample(epoch), Assert.Single(source).Sample(epoch));
            Assert.Equal(expected.Sample(epoch), Assert.Single(restored).Sample(epoch));
        }
        finally
        {
            temporary.Delete(recursive: true);
        }
    }

    [LocalBodyPackFact]
    [Trait("Category", "ExternalData")]
    public void PositionOnlyTrajectoryUsesOriginalTc3SamplesInSourceAndPack()
    {
        string archive = Environment.GetEnvironmentVariable("SPACETANGENT_SSC_ADDON_PACK")!;
        var scratch = Directory.CreateTempSubdirectory("st-ssc-position-only-");
        try
        {
            string source = scratch.CreateSubdirectory("source").FullName;
            string file = Path.Combine(archive, "data", "2008TC3.xyzv");
            string[] rows = File.ReadLines(file).Take(12).ToArray();
            var positions = rows.Chunk(3).Select(row => row[0] + " " + row[1].Trim()).ToArray();
            File.WriteAllLines(Path.Combine(source, "2008TC3.xyz"), positions);
            string catalogue = File.ReadAllText(Path.Combine(archive, "data", "2008-tc3.ssc"));
            File.WriteAllText(Path.Combine(source, "2008-tc3.ssc"),
                catalogue.Replace("2008TC3.xyzv", "2008TC3.xyz", StringComparison.Ordinal));
            string packed = Path.Combine(scratch.FullName, "packed");
            Assert.Equal(1, SscBodyCatalogue.BuildPack(source, packed, warning => throw new InvalidDataException(warning)));
            var authored = Assert.Single(SscBodyCatalogue.Load(archive), body => body.Name == "2008 TC3");
            var loose = Assert.Single(SscBodyCatalogue.Load(source));
            var relocated = Assert.Single(SscBodyCatalogue.Load(packed));
            foreach (double jd in new[] { 2454013.5, 2454015.5, 2454017.5, 2454019.5 })
            {
                var epoch = Epoch(jd);
                Assert.Equal(authored.Sample(epoch)!.Value.PositionKilometers, loose.Sample(epoch)!.Value.PositionKilometers);
                Assert.Equal(loose.Sample(epoch), relocated.Sample(epoch));
                Assert.True(loose.Sample(epoch)!.Value.VelocityKilometersPerSecond.Length > 0);
            }
            Assert.Equal(loose.Sample(Epoch(2454014.5)), relocated.Sample(Epoch(2454014.5)));
        }
        finally { scratch.Delete(recursive: true); }
    }

    [OriginalSpacecraftFact]
    [Trait("Category", "ExternalData")]
    public void OriginalConicSpacecraftKeepMissionWindowsAndModels()
    {
        var bodies = SscBodyCatalogue.Load(Environment.GetEnvironmentVariable("SPACETANGENT_STANDARD_CONIC")!);
        var hubble = Check("Hubble", "Sol/Earth", "hubble.cmod", "2020-01-01T00:00:00Z", 6900, 6950);
        Assert.Equal("Hubble:HST:1990-037B", hubble.Names);
        var launch = At("1990-04-25T02:02:00Z");
        Assert.Null(hubble.Sample(launch.AddNanoseconds(-1)));
        Assert.NotNull(hubble.Sample(launch));

        var mir = Check("Mir", "Sol/Earth", "mir.cmod", "2000-01-01T00:00:00Z", 6768, 6770);
        Assert.NotNull(mir.Sample(At("2001-03-23T06:00:28.184Z")));
        Assert.Null(mir.Sample(At("2001-03-23T06:00:28.184Z").AddNanoseconds(1)));

        Assert.Equal(7, bodies.Count(body => body.Name.StartsWith("Apollo ", StringComparison.Ordinal)));
        var apollo = Check("Apollo 11", "Sol/Earth/Moon", "apollo.3ds", "1969-07-20T12:00:00Z", 1700, 2100);
        Assert.Null(apollo.Sample(At("1969-07-19T17:21:49Z")));
        Assert.Null(apollo.Sample(At("1969-07-22T04:55:43Z")));

        var odyssey = Check("Mars Odyssey", "Sol/Mars", "marsodyssey.3ds", "2002-12-15T00:00:00Z", 3760, 3840);
        Assert.Equal(FrameAxis.Y, odyssey.Orientation!.Primary.Axis);
        Assert.Equal(DirectionQuantity.Velocity, odyssey.Orientation.Secondary.Quantity);
        Check("MGS", "Sol/Mars", "marsglobalsurvr.3ds", "2001-03-04T18:12:26Z", 3720, 3810);

        SscBodyDefinition Check(string name, string center, string mesh, string time, double minRadius, double maxRadius)
        {
            var body = Assert.Single(bodies, item => item.Name == name);
            Assert.Equal(center, body.Center);
            Assert.Equal(mesh, Path.GetFileName(body.Mesh));
            var state = Assert.IsType<CartesianState>(body.Sample(At(time)));
            Assert.InRange(state.PositionKilometers.Length, minRadius, maxRadius);
            Assert.InRange(state.VelocityKilometersPerSecond.Length, 1, 9);
            return body;
        }

        static JulianInstant At(string value)
        {
            var civil = DateTimeOffset.Parse(value, System.Globalization.CultureInfo.InvariantCulture).UtcDateTime;
            var day = DateOnly.FromDateTime(civil);
            if (day < new DateOnly(1972, 1, 1))
            {
                var terrestrial = JulianInstant.FromJulianDate(2440587.5, TimeScale.Tt)
                    .AddSeconds(new DateTimeOffset(civil).ToUnixTimeSeconds());
                return TimeConversion.Convert(terrestrial.AddSeconds(DeltaT.Seconds(terrestrial, DeltaTModel.EspenakMeeus2006)), TimeScale.Tdb);
            }
            var utc = new UtcDateTime(day, civil.Hour, civil.Minute, civil.Second, (int)(civil.Ticks % TimeSpan.TicksPerSecond) * 100);
            return TimeConversion.FromUtc(utc, TimeScale.Tdb);
        }
    }

    private sealed class OriginalSpacecraftFactAttribute : FactAttribute
    {
        public OriginalSpacecraftFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SPACETANGENT_STANDARD_CONIC")))
                Skip = "Set SPACETANGENT_STANDARD_CONIC to the original conic spacecraft catalogues.";
        }
    }

    [SmallRingsFact]
    [Trait("Category", "ExternalData")]
    public void OriginalSmallBodyRingsKeepTheirRadiiTexturesAndBarycentricOrbits()
    {
        var bodies = SscBodyCatalogue.Load(Environment.GetEnvironmentVariable("SPACETANGENT_SMALL_RINGS")!);
        Check("2060 Chiron", 341.66, 353.77, "chiron-rings.png");
        Check("10199 Chariklo", 382.5, 399.9, "chariklo-rings.png");
        Check("Haumea", 2252, 2322, "haumea-rings.png");
        double expectedHaumeaApoapsis = 43.1042753471461 * (1 + .1949599020093612) * 149_597_870.7;
        Assert.InRange(Math.Abs(bodies.Single(body => body.Name == "Haumea").OrbitApoapsisKilometers!.Value -
            expectedHaumeaApoapsis), 0, .001);
        Check("Quaoar", 2515, 4098, "quaoar-rings.png");

        void Check(string name, double inner, double outer, string texture)
        {
            var body = Assert.Single(bodies, item => item.Name == name);
            Assert.Equal("SSB", body.Center);
            var rings = Assert.IsType<SscRings>(body.Rings);
            Assert.Equal(inner, rings.InnerKilometers);
            Assert.Equal(outer, rings.OuterKilometers);
            Assert.Equal(texture, Path.GetFileName(rings.TexturePath));
            Assert.InRange(body.Sample(Epoch(2451545))!.Value.PositionKilometers.Length, 1e9, 8e9);
            Assert.NotNull(body.SampleRotation!(Epoch(2451545)));
        }
    }

    private sealed class SmallRingsFactAttribute : FactAttribute
    {
        public SmallRingsFactAttribute()
        {
            if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SPACETANGENT_SMALL_RINGS")))
                Skip = "Set SPACETANGENT_SMALL_RINGS to the original small-body SSC entries.";
        }
    }

    private static JulianInstant Epoch(double day) => JulianInstant.FromJulianDate(day, TimeScale.Tdb);

    private sealed class LocalBodyPackFactAttribute : FactAttribute
    {
        public LocalBodyPackFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SPACETANGENT_SSC_ADDON_PACK")))
                Skip = "Set SPACETANGENT_SSC_ADDON_PACK to the verified body add-on pack.";
        }
    }
}
