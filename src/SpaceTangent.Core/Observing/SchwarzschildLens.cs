// SPDX-License-Identifier: GPL-2.0-or-later
using SpaceTangent.Contracts;

namespace SpaceTangent.Core.Observing;

/// <summary>
/// Light bending by a Schwarzschild black hole, in units of its Schwarzschild radius r_s = 2GM/c^2. A null geodesic
/// with impact parameter b obeys (du/dphi)^2 = G(u) = 1/b^2 - u^2 + u^3 for u = r_s/r (Schwarzschild 1916; Darwin 1959).
/// A ray from infinity grazes the hole at the turning point u_t (G(u_t) = 0) and leaves deflected by
/// alpha(b) = 2 int_0^u_t du / sqrt(G) - pi; below the critical b_c = 3 sqrt(3) / 2 it is captured, which casts the
/// shadow, and near b_c it winds around the photon sphere (r = 1.5 r_s) any number of times, which gives the photon
/// rings. A static observer at u_o looking at angle psi from the hole sees light that left the sky along a ray of
/// b = sin(psi) / (u_o sqrt(1 - u_o)), swept by SweptAngle about the hole on its way to the observer.
/// </summary>
public static class SchwarzschildLens
{
    public static readonly double CriticalImpactParameter = 1.5 * Math.Sqrt(3);
    /// <summary>u at the photon sphere, where the critical ray circles the hole.</summary>
    public const double PhotonSphere = 2.0 / 3;

    /// <summary>The periapsis u_t of a ray with impact parameter b > b_c: the smallest root of u^2 - u^3 = 1/b^2.</summary>
    public static double TurningPoint(double b)
    {
        if (!(b > CriticalImpactParameter)) throw new ArgumentOutOfRangeException(nameof(b), "Rays below the critical impact parameter are captured.");
        // u^2 - u^3 increases on [0, 2/3], from 0 to 4/27 > 1/b^2.
        double target = 1 / (b * b), low = 0, high = PhotonSphere;
        for (int step = 0; step < 200 && high - low > 0; step++)
        {
            double middle = .5 * (low + high);
            if (middle == low || middle == high) break;
            if (middle * middle * (1 - middle) < target) low = middle; else high = middle;
        }
        return .5 * (low + high);
    }

    /// <summary>
    /// alpha(b), radians: with G = (u_t - u) H(u), H = u_t + u - u_t^2 - u_t u - u^2, and u = u_t (1 - w^2), the path
    /// integral is 4 sqrt(u_t) int_0^1 dw / sqrt(H), regular for every b > b_c. Since H = u_t (2 - w^2)(1 - x) with
    /// x = u_t (3 - 3w^2 + w^4) / (2 - w^2), and 4 int_0^1 dw / sqrt(2 - w^2) = pi, alpha integrates
    /// 4 (1 / sqrt(1 - x) - 1) / sqrt(2 - w^2) without cancelling against pi in the weak field.
    /// </summary>
    public static double Deflection(double b)
    {
        if (double.IsPositiveInfinity(b)) return 0;
        double ut = TurningPoint(b);
        return 4 * Integrate(w =>
        {
            double flat = 2 - w * w, x = ut * (3 - 3 * w * w + w * w * w * w) / flat, root = Math.Sqrt(1 - x);
            return x / (Math.Sqrt(flat) * root * (1 + root));
        }, 0, 1);
    }
    private static double TurningFactor(double ut, double u) => ut + u - (ut * ut + ut * u + u * u);

    /// <summary>
    /// Angle the ray seen at psi (radians from the direction to the hole) sweeps about the hole between the observer at
    /// u_o and infinity, where it is radial: the sky it shows lies at that angle from the hole-to-observer direction,
    /// turned toward the view ray. NaN when the ray ends in the hole (the shadow). Flat space gives pi - psi.
    /// </summary>
    public static double SweptAngle(double observerU, double psi)
    {
        if (!(observerU >= 0) || observerU >= 1 || !(psi >= 0) || psi > Math.PI) throw new ArgumentOutOfRangeException(nameof(observerU));
        if (observerU == 0) return Math.PI - psi;
        bool inward = Math.Cos(psi) > 0;
        double b = Math.Sin(psi) / (observerU * Math.Sqrt(1 - observerU)), bc = CriticalImpactParameter;
        // Outside the photon sphere only inward rays below b_c fall in; inside it only outward rays below b_c escape.
        if (observerU < PhotonSphere ? inward && b <= bc : inward || b >= bc) return double.NaN;
        if (b <= bc) return FromInfinity(observerU, b);
        double ut = TurningPoint(b), wo = Math.Sqrt(Math.Max(0, 1 - observerU / ut));
        // int_{u_o}^{u_t} du / sqrt(G), the part of the path between the observer and the periapsis.
        double between = 2 * Math.Sqrt(ut) * Integrate(w => 1 / Math.Sqrt(TurningFactor(ut, ut * (1 - w * w))), 0, wo);
        return .5 * (Math.PI + Deflection(b)) + (inward ? between : -between);
    }
    /// <summary>
    /// The straight line the light of the ray seen at psi left its source along, far from the hole (r_s units, hole at
    /// the origin, the observer along unit er, the view ray turned from the hole toward unit t, perpendicular to er): its
    /// direction away from the hole, the sky's direction at the swept angle, and its point nearest the hole, at the impact
    /// parameter b on the side the light passes. world_lens.gdshader follows it to bodies at finite distance behind the
    /// hole. Null for the shadow. Requires observerU > 0: at infinity the angular
    /// limit exists, but its nearest point has no finite representation in r_s units.
    /// </summary>
    public static (Vector3d Nearest, Vector3d Direction)? SourceLine(double observerU, double psi, Vector3d er, Vector3d t)
    {
        if (observerU == 0) throw new ArgumentOutOfRangeException(nameof(observerU), "A finite source line requires a finite observer distance.");
        double swept = SweptAngle(observerU, psi);
        if (double.IsNaN(swept)) return null;
        double b = Math.Sin(psi) / (observerU * Math.Sqrt(1 - observerU));
        return ((er * Math.Sin(swept) - t * Math.Cos(swept)) * b, er * Math.Cos(swept) + t * Math.Sin(swept));
    }
    /// <summary>int_0^{u_o} du / sqrt(G) with u = u_o t (2 - t), which keeps a tangent ray (G(u_o) = 0) regular.</summary>
    private static double FromInfinity(double observerU, double b)
    {
        double beta = b * observerU;
        return Integrate(t =>
        {
            double s = t * (2 - t), q = 1 - beta * beta * s * s * (1 - observerU * s);
            return q > 0 ? 2 * (1 - t) * beta / Math.Sqrt(q) : 0;
        }, 0, 1);
    }

    /// <summary>Deflection table sizes shared with world_lens.gdshader.</summary>
    public const int TableSize = 4096;
    public const double TableLambda = 18;
    /// <summary>Table coordinate of b: v = -ln(1 - b_c / b) / lambda, 0 at infinity and linear in alpha near b_c.</summary>
    public static double TableCoordinate(double b) => -Math.Log(1 - CriticalImpactParameter / b) / TableLambda;
    /// <summary>alpha at v = i / (TableSize - 1); the shader interpolates and continues past v = 1 with slope lambda.</summary>
    public static float[] DeflectionTable()
    {
        var table = new float[TableSize];
        // Half the cores: the table is built at startup next to texture decoding.
        Parallel.For(1, TableSize, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, System.Environment.ProcessorCount / 2) }, index =>
        {
            double v = index / (double)(TableSize - 1), x = 1 - Math.Exp(-TableLambda * v);
            table[index] = (float)Deflection(CriticalImpactParameter / x);
        });
        return table;
    }
    /// <summary>alpha(b) from the table as the shader reads it (linear interpolation, linear continuation past v = 1).</summary>
    public static double TableDeflection(ReadOnlySpan<float> table, double b)
    {
        double v = TableCoordinate(b) * (TableSize - 1);
        if (v >= TableSize - 1) return table[TableSize - 1] + (v - (TableSize - 1)) / (TableSize - 1) * TableLambda;
        int index = (int)v; double fraction = v - index;
        return table[index] + (table[index + 1] - table[index]) * fraction;
    }

    /// <summary>16-point Gauss-Legendre nodes and weights on [0, 1], as hard-coded in world_lens.gdshader.</summary>
    public static readonly (double Node, double Weight)[] Gauss16 = GaussLegendre(16);

    /// <summary>
    /// SweptAngle as world_lens.gdshader evaluates it per pixel: alpha from the table (exact when table is null), the
    /// path integrals by Gauss16: from infinity when b u_o &lt; 1/2 (then u_o &lt; u_t / 2) or b &lt;= b_c, otherwise
    /// the stretch from the observer to the periapsis.
    /// </summary>
    public static double ShaderSweptAngle(double observerU, double psi, float[]? table = null)
    {
        if (observerU == 0)
        {
            if (!(psi >= 0 && psi <= Math.PI)) throw new ArgumentOutOfRangeException(nameof(psi));
            return Math.PI - psi;
        }
        if (observerU >= 1) return double.NaN;
        bool inward = Math.Cos(psi) > 0;
        double b = Math.Sin(psi) / (observerU * Math.Sqrt(1 - observerU)), bc = CriticalImpactParameter, beta = b * observerU;
        if (observerU < PhotonSphere ? inward && b <= bc : inward || b >= bc) return double.NaN;
        double sum = 0;
        if (b <= bc || beta < .5)
        {
            foreach (var (t, weight) in Gauss16)
            {
                double s = t * (2 - t), q = 1 - beta * beta * s * s * (1 - observerU * s);
                sum += weight * 2 * (1 - t) * beta / Math.Sqrt(Math.Max(q, 1e-30));
            }
            if (b <= bc || !inward) return sum;
            return Math.PI + (table is null ? Deflection(b) : TableDeflection(table, b)) - sum;
        }
        double ut = ShaderTurningPoint(b), wo = Math.Sqrt(Math.Max(0, 1 - observerU / ut));
        foreach (var (tau, weight) in Gauss16)
        {
            double w = wo * tau, u = ut * (1 - w * w);
            sum += weight / Math.Sqrt(Math.Max(TurningFactor(ut, u), 1e-30));
        }
        double between = 2 * Math.Sqrt(ut) * wo * sum;
        return .5 * (Math.PI + (table is null ? Deflection(b) : TableDeflection(table, b))) + (inward ? between : -between);
    }
    /// <summary>The shader's u_t: the trigonometric root of the cubic, polished by two Newton steps.</summary>
    public static double ShaderTurningPoint(double b)
    {
        double theta = Math.Acos(Math.Clamp(1 - 13.5 / (b * b), -1, 1));
        double u = Math.Max(1 / b, 1.0 / 3 + 2.0 / 3 * Math.Cos(theta / 3 - 2 * Math.PI / 3));
        for (int step = 0; step < 2; step++)
        {
            double slope = 2 * u - 3 * u * u;
            if (slope > 0) u -= (u * u * (1 - u) - 1 / (b * b)) / slope;
        }
        return u;
    }

    private static (double, double)[] GaussLegendre(int count)
    {
        var result = new (double, double)[count];
        for (int index = 0; index < count; index++)
        {
            // Newton on P_n from the Chebyshev guess; map [-1, 1] to [0, 1].
            double x = Math.Cos(Math.PI * (index + .75) / (count + .5)), derivative = 0;
            for (int step = 0; step < 100; step++)
            {
                double p0 = 1, p1 = x;
                for (int k = 2; k <= count; k++) { double p2 = ((2 * k - 1) * x * p1 - (k - 1) * p0) / k; p0 = p1; p1 = p2; }
                derivative = count * (x * p1 - p0) / (x * x - 1);
                double next = x - p1 / derivative;
                if (Math.Abs(next - x) < 1e-16) { x = next; break; }
                x = next;
            }
            result[count - 1 - index] = ((1 + x) / 2, 1 / ((1 - x * x) * derivative * derivative));
        }
        return result;
    }

    /// <summary>
    /// Adaptive Gauss-Kronrod (7, 15) quadrature to about 1e-12 of the integral. Bounded: at most MaximumIntervals subintervals
    /// (the split is breadth-limited, never exponential), and a non-finite integrand value throws instead of recursing.
    /// </summary>
    public static double Integrate(Func<double, double> f, double a, double b)
    {
        ArgumentNullException.ThrowIfNull(f);
        if (!double.IsFinite(a) || !double.IsFinite(b)) throw new ArgumentOutOfRangeException(nameof(a));
        if (b <= a) return 0;
        // Accuracy relative to the whole integral, spread over its length; a subinterval also stops at 1e-10 of itself,
        // the rounding floor of integrands evaluated near a cancellation (rays near the photon sphere).
        var (estimate, _) = Rule(f, a, b);
        double density = Math.Max(1e-12 * Math.Abs(estimate), 1e-300) / (b - a);
        var pending = new Stack<(double A, double B, int Depth)>();
        pending.Push((a, b, 0));
        double total = 0; int intervals = 0;
        while (pending.Count > 0)
        {
            var (low, high, depth) = pending.Pop();
            if (++intervals > MaximumIntervals) throw new InvalidOperationException("Quadrature did not converge.");
            var (kronrod, error) = Rule(f, low, high);
            if (error <= density * (high - low) || error <= 1e-10 * Math.Abs(kronrod) || depth >= 40) { total += kronrod; continue; }
            double middle = .5 * (low + high);
            pending.Push((low, middle, depth + 1)); pending.Push((middle, high, depth + 1));
        }
        return total;
    }
    public const int MaximumIntervals = 20_000;
    private static readonly double[] KronrodNodes =
        [0.991455371120812639206854697526329, 0.949107912342758524526189684047851, 0.864864423359769072789712788640926,
         0.741531185599394439863864773280788, 0.586087235467691130294144845693013, 0.405845151377397166906606412076961,
         0.207784955007898467600689403773245, 0];
    private static readonly double[] KronrodWeights =
        [0.022935322010529224963732008058970, 0.063092092629978553290700663189204, 0.104790010322250183839876322541518,
         0.140653259715525918745189590510238, 0.169004726639267902826583426598550, 0.190350578064785409913256402421014,
         0.204432940075298892414161999234649, 0.209482141084727828012999174891714];
    private static readonly double[] GaussWeights =
        [0.129484966168869693270611432679082, 0.279705391489276667901467771423780, 0.381830050505118944950369775488975,
         0.417959183673469387755102040816327];
    private static (double Kronrod, double Error) Rule(Func<double, double> f, double a, double b)
    {
        double center = .5 * (a + b), half = .5 * (b - a), middle = f(center);
        double kronrod = KronrodWeights[7] * middle, gauss = GaussWeights[3] * middle;
        for (int index = 0; index < 7; index++)
        {
            double offset = half * KronrodNodes[index], sum = f(center - offset) + f(center + offset);
            kronrod += KronrodWeights[index] * sum;
            if (index % 2 == 1) gauss += GaussWeights[index / 2] * sum;
        }
        kronrod *= half; gauss *= half;
        if (!double.IsFinite(kronrod) || !double.IsFinite(gauss)) throw new ArithmeticException("Non-finite integrand.");
        return (kronrod, Math.Abs(kronrod - gauss));
    }
}
