// SPDX-License-Identifier: GPL-2.0-or-later
using SpaceTangent.Contracts;

namespace SpaceTangent.Core.Observing;

/// <summary>
/// A volumetric model of the Galaxy for the diffuse light of its unresolved stars, seen from anywhere: from the Sun a
/// band along the galactic equator with dust lanes and the bright bulge toward Sagittarius, from outside a barred
/// spiral. Galactocentric kiloparsecs: +X from the Sun toward the centre (galactic longitude 0), +Y toward l = 90 deg, +Z
/// toward the north galactic pole; the Sun at (-8.178, 0, 0.0208) (GRAVITY Collaboration 2019; Bennett &amp; Bovy 2019).
///
/// Light (relative to the old disk's density at the Sun):
/// - thin disk exp(-(R - R_sun) / 2.6) exp(-|z| / 0.3), thick disk 0.12 exp(-(R - R_sun) / 2.0) exp(-|z| / 0.9)
///   (Bland-Hawthorn &amp; Gerhard 2016), yellowish;
/// - a bar 27 degrees from the Sun-centre line, its near end at positive longitude, with semi-axes 3.5, 1.2 and 0.6 kpc,
///   orange; the inner disk inside about 3 kpc is cleared by the bar;
/// - four logarithmic spiral arms of pitch 12 degrees and 0.55 kpc half-width, brightening and fading along their length,
///   that raise the disk, with young blue stars (scale height 0.1 kpc) and pink HII regions in clumps along them; the
///   Sun lies between two arms.
/// Dust: exp(-(R - R_sun) / 3.2) exp(-|z| / 0.12), denser in lanes along the arms' inner edges and clumped by noise,
/// extinguishing 1 magnitude of
/// V per kpc at the Sun's density (R, G, B: 0.75, 1, 1.32 of V, an R_V = 3.1 law). Within about a kiloparsec of the Sun
/// the noise gives way to the real nearby dark clouds (LocalClouds: the Great Rift from Cygnus through Aquila to
/// Ophiuchus, the Coalsack, Taurus, Orion and others, at their measured distances), inside the nearly dust-free Local
/// Bubble (the inner 70 pc).
///
/// The Magellanic Clouds and Andromeda (Neighbours) are galaxies of the same kind, drawn by this model once they span
/// more than 4 degrees (IsVolumetric) and as catalogue objects when smaller.
///
/// Beyond the named clouds it is a smooth model with noise, not a map: farther lanes are not where they are in the sky.
/// </summary>
public static class GalacticLight
{
    public const double KiloparsecLightYears = 3261.5638;
    public static readonly Vector3d SunKiloparsecs = new(-8.178, 0, .0208);
    private const double ScaleLength = 2.6, ScaleHeight = .3, ArmPitchDegrees = 12, ArmHalfWidth = .55, DustPerKiloparsec = .921;
    private const double BarAngleDegrees = 27, Radius = 22, HalfThickness = 4;
    private static readonly double ArmTangent = Math.Tan(ArmPitchDegrees * Math.PI / 180);
    private static readonly double BarCos = Math.Cos(BarAngleDegrees * Math.PI / 180), BarSin = Math.Sin(BarAngleDegrees * Math.PI / 180);

    /// <summary>World-frame unit vectors of the galactic axes: toward l = 0, toward l = 90 deg and the north galactic pole.</summary>
    public static Basis3d ToEquatorial { get; } = new
        (GalacticAxis(0, 0), GalacticAxis(90, 0), GalacticAxis(0, 90));

    private static readonly Basis3d ToGalactic = ToEquatorial.Transpose();

    private static Vector3d GalacticAxis(double longitude, double latitude)
    {
        const double ascendingNode = 282.85958, poleDeclination = 27.1283361, longitudeAtNode = 32.932;
        Vector3d node = Equatorial(ascendingNode, 0);
        Vector3d pole = Equatorial(ascendingNode - 90, poleDeclination);
        var (sinLongitude, cosLongitude) = Math.SinCos((longitude - longitudeAtNode) * Math.PI / 180);
        var (sinLatitude, cosLatitude) = Math.SinCos(latitude * Math.PI / 180);
        return (node * cosLongitude + Vector3d.Cross(pole, node) * sinLongitude) * cosLatitude + pole * sinLatitude;
    }

    private static Vector3d Equatorial(double ra, double declination)
    {
        var (sinRa, cosRa) = Math.SinCos(ra * Math.PI / 180);
        var (sinDeclination, cosDeclination) = Math.SinCos(declination * Math.PI / 180);
        return new(cosDeclination * cosRa, cosDeclination * sinRa, sinDeclination);
    }

    private static Vector3d Unit(Vector3d vector) => vector / vector.Length;

    /// <summary>Galactocentric position (kpc) of a world-frame point given in light years from the solar system barycenter.</summary>
    public static Vector3d Galactocentric(Vector3d barycentricLightYears) =>
        SunKiloparsecs + ToGalactic.Transform(barycentricLightYears) / KiloparsecLightYears;

    /// <summary>
    /// Emitted light (R, G, B per kpc) and dust density at a galactocentric point. Given a 3D dust map (GalacticDustGrid), its dust
    /// takes over from the model's beyond 1 kpc of the Sun (blending from 0.8 to 1.2 kpc), and fades back into the
    /// model near the map's faces; nearer, the real dark clouds below stay.
    /// </summary>
    public static (Vector3d Light, double Dust) Sample(Vector3d p, GalacticDustGrid? map = null)
    {
        double radius = Math.Sqrt(p.X * p.X + p.Y * p.Y), height = Math.Abs(p.Z);
        // The bar, in its own axes, and the inner disk it clears.
        double bx = p.X * BarCos + p.Y * BarSin, by = -p.X * BarSin + p.Y * BarCos;
        double bar = 25 * Math.Exp(-(bx * bx / (3.5 * 3.5) + by * by / (1.2 * 1.2) + p.Z * p.Z / (.6 * .6)));
        double inner = Smooth((radius - 1.5) / 2.5);
        double disk = inner * Math.Exp(-(radius + SunKiloparsecs.X) / ScaleLength) * Math.Exp(-height / ScaleHeight);
        double thick = .12 * inner * Math.Exp(-(radius + SunKiloparsecs.X) / 2.0) * Math.Exp(-height / .9);
        // Arms brighten and fade along their length; the dust lanes run along their inner edges.
        double arm = Arms(p.X, p.Y, radius, 0) * inner * (.45 + 1.1 * Noise(p * .6, 5));
        double lane = Arms(p.X, p.Y, radius, .12) * inner;
        double clump = Noise(p * 3, 7) * .6 + Noise(p * 9, 11) * .4;
        double young = 4 * arm * Math.Exp(-(radius + SunKiloparsecs.X) / 3.5) * Math.Exp(-height / .1) * (.4 + 1.2 * clump);
        double hii = young * Math.Max(0, Noise(p * 22, 13) - .62) * 6;
        var light = new Vector3d(1, .9, .78) * (disk * (1 + 1.2 * arm) + thick) + new Vector3d(1, .84, .66) * bar +
            new Vector3d(.62, .76, 1) * young + new Vector3d(1, .35, .45) * hii;
        // Near the Sun, the noise's made-up clouds fade to their mean and the real ones take over.
        var fromSun = p - SunKiloparsecs;
        double near = fromSun.X * fromSun.X + fromSun.Y * fromSun.Y + fromSun.Z * fromSun.Z;
        double real = Math.Exp(-near / (1.1 * 1.1));
        double clumping = (.25 + 1.5 * clump * clump) * (1 - real) + .65 * real;
        double dust = inner * Math.Exp(-(radius + SunKiloparsecs.X) / 3.2) * Math.Exp(-height / .12) * (.35 + 1.6 * lane) * clumping;
        if (near < 2.25) dust = (dust + Clouds(p, fromSun)) * Smooth((Math.Sqrt(near) - .04) / .1);
        if (map is not null && near > MapNear * MapNear)
        {
            double weight = map.Weight(fromSun) * Smooth((Math.Sqrt(near) - MapNear) / (MapFull - MapNear));
            if (weight > 0) dust += (map.Density(fromSun) - dust) * weight;
        }
        return (light, dust);
    }

    /// <summary>Distances from the Sun (kpc) over which a dust map takes over from the model's dust.</summary>
    public const double MapNear = .8, MapFull = 1.2;

    /// <summary>
    /// A nearby dark cloud: galactic longitude and latitude (degrees), distance (pc) and radius (pc) of a Gaussian blob,
    /// and the visual extinction (magnitudes) through its centre. Distances mostly from Zucker et al. (2020), positions
    /// and extents from the CO survey of Dame, Hartmann &amp; Thaddeus (2001), extinctions rounded.
    /// </summary>
    public readonly record struct DarkCloud(string Name, double Longitude, double Latitude, double DistanceParsecs, double RadiusParsecs,
        double CentralExtinction);
    public static readonly IReadOnlyList<DarkCloud> LocalClouds =
    [
        new("Aquila Rift", 28, 4, 220, 45, 3), new("Aquila Rift", 18, 8, 200, 35, 2.5), new("Serpens", 32, 6, 280, 35, 2.5),
        new("Aquila", 40, 2, 300, 40, 2), new("Vulpecula Rift", 60, 0, 450, 50, 2), new("Cygnus Rift", 72, 1, 650, 70, 3),
        new("Cygnus Rift", 82, 2, 800, 70, 3), new("Cepheus Flare", 110, 15, 350, 35, 1.5),
        new("Ophiuchus", 353, 17, 139, 10, 6), new("Ophiuchus streamers", 4, 13, 140, 15, 2.5), new("Pipe Nebula", 0, 5, 145, 7, 3),
        new("Pipe Nebula", 357, 7, 145, 6, 2.5), new("Lupus", 340, 13, 155, 12, 2), new("Scorpius-Centaurus", 345, 5, 170, 20, 1),
        new("Corona Australis", 0, -18, 150, 5, 3), new("Coalsack", 301, -1, 185, 9, 2), new("Chamaeleon", 297, -16, 180, 7, 2.5),
        new("Taurus", 172, -15, 140, 15, 3), new("Perseus", 160, -20, 290, 15, 3), new("California", 161, -9, 470, 25, 2),
        new("Orion A", 210, -19, 410, 18, 3), new("Orion B", 206, -15, 410, 15, 3), new("Monoceros R2", 214, -12, 830, 20, 2),
        new("Scutum lane", 15, 0, 1200, 100, 2),
    ];
    /// <summary>Each cloud as galactocentric centre (kpc), radius (kpc) and peak dust density.</summary>
    private static readonly (Vector3d Centre, double Radius, double Density)[] CloudBlobs = LocalClouds.Select(cloud =>
    {
        double l = cloud.Longitude * Math.PI / 180, b = cloud.Latitude * Math.PI / 180, radius = cloud.RadiusParsecs / 1000;
        var direction = new Vector3d(Math.Cos(b) * Math.Cos(l), Math.Cos(b) * Math.Sin(l), Math.Sin(b));
        // Visual extinction through the centre: 1.086 tau, tau = density * V extinction per kpc * radius * sqrt(pi).
        double density = cloud.CentralExtinction / (1.0857 * DustPerKiloparsec * radius * Math.Sqrt(Math.PI));
        return (SunKiloparsecs + direction * (cloud.DistanceParsecs / 1000), radius, density);
    }).ToArray();
    /// <summary>The nearby clouds' dust at p (fromSun = p - the Sun), ragged by noise at the scale of 20 pc.</summary>
    private static double Clouds(Vector3d p, Vector3d fromSun)
    {
        double sum = 0;
        foreach (var (centre, radius, density) in CloudBlobs)
        {
            var d = p - centre;
            double q = (d.X * d.X + d.Y * d.Y + d.Z * d.Z) / (radius * radius);
            if (q < 9) sum += density * Math.Exp(-q);
        }
        return sum > 0 ? sum * (.3 + 1.4 * Noise(fromSun * 50, 17)) : 0;
    }

    /// <summary>
    /// Closeness (0..1) to the nearest of four logarithmic arms, R = R0 exp(phi tan(pitch)), the arms turned by shift
    /// radians (positive: toward their inner, trailing edge).
    /// </summary>
    private static double Arms(double x, double y, double radius, double shift)
    {
        if (radius < .1) return 0;
        // Phase along the spiral; arms every quarter turn, offset so the Sun (at phase 0.5 of a gap) lies between two.
        double phase = Math.Atan2(y, x) - Math.Log(radius / 8.178) / ArmTangent + shift;
        double gap = Math.PI / 2, offset = phase - Math.PI - gap / 2;
        double nearest = offset - gap * Math.Round(offset / gap);
        double distance = Math.Abs(nearest) * radius * Math.Sin(ArmPitchDegrees * Math.PI / 180);
        return Math.Exp(-distance * distance / (ArmHalfWidth * ArmHalfWidth));
    }
    private static double Smooth(double x)
    {
        x = Math.Clamp(x, 0, 1);
        return x * x * (3 - 2 * x);
    }
    /// <summary>Value noise in [0, 1].</summary>
    private static double Noise(Vector3d p, int seed)
    {
        double fx = Math.Floor(p.X), fy = Math.Floor(p.Y), fz = Math.Floor(p.Z);
        long ix = (long)fx, iy = (long)fy, iz = (long)fz;
        double u = Fade(p.X - fx), v = Fade(p.Y - fy), w = Fade(p.Z - fz);
        static double Fade(double t) => t * t * (3 - 2 * t);
        double H(long x, long y, long z)
        {
            ulong h = (ulong)(x * 73856093L ^ y * 19349663L ^ z * 83492791L ^ seed * 2654435761L);
            h ^= h >> 33; h *= 0xff51afd7ed558ccdUL; h ^= h >> 33;
            return (h >> 11) * (1.0 / (1UL << 53));
        }
        double L(double a, double b, double t) => a + (b - a) * t;
        return L(L(L(H(ix, iy, iz), H(ix + 1, iy, iz), u), L(H(ix, iy + 1, iz), H(ix + 1, iy + 1, iz), u), v),
                 L(L(H(ix, iy, iz + 1), H(ix + 1, iy, iz + 1), u), L(H(ix, iy + 1, iz + 1), H(ix + 1, iy + 1, iz + 1), u), v), w);
    }

    /// <summary>Extinction per kpc of dust density 1 in R, G, B.</summary>
    private static readonly Vector3d Extinction = new Vector3d(.75, 1, 1.32) * DustPerKiloparsec;
    public const int MaximumSteps = 256;

    /// <summary>
    /// Light reaching an observer (galactocentric kpc) along a unit galactic direction: emission summed along the ray,
    /// each step dimmed by the dust before it, through the galaxy's bounding cylinder (radius 22 kpc, |z| below 4 kpc)
    /// and the bounding spheres of the given neighbours, nearest first. Steps grow with distance from the first point
    /// inside (3 % of it, at least 3 pc, or a 150th of a neighbour's bound), at most MaximumSteps in each.
    /// </summary>
    public static Vector3d Trace(Vector3d observer, Vector3d direction, IReadOnlyList<Neighbour>? neighbours = null, GalacticDustGrid? map = null)
    {
        if (neighbours?.Count > 8)
            throw new ArgumentOutOfRangeException(nameof(neighbours));
        Span<(double Near, double Far, int Which)> segments = stackalloc (double, double, int)[1 + (neighbours?.Count ?? 0)];
        int count = 0;
        if (Enter(observer, direction, out double near, out double far)) segments[count++] = (near, far, -1);
        for (int index = 0; neighbours is not null && index < neighbours.Count; index++)
            if (neighbours[index].Enter(observer, direction, out near, out far)) segments[count++] = (near, far, index);
        if (count == 0) return default;
        segments = segments[..count];
        segments.Sort((a, b) => a.Near.CompareTo(b.Near));
        Vector3d light = default, transmission = new(1, 1, 1);
        foreach (var (start, end, which) in segments)
        {
            light += which < 0 ? March(observer, direction, start, end, .003, null, map, ref transmission)
                : March(observer, direction, start, end, neighbours![which].BoundKiloparsecs / 150, neighbours[which], map, ref transmission);
            if (transmission.X + transmission.Y + transmission.Z < 1e-4) break;
        }
        return light;
    }
    private static Vector3d March(Vector3d observer, Vector3d direction, double near, double far, double minimumStep,
        Neighbour? galaxy, GalacticDustGrid? map, ref Vector3d transmission)
    {
        Vector3d light = default;
        double t = near;
        for (int step = 0; step < MaximumSteps && t < far; step++)
        {
            double dt = Math.Min(Math.Max(minimumStep, .03 * (t - near + .02)), far - t);
            Vector3d point = observer + direction * (t + dt / 2);
            var (emitted, dust) = galaxy is null ? Sample(point, map) : galaxy.Sample(point);
            var tau = Extinction * (dust * dt);
            var pass = new Vector3d(Math.Exp(-tau.X), Math.Exp(-tau.Y), Math.Exp(-tau.Z));
            // Light emitted within the step reaches the observer through, on average, half of the step's dust.
            light += new Vector3d(transmission.X * Math.Sqrt(pass.X) * emitted.X, transmission.Y * Math.Sqrt(pass.Y) * emitted.Y,
                transmission.Z * Math.Sqrt(pass.Z) * emitted.Z) * dt;
            transmission = new(transmission.X * pass.X, transmission.Y * pass.Y, transmission.Z * pass.Z);
            if (transmission.X + transmission.Y + transmission.Z < 1e-4) break;
            t += dt;
        }
        return light;
    }
    /// <summary>Visual extinction (magnitudes) of the Galaxy's dust from observer along direction out to distance kpc, in 1 pc steps.</summary>
    public static double VisualExtinction(Vector3d observer, Vector3d direction, double kiloparsecs, GalacticDustGrid? map = null)
    {
        if (!(kiloparsecs is > 0 and <= 5)) throw new ArgumentOutOfRangeException(nameof(kiloparsecs));
        double tau = 0;
        for (double t = .0005; t < kiloparsecs; t += .001) tau += Sample(observer + direction * t, map).Dust * DustPerKiloparsec * .001;
        return 1.0857 * tau;
    }
    /// <summary>Where a ray from origin meets the bounding cylinder: the near and far distances.</summary>
    private static bool Enter(Vector3d origin, Vector3d direction, out double near, out double far)
    {
        near = 0; far = double.PositiveInfinity;
        // Slab |z| < HalfThickness.
        if (Math.Abs(direction.Z) < 1e-12) { if (Math.Abs(origin.Z) > HalfThickness) return false; }
        else
        {
            double a = (-HalfThickness - origin.Z) / direction.Z, b = (HalfThickness - origin.Z) / direction.Z;
            near = Math.Max(near, Math.Min(a, b)); far = Math.Min(far, Math.Max(a, b));
        }
        // Infinite cylinder x^2 + y^2 < Radius^2.
        double qa = direction.X * direction.X + direction.Y * direction.Y, qb = origin.X * direction.X + origin.Y * direction.Y;
        double qc = origin.X * origin.X + origin.Y * origin.Y - Radius * Radius;
        if (qa < 1e-12) { if (qc > 0) return false; }
        else
        {
            double discriminant = qb * qb - qa * qc;
            if (discriminant < 0) return false;
            double root = Math.Sqrt(discriminant);
            near = Math.Max(near, (-qb - root) / qa); far = Math.Min(far, (-qb + root) / qa);
        }
        return far > near;
    }

    /// <summary>
    /// A neighbouring galaxy in the same volumetric terms: its centre (galactocentric kpc), the major axis and normal of
    /// its disk (galactic unit vectors), and the radius (kpc) of the sphere that bounds its light. Light (R, G, B per kpc)
    /// is an exponential disk with a ring and noise clumps, plus a Gaussian bar or bulge; dust lies in the disk's mid-plane
    /// along the ring.
    /// </summary>
    public sealed record Neighbour(string Name, Vector3d CentreKiloparsecs, Vector3d Major, Vector3d Normal, double BoundKiloparsecs)
    {
        public Vector3d DiskColor { get; init; } = new(1, .9, .8);
        public double DiskLight { get; init; }
        public double ScaleLength { get; init; } = 1;
        public double ScaleHeight { get; init; } = .3;
        public double RingRadius { get; init; }
        public double RingWidth { get; init; } = 1;
        public double RingLight { get; init; }
        /// <summary>How much noise breaks the disk into clumps (0: smooth).</summary>
        public double Clumping { get; init; }
        public Vector3d BarColor { get; init; } = new(1, .82, .62);
        public double BarLight { get; init; }
        /// <summary>The bar's semi-axes (kpc) along the major axis, across it in the disk and along the normal.</summary>
        public Vector3d BarAxes { get; init; } = new(1, 1, 1);
        public double Dust { get; init; }
        public int Seed { get; init; }
        private Vector3d Minor => Vector3d.Cross(Normal, Major);

        public (Vector3d Light, double Dust) Sample(Vector3d p)
        {
            var q = p - CentreKiloparsecs;
            double a = Vector3d.Dot(q, Major), b = Vector3d.Dot(q, Minor), c = Vector3d.Dot(q, Normal);
            double radius = Math.Sqrt(a * a + b * b), height = Math.Abs(c);
            double ring = RingLight > 0 || Dust > 0 ? Math.Exp(-(radius - RingRadius) * (radius - RingRadius) / (RingWidth * RingWidth)) : 0;
            double clump = Clumping > 0 ? 1 - Clumping + 2 * Clumping * Noise(q * (3 / ScaleLength), Seed) : 1;
            double disk = DiskLight * Math.Exp(-radius / ScaleLength) * Math.Exp(-height / ScaleHeight) * (1 + RingLight * ring) * clump;
            double bar = BarLight * Math.Exp(-(a * a / (BarAxes.X * BarAxes.X) + b * b / (BarAxes.Y * BarAxes.Y) + c * c / (BarAxes.Z * BarAxes.Z)));
            double dust = Dust > 0 ? Dust * ring * Math.Exp(-height / (.4 * ScaleHeight)) * (.3 + 1.4 * Noise(q * 2, Seed + 1)) : 0;
            return (DiskColor * disk + BarColor * bar, dust);
        }
        /// <summary>Where a ray from origin meets the bounding sphere.</summary>
        public bool Enter(Vector3d origin, Vector3d direction, out double near, out double far)
        {
            var offset = origin - CentreKiloparsecs;
            double b = Vector3d.Dot(offset, direction), c = Vector3d.Dot(offset, offset) - BoundKiloparsecs * BoundKiloparsecs;
            double discriminant = b * b - c;
            near = far = 0;
            if (discriminant <= 0) return false;
            double root = Math.Sqrt(discriminant);
            near = Math.Max(0, -b - root); far = -b + root;
            return far > near;
        }
    }

    /// <summary>A neighbour drawn by this model rather than by its sprite: one spanning more than 4 degrees.</summary>
    public static bool IsVolumetric(Neighbour neighbour, Vector3d observerKpc) =>
        neighbour.BoundKiloparsecs > Math.Tan(2 * Math.PI / 180) * (observerKpc - neighbour.CentreKiloparsecs).Length;

    /// <summary>
    /// The Large and Small Magellanic Clouds and Andromeda, named as in the deep-sky catalogue, at its distances
    /// (LMC 50, SMC 60, M 31 788.9 kpc, 2.573 million light years), so that each volume lies where its label and sprite
    /// do: seen from 120 kpc, M 31 at the modern 765 kpc stood 11 degrees off them. The LMC's disk is inclined 26 degrees,
    /// line of nodes at position angle 154 (van der Marel &amp; Kallivayalil 2014); the SMC is an elongated cloud seen
    /// largely end-on; M 31 is inclined 77 degrees, major axis at position angle 38, with its dusty star-forming ring at
    /// 10 kpc.
    /// The Clouds' light is mostly their disks' (the LMC's bar gives a tenth or so, its disk's scale length is 1.6 kpc), so
    /// they spread over several degrees rather than a bar-sized spot.
    /// </summary>
    public static IReadOnlyList<Neighbour> Neighbours { get; } =
    [
        Galaxy("LMC", 80.894, -69.756, 50.0, 154, 26, 6) with
        {
            DiskColor = new(.88, .92, 1), DiskLight = 5, ScaleLength = 1.6, ScaleHeight = .35, Clumping = .6, BarLight = 1.2,
            BarAxes = new(1.5, .45, .4), BarColor = new(1, .92, .82), Dust = .4, RingRadius = 1.5, RingWidth = 1.5, Seed = 31,
        },
        Galaxy("SMC", 13.187, -72.829, 60.0, 45, 65, 4) with
        {
            DiskColor = new(.86, .9, 1), DiskLight = 1.6, ScaleLength = 1, ScaleHeight = .9, Clumping = .5, BarLight = .8,
            BarAxes = new(1.4, .5, .9), BarColor = new(.95, .95, 1), Seed = 37,
        },
        Galaxy("M 31", 10.6847, 41.2690, 788.9, 38, 77, 25) with
        {
            DiskLight = 9, ScaleLength = 5.3, ScaleHeight = .4, RingRadius = 10, RingWidth = 2, RingLight = .8, Clumping = .25,
            BarLight = 90, BarAxes = new(1.1, 1, .8), Dust = 1.2, Seed = 41,
        },
    ];
    /// <summary>A galaxy at J2000 right ascension and declination (degrees) and distance (kpc), its disk's major axis at
    /// a position angle (degrees east of north) and inclined to the sky.</summary>
    private static Neighbour Galaxy(string name, double raDegrees, double decDegrees, double kiloparsecs, double positionAngle,
        double inclination, double bound)
    {
        Vector3d r = ToGalactic.Transform(Equatorial(raDegrees, decDegrees));
        Vector3d pole = ToGalactic.Z;
        var north = Unit(pole - r * Vector3d.Dot(pole, r));
        var east = Unit(Vector3d.Cross(pole, r));
        double pa = positionAngle * Math.PI / 180, i = inclination * Math.PI / 180;
        var major = north * Math.Cos(pa) + east * Math.Sin(pa);
        var minor = north * -Math.Sin(pa) + east * Math.Cos(pa);
        var normal = Unit(r * -Math.Cos(i) + minor * Math.Sin(i));
        return new(name, SunKiloparsecs + r * kiloparsecs, major, normal, bound);
    }

    /// <summary>
    /// The sky of the Galaxy seen from observerKpc, as an equirectangular map in galactic coordinates (row 0 at b = +90,
    /// column 0 at l = -180, longitude growing with the column), linear R, G, B per texel. Rows run on at most
    /// maximumThreads threads. Includes the neighbours that are volumetric from there (IsVolumetric).
    /// </summary>
    public static float[] Bake(Vector3d observerKpc, int width, int height, int maximumThreads = 2, GalacticDustGrid? map = null, CancellationToken cancellation = default)
    {
        var neighbours = Neighbours.Where(neighbour => IsVolumetric(neighbour, observerKpc)).ToArray();
        if (width is < 8 or > 4096 || height is < 4 or > 2048 || maximumThreads < 1) throw new ArgumentOutOfRangeException(nameof(width));
        var rgb = new float[3 * width * height];
        Parallel.For(0, height, new ParallelOptions { MaxDegreeOfParallelism = maximumThreads, CancellationToken = cancellation }, row =>
        {
            double latitude = (.5 - (row + .5) / height) * Math.PI;
            for (int column = 0; column < width; column++)
            {
                double longitude = ((column + .5) / width - .5) * 2 * Math.PI;
                var direction = new Vector3d(Math.Cos(latitude) * Math.Cos(longitude), Math.Cos(latitude) * Math.Sin(longitude), Math.Sin(latitude));
                var light = Trace(observerKpc, direction, neighbours, map);
                int index = 3 * (row * width + column);
                rgb[index] = (float)light.X; rgb[index + 1] = (float)light.Y; rgb[index + 2] = (float)light.Z;
            }
        });
        return rgb;
    }
}
