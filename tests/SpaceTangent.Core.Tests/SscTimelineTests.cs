// SPDX-License-Identifier: GPL-2.0-or-later
using System.Text.Json;
using SpaceTangent.Contracts;
using SpaceTangent.Core.Addons;
using SpaceTangent.Core.Ephemerides;
using SpaceTangent.Core.Orbits;
using SpaceTangent.Core.Time;
using SpaceTangent.Core.World;
using Xunit;

namespace SpaceTangent.Core.Tests;

public sealed class SscTimelineTests
{
    [GalileoFact]
    [Trait("Category", "ExternalData")]
    public void OriginalGalileoAndProbeSwitchCentersAndKeepTheirActivePhases()
    {
        string root = Environment.GetEnvironmentVariable("SPACETANGENT_STANDARD_SPACECRAFT")!;
        string reference = Environment.GetEnvironmentVariable("SPACETANGENT_GALILEO_REFERENCE")!;
        var bodies = SscBodyCatalogue.Load(root);
        ObjectId orbiter = new("ssc/sol/jupiter/galileo"), probe = new("ssc/sol/jupiter/galileo/galileo-probe");
        var builder = new WorldGraphBuilder();
        builder.AddSolarSystem(De440Ephemeris.Load(Environment.GetEnvironmentVariable("SPACETANGENT_DE440")!));
        var galileo = Assert.Single(bodies, body => body.Name == "Galileo");
        var lander = Assert.Single(bodies, body => body.Name == "Galileo Probe");
        Assert.Equal("galileo.cmod", Path.GetFileName(galileo.Mesh));
        Assert.Equal("galileo-probe.3ds", Path.GetFileName(lander.Mesh));
        Assert.Equal("galileo_tex_02.jpg", Path.GetFileName(
            SscBodyCatalogue.ResolveTexture(root, galileo.Mesh!, "galileo_tex_02.*")));
        var craft = galileo.BindTimeline!(orbiter, Resolve);
        var payload = lander.BindTimeline!(probe, Resolve);
        builder.AddSampledMission(craft);
        builder.AddSampledMission(payload);
        var graph = builder.Build();
        using var oracle = JsonDocument.Parse(File.ReadAllText(reference));
        foreach (var entry in oracle.RootElement.EnumerateArray())
        {
            Compare(orbiter, craft, "orbiter", entry);
            Compare(probe, payload, "probe", entry);
        }

        Assert.Equal(10, oracle.RootElement.GetArrayLength());
        AssertSwitch(craft, orbiter, SolarSystemSample.SunId, SolarSystemSample.JupiterBarycenterId);
        AssertSwitch(payload, probe, orbiter, SolarSystemSample.JupiterBarycenterId);
        Assert.True(graph.Evaluate(craft.Start).TryState(orbiter, out _));
        Assert.False(graph.Evaluate(craft.Start.AddNanoseconds(-1)).TryState(orbiter, out _));
        Assert.False(graph.Evaluate(craft.End).TryState(orbiter, out _));
        var time = TimeConversion.FromUtc(new(new(1996, 1, 1)), TimeScale.Tdb);
        Assert.True(graph.Evaluate(time).TryOrientation(orbiter, out var first));
        Assert.True(graph.Evaluate(time.AddSeconds(5)).TryOrientation(orbiter, out var second));
        Assert.True(Vector3d.Dot(first.ToWorld.Z, second.ToWorld.Z) > .9999);
        Assert.InRange(Vector3d.Dot(first.ToWorld.X, second.ToWorld.X), -.001, .001);

        void AssertSwitch(SampledMission timeline, ObjectId id, ObjectId prior, ObjectId next)
        {
            Assert.Equal(2, timeline.Arcs.Count);
            var boundary = timeline.Arcs[1].Start;
            Assert.True(graph.Evaluate(boundary.AddNanoseconds(-1)).TryStateCenter(id, out var center));
            Assert.Equal(prior, center);
            Assert.True(graph.Evaluate(boundary).TryStateCenter(id, out center));
            Assert.Equal(next, center);
        }

        void Compare(ObjectId id, SampledMission timeline, string name, JsonElement entry)
        {
            var epoch = JulianInstant.FromJulianDate(entry.GetProperty("jd").GetDouble(), TimeScale.Tdb);
            var snapshot = graph.Evaluate(epoch);
            bool expected = entry.GetProperty(name + "Active").GetBoolean();
            var values = entry.GetProperty(name);
            Assert.Equal(expected, snapshot.TryState(id, out var state));
            Assert.Equal(expected, snapshot.TryOrientation(id, out _));
            if (!expected)
                return;

            var arc = Assert.Single(timeline.Arcs, arc => arc.Start.CompareTo(epoch) <= 0 && epoch.CompareTo(arc.End) < 0);
            Assert.True(snapshot.TryStateCenter(id, out var center));
            Assert.Equal(arc.Center, center);
            Assert.True(snapshot.TryState(center, out var parent));
            var offset = state - parent;
            if (center == orbiter)
            {
                // Check the fixed mounting distance and that axial mounting produces negligible tangential velocity.
                Assert.InRange(offset.PositionKilometers.Length, .0015498, .0015502);
                Assert.InRange(offset.VelocityKilometersPerSecond.Length, 0, 1e-8);
            }
            else
            {
                Vector3d position = Equatorial(0), velocity = Equatorial(3);
                double positionError = (offset.PositionKilometers - position).Length;
                double velocityError = (offset.VelocityKilometersPerSecond - velocity).Length;
                Assert.True(positionError < .002 && velocityError < 1e-5,
                    $"{id.Value} at {entry.GetProperty("date")}: {positionError:R} km, {velocityError:R} km/s. Delta {offset.PositionKilometers - position}; expected {position}; actual {offset.PositionKilometers}.");
            }

            Vector3d Equatorial(int at)
            {
                var (sine, cosine) = Math.SinCos(84381.448 / 3600 * Math.PI / 180);
                double x = values[at].GetDouble(), y = values[at + 1].GetDouble(), z = values[at + 2].GetDouble();
                return new(x, cosine * y - sine * z, sine * y + cosine * z);
            }
        }

        ObjectId Resolve(string path) => path switch
        {
            "Sol" => SolarSystemSample.SunId,
            "Sol/Earth" => SolarSystemSample.EarthId,
            "Sol/Jupiter" => SolarSystemSample.JupiterBarycenterId,
            "Sol/Jupiter/Galileo" => orbiter,
            "Sol/Jupiter/Galileo/Galileo Probe" => probe,
            _ => throw new ArgumentException(path)
        };
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void UnavailableAttitudeKeepsExplicitFixedRotationInSourceAndPack(bool timeline, bool missing)
    {
        using var fixture = new TemporaryAttitude();
        string catalog = Directory.EnumerateFiles(fixture.Source, "*.ssc").Single();
        string quaternion = Path.Combine(fixture.Source, "data", "probe.q");
        if (missing)
            File.Delete(quaternion);
        else
            File.WriteAllText(quaternion, "2451545 0 0 0 0");
        const string fields = """
            Beginning 2451545 Ending 2451547 FixedPosition [ 1 2 3 ]
            BodyFrame { EquatorJ2000 {} } FixedRotation { MeridianAngle 40 }
            """;
        string selected = fields + "\nSampledOrientation \"probe.q\"";
        File.WriteAllText(catalog, Object(fields));
        var expected = SscBodyCatalogue.Load(fixture.Source);
        File.WriteAllText(catalog, Object(selected));
        var warnings = new List<string>();
        var loose = SscBodyCatalogue.Load(fixture.Source, warning: warnings.Add);
        Assert.Contains(warnings, message => message.Contains("SampledOrientation initialization failed: probe.q", StringComparison.Ordinal));
        Assert.Equal(1, SscBodyCatalogue.BuildPack(fixture.Source, fixture.Pack, _ => { }));
        var packed = SscBodyCatalogue.Load(fixture.Pack);
        var time = JulianInstant.FromJulianDate(2451545.5, TimeScale.Tdb);
        Assert.Equal(Sample(expected), Sample(loose));
        Assert.Equal(Sample(expected), Sample(packed));

        string Object(string model) => "\"Probe\" \"Sol/Earth\" { " +
            (timeline ? "Timeline [ { " + model + " } ]" : model) + " }";

        FrameKinematics Sample(IReadOnlyList<SscBodyDefinition> definitions)
        {
            var body = Assert.Single(definitions);
            if (!timeline)
                return Assert.IsType<FrameKinematics>(body.SampleRotation!(time));
            var id = new ObjectId("probe");
            var center = SolarSystemSample.EarthId;
            var builder = new WorldGraphBuilder();
            builder.AddTrajectory(center, _ => new CartesianState(default, default));
            builder.AddSampledMission(body.BindTimeline!(id, _ => center));
            Assert.True(builder.Build().Evaluate(time).TryOrientation(id, out var rotation));
            return rotation;
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TimelineInheritsOuterLifetimeUnlessThePhaseDeclaresAnEndpoint(bool phaseBounds)
    {
        using var fixture = new TemporaryAttitude();
        string catalog = Directory.EnumerateFiles(fixture.Source, "*.ssc").Single();
        string first = phaseBounds ? "Beginning 2451546" : "";
        string last = phaseBounds ? "Ending 2451548" : "";
        File.WriteAllText(catalog, $$"""
            "Probe" "Sol/Earth" {
                Beginning 2451545 Ending 2451549
                Timeline [
                    { {{first}} Ending 2451547 FixedPosition [ 1 0 0 ] }
                    { {{last}} FixedPosition [ 2 0 0 ] }
                ]
            }
            """);
        Check(SscBodyCatalogue.Load(fixture.Source));
        Assert.Equal(1, SscBodyCatalogue.BuildPack(fixture.Source, fixture.Pack, _ => { }));
        Check(SscBodyCatalogue.Load(fixture.Pack));

        void Check(IReadOnlyList<SscBodyDefinition> definitions)
        {
            var id = new ObjectId("probe");
            var center = SolarSystemSample.EarthId;
            var mission = Assert.Single(definitions).BindTimeline!(id, _ => center);
            var start = JulianInstant.FromJulianDate(phaseBounds ? 2451546 : 2451545, TimeScale.Tdb);
            var end = JulianInstant.FromJulianDate(phaseBounds ? 2451548 : 2451549, TimeScale.Tdb);
            Assert.Equal(start, mission.Start);
            Assert.Equal(end, mission.End);
            var builder = new WorldGraphBuilder();
            builder.AddTrajectory(center, _ => new CartesianState(default, default));
            builder.AddSampledMission(mission);
            var graph = builder.Build();
            Assert.False(graph.Evaluate(start.AddNanoseconds(-1)).TryState(id, out _));
            Assert.True(graph.Evaluate(start).TryState(id, out var firstState));
            Assert.Equal(1, firstState.PositionKilometers.X);
            Assert.True(graph.Evaluate(end.AddNanoseconds(-1)).TryState(id, out var lastState));
            Assert.Equal(2, lastState.PositionKilometers.X);
            Assert.False(graph.Evaluate(end).TryState(id, out _));
        }
    }

    [OriginalSampleFact]
    [Trait("Category", "ExternalData")]
    public void StringSampledTrajectoryRetainsOriginalAsteroidStatesInsideTimeline()
    {
        using var fixture = new TemporaryAttitude();
        string source = Environment.GetEnvironmentVariable("SPACETANGENT_2008TC3_XYZV")!;
        File.Copy(source, Path.Combine(fixture.Source, "data", "asteroid.xyzv"));
        File.WriteAllText(Path.Combine(fixture.Source, "probe.ssc"), """
            "Probe" "Sol" {
                Timeline [
                    { OrbitFrame { EquatorJ2000 {} }
                      SampledTrajectory "asteroid.xyzv" }
                ]
            }
            """);

        var reference = SampledStateTrajectory.ReadXyzv(source, new FrameId("icrs"));
        Check(SscBodyCatalogue.Load(fixture.Source));
        Assert.Equal(1, SscBodyCatalogue.BuildPack(fixture.Source, fixture.Pack, _ => { }));
        Check(SscBodyCatalogue.Load(fixture.Pack));

        void Check(IReadOnlyList<SscBodyDefinition> bodies)
        {
            var center = SolarSystemSample.SunId;
            var id = new ObjectId("probe");
            var builder = new WorldGraphBuilder();
            builder.AddTrajectory(center, _ => default(CartesianState));
            builder.AddSampledMission(Assert.Single(bodies).BindTimeline!(id, _ => center));
            var world = builder.Build();
            var start = reference.Start!.Value;
            var end = reference.End!.Value;
            var middle = start.AddNanoseconds(end.DifferenceNanoseconds(start) / 2);
            foreach (var epoch in new[] { start, middle, end })
            {
                Assert.True(reference.TrySample(epoch, out var expected));
                Assert.True(world.Evaluate(epoch).TryState(id, out var actual));
                Assert.Equal(expected, actual);
            }
        }
    }

    private sealed class OriginalSampleFactAttribute : FactAttribute
    {
        public OriginalSampleFactAttribute()
        {
            if (!File.Exists(Environment.GetEnvironmentVariable("SPACETANGENT_2008TC3_XYZV")))
                Skip = "Set the original 2008 TC3 trajectory.";
        }
    }


    [Fact]
    public void PackedSampledAttitudeInterpolatesInsideThePhaseAndHoldsAtBothEnds()
    {
        using var fixture = new TemporaryAttitude();
        Verify(SscBodyCatalogue.Load(fixture.Source));
        Assert.Equal(1, SscBodyCatalogue.BuildPack(fixture.Source, fixture.Pack, _ => { }));
        Verify(SscBodyCatalogue.Load(fixture.Pack));

        static void Verify(IReadOnlyList<SscBodyDefinition> bodies)
        {
            var target = new ObjectId("probe");
            var center = SolarSystemSample.SunId;
            var builder = new WorldGraphBuilder();
            builder.AddTrajectory(center, _ => new CartesianState(default, default));
            builder.AddSampledMission(Assert.Single(bodies).BindTimeline!(target, _ => center));
            var graph = builder.Build();
            Check(2451545.25, new(1, 0, 0), 0);
            Check(2451546.5, new(Math.Sqrt(.5), -Math.Sqrt(.5), 0), -Math.PI / 172800);
            Check(2451547.5, new(0, -1, 0), 0);
            Check(2451548, new(0, -1, 0), 0);
            Assert.False(graph.Evaluate(JulianInstant.FromJulianDate(2451549, TimeScale.Tdb)).TryOrientation(target, out _));

            void Check(double day, Vector3d x, double rate)
            {
                var snapshot = graph.Evaluate(JulianInstant.FromJulianDate(day, TimeScale.Tdb));
                Assert.True(snapshot.TryOrientation(target, out var frame));
                Assert.InRange((frame.ToWorld.X - x).Length, 0, 1e-12);
                Assert.InRange((frame.AngularVelocityRadiansPerSecond!.Value - new Vector3d(0, 0, rate)).Length, 0, 1e-14);
            }
        }
    }

    private sealed class TemporaryAttitude : IDisposable
    {
        private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("space-tangent-ssc-attitude-");
        internal string Source => Path.Combine(_root.FullName, "source");
        internal string Pack => Path.Combine(_root.FullName, "pack");

        internal TemporaryAttitude()
        {
            Directory.CreateDirectory(Path.Combine(Source, "data"));
            File.WriteAllText(Path.Combine(Source, "probe.ssc"), """
                "Probe" "Sol" {
                    Timeline [
                        { Beginning 2451545 Ending 2451548 FixedPosition [ 1 0 0 ]
                          BodyFrame { EquatorJ2000 {} } SampledOrientation "probe.q" }
                        { Ending 2451549 FixedPosition [ 1 0 0 ]
                          BodyFrame { EquatorJ2000 {} } FixedRotation { MeridianAngle 90 } }
                    ]
                }
                """);
            File.WriteAllText(Path.Combine(Source, "data", "probe.q"), """
                2451546 1 0 0 0
                2451547 -0.7071067811865476 0 -0.7071067811865476 0
                """);
        }

        public void Dispose() => _root.Delete(recursive: true);
    }

    [GalileoFact]
    [Trait("Category", "ExternalData")]
    public void StandaloneProbeMountUsesTheOriginalGalileoBodyFrame()
    {
        string file = Path.Combine(Environment.GetEnvironmentVariable("SPACETANGENT_STANDARD_SPACECRAFT")!, "galileo.ssc");
        if (!File.Exists(file))
            file = Directory.GetFiles(Path.GetDirectoryName(file)!, "galileo.ssc", SearchOption.AllDirectories).Single();
        string original = File.ReadAllText(file);
        var mount = System.Text.RegularExpressions.Regex.Match(original,
            @"FixedPosition\s*(\[\s*0\s+0\s+-0\.00155\s*\])").Groups[1].Value;
        Assert.NotEmpty(mount);

        var fixture = Directory.CreateTempSubdirectory("st-ssc-body-fixed-");
        try
        {
            string source = fixture.CreateSubdirectory("source").FullName;
            string pack = Path.Combine(fixture.FullName, "pack");
            File.WriteAllText(Path.Combine(source, "probe.ssc"), $$"""
                "Galileo Probe" "Sol/Jupiter/Galileo" {
                    Beginning 2451545
                    Ending 2451546
                    OrbitFrame { BodyFixed { Center "Sol/Jupiter/Galileo" } }
                    BodyFrame { BodyFixed { Center "Sol/Jupiter/Galileo" } }
                    FixedRotation { }
                    FixedPosition {{mount}}
                }
                """);

            Check(SscBodyCatalogue.Load(source));
            Assert.Equal(1, SscBodyCatalogue.BuildPack(source, pack, _ => { }));
            Check(SscBodyCatalogue.Load(pack));
        }
        finally { fixture.Delete(recursive: true); }

        static void Check(IReadOnlyList<SscBodyDefinition> bodies)
        {
            var carrier = new ObjectId("mission/galileo");
            var probe = new ObjectId("mission/galileo-probe");
            var mission = Assert.Single(bodies).BindTimeline!(probe, path =>
                path == "Sol/Jupiter/Galileo" ? carrier : throw new InvalidDataException(path));
            var builder = new WorldGraphBuilder();
            builder.AddTrajectory(carrier, _ => default(CartesianState));
            const double speed = .001;
            var angularVelocity = new Vector3d(speed, 0, 0);
            builder.AddOrientation(carrier, epoch =>
            {
                double seconds = (double)epoch.DifferenceNanoseconds(mission.Start) / JulianInstant.NanosecondsPerSecond;
                var (sin, cos) = Math.SinCos(seconds * speed);
                return new(new(new(1, 0, 0), new(0, cos, sin), new(0, -sin, cos)), angularVelocity);
            });
            builder.AddSampledMission(mission);
            var world = builder.Build();
            foreach (var epoch in new[] { mission.Start, mission.Start.AddSeconds(500) })
            {
                var snapshot = world.Evaluate(epoch);
                Assert.True(snapshot.TryOrientation(carrier, out var frame));
                Assert.True(snapshot.TryState(probe, out var state));
                var expected = frame.ToWorld.Transform(new(0, 0, -.00155));
                Assert.Equal(expected, state.PositionKilometers);
                Assert.Equal(Vector3d.Cross(angularVelocity, expected), state.VelocityKilometersPerSecond);
                Assert.True(snapshot.TryOrientation(probe, out var attitude));
                Assert.Equal(frame, attitude);
            }
            Assert.False(world.Evaluate(mission.End).TryState(probe, out _));
        }
    }


    private sealed class GalileoFactAttribute : FactAttribute
    {
        public GalileoFactAttribute()
        {
            if (new[] { "SPACETANGENT_STANDARD_SPACECRAFT", "SPACETANGENT_GALILEO_REFERENCE", "SPACETANGENT_DE440" }
                .Any(key => string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(key))))
                Skip = "Set the original Galileo catalogue, independent state reference and DE440 paths.";
        }
    }
}
