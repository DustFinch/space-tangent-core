// SPDX-License-Identifier: GPL-2.0-or-later
using System.Globalization;
using System.Security.Cryptography;
using SpaceTangent.Contracts;
using SpaceTangent.Core.Addons;
using SpaceTangent.Core.Spice;
using SpaceTangent.Core.World;
using Xunit;

namespace SpaceTangent.Core.Tests;

public sealed class MeanEquatorTests
{
    private static readonly ObjectId Mars = new("naif/499");
    private static readonly FrameId LocalFrame = new("mars-mean-equator");
    private const string Catalog = "SPACETANGENT_PLANET_SSC", Kernel = "SPACETANGENT_SATURN_PCK";

    [MarsData]
    public void OriginalMarsMoonOrbitsUseOneMovingFrameInTheWorldGraph()
    {
        string source = Data(Catalog);
        using (FileStream input = File.OpenRead(source))
            Assert.Equal("285BAA733BFBF3B961BD8619DE5E62D39B4A61D74FC9FF7F5CB67D342EFB4F64",
                Convert.ToHexString(SHA256.HashData(input)));

        var graph = new WorldGraphBuilder();
        var centerState = new CartesianState(new(1e6, -2e6, 3e6), new(2, -3, 4));
        var phobos = new ObjectId("naif/401");
        foreach (var (id, path) in new[] { (phobos, "Sol/Mars/Mars I"), (new ObjectId("naif/402"), "Sol/Mars/Deimos") })
        {
            var orbit = SscConicOrbits.ReadBody([source], path, reference =>
            {
                Assert.Equal(new(SscConicAxes.ParentDefault, "Sol/Mars", null, null), reference);
                return new(LocalFrame, 1, JulianInstant.SecondsPerDay);
            });
            graph.AddTrajectoryInFrame(id, epoch => orbit.Orbit.Evaluate(epoch), LocalFrame, Mars);
        }
        var model = new SpicePolynomialBodyFrame(Data(Kernel), 499);
        graph.AddMeanEquator(LocalFrame, model);
        graph.AddOrientation(Mars, epoch => model.Sample(epoch));
        graph.AddTrajectory(Mars, _ => centerState);
        graph.AddTwoVectorOrientation(phobos, FrameAxis.Z, new(phobos, Mars, DirectionQuantity.Position),
            FrameAxis.Y, new(phobos, Mars, DirectionQuantity.Velocity));
        WorldGraph world = graph.Build();

        string fixture = Path.Combine(AppContext.BaseDirectory, "Fixtures", "mean-equator-reference.csv");
        foreach (string line in File.ReadLines(fixture).Where(line => !line.StartsWith('#')))
        {
            string[] fields = line.Split(',');
            double[] values = fields.Skip(1).Select(value => double.Parse(value, CultureInfo.InvariantCulture)).ToArray();
            ObjectId id = fields[0] == "Phobos" ? phobos : new("naif/402");
            var snapshot = world.Evaluate(JulianInstant.FromTdbSeconds(values[0]));
            Assert.True(snapshot.TryState(id, out var absolute));
            Assert.True(snapshot.TryStateCenter(id, out var origin));
            Assert.Equal(Mars, origin);
            AssertClose(absolute.PositionKilometers - centerState.PositionKilometers, ReadVector(1), 4e-8);
            AssertClose(absolute.VelocityKilometersPerSecond - centerState.VelocityKilometersPerSecond, ReadVector(4), 4e-11);
            Assert.True(snapshot.TryFrame(LocalFrame, out var frame));
            Assert.True(snapshot.TryOrientation(Mars, out var body));
            Vector3d spin = Assert.IsType<Vector3d>(frame.AngularVelocityRadiansPerSecond);
            Vector3d[] axes = [frame.ToWorld.X, frame.ToWorld.Y, frame.ToWorld.Z];
            for (int axis = 0; axis < 3; axis++)
            {
                AssertClose(axes[axis], ReadVector(7 + axis * 6), 2e-13);
                AssertClose(Vector3d.Cross(spin, axes[axis]), ReadVector(10 + axis * 6), 2e-18);
            }
            AssertClose(frame.ToWorld.Z, body.ToWorld.Z, 2e-15);
            if (id == phobos)
            {
                Assert.True(snapshot.TryOrientation(id, out var attitude));
                Vector3d direction = centerState.PositionKilometers - absolute.PositionKilometers;
                AssertClose(attitude.ToWorld.Z, direction / direction.Length, 2e-15);
            }
            Vector3d ReadVector(int index) => new(values[index], values[index + 1], values[index + 2]);
        }
    }

    [MarsData]
    public void ExplicitFreezeKeepsThePoleObjectSeparateFromTheOrigin()
    {
        string patch = Path.GetTempFileName();
        try
        {
            File.WriteAllText(patch, """
                Modify "Mars I" "Sol/Mars" {
                    OrbitFrame { MeanEquator { Center "Sol/Mars/Anchor" Object "Sol/Mars" Freeze 2460000 } }
                }
                """);
            var orbit = SscConicOrbits.ReadBody([Data(Catalog), patch], "Sol/Mars/Phobos",
                _ => new(LocalFrame, 1, JulianInstant.SecondsPerDay));
            Assert.Equal(new(SscConicAxes.MeanEquator, "Sol/Mars/Anchor", "Sol/Mars", orbit.ElementEpoch), orbit.Reference);

            var model = new SpicePolynomialBodyFrame(Data(Kernel), 499);
            var graph = new WorldGraphBuilder();
            var anchor = new ObjectId("test/anchor");
            var frameId = new FrameId(anchor.Value);
            var probe = new ObjectId("test/probe");
            graph.AddTrajectory(anchor, _ => new CartesianState(new(100, 200, 300), new(1, 2, 3)));
            graph.AddOrientation(anchor, _ => new(Basis3d.Identity, default(Vector3d)));
            graph.AddMeanEquator(frameId, model, orbit.Reference.Freeze);
            graph.AddTrajectoryInFrame(probe, _ => new CartesianState(new(0, 0, 1000), default), frameId, anchor);
            var snapshot = graph.Build().Evaluate(orbit.ElementEpoch.AddSeconds(86400 * 36525d));
            Assert.True(snapshot.TryFrame(frameId, out var frozen));
            Assert.True(snapshot.TryOrientation(anchor, out var anchorAttitude));
            Assert.Equal(Basis3d.Identity, anchorAttitude.ToWorld);
            Assert.Equal(model.SampleMeanEquator(orbit.ElementEpoch).ToWorld, frozen.ToWorld);
            Assert.Equal(default(Vector3d), Assert.IsType<Vector3d>(frozen.AngularVelocityRadiansPerSecond));
            Assert.True(snapshot.TryStateCenter(probe, out var origin));
            Assert.Equal(anchor, origin);
            Assert.True(snapshot.TryState(probe, out var state));
            Assert.Equal(new(1, 2, 3), state.VelocityKilometersPerSecond);
        }
        finally
        {
            File.Delete(patch);
        }
    }

    private static void AssertClose(Vector3d actual, Vector3d expected, double tolerance) =>
        Assert.InRange((actual - expected).Length, 0, tolerance);

    private static string Data(string variable) => Environment.GetEnvironmentVariable(variable)!;

    private sealed class MarsDataAttribute : FactAttribute
    {
        public MarsDataAttribute()
        {
            if (string.IsNullOrWhiteSpace(Data(Catalog)) || string.IsNullOrWhiteSpace(Data(Kernel)))
                Skip = "Set the original solar-system catalogue and the text PCK.";
        }
    }
}
