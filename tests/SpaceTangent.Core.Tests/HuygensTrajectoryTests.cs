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
    public void OriginalSscSwitchesCarrierFlightAndLandedStates()
    {
        string root = Data("SPACETANGENT_SSC_WORLD_ROOT");
        string catalog = Path.Combine(root, "ssc", "cassini.ssc");
        SscSampledSpacecraft carrier = SscSampledCatalog.Load(catalog, root, "Sol/Saturn/Cassini");
        var titanFrame = new SpicePolynomialBodyFrame(Data("SPACETANGENT_SATURN_PCK"), 606);
        SscHuygensTrack probe = SscHuygensTrack.Load(catalog, root, carrier, titanFrame);
        Assert.EndsWith("huygens.3ds", probe.MeshPath);
        var builder = new WorldGraphBuilder();
        builder.AddSolarSystem(De440Ephemeris.Load(Data("SPACETANGENT_DE440")));
        builder.AddSampledSpacecraft(carrier);
        ObjectId titan = builder.AddSpkBody(new(Data("SPACETANGENT_SAT083")), 606, 6, titanFrame);
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

        Assert.False(graph.Evaluate(probe.Start.AddNanoseconds(-1)).TryState(probe.Id, out _));
        RelativeAt(probe.Start, carrier.Id);
        RelativeAt(probe.Separation.AddNanoseconds(-1), carrier.Id);
        RelativeAt(probe.Separation, titan);
        CartesianState lastFlight = RelativeAt(probe.Landing.AddNanoseconds(-1), titan);
        CartesianState firstLanded = RelativeAt(probe.Landing, titan);
        Assert.InRange(Math.Abs(firstLanded.PositionKilometers.Length - (2575.15 + .91926)), 0, 1e-6);
        Assert.True((lastFlight.VelocityKilometersPerSecond - firstLanded.VelocityKilometersPerSecond).Length > .001);
        Assert.False(graph.Evaluate(JulianInstant.FromTdbSeconds(410875200).AddNanoseconds(1)).TryState(probe.Id, out _));

        CartesianState RelativeAt(JulianInstant epoch, ObjectId origin)
        {
            WorldSnapshot snapshot = graph.Evaluate(epoch);
            Assert.True(snapshot.TryStateCenter(probe.Id, out ObjectId actualCenter));
            Assert.Equal(origin, actualCenter);
            Assert.True(snapshot.TryState(probe.Id, out var absolute));
            Assert.True(snapshot.TryState(origin, out var parent));
            return absolute - parent;
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
