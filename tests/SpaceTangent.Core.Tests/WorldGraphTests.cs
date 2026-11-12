// SPDX-License-Identifier: GPL-2.0-or-later
using SpaceTangent.Contracts;
using SpaceTangent.Core.Addons;
using SpaceTangent.Core.Ephemerides;
using SpaceTangent.Core.World;
using Xunit;

namespace SpaceTangent.Core.Tests;

public sealed class WorldGraphTests
{
    private static readonly ObjectId Center = new("center");
    private static readonly ObjectId Vehicle = new("vehicle");
    private static readonly ObjectId Frame = new("rotating-frame");
    private static readonly JulianInstant Epoch = new(2_451_545, 0, TimeScale.Tdb);

    [Fact]
    public void RotatingTrajectoryUsesParentOrientationAndVelocityBeforeComposition()
    {
        var graph = new WorldGraphBuilder();
        graph.AddTrajectory(Vehicle, _ => new CartesianState(new(2, 0, 0), new(1, 0, 0)), Center, Frame);
        graph.AddOrientation(Frame, _ => new FrameKinematics(new Basis3d(new(0, 1, 0), new(-1, 0, 0), new(0, 0, 1)),
            new Vector3d(0, 0, 2)));
        graph.AddTrajectory(Center, _ => new CartesianState(new(1, 0, 0), new(1, 1, 0)));

        WorldSnapshot snapshot = graph.Build().Evaluate(Epoch);
        Assert.True(snapshot.TryState(Vehicle, out var state));
        Assert.Equal(new Vector3d(1, 2, 0), state.PositionKilometers);
        Assert.Equal(new Vector3d(-3, 2, 0), state.VelocityKilometersPerSecond);
    }

    [Fact]
    public void TwoVectorAttitudeUsesTheSameEvaluatedStatesWithoutReenteringSources()
    {
        int vehicleSamples = 0;
        var graph = new WorldGraphBuilder();
        graph.AddTwoVectorOrientation(Vehicle, FrameAxis.NegativeX, new(Vehicle, Center, DirectionQuantity.Position),
            FrameAxis.NegativeY, new(Vehicle, Center, DirectionQuantity.Velocity));
        graph.AddTrajectory(Vehicle, _ =>
        {
            vehicleSamples++;
            return new CartesianState(new(2, 0, 0), new(1, 3, 0));
        }, Center);
        graph.AddTrajectory(Center, _ => new CartesianState(new(1, 0, 0), new(0, 1, 0)));

        WorldSnapshot snapshot = graph.Build().Evaluate(Epoch);
        Assert.True(snapshot.TryOrientation(Vehicle, out var frame));
        Assert.Equal(Basis3d.Identity, frame.ToWorld);
        Assert.Null(frame.AngularVelocityRadiansPerSecond);
        Assert.Equal(1, vehicleSamples);
    }

    [Fact]
    public void MissingRatesAndCyclesDoNotFabricateWorldStates()
    {
        var graph = new WorldGraphBuilder();
        graph.AddTrajectory(Vehicle, _ => new CartesianState(new(2, 0, 0), default), Center, Frame);
        graph.AddTrajectory(Center, _ => default(CartesianState));
        graph.AddOrientation(Frame, _ => new FrameKinematics(Basis3d.Identity, null));
        Assert.False(graph.Build().Evaluate(Epoch).TryState(Vehicle, out _));

        var cycle = new WorldGraphBuilder();
        cycle.AddTrajectory(Vehicle, _ => default(CartesianState), Center, Vehicle);
        cycle.AddTrajectory(Center, _ => default(CartesianState));
        cycle.AddTwoVectorOrientation(Vehicle, FrameAxis.X, new(Vehicle, Center, DirectionQuantity.Position),
            FrameAxis.Y, new(Vehicle, Center, DirectionQuantity.Velocity));
        Assert.Contains("cycle", Assert.Throws<InvalidOperationException>(() => cycle.Build()).Message);

        var missing = new WorldGraphBuilder();
        missing.AddTrajectory(Vehicle, _ => default(CartesianState), Center);
        Assert.Contains("center State", Assert.Throws<InvalidOperationException>(() => missing.Build()).Message);
    }

    [RealCassiniFact]
    public void CassiniGraphComposesDe440SaturnBarycenterAndRealSpiceState()
    {
        string data = Environment.GetEnvironmentVariable("SPACETANGENT_DE440")!;
        string root = Environment.GetEnvironmentVariable("SPACETANGENT_CASSINI_SSC_ROOT")!;
        var builder = new WorldGraphBuilder();
        builder.AddSolarSystem(De440Ephemeris.Load(data));
        ObjectId id = builder.AddSpiceSpacecraft(Assert.Single(SscSpiceCatalog.Load(Path.Combine(root, "cassini.ssc"), root)));
        WorldGraph graph = builder.Build();
        var epoch = JulianInstant.FromTdbSeconds(141911269.46157101);
        var expectedPosition = new Vector3d(-383392921.5663619, 1192484289.1635075, 509134284.46257609);
        var expectedVelocity = new Vector3d(.64444705240392608, 13.566704028584786, 1.7730964397870892);
        Parallel.For(0, 8, _ =>
        {
            WorldSnapshot snapshot = graph.Evaluate(epoch);
            Assert.True(snapshot.TryState(id, out var cassini));
            Assert.InRange((cassini.PositionKilometers - expectedPosition).Length, 0, 8e-6);
            Assert.InRange((cassini.VelocityKilometersPerSecond - expectedVelocity).Length, 0, 1e-9);
            Assert.True(snapshot.TryOrientation(id, out var orientation));
            Assert.InRange(Math.Abs(orientation.ToWorld.X.Z - .92616579426881118), 0, 3e-8);
        });
        var remoteEpoch = new JulianInstant(2_488_070, JulianInstant.NanosecondsPerDay / 2, TimeScale.Tdb);
        WorldSnapshot remote = graph.Evaluate(remoteEpoch);
        Assert.True(remote.TryState(SolarSystemSample.SunId, out _));
        Assert.False(remote.TryState(id, out _));
        Assert.False(remote.TryOrientation(id, out _));
    }

    private sealed class RealCassiniFactAttribute : FactAttribute
    {
        public RealCassiniFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SPACETANGENT_DE440")) ||
                string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SPACETANGENT_CASSINI_SSC_ROOT")))
                Skip = "The world composition check requires DE440 and the original Cassini SSC files.";
        }
    }

    [RealScienceFact]
    public void ScienceSatellitesShareTheSameSolarSnapshotAndTwoVectorEvaluation()
    {
        var builder = new WorldGraphBuilder();
        builder.AddSolarSystem(De440Ephemeris.Load(Environment.GetEnvironmentVariable("SPACETANGENT_DE440")!));
        string root = Environment.GetEnvironmentVariable("SPACETANGENT_SCIENCE_ROOT")!;
        var tracks = ScienceSatelliteCatalog.Load(Path.Combine(root, "catalog", "sciencesat.json"), root);
        foreach (ScienceSatellite track in tracks)
            builder.AddScienceSatellite(track);
        WorldGraph world = builder.Build();
        foreach (ScienceSatellite track in tracks)
        {
            WorldSnapshot snapshot = world.Evaluate(track.Epoch.AddSeconds(3600));
            Assert.NotNull(snapshot.SolarSystem);
            Assert.True(snapshot.TryState(track.Id, out var state));
            Assert.True(snapshot.TryState(SolarSystemSample.EarthId, out var earth));
            Assert.True(snapshot.TryOrientation(track.Id, out var frame));
            Assert.True(track.TrySample(snapshot.Epoch, out var expected));
            Assert.InRange((state.PositionKilometers - earth.PositionKilometers -
                expected.EarthRelativeState.PositionKilometers).Length, 0, 3e-8);
            Assert.InRange((frame.ToWorld.X - expected.InstrumentToTrajectory.X).Length, 0, 1e-11);
            Assert.InRange((frame.ToWorld.Y - expected.InstrumentToTrajectory.Y).Length, 0, 1e-11);
        }
    }

    private sealed class RealScienceFactAttribute : FactAttribute
    {
        public RealScienceFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SPACETANGENT_DE440")) ||
                string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SPACETANGENT_SCIENCE_ROOT")))
                Skip = "The world composition check requires DE440 and the original science satellites.";
        }
    }
}
