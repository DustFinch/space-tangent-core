// SPDX-License-Identifier: GPL-2.0-or-later
using System.Text.Json;
using SpaceTangent.Contracts;
using SpaceTangent.Core.Addons;
using SpaceTangent.Core.Ephemerides;
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
