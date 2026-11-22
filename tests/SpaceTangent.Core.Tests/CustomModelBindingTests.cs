// SPDX-License-Identifier: GPL-2.0-or-later
using SpaceTangent.Contracts;
using SpaceTangent.Core.Addons;
using SpaceTangent.Core.Ephemerides;
using SpaceTangent.Core.World;
using Xunit;

namespace SpaceTangent.Core.Tests;

public sealed class CustomModelBindingTests
{
    private static readonly JulianInstant Epoch = JulianInstant.FromJulianDate(2451545, TimeScale.Tdb);
    private sealed class Ephemeris : ISolarSystemEphemeris
    {
        public JulianInstant Start => Epoch;
        public JulianInstant End => Epoch.AddSeconds(100);
        public int Calls;
        private static CartesianState State(int value) => new(new(value, value * 2, value * 3), new(-value, value / 2d, 0));
        public SolarSystemSample Sample(JulianInstant epoch)
        {
            Calls++;
            return new(epoch, State(10), State(3), State(30), State(6))
            { Mercury = State(1), Venus = State(2), EarthMoonBarycenter = State(9), MarsBarycenter = State(4),
                JupiterBarycenter = State(5), UranusBarycenter = State(7), NeptuneBarycenter = State(8) };
        }
    }

    [Theory]
    [InlineData("mercury", -9)]
    [InlineData("VENUS", -8)]
    [InlineData("earth-sun", -7)]
    [InlineData("jpl-mars-sun", -6)]
    [InlineData("jupiter-jpl", -5)]
    [InlineData("saturn", -4)]
    [InlineData("uranus-sun", -3)]
    [InlineData("neptune-jpl", -2)]
    [InlineData("emb", -1)]
    [InlineData("sun", 10)]
    [InlineData("jpl-earth-ssb", 3)]
    [InlineData("JPL-SUN-SSB", 10)]
    public void SupportedAliasesRetainNativeCenterAndVelocity(string alias, int x)
    {
        var ephemeris = new Ephemeris();
        var models = SscCustomModels.FromSolarSystem(ephemeris, _ => null);
        var orbit = Assert.IsType<Func<JulianInstant, CartesianState?>>(models.Orbit(alias));
        foreach (var instant in new[] { ephemeris.Start, ephemeris.Start.AddSeconds(50), ephemeris.End })
        {
            var state = Assert.IsType<CartesianState>(orbit(instant));
            Assert.Equal(new Vector3d(x, 2 * x, 3 * x), state.PositionKilometers);
            Assert.Equal(new Vector3d(-x, x / 2d, 0), state.VelocityKilometersPerSecond);
        }
        Assert.Equal(3, ephemeris.Calls);
        Assert.Null(orbit(ephemeris.Start.AddSeconds(-.001)));
        Assert.Null(orbit(ephemeris.End.AddSeconds(.001)));
        Assert.Equal(3, ephemeris.Calls);
    }

    [Theory]
    [InlineData("iau-mercury", 199)]
    [InlineData("IAU-VENUS", 299)]
    [InlineData("iau-earth", 399)]
    [InlineData("iau-mars", 499)]
    [InlineData("iau-jupiter", 599)]
    [InlineData("iau-saturn", 699)]
    [InlineData("iau-uranus", 799)]
    [InlineData("iau-neptune", 899)]
    public void RotationFactoryReceivesThePhysicalNaifIdAndOriginalEpoch(string alias, int naif)
    {
        var frame = new FrameKinematics(new(new(0, 1, 0), new(-1, 0, 0), new(0, 0, 1)), new(0, 0, .1));
        JulianInstant? received = null;
        var models = SscCustomModels.FromSolarSystem(new Ephemeris(), id =>
        {
            Assert.Equal(naif, id);
            return instant => { received = instant; return frame; };
        });
        Assert.Equal(frame, models.Rotation(alias)!(Epoch));
        Assert.Equal(Epoch, received);
    }

    [Theory]
    [InlineData("pluto")]
    [InlineData("unknown")]
    [InlineData("vsop87-earth")]
    [InlineData("vsop87-unknown")]
    [InlineData("jpl-moon-ssb")]
    public void UnsupportedModelsAreNotSubstitutedWithAnotherBody(string name)
    {
        var ephemeris = new Ephemeris();
        var models = SscCustomModels.FromSolarSystem(ephemeris, _ => throw new InvalidOperationException());
        Assert.Null(models.Orbit(name));
        Assert.Null(models.Rotation(name));
        Assert.Null(models.Rotation("iau-unknown"));
        Assert.Equal(0, ephemeris.Calls);
    }

    [Fact]
    public void BindingRequiresBothHostServices()
    {
        Assert.Throws<ArgumentNullException>(() => SscCustomModels.FromSolarSystem(null!, _ => null));
        Assert.Throws<ArgumentNullException>(() => SscCustomModels.FromSolarSystem(new Ephemeris(), null!));
        Assert.Null(SscCustomModels.FromSolarSystem(new Ephemeris(), _ => null).Rotation("iau-earth"));
    }
}
