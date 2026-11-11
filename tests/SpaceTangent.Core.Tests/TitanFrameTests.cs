// SPDX-License-Identifier: GPL-2.0-or-later
using System.Globalization;
using System.Security.Cryptography;
using SpaceTangent.Contracts;
using SpaceTangent.Core.Ephemerides;
using SpaceTangent.Core.Spice;
using SpaceTangent.Core.World;
using Xunit;

namespace SpaceTangent.Core.Tests;

public sealed class TitanFrameTests
{
    [TitanDataFact]
    public void OriginalKernelsMatchCspiceStatesOrientationsAndFixedPointVelocity()
    {
        string titanPath = Data("SPACETANGENT_SAT083");
        string saturnPath = Data("SPACETANGENT_SAT164");
        string pck = Data("SPACETANGENT_SATURN_PCK");
        CheckHash(titanPath, "0F1FF4A342B322A24CF77B085680F9AA7BAAC9B6491DAE883C508F3E4CFCBC74");
        CheckHash(saturnPath, "57B0E5F5454BD22120FAC715F28F4C5D4E892EFD490BCDCA3F52D6C694A48681");
        CheckHash(pck, "3DFF7B1DBECEAA01F25467767D3FA25816051C85D162D1EDF04ACB310EE28BB1");
        var titanFrame = new SpicePolynomialBodyFrame(pck, 606);
        Assert.Equal(new Vector3d(2575.15, 2574.78, 2574.47), titanFrame.RadiiKilometers);
        var titanKernel = new SpkKernel(titanPath);
        var saturnKernel = new SpkKernel(saturnPath);
        var builder = new WorldGraphBuilder();
        builder.AddSolarSystem(De440Ephemeris.Load(Data("SPACETANGENT_DE440")));
        ObjectId titan = builder.AddSpkBody(titanKernel, 606, 6, titanFrame);
        ObjectId saturn = builder.AddSpkBody(saturnKernel, 699, 6, new(pck, 699));
        var fixedPoint = new ObjectId("test/titan-point");
        Vector3d local = new(1700, 500, -800);
        builder.AddTrajectory(fixedPoint, _ => new CartesianState(local, default), titan, titan);
        WorldGraph graph = builder.Build();

        string path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "titan-frames-reference.csv");
        foreach (string line in File.ReadLines(path).Where(line => !line.StartsWith('#')))
        {
            double[] row = line.Split(',').Select(value => double.Parse(value, CultureInfo.InvariantCulture)).ToArray();
            bool isTitan = row[0] == 606;
            ObjectId id = isTitan ? titan : saturn;
            var epoch = JulianInstant.FromTdbSeconds(row[1]);
            WorldSnapshot snapshot = graph.Evaluate(epoch);
            Assert.True(snapshot.TryState(id, out var actual));
            Assert.True(snapshot.TryState(SolarSystemSample.SaturnBarycenterId, out var origin));
            Assert.True(snapshot.TryStateCenter(id, out var center));
            Assert.Equal(SolarSystemSample.SaturnBarycenterId, center);
            CartesianState relative = actual - origin;
            Assert.InRange((relative.PositionKilometers - Vector(2)).Length, 0, 1e-6);
            Assert.InRange((relative.VelocityKilometersPerSecond - Vector(5)).Length, 0, 1e-10);
            Assert.True(snapshot.TryOrientation(id, out var frame));
            Vector3d spin = Assert.IsType<Vector3d>(frame.AngularVelocityRadiansPerSecond);
            Vector3d[] axes = [frame.ToWorld.X, frame.ToWorld.Y, frame.ToWorld.Z];
            for (int i = 0; i < axes.Length; i++)
            {
                // The wrapped phase and CSPICE's large-argument evaluation can round differently.
                Assert.InRange((axes[i] - Vector(8 + 3 * i)).Length, 0, 2e-11);
                Assert.InRange((Vector3d.Cross(spin, axes[i]) - Vector(17 + 3 * i)).Length, 0, 2e-15);
            }
            if (isTitan)
            {
                Assert.True(snapshot.TryState(fixedPoint, out var point));
                Vector3d expectedVelocity = Vector(17) * local.X + Vector(20) * local.Y + Vector(23) * local.Z;
                Assert.InRange(((point - actual).VelocityKilometersPerSecond - expectedVelocity).Length, 0, 2e-11);
            }
            Vector3d Vector(int i) => new(row[i], row[i + 1], row[i + 2]);
        }
        Assert.False(graph.Evaluate(JulianInstant.FromTdbSeconds(126273600).AddNanoseconds(-1)).TryState(titan, out _));
    }

    private static string Data(string variable) => Environment.GetEnvironmentVariable(variable)!;

    private static void CheckHash(string path, string expected) =>
        Assert.Equal(expected, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))));

    private sealed class TitanDataFactAttribute : FactAttribute
    {
        public TitanDataFactAttribute()
        {
            foreach (string variable in new[] { "SPACETANGENT_SAT083", "SPACETANGENT_SAT164", "SPACETANGENT_SATURN_PCK", "SPACETANGENT_DE440" })
            {
                if (string.IsNullOrWhiteSpace(Data(variable)))
                {
                    Skip = "Set " + variable + " to the published input kernel.";
                    break;
                }
            }
        }
    }
}
