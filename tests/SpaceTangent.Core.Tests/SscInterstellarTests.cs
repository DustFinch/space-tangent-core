// SPDX-License-Identifier: GPL-2.0-or-later
using SpaceTangent.Contracts;
using SpaceTangent.Core.Addons;
using SpaceTangent.Core.Time;
using Xunit;

namespace SpaceTangent.Core.Tests;

public sealed class SscInterstellarTests
{
    [InterstellarFact]
    [Trait("Category", "ExternalData")]
    public void OriginalInterstellarCatalogueKeepsUnwrappedMeanAnomalyAndExplicitSpins()
    {
        string root = Environment.GetEnvironmentVariable("SPACETANGENT_INTERSTELLAR_SSC")!;
        var bodies = SscBodyCatalogue.Load(root);
        Assert.Equal(3, bodies.Count);
        Check("1I 'Oumuamua", "roughsphere.cms", 2458080.5, 1.4351831770734, -1.27234500742808,
            1.201133796102373, 122.7417062847286, 24.59690955523242, 241.8105360304898, 51.1576197938249);
        Check("2I Borisov", "asteroid.cms", 2458853.5, .7857258713194, -.8514922551937886,
            3.356475782676596, 44.05264247909138, 308.1477292269942, 209.1236864378081, 34.4294703072178);
        Check("3I ATLAS", "asteroid.cms", 2460857.5, .1325467009628, -.2599636435166625,
            6.297880223569178, 175.1184534160558, 322.3053240978721, 127.7776677558638, -893.5472819943519);
        var atlas = Assert.Single(bodies, body => body.Name == "3I ATLAS");
        var time = JulianInstant.FromJulianDate(2460857.5, TimeScale.Tdb);
        var spin = atlas.SampleRotation!;
        var first = spin(time)!.Value;
        var quarter = spin(time.AddSeconds(16.16 * 900))!.Value;
        Assert.InRange(Math.Abs(Vector3d.Dot(first.ToWorld.X, quarter.ToWorld.X)), 0, 1e-10);
        Assert.InRange((quarter.ToWorld.Z - first.ToWorld.Z).Length, 0, 1e-10);

        void Check(string name, string mesh, double day, double years, double au, double eccentricity,
            double inclination, double node, double argument, double anomaly)
        {
            var body = Assert.Single(bodies, body => body.Name == name);
            Assert.Equal(mesh, Path.GetFileName(body.Mesh));
            Assert.NotNull(body.SurfaceMap);
            Assert.NotNull(body.SampleRotation);
            var epoch = JulianInstant.FromJulianDate(day, TimeScale.Tdb);
            const double radians = Math.PI / 180;
            double motion = Math.Tau / (years * 31_557_600);
            double semimajor = -au * 149_597_870.7;
            foreach (double days in new[] { -120d, 0d, 40d, 500d })
            {
                double elapsed = days * 86400;
                double mean = anomaly * radians + elapsed * motion;
                // The inverse of e*sinh(H)-H is monotone; bisect directly as an independent oracle.
                double lo = -10, hi = 10;
                for (int i = 0; i < 80; i++)
                {
                    double mid = (lo + hi) / 2;
                    if (eccentricity * Math.Sinh(mid) - mid < mean)
                        lo = mid;
                    else
                        hi = mid;
                }
                double hyperbolicAnomaly = (lo + hi) / 2;
                double sinh = Math.Sinh(hyperbolicAnomaly), cosh = Math.Cosh(hyperbolicAnomaly);
                double rootFactor = Math.Sqrt(eccentricity * eccentricity - 1);
                double rate = motion / (eccentricity * cosh - 1);
                var position = Frame(semimajor * (eccentricity - cosh), semimajor * rootFactor * sinh);
                var velocity = Frame(-semimajor * sinh * rate, semimajor * rootFactor * cosh * rate);
                var actual = Assert.IsType<CartesianState>(body.Sample(epoch.AddSeconds(elapsed)));
                Assert.InRange((actual.PositionKilometers - position).Length, 0, 1e-3);
                Assert.InRange((actual.VelocityKilometersPerSecond - velocity).Length, 0, 1e-8);
            }

            Vector3d Frame(double x, double y)
            {
                var (sa, ca) = Math.SinCos(argument * radians);
                var (si, ci) = Math.SinCos(inclination * radians);
                var (sn, cn) = Math.SinCos(node * radians);
                double px = ca * x - sa * y, py = sa * x + ca * y;
                var (so, co) = Math.SinCos(23.4392911 * radians);
                var ecliptic = new Vector3d(cn * px - sn * py * ci, sn * px + cn * py * ci, py * si);
                return new(ecliptic.X, ecliptic.Y * co - ecliptic.Z * so, ecliptic.Y * so + ecliptic.Z * co);
            }
        }
    }

    [Fact]
    public void OpenConicCanUsePericenterDistanceAndAnExplicitPeriodUnit()
    {
        string root = Directory.CreateTempSubdirectory().FullName;
        try
        {
            string file = Path.Combine(root, "pass.ssc");
            File.WriteAllText(file, """
                "Pass" "Sol/Earth" {
                    EllipticalOrbit { PericenterDistance 7000 Eccentricity 2 Period <h> 1 }
                }
                """);
            var source = Assert.Single(SscConicOrbits.Read(file, _ => new(new FrameId("equatorial"), 1, 86400)));
            var state = source.Orbit.Evaluate(source.ElementEpoch);
            Assert.Equal(7000, state.PositionKilometers.X, 10);
            Assert.Equal(Math.Tau / 3600 * 7000 * Math.Sqrt(3), state.VelocityKilometersPerSecond.Y, 10);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed class InterstellarFactAttribute : FactAttribute
    {
        public InterstellarFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SPACETANGENT_INTERSTELLAR_SSC")))
                Skip = "Set the original interstellar SSC corpus path.";
        }
    }
}
