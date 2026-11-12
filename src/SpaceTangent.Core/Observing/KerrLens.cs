// SPDX-License-Identifier: GPL-2.0-or-later
using SpaceTangent.Contracts;

namespace SpaceTangent.Core.Observing;

/// <summary>Where a Kerr ray meets the equatorial disk: radius (M), which plane crossing (1 = direct image), g = nu_obs / nu_emit, azimuth.</summary>
public readonly record struct KerrDiskHit(double Radius, int Crossing, double Redshift, double Azimuth);

/// <summary>A traced Kerr ray: captured by the hole, or escaped toward the unit Direction (hole frame), with the disk images it crossed.</summary>
public readonly record struct KerrRay(bool Captured, Vector3d Direction, IReadOnlyList<KerrDiskHit> Disk);

/// <summary>
/// Light near a rotating (Kerr) black hole, as world_lens.gdshader traces it per pixel, in units of M (G = c = 1; the
/// renderer's r_s is 2 M). Hole frame: the spin along +Z, Boyer-Lindquist r and mu = cos(theta) from the oblate
/// spheroidal coordinates of the Cartesian point. A null geodesic of energy E has constants b = L_z / E and Carter's
/// q = Q / E^2; in Mino time (d lambda = Sigma d tau) r and mu obey one-dimensional equations without sign changes
/// (Carter 1968; Mino 2003). Written for u = 1 / r, the radial one stays well conditioned from far away (R grows as
/// r^4, and the second-order form keeps R + C for a rounding error C, which in r would turn rays back far from the hole):
///   u'' = U'(u) / 2, U = u^4 R(1 / u) = (1 + (a^2 - a b) u^2)^2 - u^2 (1 - 2 u + a^2 u^2) (q + (b - a)^2),
///   mu'' = M'(mu) / 2, M = q (1 - mu^2) + a^2 mu^2 (1 - mu^2) - b^2 mu^2,
///   phi' = a (1 + (a^2 - a b) u^2) / (1 - 2 u + a^2 u^2) - a + b / (1 - mu^2),
/// with R = (r^2 + a^2 - a b)^2 - Delta (q + (b - a)^2), Delta = r^2 - 2 r + a^2. The observer is the zero-angular-momentum observer at the camera
/// (any distance outside the horizon). The photon it sees along a view direction travels the other way, and frame
/// dragging makes the two differ: its constants and derivatives come from the opposite direction (Start), and the ray
/// is followed back in time, its velocities and phi' negated. A camera
/// farther than FarRadius starts the ray where it enters that sphere on a straight line. Fourth-order Runge-Kutta with
/// d tau = StepFraction u / (1 + u sqrt(b^2 + q)) (about StepFraction r in r far out, a twentieth of a radian of turn
/// near the hole); the ray is captured inside 1.01 r_+ or after
/// MaximumSteps (it winds on the photon shell), and escapes once it leaves FarRadius (or the camera's radius) outward.
/// The thin disk lies in the equatorial plane from the prograde innermost stable orbit outward; its gas on prograde
/// circular orbits, Omega = 1 / (r^3/2 + a), shifts light by g = 1 / (u^t E (1 - Omega b)).
/// </summary>
public static class KerrLens
{
    public const double FarRadius = 1000, StepFraction = .05;
    /// <summary>
    /// Sine of the polar cap's radius about the spin axis. Boyer-Lindquist coordinates are singular on the axis, where
    /// the term b / sin^2(theta) of phi' grows without bound and a ray through or near the pole would be mirrored back.
    /// Near the axis that term integrates in closed form: with Theta = q + a^2 - b^2 / theta^2, the ray sweeps
    /// A(s) = acos(s_min / s) in phi from its closest approach sin(theta_min) = s_min = |b| / sqrt(q + a^2) to sin(theta) = s
    /// (pi in all for a ray through the pole). So the rest of phi' is always integrated numerically, and that term too
    /// while a step stays outside the cap; a step that touches the cap takes it as the change of A, negative while the ray
    /// still nears the axis. Outside, steps are kept below StepFraction radians of that term.
    /// </summary>
    public const double PolarCap = .02;
    public const int MaximumSteps = 900;

    /// <summary>The event horizon r_+ = 1 + sqrt(1 - a^2).</summary>
    public static double Horizon(double a) => 1 + Math.Sqrt(1 - a * a);
    /// <summary>The prograde innermost stable circular orbit (Bardeen, Press &amp; Teukolsky 1972).</summary>
    public static double Isco(double a)
    {
        double z1 = 1 + Math.Cbrt(1 - a * a) * (Math.Cbrt(1 + a) + Math.Cbrt(1 - a)), z2 = Math.Sqrt(3 * a * a + z1 * z1);
        return 3 + z2 - Math.Sqrt((3 - z1) * (3 + z1 + 2 * z2));
    }
    /// <summary>Radiative efficiency of the thin disk: 1 - E at the innermost stable orbit, E = sqrt(1 - 2 / (3 r_isco)).</summary>
    public static double Efficiency(double a) => 1 - Math.Sqrt(1 - 2 / (3 * Isco(a)));
    /// <summary>Equatorial circular photon orbits: prograde and retrograde radii (Bardeen et al. 1972).</summary>
    public static double PhotonOrbit(double a, bool prograde) => 2 * (1 + Math.Cos(2.0 / 3 * Math.Acos(prograde ? -a : a)));
    /// <summary>b of the equatorial circular photon orbit at r (signed: positive is prograde).</summary>
    public static double PhotonOrbitImpact(double a, double r) => a == 0 ? Math.Sign(r) * 3 * Math.Sqrt(3) :
        -(r * r * r - 3 * r * r + a * a * r + a * a) / (a * (r - 1));

    /// <summary>Boyer-Lindquist r of a Cartesian point (x^2 + y^2 = (r^2 + a^2) sin^2 theta, z = r cos theta).</summary>
    public static double Radius(Vector3d p, double a)
    {
        double rho2 = p.X * p.X + p.Y * p.Y + p.Z * p.Z, k = rho2 - a * a;
        return Math.Sqrt(.5 * (k + Math.Sqrt(k * k + 4 * a * a * p.Z * p.Z)));
    }

    /// <summary>
    /// The ray seen along unit direction from observer (hole frame, M units) traced backward: captured, or the direction
    /// of the sky it shows, and up to maximumImages disk crossings (diskOuter in M; zero: no disk).
    /// </summary>
    public static KerrRay Trace(double a, Vector3d observer, Vector3d direction, double diskOuter = 0, int maximumImages = 4)
    {
        if (!(a >= 0 && a < 1)) throw new ArgumentOutOfRangeException(nameof(a));
        var hits = new List<KerrDiskHit>();
        double horizon = Horizon(a), inner = Isco(a);
        var start = observer;
        double distance = observer.Length;
        if (distance > FarRadius)
        {
            // Straight to the sphere of FarRadius; a ray that misses it is not bent noticeably.
            // The miss distance from the cross product: distance^2 - along^2 cancels catastrophically in floats.
            Vector3d impact = Vector3d.Cross(observer, direction);
            double along = -Vector3d.Dot(observer, direction), closest2 = Vector3d.Dot(impact, impact);
            if (along <= 0 || closest2 >= FarRadius * FarRadius) return new(false, direction, hits);
            start = observer + direction * (along - Math.Sqrt(FarRadius * FarRadius - closest2));
        }
        if (Radius(start, a) <= horizon) return new(true, default, hits);
        var s = Start(a, start, direction * -1);
        // Back in time: the derivatives negated; u' = -r' / r^2.
        double u = 1 / s.R, vu = s.Vr / (s.R * s.R), mu = s.Mu, vmu = -s.Vmu, phi = s.Phi;
        double escape = 1 / (Math.Max(FarRadius, s.R) * 1.0001), rate = Math.Sqrt(s.B * s.B + Math.Max(0, s.Q));
        double closest = Math.Abs(s.B) / Math.Sqrt(Math.Max(s.Q + a * a, 1e-300));
        // Back in time phi turns against the photon's sense (b's sign; either way through the pole).
        double turn = s.B < 0 ? 1 : -1;
        // The sweep about the axis from the closest approach, negative before it (|mu| still growing).
        double Sweep(double m, double vm)
        {
            double sin = Math.Sqrt(Math.Max(0, 1 - m * m)), angle = Math.Acos(sin > 0 ? Math.Min(1, closest / sin) : 0);
            return m * vm > 0 ? -angle : angle;
        }
        int crossing = 0;
        for (int step = 0; step < MaximumSteps; step++)
        {
            double sin2 = 1 - mu * mu;
            double dt = StepFraction * u / (1 + u * rate);
            if (sin2 >= PolarCap * PolarCap && s.B != 0) dt = Math.Min(dt, StepFraction * sin2 / Math.Abs(s.B));
            var (nu, nvu, nmu, nvmu, regular, singular) = Step(a, s.B, s.Q, u, vu, mu, vmu, dt);
            bool touches = sin2 < PolarCap * PolarCap || 1 - nmu * nmu < PolarCap * PolarCap;
            double nphi = phi + regular + (touches ? turn * (Sweep(nmu, nvmu) - Sweep(mu, vmu)) : singular);
            if (nmu * mu < 0 || (nmu == 0 && mu != 0))
            {
                crossing++;
                double f = mu / (mu - nmu), radius = 1 / (u + (nu - u) * f);
                if (diskOuter > 0 && radius >= inner && radius <= diskOuter)
                {
                    double omega = 1 / (Math.Pow(radius, 1.5) + a);
                    double ut = (Math.Pow(radius, 1.5) + a) / (Math.Pow(radius, .75) * Math.Sqrt(Math.Pow(radius, 1.5) - 3 * Math.Sqrt(radius) + 2 * a));
                    hits.Add(new(radius, crossing, 1 / (ut * s.E * (1 - omega * s.B)), phi + (nphi - phi) * f));
                    if (hits.Count >= maximumImages) return new(false, default, hits);
                }
            }
            (u, vu, mu, vmu, phi) = (nu, nvu, nmu, nvmu, nphi);
            if (u > 1 / (1.01 * horizon)) return new(true, default, hits);
            if (u < escape && vu < 0) return new(false, Escaped(a, s.B, u, vu, mu, vmu, phi), hits);
        }
        return new(true, default, hits);
    }

    /// <summary>
    /// A photon at p moving along unit d in the zero-angular-momentum observer's frame: b, q, E (per unit local energy),
    /// r, mu, phi and the Mino-time derivatives of r and mu.
    /// </summary>
    public readonly record struct State(double B, double Q, double E, double R, double Vr, double Mu, double Vmu, double Phi);
    public static State Start(double a, Vector3d p, Vector3d d)
    {
        double r = Radius(p, a), mu = Math.Clamp(p.Z / r, -1, 1), sin = Math.Sqrt(Math.Max(1e-24, 1 - mu * mu));
        double phi = Math.Atan2(p.Y, p.X), cosPhi = Math.Cos(phi), sinPhi = Math.Sin(phi);
        var er = new Vector3d(sin * cosPhi, sin * sinPhi, mu);
        var eTheta = new Vector3d(mu * cosPhi, mu * sinPhi, -sin);
        var ePhi = new Vector3d(-sinPhi, cosPhi, 0);
        double nr = Vector3d.Dot(d, er), nTheta = Vector3d.Dot(d, eTheta), nPhi = Vector3d.Dot(d, ePhi);
        double sigma = r * r + a * a * mu * mu, delta = r * r - 2 * r + a * a;
        double big = (r * r + a * a) * (r * r + a * a) - a * a * delta * sin * sin;
        double alpha = Math.Sqrt(sigma * delta / big), omega = 2 * a * r / big, varpi = sin * Math.Sqrt(big / sigma);
        // Zero-angular-momentum observer: E = alpha + omega varpi n_phi, L = varpi n_phi, p_theta = sqrt(Sigma) n_theta.
        double e = alpha + omega * varpi * nPhi, b = varpi * nPhi / e, pTheta = Math.Sqrt(sigma) * nTheta / e;
        double q = pTheta * pTheta + mu * mu * (b * b / (sin * sin) - a * a);
        return new(b, q, e, r, Math.Sqrt(sigma * delta) * nr / e, mu, -sin * pTheta, phi);
    }

    /// <summary>One fourth-order step of u, u', mu and mu', and the change of phi: its regular part and the singular b / sin^2 part.</summary>
    private static (double U, double Vu, double Mu, double Vmu, double Regular, double Singular) Step(double a, double b, double q,
        double u, double vu, double mu, double vmu, double dt)
    {
        double k = q + (b - a) * (b - a), c = a * a - a * b;
        // U'(u) / 2.
        double Au(double x) => 2 * c * x * (1 + c * x * x) - k * (x - 3 * x * x + 2 * a * a * x * x * x);
        double Amu(double m) => -q * m + a * a * m * (1 - 2 * m * m) - b * b * m;
        // Back in time: phi' of the photon, negated: its regular part, and the singular one (used outside the polar cap).
        double Phi(double x, double m) => -(a * (1 + c * x * x) / (1 - 2 * x + a * a * x * x) - a);
        double Singular(double m) => -b / Math.Max(1 - m * m, PolarCap * PolarCap);
        double k1u = vu, k1v = Au(u), k1m = vmu, k1w = Amu(mu), k1p = Phi(u, mu);
        double k2u = vu + k1v * dt / 2, k2v = Au(u + k1u * dt / 2), k2m = vmu + k1w * dt / 2, k2w = Amu(mu + k1m * dt / 2), k2p = Phi(u + k1u * dt / 2, mu + k1m * dt / 2);
        double k3u = vu + k2v * dt / 2, k3v = Au(u + k2u * dt / 2), k3m = vmu + k2w * dt / 2, k3w = Amu(mu + k2m * dt / 2), k3p = Phi(u + k2u * dt / 2, mu + k2m * dt / 2);
        double k4u = vu + k3v * dt, k4v = Au(u + k3u * dt), k4m = vmu + k3w * dt, k4w = Amu(mu + k3m * dt), k4p = Phi(u + k3u * dt, mu + k3m * dt);
        double singular = (Singular(mu) + 2 * Singular(mu + k1m * dt / 2) + 2 * Singular(mu + k2m * dt / 2) + Singular(mu + k3m * dt)) * dt / 6;
        return (u + (k1u + 2 * k2u + 2 * k3u + k4u) * dt / 6, vu + (k1v + 2 * k2v + 2 * k3v + k4v) * dt / 6,
            mu + (k1m + 2 * k2m + 2 * k3m + k4m) * dt / 6, vmu + (k1w + 2 * k2w + 2 * k3w + k4w) * dt / 6,
            (k1p + 2 * k2p + 2 * k3p + k4p) * dt / 6, singular);
    }

    /// <summary>The direction back toward the source far out: dr, r d theta, r sin theta d phi of the reversed ray, over r.</summary>
    private static Vector3d Escaped(double a, double b, double u, double vu, double mu, double vmu, double phi)
    {
        double sin = Math.Sqrt(Math.Max(1e-24, 1 - mu * mu)), cosPhi = Math.Cos(phi), sinPhi = Math.Sin(phi);
        double dPhi = -(a * (1 + (a * a - a * b) * u * u) / (1 - 2 * u + a * a * u * u) - a + b / (sin * sin));
        var er = new Vector3d(sin * cosPhi, sin * sinPhi, mu);
        var eTheta = new Vector3d(mu * cosPhi, mu * sinPhi, -sin);
        var ePhi = new Vector3d(-sinPhi, cosPhi, 0);
        Vector3d ray = er * (-vu / u) + eTheta * (-vmu / sin) + ePhi * (sin * dPhi);
        return ray / ray.Length;
    }
}
