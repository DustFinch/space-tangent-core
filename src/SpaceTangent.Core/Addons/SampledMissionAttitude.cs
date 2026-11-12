// SPDX-License-Identifier: GPL-2.0-or-later
using System.Globalization;
using SpaceTangent.Contracts;
using SpaceTangent.Core.Coordinates;
using SpaceTangent.Core.World;

namespace SpaceTangent.Core.Addons;

/// <summary>A sampled mission attitude with native WXYZ interpolation and angular rates.</summary>
public sealed class SampledMissionAttitude
{
    // The source linear branch is deliberately signed, including at antipodal quaternions.
    private const double LinearThreshold = 1 - 2.220e-16;
    private const int MaximumSamples = 1_000_000;
    private const int MaximumFileBytes = 64 * 1024 * 1024;
    private readonly double[] _seconds;
    private readonly RigidQuaternion[] _orientations;

    private SampledMissionAttitude(double[] seconds, RigidQuaternion[] orientations)
    {
        _seconds = seconds;
        _orientations = orientations;
    }

    public static SampledMissionAttitude Read(string path, bool legacyAxes = false, CancellationToken cancellation = default) =>
        AddonDiagnostics.Read(AddonStage.Trajectory, path, () =>
        {
            using var file = AddonDependencies.OpenRead(path, MaximumFileBytes);
            using var reader = new StreamReader(file);
            return Parse(reader.ReadToEnd(), legacyAxes, cancellation);
        });

    public FrameKinematics Sample(JulianInstant epoch)
    {
        if (epoch.Scale != TimeScale.Tdb)
            throw new ArgumentException("Mission attitude requires TDB.", nameof(epoch));
        double seconds = Seconds(epoch.ToJulianDate());
        int upper = LowerBound(seconds);
        RigidQuaternion q = _orientations.Length == 0 ? new(1, 0, 0, 0) : upper == 0 ? _orientations[0] :
            upper == _seconds.Length ? _orientations[^1] :
            Interpolate(_orientations[upper - 1], _orientations[upper], (seconds - _seconds[upper - 1]) / (_seconds[upper] - _seconds[upper - 1]));
        int lower = Math.Clamp(upper - 1, 0, Math.Max(0, _seconds.Length - 2));
        Vector3d rate = _seconds.Length > 1 ? RateBetween(lower) : default;
        return new(QuaternionBasis.FromRaw(q.W, q.X, q.Y, q.Z), rate);
    }

    private static SampledMissionAttitude Parse(string text, bool legacyAxes, CancellationToken cancellation)
    {
        var seconds = new List<double>();
        var orientations = new List<RigidQuaternion>();
        var quarterTurn = new RigidQuaternion(Math.Cos(Math.PI / 4), Math.Sin(Math.PI / 4), 0, 0);
        Span<double> row = stackalloc double[5];
        int field = 0, at = 0;
        while (at < text.Length)
        {
            if (char.IsWhiteSpace(text[at]))
            {
                at++;
                continue;
            }
            if (text[at] == '#')
            {
                while (at < text.Length && text[at] != '\n')
                    at++;
                continue;
            }
            int start = at;
            while (at < text.Length && !char.IsWhiteSpace(text[at]) && text[at] != '#')
                at++;
            if (!double.TryParse(text.AsSpan(start, at - start), NumberStyles.Float, CultureInfo.InvariantCulture, out double value) ||
                !double.IsFinite(value))
                throw new InvalidDataException($"Invalid mission attitude number at character {start}.");
            row[field++] = value;
            if (field != row.Length)
                continue;
            field = 0;
            if ((seconds.Count & 4095) == 0)
                cancellation.ThrowIfCancellationRequested();
            double time = Seconds(row[0]);
            if (!double.IsFinite(time) || (seconds.Count > 0 && time < seconds[^1]))
                throw new InvalidDataException("Mission attitude epochs must be finite and ordered.");
            if (seconds.Count == MaximumSamples)
                throw new InvalidDataException("The mission attitude has too many samples.");
            double w = row[1], x = row[2], y = row[3], z = row[4];
            double norm = Math.Sqrt(((x * x + y * y) + z * z) + w * w);
            if (!(norm > 0) || !double.IsFinite(norm))
                throw new InvalidDataException("Mission attitude has a zero or nonfinite quaternion norm.");
            var turn = new RigidQuaternion(w / norm, x / norm, y / norm, z / norm);
            orientations.Add(legacyAxes ? Conjugate(Product(quarterTurn, turn)) : turn);
            seconds.Add(time);
        }
        if (field != 0)
            throw new InvalidDataException("Incomplete sampled mission attitude record.");

        var track = new SampledMissionAttitude(seconds.ToArray(), orientations.ToArray());
        for (int lower = 0; lower + 1 < seconds.Count; lower++)
        {
            if ((lower & 4095) == 0)
                cancellation.ThrowIfCancellationRequested();
            if ((lower == 0 || lower == seconds.Count - 2 || seconds[lower + 1] > seconds[lower]) &&
                !track.RateBetween(lower).IsFinite)
                throw new InvalidDataException($"Mission attitude angular velocity is undefined at record {lower + 2}.");
        }
        return track;
    }

    private static double Seconds(double julianDate) => (julianDate - JulianInstant.J2000Day) * JulianInstant.SecondsPerDay;

    private int LowerBound(double seconds)
    {
        int low = 0, high = _seconds.Length;
        while (low < high)
        {
            int middle = low + (high - low) / 2;
            if (_seconds[middle] < seconds)
                low = middle + 1;
            else
                high = middle;
        }
        return low;
    }

    private static RigidQuaternion Interpolate(RigidQuaternion first, RigidQuaternion last, double fraction)
    {
        double dot = ((first.X * last.X + first.Y * last.Y) + first.Z * last.Z) + first.W * last.W;
        double cosine = Math.Abs(dot);
        double a = 1 - fraction, b = fraction;
        if (cosine < LinearThreshold)
        {
            double theta = Math.Acos(cosine), sine = Math.Sin(theta);
            a = Math.Sin(a * theta) / sine;
            b = Math.Sin(b * theta) / sine;
            if (dot < 0)
                b = -b;
        }
        return new(a * first.W + b * last.W, a * first.X + b * last.X,
            a * first.Y + b * last.Y, a * first.Z + b * last.Z);
    }

    private Vector3d RateBetween(int lower)
    {
        RigidQuaternion delta = Product(_orientations[lower + 1], Conjugate(_orientations[lower]));
        if (Math.Abs(delta.W) > LinearThreshold)
            return default;
        double length = Math.Sqrt((delta.X * delta.X + delta.Y * delta.Y) + delta.Z * delta.Z);
        double speed = 2 * Math.Acos(delta.W) / (_seconds[lower + 1] - _seconds[lower]);
        return new(delta.X / length * speed, delta.Y / length * speed, delta.Z / length * speed);
    }

    private static RigidQuaternion Conjugate(RigidQuaternion q) => new(q.W, -q.X, -q.Y, -q.Z);

    private static RigidQuaternion Product(RigidQuaternion a, RigidQuaternion b) => new(
        a.W * b.W - a.X * b.X - a.Y * b.Y - a.Z * b.Z,
        a.W * b.X + a.X * b.W + a.Y * b.Z - a.Z * b.Y,
        a.W * b.Y + a.Y * b.W + a.Z * b.X - a.X * b.Z,
        a.W * b.Z + a.Z * b.W + a.X * b.Y - a.Y * b.X);
}
