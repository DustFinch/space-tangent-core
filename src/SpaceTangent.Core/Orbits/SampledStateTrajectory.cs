// SPDX-License-Identifier: GPL-2.0-or-later
using System.Globalization;
using SpaceTangent.Core.Addons;
using System.Text;
using SpaceTangent.Contracts;

namespace SpaceTangent.Core.Orbits;

public enum SampledEndpointPolicy { Reject, ClampState }
public enum SampledClockPolicy { ExactNanoseconds, SourceJulianDouble }
public readonly record struct TimedState(JulianInstant Epoch, CartesianState State);

/// <summary>A sampled position/velocity curve with explicit clock and endpoint policies.</summary>
public sealed class SampledStateTrajectory
{
    private const int MaximumSamples = 1_000_000;
    private readonly TimedState[] _samples;
    private readonly double[] _sourceSeconds;
    private readonly SampledEndpointPolicy _endpoints;
    private readonly SampledClockPolicy _clock;

    public SampledStateTrajectory(IEnumerable<TimedState> samples, FrameId frame,
        SampledEndpointPolicy endpoints = SampledEndpointPolicy.Reject,
        SampledClockPolicy clock = SampledClockPolicy.ExactNanoseconds)
        : this(samples.Take(MaximumSamples + 1).ToArray(), null, frame, endpoints, clock)
    {
    }

    private SampledStateTrajectory(TimedState[] samples, double[]? sourceSeconds, FrameId frame,
        SampledEndpointPolicy endpoints, SampledClockPolicy clock)
    {
        if (frame == default || endpoints is < SampledEndpointPolicy.Reject or > SampledEndpointPolicy.ClampState ||
            clock is < SampledClockPolicy.ExactNanoseconds or > SampledClockPolicy.SourceJulianDouble)
            throw new ArgumentException("Sampled trajectory policies and reference frame must be defined.");
        if (samples.Length > MaximumSamples || (samples.Length == 0 && clock != SampledClockPolicy.SourceJulianDouble))
            throw new ArgumentException("Invalid sampled trajectory count.", nameof(samples));
        _samples = samples;
        _sourceSeconds = clock == SampledClockPolicy.SourceJulianDouble
            ? sourceSeconds ?? samples.Select(sample => SourceSeconds(sample.Epoch)).ToArray()
            : [];
        _endpoints = endpoints;
        _clock = clock;
        Frame = frame;

        for (int i = 0; i < samples.Length; i++)
        {
            TimedState sample = samples[i];
            if (sample.Epoch.Scale != TimeScale.Tdb || !sample.State.PositionKilometers.IsFinite ||
                !sample.State.VelocityKilometersPerSecond.IsFinite)
                throw new ArgumentException("Samples require finite TDB states.", nameof(samples));
            if (i == 0)
                continue;
            bool ordered = clock == SampledClockPolicy.SourceJulianDouble
                ? _sourceSeconds[i] >= _sourceSeconds[i - 1]
                : sample.Epoch.CompareTo(samples[i - 1].Epoch) > 0;
            if (!ordered)
                throw new ArgumentException("Sample epochs are not ordered according to the clock policy.", nameof(samples));
        }
    }

    public FrameId Frame { get; }
    public int Count => _samples.Length;
    public JulianInstant? Start => _samples.Length == 0 ? null : _samples[0].Epoch;
    public JulianInstant? End => _samples.Length == 0 ? null : _samples[^1].Epoch;

    public static SampledStateTrajectory ReadXyzv(string path, FrameId frame) =>
        AddonDiagnostics.Read(AddonStage.Trajectory, path, () => ReadXyzvCore(path, frame));

    private static SampledStateTrajectory ReadXyzvCore(string path, FrameId frame)
    {
        using FileStream file = AddonDependencies.OpenRead(path, 64L * 1024 * 1024);
        if (file.Length > 64L * 1024 * 1024)
            throw new InvalidDataException("XYZV file exceeds 64 MiB.");
        using var reader = new StreamReader(file, new UTF8Encoding(false, true));
        var samples = new List<TimedState>();
        var seconds = new List<double>();
        double[] values = new double[7];
        int field = 0;
        while (reader.ReadLine() is { } line)
        {
            int comment = line.IndexOf('#');
            foreach (string token in (comment < 0 ? line : line[..comment])
                         .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            {
                if (!double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out double value) ||
                    !double.IsFinite(value))
                    throw new InvalidDataException("XYZV contains an invalid number.");
                values[field++] = value;
                if (field != 7)
                    continue;
                if (samples.Count == MaximumSamples)
                    throw new InvalidDataException("XYZV contains too many samples.");
                samples.Add(new(JulianInstant.FromJulianDate(values[0], TimeScale.Tdb),
                    new(new(values[1], values[2], values[3]), new(values[4], values[5], values[6]))));
                seconds.Add((values[0] - JulianInstant.J2000Day) * JulianInstant.SecondsPerDay);
                field = 0;
            }
        }
        if (field != 0)
            throw new InvalidDataException("XYZV contains an incomplete sample.");
        return new([.. samples], [.. seconds], frame, SampledEndpointPolicy.ClampState, SampledClockPolicy.SourceJulianDouble);
    }

    public bool TrySample(JulianInstant epoch, out CartesianState state)
    {
        if (epoch.Scale != TimeScale.Tdb)
            throw new ArgumentException("Sampled trajectories require TDB.", nameof(epoch));
        state = default;
        if (_samples.Length == 0)
            return _clock == SampledClockPolicy.SourceJulianDouble;

        double sourceTime = _clock == SampledClockPolicy.SourceJulianDouble ? SourceSeconds(epoch) : 0;
        int Comparison(int index) => _clock == SampledClockPolicy.SourceJulianDouble
            ? sourceTime.CompareTo(_sourceSeconds[index])
            : epoch.CompareTo(_samples[index].Epoch);

        int first = Comparison(0), last = Comparison(_samples.Length - 1);
        if ((first < 0 || last > 0) && _endpoints == SampledEndpointPolicy.Reject)
            return false;
        if (first <= 0)
        {
            state = _samples[0].State;
            return true;
        }
        if (last > 0)
        {
            state = _samples[^1].State;
            return true;
        }

        int lower = 0, upper = _samples.Length - 1;
        while (lower < upper)
        {
            int middle = lower + (upper - lower) / 2;
            if (Comparison(middle) > 0)
                lower = middle + 1;
            else
                upper = middle;
        }
        if (Comparison(lower) == 0)
        {
            state = _samples[lower].State;
            return true;
        }

        int previous = lower - 1;
        double duration, elapsed;
        if (_clock == SampledClockPolicy.SourceJulianDouble)
        {
            duration = _sourceSeconds[lower] - _sourceSeconds[previous];
            elapsed = sourceTime - _sourceSeconds[previous];
        }
        else
        {
            duration = (double)_samples[lower].Epoch.DifferenceNanoseconds(_samples[previous].Epoch) /
                JulianInstant.NanosecondsPerSecond;
            elapsed = (double)epoch.DifferenceNanoseconds(_samples[previous].Epoch) / JulianInstant.NanosecondsPerSecond;
        }
        state = Interpolate(_samples[previous].State, _samples[lower].State, duration, elapsed / duration);
        return state.PositionKilometers.IsFinite && state.VelocityKilometersPerSecond.IsFinite;
    }

    private static CartesianState Interpolate(CartesianState first, CartesianState last, double duration, double t)
    {
        double t2 = t * t, t3 = t2 * t;
        Vector3d v0 = first.VelocityKilometersPerSecond * duration;
        Vector3d v1 = last.VelocityKilometersPerSecond * duration;
        Vector3d displacement = last.PositionKilometers - first.PositionKilometers;
        Vector3d position = first.PositionKilometers + displacement * (-2 * t3 + 3 * t2) +
            v0 * (t3 - 2 * t2 + t) + v1 * (t3 - t2);
        Vector3d velocity = (displacement * (-6 * t2 + 6 * t) + v0 * (3 * t2 - 4 * t + 1) +
            v1 * (3 * t2 - 2 * t)) / duration;
        return new(position, velocity);
    }

    private static double SourceSeconds(JulianInstant epoch) =>
        (epoch.ToJulianDate() - JulianInstant.J2000Day) * JulianInstant.SecondsPerDay;
}
