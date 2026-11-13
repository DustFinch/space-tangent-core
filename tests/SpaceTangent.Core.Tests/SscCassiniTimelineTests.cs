// SPDX-License-Identifier: GPL-2.0-or-later
using SpaceTangent.Contracts;
using SpaceTangent.Core.Addons;
using SpaceTangent.Core.Ephemerides;
using SpaceTangent.Core.Spice;
using SpaceTangent.Core.Time;
using SpaceTangent.Core.World;
using Xunit;

namespace SpaceTangent.Core.Tests;

public sealed class SscCassiniTimelineTests
{
    [CassiniFact]
    [Trait("Category", "ExternalData")]
    public void OriginalPackedHuygensMovesFromCarrierToFlightToFixedTitanSite()
    {
        string source = Data("SPACETANGENT_GENERIC_CASSINI_SSC");
        string reference = Data("SPACETANGENT_SSC_WORLD_ROOT");
        string original = Path.Combine(reference, "ssc", "cassini.ssc");
        var legacyCarrier = SscSampledCatalog.Load(original, reference, "Sol/Saturn/Cassini");
        var titanFrame = new SpicePolynomialBodyFrame(Data("SPACETANGENT_SATURN_PCK"), 606);
        var legacyProbe = SscHuygensTrack.Load(original, reference, legacyCarrier, titanFrame);
        var de440 = De440Ephemeris.Load(Data("SPACETANGENT_DE440"));
        var saturnKernel = new SpkKernel(Data("SPACETANGENT_SAT083"));

        var baseline = Builder(out var saturn, out var titan);
        baseline.AddSampledSpacecraft(legacyCarrier);
        baseline.AddHuygens(legacyProbe);
        WorldGraph expected = baseline.Build();
        var observed = Builder(out _, out _);
        var definitions = SscBodyCatalogue.Load(source);
        var carrier = Assert.Single(definitions, body => body.Name == "Cassini");
        var probe = Assert.Single(definitions, body => body.Name == "Huygens");
        var craft = carrier.BindTimeline!(legacyCarrier.Id, Resolve);
        Assert.Throws<InvalidDataException>(() => probe.BindTimeline!(legacyProbe.Id, Resolve));
        var lander = probe.BindTimeline!(legacyProbe.Id, Resolve,
            body => body == titan ? titanFrame.RadiiKilometers.X : throw new ArgumentException(body.Value));
        Assert.Equal(15, craft.Arcs.Count);
        Assert.Equal(new[] { legacyCarrier.Id, titan, titan }, lander.Arcs.Select(arc => arc.Center));
        Assert.IsType<MissionFrame.BodyFixed>(lander.Arcs[2].TrajectoryFrame);
        observed.AddSampledMission(craft);
        observed.AddSampledMission(lander);
        WorldGraph actual = observed.Build();

        // Compare well inside each phase because the specialized loader interprets quoted dates as TDB,
        // while the general catalogue converts civil dates to TDB.
        var attached = actual.Evaluate(lander.Start.AddSeconds(86400));
        Assert.True(attached.TryStateCenter(legacyProbe.Id, out var mountedOn));
        Assert.Equal(legacyCarrier.Id, mountedOn);
        Assert.True(attached.TryState(legacyProbe.Id, out var mounted));
        Assert.True(attached.TryState(legacyCarrier.Id, out var carrierState));
        Assert.True(attached.TryOrientation(legacyCarrier.Id, out var carrierAttitude));
        Assert.InRange(((mounted - carrierState).PositionKilometers -
            carrierAttitude.ToWorld.Transform(new(-.0014, 0, -.0002))).Length, 0, 2e-8);
        Compare(legacyProbe.Separation.AddSeconds(86400), titan, .000002, 2e-8);
        Compare(legacyProbe.Landing.AddSeconds(86400), titan, .000002, 2e-8);
        var boundary = lander.Arcs[2].Start;
        var landed = actual.Evaluate(boundary);
        Assert.True(landed.TryStateCenter(legacyProbe.Id, out var origin));
        Assert.Equal(titan, origin);
        Assert.True(landed.TryState(legacyProbe.Id, out var state));
        Assert.True(landed.TryState(titan, out var parent));
        Assert.InRange(Math.Abs((state - parent).PositionKilometers.Length - (titanFrame.RadiiKilometers.X + .91926)), 0, 1e-6);
        Assert.True(actual.Evaluate(boundary.AddNanoseconds(-1)).TryState(legacyProbe.Id, out _));

        void Compare(JulianInstant epoch, ObjectId origin, double positionTolerance, double velocityTolerance)
        {
            var earlier = expected.Evaluate(epoch);
            var current = actual.Evaluate(epoch);
            Assert.True(earlier.TryState(legacyProbe.Id, out var previous));
            Assert.True(current.TryState(legacyProbe.Id, out var next));
            Assert.True(current.TryStateCenter(legacyProbe.Id, out var center));
            Assert.Equal(origin, center);
            Assert.InRange((previous.PositionKilometers - next.PositionKilometers).Length, 0, positionTolerance);
            Assert.InRange((previous.VelocityKilometersPerSecond - next.VelocityKilometersPerSecond).Length, 0, velocityTolerance);
            Assert.True(current.TryOrientation(legacyProbe.Id, out var rotation));
            Assert.True(earlier.TryOrientation(legacyProbe.Id, out var referenceRotation));
            Assert.InRange((rotation.ToWorld.X - referenceRotation.ToWorld.X).Length, 0, 1e-4);
            Assert.InRange((rotation.ToWorld.Z - referenceRotation.ToWorld.Z).Length, 0, 1e-4);
        }

        WorldGraphBuilder Builder(out ObjectId saturn, out ObjectId titan)
        {
            var builder = new WorldGraphBuilder();
            builder.AddSolarSystem(de440);
            saturn = builder.AddSpkBody(saturnKernel, 699, 6);
            titan = builder.AddSpkBody(saturnKernel, 606, 6, titanFrame);
            return builder;
        }

        ObjectId Resolve(string name) => name switch
        {
            "Sol" => SolarSystemSample.SunId,
            "Sol/Earth" => SolarSystemSample.EarthId,
            "Sol/Saturn" => saturn,
            "Sol/Saturn/Titan" => titan,
            "Sol/Saturn/Cassini" => legacyCarrier.Id,
            "Sol/Saturn/Titan/Huygens" => legacyProbe.Id,
            _ => throw new ArgumentException(name)
        };
    }

    private static string Data(string name) => Environment.GetEnvironmentVariable(name)!;

    private sealed class CassiniFactAttribute : FactAttribute
    {
        public CassiniFactAttribute()
        {
            if (new[] { "SPACETANGENT_GENERIC_CASSINI_SSC", "SPACETANGENT_SSC_WORLD_ROOT",
                "SPACETANGENT_SATURN_PCK", "SPACETANGENT_DE440", "SPACETANGENT_SAT083" }
                .Any(name => string.IsNullOrWhiteSpace(Data(name))))
                Skip = "Set the original Cassini catalogue and ephemeris paths.";
        }
    }
}
