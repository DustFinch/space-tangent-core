// SPDX-License-Identifier: GPL-2.0-or-later
using SpaceTangent.Contracts;

namespace SpaceTangent.Core.Addons;

public enum MissionParticleGenerator { Point, Box, Strip, Disc, ArcStrip }
public readonly record struct MissionParticleBirth(Vector3d Position, Vector3d Velocity);
public readonly record struct MissionParticleArc(Vector3d Position, double SpeedKilometersPerSecond);
public readonly record struct MissionParticleColor(Vector3d Color, double Opacity);
public readonly record struct MissionParticle(Vector3d Position, Vector3d Velocity, double HalfSizeKilometers,
    Vector3d Color, double Opacity);

/// <summary>Deterministic, newest-first particles in the source emitter's body axes.</summary>
public sealed record MissionParticleEmitter
{
    public MissionParticleGenerator Generator { get; init; }
    public Vector3d Center { get; init; }
    public Vector3d Sides { get; init; }
    public Vector3d Velocity { get; init; }
    public Vector3d Force { get; init; }
    public double DiscRadiusKilometers { get; init; }
    public IReadOnlyList<MissionParticleBirth> Strip { get; init; } = [];
    public IReadOnlyList<MissionParticleArc> Arcs { get; init; } = [];
    public IReadOnlyList<MissionParticleColor> Colors { get; init; } = [new(new(1, 1, 1), 1)];
    public double SpawnRate { get; init; } = 1;
    public double LifetimeSeconds { get; init; } = 1;
    public double StartSize { get; init; }
    public double EndSize { get; init; } = 1;
    public double TraceSeconds { get; init; }
    public double VelocityVariation { get; init; }
    public double StartJulianDate { get; init; } = JulianInstant.J2000Day;
    public double EndJulianDate { get; init; } = JulianInstant.J2000Day;
    public string? TexturePath { get; init; }

    public const int MaximumLiveParticles = 200_000;

    public double BoundingRadiusKilometers
    {
        get
        {
            double origin = Center.Length, speed = Velocity.Length;
            switch (Generator)
            {
                case MissionParticleGenerator.Box:
                    origin += Sides.Length / 2;
                    break;
                case MissionParticleGenerator.Disc:
                    origin = DiscRadiusKilometers;
                    break;
                case MissionParticleGenerator.Strip:
                    origin = 0;
                    speed = 0;
                    foreach (MissionParticleBirth point in Strip)
                    {
                        origin = Math.Max(origin, point.Position.Length);
                        speed = Math.Max(speed, point.Velocity.Length);
                    }
                    break;
                case MissionParticleGenerator.ArcStrip:
                    origin = 0;
                    speed = 0;
                    foreach (MissionParticleArc point in Arcs)
                    {
                        origin = Math.Max(origin, point.Position.Length);
                        speed = Math.Max(speed, Math.Abs(point.SpeedKilometersPerSecond));
                    }
                    break;
            }
            return origin + LifetimeSeconds * (speed + VelocityVariation + .5 * LifetimeSeconds * Force.Length);
        }
    }

    /// <summary>Appends at most <paramref name="limit"/> samples and returns the unthinned count.</summary>
    public long Sample(double tdbJulianDate, int limit, List<MissionParticle> output)
    {
        ArgumentNullException.ThrowIfNull(output);
        if (!double.IsFinite(tdbJulianDate) || !(SpawnRate > 0) || !(LifetimeSeconds > 0) ||
            SpawnRate * LifetimeSeconds > MaximumLiveParticles || limit <= 0 ||
            Generator == MissionParticleGenerator.Strip && Strip.Count < 2 ||
            Generator == MissionParticleGenerator.ArcStrip && Arcs.Count < 2)
            return 0;
        const double j2000 = JulianInstant.J2000Day;
        const double secondsPerDay = JulianInstant.SecondsPerDay;
        double now = (tdbJulianDate - j2000) * secondsPerDay;
        double start = (StartJulianDate - j2000) * secondsPerDay, end = (EndJulianDate - j2000) * secondsPerDay;
        if (now < start || now > end + LifetimeSeconds)
            return 0;
        double elapsed = now - start, interval = 1 / SpawnRate;
        double location = elapsed * SpawnRate % 2147483648.0;
        if (!double.IsFinite(interval) || !double.IsFinite(location))
            return 0;
        int birth = (int)Math.Floor(location);
        double age = (location - birth) * interval, maximumAge = Math.Min(LifetimeSeconds, elapsed);
        if (now > end)
        {
            int skipped = (int)((now - end) * SpawnRate);
            birth = unchecked(birth - skipped);
            age += skipped * interval;
        }
        long count = BirthCount(age, interval, maximumAge);
        if (count == 0)
            return 0;
        long stride = Math.Max(1, (count + limit - 1) / limit);
        float inverseLife = (float)(1 / LifetimeSeconds);
        var force = new FloatVector(Force);
        for (; age < maximumAge && limit > 0; birth = unchecked(birth - 1), age += interval)
        {
            long skipped = unchecked((uint)birth) % stride;
            if (skipped > 0)
            {
                birth = unchecked(birth - (int)skipped);
                age += skipped * interval;
                if (age >= maximumAge)
                    break;
            }
            var (initialPosition, initialVelocity) = Generate(birth);
            float seconds = (float)age, progress = seconds * inverseLife;
            FloatVector velocity = initialVelocity + force * seconds;
            FloatVector position = initialPosition + (initialVelocity + force * (seconds * .5f)) * seconds;
            float size = progress * (float)EndSize + (1f - progress) * (float)StartSize;
            var (color, opacity) = InterpolateColor(progress);
            output.Add(new(position.ToVector(), velocity.ToVector(), size, color, opacity));
            limit--;
        }
        return count;
    }

    private (FloatVector Position, FloatVector Velocity) Generate(int birth)
    {
        var random = new SourceRandom(unchecked((ulong)(long)birth * 1103515245UL) ^ 0xaaaaaaaaaaaaaaaaUL);
        FloatVector position = new(Center), velocity = new(Velocity);
        switch (Generator)
        {
            case MissionParticleGenerator.Box:
                FloatVector cube = random.Cube();
                position += new FloatVector(cube.X * (float)Sides.X, cube.Y * (float)Sides.Y, cube.Z * (float)Sides.Z) * .5f;
                break;
            case MissionParticleGenerator.Disc:
                float x, y;
                do
                {
                    y = random.Signed();
                    x = random.Signed();
                } while (x * x + y * y > 1f);
                position = new(x * (float)DiscRadiusKilometers, y * (float)DiscRadiusKilometers, 0);
                break;
            case MissionParticleGenerator.Strip:
                int segment = (int)(random.Next() % (uint)(Strip.Count - 1));
                float along = random.Unsigned();
                position = new FloatVector(Strip[segment].Position) * (1f - along) + new FloatVector(Strip[segment + 1].Position) * along;
                velocity = new FloatVector(Strip[segment].Velocity) * (1f - along) + new FloatVector(Strip[segment + 1].Velocity) * along;
                break;
            case MissionParticleGenerator.ArcStrip:
                int arc = (int)(random.Next() % (uint)(Arcs.Count - 1));
                float fraction = random.Unsigned();
                var a = new FloatVector(Arcs[arc].Position);
                var b = new FloatVector(Arcs[arc + 1].Position);
                var direction = (a.Unit * (1f - fraction) + b.Unit * fraction).Unit;
                position = direction * ((1f - fraction) * a.Norm + fraction * b.Norm);
                velocity = direction * ((1f - fraction) * (float)Arcs[arc].SpeedKilometersPerSecond +
                    fraction * (float)Arcs[arc + 1].SpeedKilometersPerSecond);
                break;
        }
        if (VelocityVariation > 0)
        {
            FloatVector variation;
            do { variation = random.Cube(); } while (variation.X * variation.X + variation.Y * variation.Y + variation.Z * variation.Z > 1f);
            velocity += variation * (float)VelocityVariation;
        }
        return (position, velocity);
    }

    private (Vector3d Color, double Opacity) InterpolateColor(float progress)
    {
        if (Colors.Count == 0)
            return (new(1, 1, 1), 1);
        if (Colors.Count == 1)
            return (new FloatVector(Colors[0].Color).ToVector(), (float)Colors[0].Opacity);
        float scaled = progress * (Colors.Count - 1.00001f);
        int index = Math.Clamp((int)scaled, 0, Colors.Count - 2);
        float t = Math.Clamp(scaled - index, 0, 1);
        var a = Colors[index];
        var b = Colors[index + 1];
        return ((new FloatVector(a.Color) * (1f - t) + new FloatVector(b.Color) * t).ToVector(),
            (1f - t) * (float)a.Opacity + t * (float)b.Opacity);
    }

    // Repeated binary64 addition can give one more birth at a boundary than a rounded quotient.
    // Jump along equal rounded increments inside each exponent interval.
    private static long BirthCount(double age, double interval, double end)
    {
        long count = 0;
        while (age < end)
        {
            double next = age + interval;
            if (!(next > age))
                return count;
            ulong bits = BitConverter.DoubleToUInt64Bits(age), nextBits = BitConverter.DoubleToUInt64Bits(next);
            ulong exponent = bits >> 52, delta = nextBits - bits;
            ulong after = BitConverter.DoubleToUInt64Bits(next + interval);
            if (nextBits >> 52 == exponent && after >> 52 == exponent && after - nextBits == delta)
            {
                ulong last = Math.Min(((exponent + 1) << 52) - 1, BitConverter.DoubleToUInt64Bits(end) - 1);
                ulong jumps = (last - bits) / delta;
                if (jumps > 0)
                {
                    age = BitConverter.UInt64BitsToDouble(bits + jumps * delta);
                    count += (long)jumps;
                    continue;
                }
            }
            count++;
            age = next;
        }
        return count;
    }

    private readonly record struct FloatVector(float X, float Y, float Z)
    {
        public FloatVector(Vector3d source) : this((float)source.X, (float)source.Y, (float)source.Z) { }
        public static FloatVector operator +(FloatVector a, FloatVector b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
        public static FloatVector operator *(FloatVector a, float factor) => new(a.X * factor, a.Y * factor, a.Z * factor);
        public float Norm => MathF.Sqrt(X * X + Y * Y + Z * Z);
        public FloatVector Unit
        {
            get
            {
                float length = Norm;
                return length > 0 ? new(X / length, Y / length, Z / length) : default;
            }
        }
        public Vector3d ToVector() => new(X, Y, Z);
    }

    private struct SourceRandom(ulong seed)
    {
        private ulong _seed = seed;
        public uint Next()
        {
            _seed = unchecked(_seed * 25214903917UL + 11);
            return (uint)((_seed & 0x0000ffffffff0000UL) >> 16);
        }
        public float Unsigned() => BitConverter.Int32BitsToSingle((int)((Next() & 0x007fffff) | 0x3f800000)) - 1f;
        public float Signed() => BitConverter.Int32BitsToSingle((int)((Next() & 0x007fffff) | 0x40000000)) - 3f;
        public FloatVector Cube()
        {
            // Match the right-to-left draws of the pinned source oracle.
            float z = Signed(), y = Signed(), x = Signed();
            return new(x, y, z);
        }
    }
}
