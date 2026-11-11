// SPDX-License-Identifier: GPL-2.0-or-later
using System.Globalization;
using SpaceTangent.Contracts;
using SpaceTangent.Core.Addons;
using SpaceTangent.Core.Ephemerides;
using SpaceTangent.Core.Spice;
using SpaceTangent.Core.World;
using Xunit;

namespace SpaceTangent.Core.Tests;

public sealed class HuygensTrajectoryTests
{
    [HuygensFact]
    public void OriginalSscSwitchesCarrierFlightAndLandedStatesAndAttitudes()
    {
        string root = Data("SPACETANGENT_SSC_WORLD_ROOT");
        string catalog = Path.Combine(root, "ssc", "cassini.ssc");
        SscSampledSpacecraft carrier = SscSampledCatalog.Load(catalog, root, "Sol/Saturn/Cassini");
        var titanFrame = new SpicePolynomialBodyFrame(Data("SPACETANGENT_SATURN_PCK"), 606);
        SscHuygensTrack probe = SscHuygensTrack.Load(catalog, root, carrier, titanFrame);
        Assert.EndsWith("huygens.3ds", probe.MeshPath);
        AssertBasis(probe.MeshToBody, new(new(0, 0, 1), new(-1, 0, 0), new(0, -1, 0)));
        var builder = new WorldGraphBuilder();
        builder.AddSolarSystem(De440Ephemeris.Load(Data("SPACETANGENT_DE440")));
        builder.AddSampledSpacecraft(carrier);
        var saturnSystem = new SpkKernel(Data("SPACETANGENT_SAT083"));
        ObjectId saturn = builder.AddSpkBody(saturnSystem, 699, 6);
        ObjectId titan = builder.AddSpkBody(saturnSystem, 606, 6, titanFrame);
        builder.AddHuygens(probe);
        WorldGraph graph = builder.Build();

        string path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "huygens-trajectory-reference.csv");
        foreach (string line in File.ReadLines(path).Where(line => !line.StartsWith('#')))
        {
            double[] row = line.Split(',').Select(value => double.Parse(value, CultureInfo.InvariantCulture)).ToArray();
            JulianInstant epoch = JulianInstant.FromJulianDate(row[0], TimeScale.Tdb);
            ObjectId center = row[1] == 1 ? carrier.Id : titan;
            CartesianState state = RelativeAt(epoch, center);
            Assert.InRange((state.PositionKilometers - new Vector3d(row[2], row[3], row[4])).Length, 0, 2e-6);
            Assert.InRange((state.VelocityKilometersPerSecond - new Vector3d(row[5], row[6], row[7])).Length, 0, 2e-8);
        }

        var beforeLaunch = graph.Evaluate(probe.Start.AddNanoseconds(-1));
        Assert.False(beforeLaunch.TryState(probe.Id, out _));
        Assert.False(beforeLaunch.TryOrientation(probe.Id, out _));
        RelativeAt(probe.Start, carrier.Id);
        RelativeAt(probe.Separation.AddNanoseconds(-1), carrier.Id);
        RelativeAt(probe.Separation, titan);
        CartesianState lastFlight = RelativeAt(probe.Landing.AddNanoseconds(-1), titan);
        CartesianState firstLanded = RelativeAt(probe.Landing, titan);
        Assert.InRange(Math.Abs(firstLanded.PositionKilometers.Length - (2575.15 + .91926)), 0, 1e-6);
        Assert.True((lastFlight.VelocityKilometersPerSecond - firstLanded.VelocityKilometersPerSecond).Length > .001);
        var afterCoverage = graph.Evaluate(JulianInstant.FromTdbSeconds(410875200).AddNanoseconds(1));
        Assert.False(afterCoverage.TryState(probe.Id, out _));
        Assert.False(afterCoverage.TryOrientation(probe.Id, out _));

        foreach (JulianInstant epoch in new[] { probe.Start, probe.Separation.AddNanoseconds(-1) })
        {
            WorldSnapshot mounted = graph.Evaluate(epoch);
            Assert.True(mounted.TryOrientation(carrier.Id, out var carrierAttitude));
            Assert.True(mounted.TryOrientation(probe.Id, out var probeAttitude));
            Basis3d frame = carrierAttitude.ToWorld;
            AssertBasis(probeAttitude.ToWorld, new(frame.Y * -1, frame.Z, frame.X * -1));
        }

        const decimal Period = .0022222m * 3600;
        foreach (JulianInstant epoch in new[] { probe.Separation, probe.Separation.AddSeconds((double)(Period / 4)),
                     probe.Landing.AddNanoseconds(-1), probe.Landing, probe.Landing.AddSeconds(3600) })
        {
            bool isFlight = epoch.CompareTo(probe.Landing) < 0;
            WorldSnapshot snapshot = graph.Evaluate(epoch);
            Assert.True(snapshot.TryOrientation(probe.Id, out var attitude));
            Assert.True(snapshot.TryState(probe.Id, out var observer));
            Assert.True(snapshot.TryState(isFlight ? saturn : titan, out var target));
            CartesianState guide = target - observer;
            Vector3d z = Unit(guide.PositionKilometers);
            Vector3d x = Unit(Vector3d.Cross(guide.VelocityKilometersPerSecond, z));
            Vector3d y = Vector3d.Cross(z, x);
            decimal elapsedSeconds = (decimal)epoch.DifferenceNanoseconds(JulianInstant.FromTdbSeconds(0)) /
                JulianInstant.NanosecondsPerSecond;
            double angle = Math.PI + (isFlight ? (double)(elapsedSeconds % Period / Period) * Math.Tau : 0);
            double c = Math.Cos(angle), s = Math.Sin(angle);
            AssertBasis(attitude.ToWorld, new(x * c + y * s, y * c - x * s, z));
        }

        CartesianState RelativeAt(JulianInstant epoch, ObjectId origin)
        {
            WorldSnapshot snapshot = graph.Evaluate(epoch);
            Assert.True(snapshot.TryStateCenter(probe.Id, out ObjectId actualCenter));
            Assert.Equal(origin, actualCenter);
            Assert.True(snapshot.TryState(probe.Id, out var absolute));
            Assert.True(snapshot.TryState(origin, out var parent));
            return absolute - parent;
        }

        static Vector3d Unit(Vector3d vector) => vector / vector.Length;

        static void AssertBasis(Basis3d actual, Basis3d expected)
        {
            Assert.InRange((actual.X - expected.X).Length, 0, 1e-7);
            Assert.InRange((actual.Y - expected.Y).Length, 0, 1e-7);
            Assert.InRange((actual.Z - expected.Z).Length, 0, 1e-7);
        }
    }

    private static string Data(string name) => Environment.GetEnvironmentVariable(name)!;

    private sealed class HuygensFactAttribute : FactAttribute
    {
        public HuygensFactAttribute()
        {
            foreach (string variable in new[] { "SPACETANGENT_SSC_WORLD_ROOT", "SPACETANGENT_SAT083",
                         "SPACETANGENT_SATURN_PCK", "SPACETANGENT_DE440" })
            {
                if (string.IsNullOrWhiteSpace(Data(variable)))
                {
                    Skip = "Set " + variable + " to the original Huygens inputs.";
                    break;
                }
            }
        }
    }
}
