// SPDX-License-Identifier: GPL-2.0-or-later
using SpaceTangent.Contracts;

namespace SpaceTangent.Core.World;

/// <summary>
/// A planet made entirely of noise, for testing the terrain, the oceans and the atmosphere: one set of functions gives
/// its heights, its colours and its clouds, so the maps seen from afar and the terrain patches seen close up agree.
///
/// Elevation (km above sea level, negative under the sea), on a unit-sphere direction d:
/// - continents: fractal gradient noise on d warped by more noise, less a sea threshold c; above 0 it is land rising
///   slowly to plateaus, below 0 a continental shelf falling into abyssal plains near SeaFloor;
/// - mountain belts: ridged multifractal noise (1 - |n|)^2 with each octave weighted by the last, where a slower noise
///   marks belts across the land;
/// - roughness below that, down to metres: gradient-noise octaves of wavelength lambda_0 2^-k and amplitude
///   Slope lambda_0 2^-0.8k, three times as rough in the mountains as on the plains.
/// Every octave shorter than the requested wavelength is left out, so a patch only sums what its vertices can show.
///
/// Colour: linear albedo by biome from a temperature (28 C at the equator, -18 at the poles, -6 C per km of height,
/// with noise) and a moisture (noise, dry at 24 degrees of latitude, wet at the equator): desert, savanna, grass,
/// forest, tundra, bare rock on steep or high ground, snow, beaches, shallow and deep water, sea ice. W is 1 on open
/// water (the renderer's specular sea) and 0 elsewhere. Clouds: a coverage of warped noise with an equatorial band,
/// clear subtropics and mid-latitude storm tracks.
///
/// That is the Terran kind (Nova). The same machinery makes other kinds (ProceduralWorldKind):
/// - Desert: no water; the low ground is basins down to 3 km with sand seas of dunes (ridged noise of 1.5 km
///   wavelength, 80 m high), salt flats at the bottom, red rock highlands, small polar caps; no clouds.
/// - Ice: a frozen world of gentle ice sheets, ridged mountains, and lineae (cracks, Europa's reddish double ridges)
///   across fresh and old ice; thin clouds.
/// - Volcanic: dark basalt plains, shield volcanoes up to 9 km with summit calderas, sulfur and ash around them (Io),
///   and lava glowing in channels across the plains and in the calderas (Emission, drawn on the night side).
/// - GasGiant: no surface; latitude bands stretched by turbulence, in warm or icy colours by the seed, with a few
///   storms whose ovals swirl the bands around them, and a ring system (BakeRings).
/// - Cratered: an airless moon. Bright highlands and a few great basins flooded with dark lava (maria), under craters
///   at every scale from 120 km down to 250 m: bowls a fifth as deep as they are wide (shallower when large, with a
///   central peak), raised rims and ejecta falling off as the cube of the distance; the youngest are ringed by bright
///   ejecta. Fewer large craters on the younger maria.
/// </summary>
public enum ProceduralWorldKind : byte { Terran, Desert, Ice, Volcanic, GasGiant, Cratered }

public readonly record struct ProceduralAlbedo(float Red, float Green, float Blue, float Water);

/// <summary>Deterministic height, material, emission and cloud functions for a Y-up body frame.</summary>
public sealed class ProceduralSurface
{
    public const double SeaFloorKilometers = 6, Slope = .02, Hurst = .8;
    /// <summary>The shortest wavelength summed, kilometres.</summary>
    public const double ShortestWavelengthKilometers = .004;
    /// <summary>Continents: above this noise value is land (about 35 % of the surface).</summary>
    public const double SeaThreshold = .06;
    private readonly int _seed;

    public ProceduralSurface(int seed, double radiusKilometers, ProceduralWorldKind kind = ProceduralWorldKind.Terran)
    {
        if (!(radiusKilometers > 0) || !double.IsFinite(radiusKilometers)) throw new ArgumentOutOfRangeException(nameof(radiusKilometers));
        if (!Enum.IsDefined(kind)) throw new ArgumentOutOfRangeException(nameof(kind));
        _seed = seed; RadiusKilometers = radiusKilometers; Kind = kind;
    }
    public int Seed => _seed;
    public double RadiusKilometers { get; }
    public ProceduralWorldKind Kind { get; }
    /// <summary>Only the Terran kind has seas; a gas giant has no solid surface (no terrain).</summary>
    public bool HasWater => Kind == ProceduralWorldKind.Terran;
    public bool HasSurface => Kind != ProceduralWorldKind.GasGiant;
    /// <summary>
    /// A bound on |elevation| (km): plateaus or basins, ridges or shields, hills and the roughness series (Terran: 1.6 km
    /// of plateau, 6.5 of ridges, 0.5 of hills).
    /// </summary>
    public double LargestKilometers => Kind switch
    {
        ProceduralWorldKind.GasGiant => 0,
        ProceduralWorldKind.Volcanic => 9 + 3 + .8 + .3 + 2.1 * RoughnessSeries + .5,
        // Highlands and basins, then two overlapping craters' relief at every level.
        ProceduralWorldKind.Cratered => 1.5 + MareDepthKilometers + .3 + 2 * CraterDepthBound + .5 * RoughnessSeries,
        _ => 1.6 + 6.5 + .5 + 3 * RoughnessSeries + .5,
    } + (Kind == ProceduralWorldKind.Desert ? 3 : 0);
    private static double RoughnessSeries => RoughnessBase * Slope / (1 - Math.Pow(2, -Hurst));
    /// <summary>Wavelength (km) of the first roughness octave.</summary>
    private const double RoughnessBase = 25;

    /// <summary>Fractal gradient noise at frequency f (cycles over the unit sphere), octaves while R / f stays above minimum.</summary>
    private double Fbm(Vector3d p, double frequency, int octaves, int seed, double minimumKilometers, double gain = .5)
    {
        double sum = 0, amplitude = 1;
        for (int octave = 0; octave < octaves; octave++)
        {
            double wavelength = RadiusKilometers / frequency;
            if (wavelength < minimumKilometers) break;
            // The last octave fades in over a factor two, so a finer patch does not jump.
            double fade = Math.Clamp(wavelength / minimumKilometers - 1, 0, 1);
            sum += fade * amplitude * Gradient(p.X * frequency, p.Y * frequency, p.Z * frequency, seed + octave * 7919);
            amplitude *= gain; frequency *= 2;
        }
        return sum;
    }
    /// <summary>Ridged multifractal noise in [0, about 1]: sharp crests where the gradient noise crosses zero.</summary>
    private double Ridged(Vector3d p, double frequency, int octaves, int seed, double minimumKilometers)
    {
        double sum = 0, amplitude = .5, weight = 1;
        for (int octave = 0; octave < octaves; octave++)
        {
            double wavelength = RadiusKilometers / frequency;
            if (wavelength < minimumKilometers) break;
            double fade = Math.Clamp(wavelength / minimumKilometers - 1, 0, 1);
            double n = 1 - Math.Abs(Gradient(p.X * frequency, p.Y * frequency, p.Z * frequency, seed + octave * 7919));
            n *= n * weight;
            weight = Math.Clamp(2 * n, 0, 1);
            sum += fade * n * amplitude;
            amplitude *= .5; frequency *= 2;
        }
        return sum;
    }
    private static double SmoothStep(double edge0, double edge1, double x)
    {
        double t = Math.Clamp((x - edge0) / (edge1 - edge0), 0, 1);
        return t * t * (3 - 2 * t);
    }

    /// <summary>The continent field less the sea threshold (positive on land) and the mountain weight, at d.</summary>
    private (double Coast, double Mountains, Vector3d Warped) Continents(Vector3d d, double minimumKilometers)
    {
        var warp = new Vector3d(Fbm(d, 1.5, 3, _seed + 11, minimumKilometers), Fbm(d, 1.5, 3, _seed + 12, minimumKilometers),
            Fbm(d, 1.5, 3, _seed + 13, minimumKilometers));
        var q = d + warp * .4;
        double coast = Fbm(q, 1.1, 9, _seed + 1, minimumKilometers) - SeaThreshold;
        double belts = SmoothStep(-.05, .2, Fbm(q, .9, 3, _seed + 21, minimumKilometers));
        return (coast, belts * SmoothStep(-.02, .1, coast), q);
    }

    /// <summary>Elevation above sea level (km) at a unit direction, keeping wavelengths of at least minimumKilometers.</summary>
    public double Elevation(Vector3d d, double minimumKilometers)
    {
        minimumKilometers = Math.Max(minimumKilometers, ShortestWavelengthKilometers);
        switch (Kind)
        {
            case ProceduralWorldKind.GasGiant: return 0;
            case ProceduralWorldKind.Desert: return DesertElevation(d, minimumKilometers);
            case ProceduralWorldKind.Ice: return IceElevation(d, minimumKilometers);
            case ProceduralWorldKind.Volcanic: return VolcanicElevation(d, minimumKilometers);
            case ProceduralWorldKind.Cratered: return CrateredElevation(d, minimumKilometers);
        }
        var (c, mountains, q) = Continents(d, minimumKilometers);
        // Land: plains rising to plateaus. Sea: a shelf near the coast, then the abyss, bounded by SeaFloor.
        double e = c >= 0 ? 1.4 * c + 2.5 * c * c : -SeaFloorKilometers * Math.Tanh((-2.5 * c + 45 * c * c) / SeaFloorKilometers);
        e += 6.5 * mountains * Ridged(q, 3.5, 12, _seed + 31, minimumKilometers);
        double land = SmoothStep(-.02, .06, c);
        e += .5 * land * Fbm(d, 14, 5, _seed + 41, minimumKilometers);
        // Roughness down to metres, rougher in the mountains.
        return e + (.35 + 2.65 * mountains) * Roughness(d, minimumKilometers);
    }
    /// <summary>Gradient-noise roughness from RoughnessBase down to minimumKilometers at slope Slope (Hurst exponent Hurst).</summary>
    private double Roughness(Vector3d d, double minimumKilometers)
    {
        double wavelength = RoughnessBase, amplitude = Slope * RoughnessBase, detail = 0;
        for (int octave = 0; octave < 24 && wavelength >= minimumKilometers; octave++)
        {
            double fade = Math.Clamp(wavelength / minimumKilometers - 1, 0, 1), f = RadiusKilometers / wavelength;
            detail += fade * amplitude * Gradient(d.X * f, d.Y * f, d.Z * f, _seed + 101 + octave * 7919);
            wavelength *= .5; amplitude *= Math.Pow(2, -Hurst);
        }
        return detail;
    }
    /// <summary>Basins (where the continent field is negative) down to 3 km, highlands, ridges and dune fields in the lowlands.</summary>
    private double DesertElevation(Vector3d d, double minimumKilometers)
    {
        var (c, mountains, q) = Continents(d, minimumKilometers);
        double e = c >= 0 ? 1.0 * c + 2 * c * c : -3 * Math.Tanh((-2 * c + 20 * c * c) / 3);
        e += 4 * mountains * Ridged(q, 3.5, 12, _seed + 31, minimumKilometers);
        e += .08 * SandSea(c) * Ridged(d, RadiusKilometers / DuneWavelengthKilometers, 4, _seed + 37, minimumKilometers);
        e += .4 * Fbm(d, 14, 5, _seed + 41, minimumKilometers);
        return e + (.5 + 2 * mountains) * Roughness(d, minimumKilometers);
    }
    public const double DuneWavelengthKilometers = 1.5;
    private static double SandSea(double coast) => SmoothStep(.05, -.08, coast);
    /// <summary>Gentle ice sheets, ridged mountains, and lineae: double ridges a hundred metres high along the cracks.</summary>
    private double IceElevation(Vector3d d, double minimumKilometers)
    {
        var (c, mountains, q) = Continents(d, minimumKilometers);
        double e = .8 * Math.Tanh(2 * c) + 2.5 * mountains * Ridged(q, 3.5, 10, _seed + 31, minimumKilometers);
        e += .15 * Math.Pow(Cracks(d, minimumKilometers), 3);
        e += .3 * Fbm(d, 14, 5, _seed + 41, minimumKilometers);
        return e + .4 * Roughness(d, minimumKilometers);
    }
    private double Cracks(Vector3d d, double minimumKilometers) => Ridged(d, 6, 7, _seed + 43, minimumKilometers);
    /// <summary>Low lava plains, shield volcanoes up to 9 km with a caldera at each summit, and a few ridges.</summary>
    private double VolcanicElevation(Vector3d d, double minimumKilometers)
    {
        var (c, mountains, q) = Continents(d, minimumKilometers);
        double e = c >= 0 ? .8 * c : -1.5 * Math.Tanh(-3 * c);
        double shield = Shields(d, minimumKilometers);
        e += 9 * Math.Pow(SmoothStep(.15, .6, shield), 2) - 2 * SmoothStep(.5, .65, shield);
        e += 3 * mountains * Ridged(q, 3.5, 10, _seed + 31, minimumKilometers);
        e += .3 * Fbm(d, 14, 5, _seed + 41, minimumKilometers);
        return e + (.6 + 1.5 * mountains) * Roughness(d, minimumKilometers);
    }
    private double Shields(Vector3d d, double minimumKilometers) => Fbm(d, 3, 4, _seed + 33, minimumKilometers);

    public const double LargestCraterKilometers = 120, SmallestCraterKilometers = .25, MareDepthKilometers = 2.5;
    /// <summary>Depth (km) of a crater of radius r: two fifths of its radius when small, levelling off near 3 km.</summary>
    public static double CraterDepth(double radiusKilometers) => .4 * radiusKilometers / (1 + radiusKilometers / 7.5);
    private static readonly double CraterDepthBound = Enumerable.Range(0, 16)
        .Select(level => LargestCraterKilometers / Math.Pow(2, level)).TakeWhile(r => r >= SmallestCraterKilometers)
        .Sum(r => 1.3 * CraterDepth(r));
    /// <summary>How deep in a mare (0 to 1) d lies: a few great basins placed by the seed.</summary>
    public double Mare(Vector3d d, double minimumKilometers)
    {
        if (Kind != ProceduralWorldKind.Cratered) return 0;
        double mare = 0;
        int basins = 5 + (_seed & 3);
        for (int k = 0; k < basins; k++)
        {
            var centre = FromLongitudeLatitude(Hash(k, 11) * 2 * Math.PI, Math.Asin(2 * Hash(k, 12) - 1) * .8);
            double angle = .18 + .27 * Hash(k, 13);
            // Lava shores wander with noise.
            double edge = Math.Acos(Math.Clamp(Vector3d.Dot(d, centre), -1, 1)) / angle + .25 * Fbm(d, 6, 4, _seed + 47 + k, minimumKilometers);
            mare = Math.Max(mare, SmoothStep(1, .85, edge));
        }
        return mare;
    }
    /// <summary>
    /// Craters at each level from LargestCraterKilometers, halving, down to SmallestCraterKilometers and the resolved
    /// scale: about half of the cells of a lattice three radii wide hold one, jittered within the cell. Returns their
    /// relief (km) and the brightness of young ejecta (0 to about 1).
    /// </summary>
    private (double Height, double Ejecta) Craters(Vector3d d, double minimumKilometers, double mare)
    {
        double height = 0, ejecta = 0, radius = LargestCraterKilometers;
        for (int level = 0; radius >= SmallestCraterKilometers && 2 * radius >= minimumKilometers; level++, radius *= .5)
        {
            // Like an octave, a level fades in over a factor two of the resolved scale.
            double fade = Math.Clamp(2 * radius / minimumKilometers - 1, 0, 1);
            double cells = RadiusKilometers / (3 * radius);
            long cx = (long)Math.Floor(d.X * cells), cy = (long)Math.Floor(d.Y * cells), cz = (long)Math.Floor(d.Z * cells);
            // Maria are younger: fewer of the large craters, the small ones as many.
            double density = .5 * (1 - .8 * mare * SmoothStep(2, 20, radius));
            for (long i = cx - 1; i <= cx + 1; i++)
                for (long j = cy - 1; j <= cy + 1; j++)
                    for (long k = cz - 1; k <= cz + 1; k++)
                    {
                        if (CellHash(i, j, k, level, 0) >= density) continue;
                        var centre = new Vector3d((i + CellHash(i, j, k, level, 1)) / cells, (j + CellHash(i, j, k, level, 2)) / cells,
                            (k + CellHash(i, j, k, level, 3)) / cells);
                        if (centre.Length < .5) continue;
                        double r = radius * (.55 + .45 * CellHash(i, j, k, level, 4));
                        double x = Math.Acos(Math.Clamp(Vector3d.Dot(d, centre / centre.Length), -1, 1)) * RadiusKilometers / r;
                        if (x >= 3) continue;
                        double depth = CraterDepth(r), rim = .25 * depth;
                        double profile = x < 1 ? -depth + (depth + rim) * x * x : rim * Math.Pow(x, -3) * SmoothStep(3, 2.2, x);
                        // Complex craters (wider than 20 km) have a central peak.
                        if (r > 10) profile += .3 * depth * Math.Exp(-x * x / .02);
                        height += fade * profile;
                        double age = CellHash(i, j, k, level, 5);
                        if (age < .12 && x > .7)
                            ejecta = Math.Max(ejecta, fade * (1 - age / .12) * Math.Exp(-1.6 * (x - 1)) * SmoothStep(.7, 1, x));
                    }
        }
        return (height, ejecta);
    }
    /// <summary>Uniform value in [0, 1) of a lattice cell, a crater level and a channel.</summary>
    private double CellHash(long i, long j, long k, int level, int channel)
    {
        ulong h = (ulong)(i * 73856093L ^ j * 19349663L ^ k * 83492791L ^ level * 2654435761L ^ channel * 40503L ^ _seed * 9176L);
        h ^= h >> 33; h *= 0xff51afd7ed558ccdUL; h ^= h >> 33; h *= 0xc4ceb9fe1a85ec53UL; h ^= h >> 33;
        return (h >> 11) * (1.0 / (1UL << 53));
    }
    /// <summary>Highlands and flooded basins under the craters, and roughness below them.</summary>
    private double CrateredElevation(Vector3d d, double minimumKilometers)
    {
        double mare = Mare(d, minimumKilometers);
        double highlands = .8 * Fbm(d, 2, 6, _seed + 31, minimumKilometers) + .3;
        double floor = -MareDepthKilometers + .15 * Fbm(d, 8, 4, _seed + 39, minimumKilometers);
        double e = highlands + (floor - highlands) * mare;
        return e + Craters(d, minimumKilometers, mare).Height + .5 * Roughness(d, minimumKilometers);
    }
    private (double R, double G, double B) CrateredColor(Vector3d d, double minimumKilometers)
    {
        double mare = Mare(d, minimumKilometers);
        var ground = Mix((.20, .19, .18), (.075, .072, .068), mare);
        ground = Mix(ground, Mix((.17, .16, .15), (.06, .062, .065), mare), SmoothStep(-.1, .4, Fbm(d, 10, 5, _seed + 53, minimumKilometers)) * .6);
        double ejecta = Craters(d, minimumKilometers, mare).Ejecta;
        return Mix(ground, (.36, .35, .34), Math.Min(1, ejecta) * .7);
    }

    /// <summary>
    /// Linear albedo (X, Y, Z) and open water (W) at unit direction d with elevation (km) and the cosine of the slope
    /// (the surface normal against the local vertical), keeping wavelengths of at least minimumKilometers.
    /// </summary>
    public ProceduralAlbedo Surface(Vector3d d, double elevation, double upness, double minimumKilometers)
    {
        minimumKilometers = Math.Max(minimumKilometers, ShortestWavelengthKilometers);
        switch (Kind)
        {
            case ProceduralWorldKind.GasGiant: return Opaque(GasGiantColor(d, minimumKilometers));
            case ProceduralWorldKind.Desert: return Opaque(Mottled(DesertColor(d, elevation, upness, minimumKilometers), d, minimumKilometers));
            case ProceduralWorldKind.Ice: return Opaque(Mottled(IceColor(d, elevation, upness, minimumKilometers), d, minimumKilometers));
            case ProceduralWorldKind.Volcanic: return Opaque(Mottled(VolcanicColor(d, elevation, upness, minimumKilometers), d, minimumKilometers));
            case ProceduralWorldKind.Cratered: return Opaque(Mottled(CrateredColor(d, minimumKilometers), d, minimumKilometers));
        }
        double latitude = Math.Abs(Math.Asin(Math.Clamp(d.Y, -1, 1))) * 180 / Math.PI;
        double temperature = 28 - 46 * Math.Pow(Math.Abs(d.Y), 3) - 6 * Math.Max(elevation, 0) + 8 * Fbm(d, 4, 3, _seed + 51, minimumKilometers);
        if (elevation < 0)
        {
            double depth = Math.Sqrt(SmoothStep(0, .3, -elevation));
            var water = Mix((.035, .16, .16), (.003, .016, .045), depth);
            // Sea ice where it is cold enough, broken by noise at its edge.
            double ice = SmoothStep(-5, -11, temperature + 4 * Fbm(d, 30, 3, _seed + 57, minimumKilometers));
            var color = Mix(water, (.62, .70, .78), ice);
            return new((float)color.R, (float)color.G, (float)color.B, (float)(1 - ice));
        }
        double moisture = .62 + .7 * Fbm(d, 2.5, 6, _seed + 61, minimumKilometers) - .5 * Gauss(latitude - 24, 9) + .25 * Gauss(latitude, 12);
        var dry = Mix((.50, .33, .17), (.27, .22, .09), SmoothStep(.12, .32, moisture));
        var wet = Mix((.10, .15, .04), (.025, .06, .022), SmoothStep(.5, .78, moisture));
        var ground = Mix(dry, wet, SmoothStep(.28, .48, moisture));
        ground = Mix((.17, .16, .11), ground, SmoothStep(-6, 5, temperature));
        // Bare rock on cliffs and above the tree line; snow where it is cold and the ground holds it.
        double rock = Math.Clamp(SmoothStep(.9, .74, upness) + .8 * SmoothStep(2.2, 3.6, elevation), 0, 1);
        ground = Mix(ground, (.16, .14, .12), rock);
        // Sand along the coasts, then snow over everything cold enough.
        ground = Mix((.45, .39, .27), ground, SmoothStep(.004, .025, elevation));
        double snow = SmoothStep(-1, -7, temperature + 3 * Fbm(d, 40, 3, _seed + 67, minimumKilometers)) * SmoothStep(.62, .8, upness);
        ground = Mix(ground, (.80, .82, .86), snow);
        // Mottling at every scale the caller resolves.
        double mottle = .86 + .28 * (.5 + .5 * Fbm(d, 120, 14, _seed + 71, minimumKilometers, .7));
        return new((float)(ground.R * mottle), (float)(ground.G * mottle), (float)(ground.B * mottle), 0);
    }
    private static ProceduralAlbedo Opaque((double R, double G, double B) c) => new((float)c.R, (float)c.G, (float)c.B, 0);
    private (double R, double G, double B) Mottled((double R, double G, double B) c, Vector3d d, double minimumKilometers)
    {
        double mottle = .86 + .28 * (.5 + .5 * Fbm(d, 120, 14, _seed + 71, minimumKilometers, .7));
        return (c.R * mottle, c.G * mottle, c.B * mottle);
    }
    private (double R, double G, double B) DesertColor(Vector3d d, double elevation, double upness, double minimumKilometers)
    {
        double latitude = Math.Abs(Math.Asin(Math.Clamp(d.Y, -1, 1))) * 180 / Math.PI;
        var ground = Mix((.36, .20, .11), (.56, .40, .22), SmoothStep(.6, -.6, elevation + .6 * Fbm(d, 6, 4, _seed + 53, minimumKilometers)));
        ground = Mix(ground, (.78, .74, .66), SmoothStep(-2.0, -2.6, elevation) * (.6 + .4 * Fbm(d, 20, 4, _seed + 55, minimumKilometers)));
        ground = Mix(ground, (.18, .12, .09), SmoothStep(2.5, 4, elevation));
        ground = Mix(ground, (.24, .14, .09), SmoothStep(.9, .74, upness));
        return Mix(ground, (.85, .84, .82), SmoothStep(78, 84, latitude + 4 * Fbm(d, 12, 4, _seed + 57, minimumKilometers)));
    }
    private (double R, double G, double B) IceColor(Vector3d d, double elevation, double upness, double minimumKilometers)
    {
        var ice = Mix((.78, .85, .92), (.58, .60, .64), SmoothStep(-.2, .3, Fbm(d, 5, 5, _seed + 59, minimumKilometers)));
        ice = Mix(ice, (.42, .30, .22), SmoothStep(.55, .85, Cracks(d, minimumKilometers)));
        return Mix(ice, (.20, .19, .18), SmoothStep(.85, .7, upness) * SmoothStep(.8, 1.6, elevation));
    }
    private (double R, double G, double B) VolcanicColor(Vector3d d, double elevation, double upness, double minimumKilometers)
    {
        double shield = Shields(d, minimumKilometers);
        var ground = Mix((.045, .040, .035), (.20, .19, .18), SmoothStep(.3, 2.5, elevation));
        ground = Mix(ground, (.32, .12, .05), SmoothStep(.1, .4, Fbm(d, 9, 4, _seed + 63, minimumKilometers)) * .7);
        ground = Mix(ground, (.62, .52, .14), SmoothStep(.35, .55, shield) * SmoothStep(-.1, .3, Fbm(d, 25, 4, _seed + 65, minimumKilometers)));
        return Mix(ground, (.03, .028, .025), SmoothStep(.9, .74, upness));
    }
    /// <summary>
    /// Glowing lava (linear RGB, about 1 for the brightest) of a Volcanic planet: thin channels across the low plains
    /// and lakes in the summit calderas; zero for the other kinds.
    /// </summary>
    public Vector3d Emission(Vector3d d, double minimumKilometers)
    {
        if (Kind != ProceduralWorldKind.Volcanic) return default;
        minimumKilometers = Math.Max(minimumKilometers, ShortestWavelengthKilometers);
        var (c, _, _) = Continents(d, minimumKilometers);
        double channel = 1 - SmoothStep(0, .035, Math.Abs(Fbm(d, 20, 5, _seed + 69, minimumKilometers)));
        double glow = .8 * channel * SmoothStep(.05, -.1, c) + SmoothStep(.58, .66, Shields(d, minimumKilometers));
        return new((float)glow, (float)(.32 * glow), (float)(.05 * glow));
    }
    /// <summary>A gas giant's bands, turbulence and storms (swirled bands inside their ovals).</summary>
    private (double R, double G, double B) GasGiantColor(Vector3d d, double minimumKilometers)
    {
        // Each storm turns the bands around its centre by an angle that falls off over its radius.
        int storms = 3 + (_seed & 3);
        var stormColor = (R: 0.0, G: 0.0, B: 0.0); double stormWeight = 0;
        for (int k = 0; k < storms; k++)
        {
            double lat = (Hash(k, 1) - .5) * 1.2, lon = Hash(k, 2) * 2 * Math.PI, radius = .04 + .1 * Hash(k, 3) * Hash(k, 3);
            var centre = FromLongitudeLatitude(lon, lat);
            double distance = Math.Acos(Math.Clamp(Vector3d.Dot(d, centre), -1, 1));
            if (distance > 3 * radius) continue;
            // Ovals: stretched east-west by two.
            double weight = Math.Exp(-distance * distance / (radius * radius));
            d = Rotate(d, centre, 2.5 * weight * (Hash(k, 4) < .7 ? 1 : -1));
            var tint = Hash(k, 5) < .5 ? (.62, .30, .18) : (.92, .90, .86);
            stormColor = (stormColor.R + tint.Item1 * weight, stormColor.G + tint.Item2 * weight, stormColor.B + tint.Item3 * weight);
            stormWeight += weight;
        }
        double latitude = Math.Asin(Math.Clamp(d.Y, -1, 1));
        // Turbulence stretched along the bands: noise on a point squeezed toward the equatorial plane.
        var squeezed = new Vector3d(d.X, d.Y * 4, d.Z);
        double turbulence = Fbm(squeezed, 3, 7, _seed + 85, minimumKilometers, .55);
        double bands = 11 + 6 * Hash(9, 6);
        double band = .5 + .5 * Math.Sin(latitude * bands + 1.8 * turbulence + 6 * Hash(9, 7));
        double belt = .5 + .5 * Math.Sin(latitude * bands * .37 + 2.5 * Hash(9, 8));
        bool icy = (_seed & 1) == 1 && (_seed & 2) == 0;
        var light = icy ? (.62, .74, .86) : (.86, .79, .64);
        var dark = icy ? (.34, .48, .70) : (.56, .38, .24);
        var mid = icy ? (.50, .62, .80) : (.74, .58, .40);
        var color = Mix(Mix(dark, mid, belt), light, band);
        double polar = SmoothStep(.9, 1.35, Math.Abs(latitude));
        color = Mix(color, Mix(dark, (.3, .3, .32), .5), polar * .6);
        if (stormWeight > 0)
            color = Mix(color, (stormColor.R / stormWeight, stormColor.G / stormWeight, stormColor.B / stormWeight), Math.Min(1, stormWeight) * .8);
        return color;
    }
    /// <summary>Uniform pseudo-random value in [0, 1) of the seed, an index and a channel.</summary>
    private double Hash(int index, int channel)
    {
        ulong h = (ulong)(_seed * 2654435761L ^ index * 40503L ^ channel * 9176L + 12345);
        h ^= h >> 33; h *= 0xff51afd7ed558ccdUL; h ^= h >> 33; h *= 0xc4ceb9fe1a85ec53UL; h ^= h >> 33;
        return (h >> 11) * (1.0 / (1UL << 53));
    }
    /// <summary>v turned by angle radians about the unit axis (Rodrigues).</summary>
    private static Vector3d Rotate(Vector3d v, Vector3d axis, double angle)
    {
        double cos = Math.Cos(angle), sin = Math.Sin(angle);
        return v * cos + Vector3d.Cross(axis, v) * sin + axis * (Vector3d.Dot(axis, v) * (1 - cos));
    }
    private static double Gauss(double x, double width) => Math.Exp(-x * x / (2 * width * width));
    private static (double R, double G, double B) Mix((double R, double G, double B) a, (double R, double G, double B) b, double t) =>
        (a.R + (b.R - a.R) * t, a.G + (b.G - a.G) * t, a.B + (b.B - a.B) * t);

    /// <summary>Cloud cover in [0, 1] at a unit direction (thin on an Ice world, none on the others but Terran).</summary>
    public double Clouds(Vector3d d, double minimumKilometers)
    {
        if (Kind == ProceduralWorldKind.Ice) return .6 * SmoothStep(.25, .6, Fbm(d, 3, 8, _seed + 91, minimumKilometers, .55));
        if (Kind != ProceduralWorldKind.Terran) return 0;
        var warp = new Vector3d(Fbm(d, 2, 3, _seed + 81, minimumKilometers), Fbm(d, 2, 3, _seed + 82, minimumKilometers),
            Fbm(d, 2, 3, _seed + 83, minimumKilometers));
        double latitude = Math.Abs(Math.Asin(Math.Clamp(d.Y, -1, 1))) * 180 / Math.PI;
        double bands = .16 * Gauss(latitude, 7) - .14 * Gauss(latitude - 24, 8) + .1 * Gauss(latitude - 55, 12);
        double n = Fbm(d + warp * .5, 3, 9, _seed + 91, minimumKilometers, .55);
        return SmoothStep(.1, .45, n + bands);
    }

    /// <summary>Unit direction at the centre of texel (x, y) of a width x height equirectangular map (row 0 north, column 0 at -180).</summary>
    public static Vector3d TexelDirection(int x, int y, int width, int height) =>
        FromLongitudeLatitude(((x + .5) / width - .5) * 2 * Math.PI, (.5 - (y + .5) / height) * Math.PI);

    /// <summary>
    /// Bakes the equirectangular maps the planet is drawn with from afar: sRGB colour with open water in alpha
    /// (RGBA8), and a normal map (RGB8: east, south, up in the native planet map convention) with the relief exaggerated by
    /// normalRelief. Rows run on at most maximumThreads threads.
    /// </summary>
    public (byte[] Color, byte[] Normal) BakeSurface(int width, int height, double normalRelief = 3, int maximumThreads = 4)
    {
        if (width < 8 || height < 4 || width > 8192 || height > 4096 || maximumThreads < 1) throw new ArgumentOutOfRangeException(nameof(width));
        double spacing = 2 * Math.PI * RadiusKilometers / width, minimum = 2 * spacing;
        var elevation = new double[width * height];
        var options = new ParallelOptions { MaxDegreeOfParallelism = maximumThreads };
        Parallel.For(0, height, options, y =>
        {
            for (int x = 0; x < width; x++) elevation[y * width + x] = Elevation(TexelDirection(x, y, width, height), minimum);
        });
        var color = new byte[4 * width * height]; var normal = new byte[3 * width * height];
        Parallel.For(0, height, options, y =>
        {
            double latitude = (.5 - (y + .5) / height) * Math.PI;
            double east = Math.Max(Math.Cos(latitude), 1e-3) * spacing, south = Math.PI * RadiusKilometers / height;
            for (int x = 0; x < width; x++)
            {
                double Sea(int column, int row)
                {
                    column = (column % width + width) % width; row = Math.Clamp(row, 0, height - 1);
                    return HasWater ? Math.Max(elevation[row * width + column], 0) : elevation[row * width + column];
                }
                double dEast = (Sea(x + 1, y) - Sea(x - 1, y)) / (2 * east), dSouth = (Sea(x, y + 1) - Sea(x, y - 1)) / (2 * south);
                double nx = -normalRelief * dEast, ny = -normalRelief * dSouth, length = Math.Sqrt(nx * nx + ny * ny + 1);
                double upness = 1 / Math.Sqrt(dEast * dEast + dSouth * dSouth + 1);
                var surface = Surface(TexelDirection(x, y, width, height), elevation[y * width + x], upness, minimum);
                int index = y * width + x;
                color[4 * index] = Srgb(surface.Red); color[4 * index + 1] = Srgb(surface.Green); color[4 * index + 2] = Srgb(surface.Blue);
                color[4 * index + 3] = (byte)Math.Round(surface.Water * 255);
                normal[3 * index] = Unit(nx / length); normal[3 * index + 1] = Unit(ny / length); normal[3 * index + 2] = Unit(1 / length);
            }
        });
        return (color, normal);
    }
    /// <summary>Bakes the glowing lava (Emission) as an sRGB RGB8 map, black for the kinds without it.</summary>
    public byte[] BakeEmission(int width, int height, int maximumThreads = 4)
    {
        if (width < 8 || height < 4 || width > 8192 || height > 4096 || maximumThreads < 1) throw new ArgumentOutOfRangeException(nameof(width));
        double minimum = 2 * 2 * Math.PI * RadiusKilometers / width;
        var rgb = new byte[3 * width * height];
        Parallel.For(0, height, new ParallelOptions { MaxDegreeOfParallelism = maximumThreads }, y =>
        {
            for (int x = 0; x < width; x++)
            {
                var glow = Emission(TexelDirection(x, y, width, height), minimum);
                int index = 3 * (y * width + x);
                rgb[index] = Srgb((float)glow.X); rgb[index + 1] = Srgb((float)glow.Y); rgb[index + 2] = Srgb((float)glow.Z);
            }
        });
        return rgb;
    }
    /// <summary>
    /// A ring strip (RGBA8, width texels from the inner to the outer edge, sRGB colour and opacity) for a GasGiant:
    /// ringlets of noise, a few clear gaps and an outer ring fading out.
    /// </summary>
    public byte[] BakeRings(int width)
    {
        if (width is < 8 or > 4096) throw new ArgumentOutOfRangeException(nameof(width));
        var rgba = new byte[4 * width];
        double gap1 = .3 + .2 * Hash(20, 1), gap2 = .65 + .15 * Hash(20, 2);
        for (int x = 0; x < width; x++)
        {
            double t = (x + .5) / width;
            double ringlets = .55 + .45 * Gradient(t * 60, .5, .5, _seed + 97) + .2 * Gradient(t * 300, .5, .5, _seed + 98);
            double opacity = Math.Clamp(ringlets, 0, 1) * SmoothStep(0, .08, t) * SmoothStep(1, .85, t);
            opacity *= 1 - .95 * Gauss(t - gap1, .012) - .9 * Gauss(t - gap2, .006);
            var color = Mix((.62, .56, .48), (.82, .76, .66), Math.Clamp(ringlets, 0, 1));
            rgba[4 * x] = Srgb(color.R); rgba[4 * x + 1] = Srgb(color.G); rgba[4 * x + 2] = Srgb(color.B);
            rgba[4 * x + 3] = (byte)Math.Round(Math.Clamp(opacity, 0, 1) * 255);
        }
        return rgba;
    }
    /// <summary>Bakes cloud cover as an 8-bit coverage map.</summary>
    public byte[] BakeClouds(int width, int height, int maximumThreads = 4)
    {
        if (width < 8 || height < 4 || width > 8192 || height > 4096 || maximumThreads < 1) throw new ArgumentOutOfRangeException(nameof(width));
        double minimum = 2 * 2 * Math.PI * RadiusKilometers / width;
        var cover = new byte[width * height];
        Parallel.For(0, height, new ParallelOptions { MaxDegreeOfParallelism = maximumThreads }, y =>
        {
            for (int x = 0; x < width; x++) cover[y * width + x] = (byte)Math.Round(255 * Clouds(TexelDirection(x, y, width, height), minimum));
        });
        return cover;
    }
    private static Vector3d FromLongitudeLatitude(double longitude, double latitude) =>
        new(-Math.Cos(latitude) * Math.Cos(longitude), Math.Sin(latitude), Math.Cos(latitude) * Math.Sin(longitude));

    private static byte Srgb(double linear)
    {
        linear = Math.Clamp(linear, 0, 1);
        double encoded = linear <= .0031308 ? 12.92 * linear : 1.055 * Math.Pow(linear, 1 / 2.4) - .055;
        return (byte)Math.Round(encoded * 255);
    }
    private static byte Unit(double value) => (byte)Math.Round(Math.Clamp(value * .5 + .5, 0, 1) * 255);

    private static double Gradient(double x, double y, double z, int seed)
    {
        double fx = Math.Floor(x), fy = Math.Floor(y), fz = Math.Floor(z);
        long ix = (long)fx, iy = (long)fy, iz = (long)fz;
        double dx = x - fx, dy = y - fy, dz = z - fz;
        static double Fade(double t) => t * t * t * (t * (t * 6 - 15) + 10);
        double u = Fade(dx), v = Fade(dy), w = Fade(dz);
        double Corner(long cx, long cy, long cz, double px, double py, double pz)
        {
            ulong h = (ulong)(cx * 73856093L ^ cy * 19349663L ^ cz * 83492791L ^ seed * 2654435761L);
            h ^= h >> 33; h *= 0xff51afd7ed558ccdUL; h ^= h >> 33; h *= 0xc4ceb9fe1a85ec53UL; h ^= h >> 33;
            // Twelve edge gradients of the cube.
            return (h % 12) switch
            {
                0 => px + py, 1 => -px + py, 2 => px - py, 3 => -px - py, 4 => px + pz, 5 => -px + pz,
                6 => px - pz, 7 => -px - pz, 8 => py + pz, 9 => -py + pz, 10 => py - pz, _ => -py - pz
            };
        }
        double Lerp(double a, double b, double t) => a + (b - a) * t;
        return Lerp(
            Lerp(Lerp(Corner(ix, iy, iz, dx, dy, dz), Corner(ix + 1, iy, iz, dx - 1, dy, dz), u),
                 Lerp(Corner(ix, iy + 1, iz, dx, dy - 1, dz), Corner(ix + 1, iy + 1, iz, dx - 1, dy - 1, dz), u), v),
            Lerp(Lerp(Corner(ix, iy, iz + 1, dx, dy, dz - 1), Corner(ix + 1, iy, iz + 1, dx - 1, dy, dz - 1), u),
                 Lerp(Corner(ix, iy + 1, iz + 1, dx, dy - 1, dz - 1), Corner(ix + 1, iy + 1, iz + 1, dx - 1, dy - 1, dz - 1), u), v), w);
    }
}
