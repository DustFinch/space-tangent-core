// SPDX-License-Identifier: GPL-2.0-or-later
using SpaceTangent.Contracts;
using SpaceTangent.Core.Addons;
using Xunit;

namespace SpaceTangent.Core.Tests;

public sealed class ParticleContractTests
{
    private const double Start = JulianInstant.J2000Day;
    private static double At(double seconds) => Start + seconds / 86400;
    private static MissionParticleEmitter Emitter => new()
    {
        StartJulianDate = Start, EndJulianDate = At(100), SpawnRate = 2, LifetimeSeconds = 10,
        StartSize = 2, EndSize = 4
    };

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void NonfiniteEpochDoesNotAppendParticles(double epoch)
    {
        var output = new List<MissionParticle>();
        Assert.Equal(0, Emitter.Sample(epoch, 10, output));
        Assert.Empty(output);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void NonpositiveSpawnRateDoesNotAppendParticles(double rate)
    {
        var output = new List<MissionParticle>();
        Assert.Equal(0, (Emitter with { SpawnRate = rate }).Sample(At(5), 10, output));
        Assert.Empty(output);
    }

    [Fact]
    public void ExcessLivePopulationIsRejectedBeforeSampling()
    {
        var output = new List<MissionParticle>();
        Assert.Equal(0, (Emitter with { SpawnRate = MissionParticleEmitter.MaximumLiveParticles + 1,
            LifetimeSeconds = 1 }).Sample(At(5), 10, output));
        Assert.Empty(output);
    }

    [Fact]
    public void ZeroLimitPreservesExistingOutput()
    {
        var sentinel = new MissionParticle(new(1, 2, 3), default, 7, new(1, 0, 0), .5);
        var output = new List<MissionParticle> { sentinel };
        Assert.Equal(0, Emitter.Sample(At(5), 0, output));
        Assert.Equal(sentinel, Assert.Single(output));
    }

    [Fact]
    public void MissingOutputIsAnExplicitArgumentError()
    {
        Assert.Throws<ArgumentNullException>(() => Emitter.Sample(At(5), 1, null!));
    }

    [Fact]
    public void EpochOutsideEmissionAndLifetimeProducesNoParticles()
    {
        var output = new List<MissionParticle>();
        Assert.Equal(0, Emitter.Sample(At(-1), 100, output));
        Assert.Equal(0, Emitter.Sample(At(111), 100, output));
        Assert.Empty(output);
    }

    [Fact]
    public void PointParticlesFollowTheConstantAccelerationSolution()
    {
        var emitter = Emitter with { Center = new(3, 0, 0), Velocity = new(0, 2, 0), Force = new(0, 0, 4) };
        var output = new List<MissionParticle>();
        // The source emitter starts births after its beginning, not at the beginning itself.
        Assert.Equal(10, emitter.Sample(At(5.25), 100, output));
        Assert.Equal(10, output.Count);
        foreach (var particle in output)
        {
            double age = particle.Velocity.Z / 4;
            Assert.Equal(3, particle.Position.X);
            Assert.InRange(Math.Abs(particle.Position.Y - 2 * age), 0, 1e-5);
            Assert.InRange(Math.Abs(particle.Position.Z - 2 * age * age), 0, 2e-5);
            Assert.InRange(Math.Abs(particle.HalfSizeKilometers - (2 + .2 * age)), 0, 1e-5);
        }
    }

    [Fact]
    public void SamplingAfterAReversedEpochIsDeterministic()
    {
        var emitter = Emitter with { Generator = MissionParticleGenerator.Box, Sides = new(2, 4, 6), VelocityVariation = 1 };
        var first = new List<MissionParticle>();
        var repeat = new List<MissionParticle>();
        emitter.Sample(At(5.25), 100, first);
        emitter.Sample(At(90), 100, repeat);
        repeat.Clear();
        emitter.Sample(At(5.25), 100, repeat);
        Assert.Equal(first, repeat);
    }

    [Fact]
    public void ThinningPreservesPopulationAndSelectsOriginalParticles()
    {
        var all = new List<MissionParticle>();
        var thinned = new List<MissionParticle>();
        long count = Emitter.Sample(At(5.25), 100, all);
        Assert.Equal(count, Emitter.Sample(At(5.25), 3, thinned));
        Assert.InRange(thinned.Count, 1, 3);
        Assert.All(thinned, particle => Assert.Contains(particle, all));
    }

    [Fact]
    public void AStationaryBoxKeepsEveryBirthInsideItsPhysicalBounds()
    {
        var emitter = Emitter with { Generator = MissionParticleGenerator.Box, Center = new(10, 20, 30), Sides = new(2, 4, 6) };
        var output = new List<MissionParticle>();
        emitter.Sample(At(5.25), 100, output);
        Assert.NotEmpty(output);
        Assert.All(output, particle =>
        {
            Assert.InRange(particle.Position.X, 9, 11);
            Assert.InRange(particle.Position.Y, 18, 22);
            Assert.InRange(particle.Position.Z, 27, 33);
        });
    }

    [Fact]
    public void AStationaryDiscDoesNotExceedItsRadius()
    {
        var output = new List<MissionParticle>();
        (Emitter with { Generator = MissionParticleGenerator.Disc, DiscRadiusKilometers = 7 }).Sample(At(5.25), 100, output);
        Assert.NotEmpty(output);
        Assert.All(output, particle => Assert.InRange(particle.Position.Length, 0, 7.000001));
    }

    [Fact]
    public void AStripInterpolatesOnlyBetweenItsBirthEndpoints()
    {
        var emitter = Emitter with { Generator = MissionParticleGenerator.Strip,
            Strip = [new(new(10, 2, 3), default), new(new(20, 2, 3), default)] };
        var output = new List<MissionParticle>();
        emitter.Sample(At(5.25), 100, output);
        Assert.NotEmpty(output);
        Assert.All(output, particle =>
        {
            Assert.InRange(particle.Position.X, 10, 20);
            Assert.Equal(2, particle.Position.Y, 5);
            Assert.Equal(3, particle.Position.Z, 5);
            Assert.Equal(default, particle.Velocity);
        });
    }

    [Fact]
    public void IncompleteStripDoesNotPublishAnInventedEmitter()
    {
        var output = new List<MissionParticle>();
        Assert.Equal(0, (Emitter with { Generator = MissionParticleGenerator.Strip,
            Strip = [new(new(1, 2, 3), default)] }).Sample(At(5), 10, output));
        Assert.Empty(output);
    }

    [Fact]
    public void IncompleteArcStripDoesNotPublishAnInventedEmitter()
    {
        var output = new List<MissionParticle>();
        Assert.Equal(0, (Emitter with { Generator = MissionParticleGenerator.ArcStrip,
            Arcs = [new(new(1, 0, 0), 1)] }).Sample(At(5), 10, output));
        Assert.Empty(output);
    }

    [Fact]
    public void EmptyColorsUseOpaqueWhite()
    {
        var output = new List<MissionParticle>();
        (Emitter with { Colors = [] }).Sample(At(5.25), 100, output);
        Assert.NotEmpty(output);
        Assert.All(output, particle => Assert.Equal((new Vector3d(1, 1, 1), 1d), (particle.Color, particle.Opacity)));
    }

    [Fact]
    public void ColorAndOpacityInterpolateAlongTheSameLifetime()
    {
        var output = new List<MissionParticle>();
        (Emitter with { Colors = [new(new(1, 0, 0), 1), new(new(0, 0, 1), 0)] }).Sample(At(5.25), 100, output);
        Assert.NotEmpty(output);
        Assert.All(output, particle =>
        {
            Assert.InRange(Math.Abs(particle.Color.X + particle.Color.Z - 1), 0, 1e-6);
            Assert.Equal(0, particle.Color.Y);
            Assert.Equal(particle.Color.X, particle.Opacity, 6);
        });
    }

    [Fact]
    public void BoundsIncludeCenterBoxVelocityVariationAndAcceleration()
    {
        var emitter = Emitter with { Generator = MissionParticleGenerator.Box, Center = new(3, 4, 0),
            Sides = new(0, 0, 6), Velocity = new(0, 2, 0), VelocityVariation = 1, Force = new(0, 0, 4) };
        Assert.Equal(238, emitter.BoundingRadiusKilometers);
    }
}
