// SPDX-License-Identifier: GPL-2.0-or-later
using SpaceTangent.Contracts;
using SpaceTangent.Core.Spice;

namespace SpaceTangent.Core.Ephemerides;

/// <summary>Uses the exact kernel where available and joins to a fallback across each coverage boundary.</summary>
public sealed class KernelBackedTrajectory
{
    private readonly SpkKernel _kernel;
    private readonly int _target, _center;
    private readonly Func<JulianInstant, CartesianState> _fallback;
    private readonly double _transitionSeconds;
    private readonly (Boundary Start, Boundary End)[] _coverage;

    private sealed record Boundary(JulianInstant Epoch, Lazy<CartesianState> Difference);

    public KernelBackedTrajectory(SpkKernel kernel, int target, int center, Func<JulianInstant, CartesianState> fallback,
        double transitionSeconds)
    {
        ArgumentNullException.ThrowIfNull(kernel);
        ArgumentNullException.ThrowIfNull(fallback);
        if (!double.IsFinite(transitionSeconds) || transitionSeconds <= 0)
            throw new ArgumentOutOfRangeException(nameof(transitionSeconds));
        _kernel = kernel;
        _target = target;
        _center = center;
        _fallback = fallback;
        _transitionSeconds = transitionSeconds;
        _coverage = kernel.GetCoverage(target, center).Select(interval => (At(interval.Start), At(interval.End))).ToArray();

        Boundary At(JulianInstant epoch) => new(epoch, new(() =>
        {
            if (!kernel.TryState(target, center, epoch, out CartesianState state))
                throw new InvalidDataException("A kernel coverage boundary does not contain its recorded state.");
            return state - fallback(epoch);
        }));
    }

    public CartesianState Sample(JulianInstant epoch)
    {
        if (_kernel.TryState(_target, _center, epoch, out CartesianState exact))
            return exact;
        CartesianState state = _fallback(epoch);
        Boundary? earlier = null, later = null;
        foreach (var (start, end) in _coverage)
        {
            if (epoch.CompareTo(start.Epoch) < 0)
            {
                later = start;
                break;
            }
            earlier = end;
        }
        double past = earlier is null ? double.PositiveInfinity : SecondsBetween(epoch, earlier.Epoch);
        double future = later is null ? double.PositiveInfinity : SecondsBetween(later.Epoch, epoch);
        double limit = Math.Min(_transitionSeconds, (past + future) / 2);
        double signed = past <= future ? past : -future;
        if (Math.Abs(signed) >= limit)
            return state;

        CartesianState delta = (past <= future ? earlier : later)!.Difference.Value;
        return ApplyBoundaryCorrection(state, delta, signed, limit);
    }

    internal static CartesianState ApplyBoundaryCorrection(CartesianState approximate, CartesianState delta,
        double signedSeconds, double transitionSeconds)
    {
        double direction = Math.Sign(signedSeconds);
        double fraction = Math.Abs(signedSeconds) / transitionSeconds;
        double left = 1 - fraction;
        double positionWeight = (1 + 2 * fraction) * left * left;
        double velocityWeight = direction * transitionSeconds * fraction * left * left;
        double positionRate = direction * (-6 * fraction * left) / transitionSeconds;
        double velocityRate = left * (1 - 3 * fraction);
        return new(approximate.PositionKilometers + delta.PositionKilometers * positionWeight +
            delta.VelocityKilometersPerSecond * velocityWeight,
            approximate.VelocityKilometersPerSecond + delta.PositionKilometers * positionRate +
            delta.VelocityKilometersPerSecond * velocityRate);
    }

    private static double SecondsBetween(JulianInstant later, JulianInstant earlier) =>
        (double)later.DifferenceNanoseconds(earlier) / JulianInstant.NanosecondsPerSecond;
}
