// SPDX-License-Identifier: GPL-2.0-or-later
using SpaceTangent.Contracts;
using SpaceTangent.Core.Addons;
using SpaceTangent.Core.World;
using Xunit;

namespace SpaceTangent.Core.Tests;

public sealed class SscSurfaceTimelineContractTests : IDisposable
{
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("st-surface-timeline-");
    private static readonly ObjectId Parent = new("parent"), Probe = new("probe");
    private SscBodyDefinition Read(string timeline)
    {
        File.WriteAllText(Path.Combine(_root.FullName, "witness.ssc"),
            $"\"Probe\" \"Parent\" {{ Radius 1 Beginning 2451545 Ending 2451548 Timeline {timeline} }}");
        return Assert.Single(SscBodyCatalogue.Load(_root.FullName));
    }

    [Theory]
    [InlineData("Planetographic", 0, 0, 102, 0, 0)]
    [InlineData("Planetocentric", 90, 0, 0, 102, 0)]
    [InlineData("Planetographic", 0, 90, 0, 0, 102)]
    [InlineData("Planetocentric", -90, -90, 0, 0, -102)]
    public void SurfacePositionsBindPhysicalRadiusAndCarryParentRotation(string coordinates, double longitude, double latitude,
        double x, double y, double z)
    {
        string phase = FormattableString.Invariant($"[ {{ FixedPosition {{ {coordinates} [ {longitude} {latitude} 2 ] }} }} ]");
        var body = Read(phase);
        var track = body.BindTimeline!(Probe, _ => Parent, _ => 100);
        var graph = new WorldGraphBuilder(); graph.AddTrajectory(Parent, _ => default(CartesianState));
        graph.AddOrientation(Parent, _ => new(Basis3d.Identity, new Vector3d(0, 0, .01)));
        graph.AddSampledMission(track);
        var snapshot = graph.Build().Evaluate(new(2451545, 0, TimeScale.Tdb));
        Assert.True(snapshot.TryState(Probe, out var state));
        Assert.InRange((state.PositionKilometers - new Vector3d(x, y, z)).Length, 0, 1e-12);
        Assert.InRange((state.VelocityKilometersPerSecond - new Vector3d(-y * .01, x * .01, 0)).Length, 0, 1e-12);
        Assert.IsType<MissionFrame.BodyFixed>(Assert.Single(track.Arcs).TrajectoryFrame);
    }

    [Theory]
    [InlineData(0, 0, 0)]
    [InlineData(-1, 0, 0)]
    [InlineData(100, 0, -101)]
    [InlineData(100, 91, 0)]
    public void ImpossibleSurfaceCoordinatesOrRadiiAreRejectedWhenBinding(double radius, double latitude, double height)
    {
        var body = Read(FormattableString.Invariant($"[ {{ FixedPosition {{ Planetographic [0 {latitude} {height}] }} }} ]"));
        Assert.Throws<InvalidDataException>(() => body.BindTimeline!(Probe, _ => Parent, _ => radius));
    }

    [Fact]
    public void SurfaceBindingRequiresAnExplicitPhysicalRadiusProvider()
    {
        var body = Read("[ { FixedPosition { Planetocentric [0 0 0] } } ]");
        Assert.Throws<InvalidDataException>(() => body.BindTimeline!(Probe, _ => Parent));
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("[1]")]
    [InlineData("[{ Ending 2451546 FixedPosition [1 0 0] } { Beginning 2451546 FixedPosition [2 0 0] }]")]
    [InlineData("[{ Ending 2451544 FixedPosition [1 0 0] }]")]
    [InlineData("[{ FixedPosition [1 0 0] } { FixedPosition [2 0 0] }]")]
    [InlineData("[{ FixedPosition [1 0 0] OrbitFrame { EquatorJ2000 {} BodyFixed {} } }]")]
    public void MalformedTimelinePhasesFailWithNoDefinitionPublished(string phases)
    {
        Assert.Throws<InvalidDataException>(() => Read(phases));
    }

    [Fact]
    public void MoreThan128TimelinePhasesAreRejected()
    {
        Assert.Throws<InvalidDataException>(() => Read("[" + string.Concat(Enumerable.Repeat("{}", 129)) + "]"));
    }

    public void Dispose() => _root.Delete(recursive: true);
}
