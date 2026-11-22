// SPDX-License-Identifier: GPL-2.0-or-later
using SpaceTangent.Contracts;
using SpaceTangent.Core.Ephemerides;
using SpaceTangent.Core.World;
using Xunit;

namespace SpaceTangent.Core.Tests;

public sealed class WorldGraphBoundaryTests
{
    private static readonly ObjectId Origin = new("origin"), Target = new("target"), Frame = new("frame");

    [Fact]
    public void AGraphCannotSilentlyReplaceAnExistingSolarSystemOrTrajectory()
    {
        var graph = new WorldGraphBuilder(); graph.AddSolarSystem(new Ephemeris());
        Assert.Throws<InvalidOperationException>(() => graph.AddSolarSystem(new Ephemeris()));
        var other = new WorldGraphBuilder(); other.AddTrajectory(SolarSystemSample.EarthId, _ => default(CartesianState));
        Assert.Throws<InvalidOperationException>(() => other.AddSolarSystem(new Ephemeris()));
        Assert.Throws<InvalidOperationException>(() => other.AddTrajectory(SolarSystemSample.EarthId, _ => default(CartesianState)));
        Assert.Throws<ArgumentException>(() => new WorldGraphBuilder().AddTrajectory(default, _ => default(CartesianState)));
    }

    [Theory]
    [InlineData("same-axis")]
    [InlineData("invalid-axis")]
    [InlineData("invalid-quantity")]
    public void InvalidTwoVectorDefinitionsFailBeforeGraphPublication(string fault)
    {
        var primary = new RelativeDirection(Origin, Target, fault == "invalid-quantity" ? (DirectionQuantity)999 : DirectionQuantity.Position);
        var secondary = new RelativeDirection(Origin, Target, DirectionQuantity.Velocity);
        var graph = new WorldGraphBuilder();
        if (fault == "invalid-axis")
            Assert.Throws<ArgumentOutOfRangeException>(() => graph.AddTwoVectorOrientation(Frame, (FrameAxis)999, primary, FrameAxis.Y, secondary));
        else
            Assert.Throws<ArgumentException>(() => graph.AddTwoVectorOrientation(Frame, FrameAxis.X, primary,
                fault == "same-axis" ? FrameAxis.NegativeX : FrameAxis.Y, secondary));
    }

    [Fact]
    public void CollinearDirectionsLeaveTheAttitudeUnavailableWithoutLosingPhysicalStates()
    {
        var graph = new WorldGraphBuilder(); graph.AddTrajectory(Origin, _ => default(CartesianState));
        graph.AddTrajectory(Target, _ => new(new(1, 0, 0), new(1, 0, 0)));
        graph.AddTwoVectorOrientation(Frame, FrameAxis.X, new(Origin, Target, DirectionQuantity.Position),
            FrameAxis.Y, new(Origin, Target, DirectionQuantity.Velocity));
        var snapshot = graph.Build().Evaluate(new(2451545, 0, TimeScale.Tdb));
        Assert.True(snapshot.TryState(Target, out _)); Assert.False(snapshot.TryOrientation(Frame, out _));
    }

    private sealed class Ephemeris : ISolarSystemEphemeris
    {
        public JulianInstant Start => new(1, 0, TimeScale.Tdb);
        public JulianInstant End => new(3000000, 0, TimeScale.Tdb);
        public SolarSystemSample Sample(JulianInstant epoch) => new(epoch, default, default, default, default);
    }
}
