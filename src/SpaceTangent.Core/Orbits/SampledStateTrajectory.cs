// SPDX-License-Identifier: GPL-2.0-or-later
using System.Globalization;
using SpaceTangent.Core.Addons;
using System.Text;
using SpaceTangent.Contracts;

namespace SpaceTangent.Core.Orbits;

public enum SampledEndpointPolicy { Reject, ClampState, ClampPosition }
public enum SampledClockPolicy { ExactNanoseconds, SourceJulianDouble, JulianDays }
public readonly record struct TimedState(JulianInstant Epoch, CartesianState State);

/// <summary>A sampled position/velocity curve with explicit clock and endpoint policies.</summary>
public sealed class SampledStateTrajectory
{
    private const int MaximumSamples = 1_000_000;
    private readonly TimedState[] _samples;
    private readonly double[] _sourceTimes;
    private readonly SampledEndpointPolicy _endpoints;
    private readonly SampledClockPolicy _clock;

    public SampledStateTrajectory(IEnumerable<TimedState> samples, FrameId frame,
        SampledEndpointPolicy endpoints = SampledEndpointPolicy.Reject,
        SampledClockPolicy clock = SampledClockPolicy.ExactNanoseconds)
        : this(samples.Take(MaximumSamples + 1).ToArray(), null, frame, endpoints, clock)
    {
    }

    private SampledStateTrajectory(TimedState[] samples, double[]? sourceTimes, FrameId frame,
        SampledEndpointPolicy endpoints, SampledClockPolicy clock)
    {
        if (frame == default || endpoints is < SampledEndpointPolicy.Reject or > SampledEndpointPolicy.ClampPosition ||
            clock is < SampledClockPolicy.ExactNanoseconds or > SampledClockPolicy.JulianDays)
            throw new ArgumentException("Sampled trajectory policies and reference frame must be defined.");
        if (samples.Length > MaximumSamples || (samples.Length == 0 && clock != SampledClockPolicy.SourceJulianDouble))
            throw new ArgumentException("Invalid sampled trajectory count.", nameof(samples));
        _samples = samples;
        _sourceTimes = clock switch
        {
            SampledClockPolicy.SourceJulianDouble => sourceTimes ?? samples.Select(sample => SourceSeconds(sample.Epoch)).ToArray(),
            SampledClockPolicy.JulianDays => sourceTimes ?? samples.Select(sample => sample.Epoch.ToJulianDate()).ToArray(),
            _ => []
        };
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
            bool ordered = clock switch
            {
                SampledClockPolicy.SourceJulianDouble => _sourceTimes[i] >= _sourceTimes[i - 1],
                SampledClockPolicy.JulianDays => _sourceTimes[i] > _sourceTimes[i - 1],
                _ => sample.Epoch.CompareTo(samples[i - 1].Epoch) > 0
            };
            if (!ordered)
                throw new ArgumentException("Sample epochs are not ordered according to the clock policy.", nameof(samples));
        }
    }

    public FrameId Frame { get; }
    public int Count => _samples.Length;
    public JulianInstant? Start => _samples.Length == 0 ? null : _samples[0].Epoch;
    public JulianInstant? End => _samples.Length == 0 ? null : _samples[^1].Epoch;

    public static SampledStateTrajectory ReadXyzv(string path, FrameId frame) =>
        AddonDiagnostics.Read(AddonStage.Trajectory, path, () => ReadCoordinates(path, frame, positionsOnly: false));

    public static SampledStateTrajectory ReadXyz(string path, FrameId frame) =>
        AddonDiagnostics.Read(AddonStage.Trajectory, path, () => ReadCoordinates(path, frame, positionsOnly: true));

    internal static SampledStateTrajectory ReadLegacy(string path, FrameId frame) =>
        AddonDiagnostics.Read(AddonStage.Trajectory, path, () => Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".xyz" => ReadCoordinates(path, frame, positionsOnly: true, legacy: true),
            ".xyzv" => ReadCoordinates(path, frame, positionsOnly: false, legacy: true),
            _ => throw new NotSupportedException("An SSC SampledOrbit needs XYZ or XYZV samples.")
        });

    private static SampledStateTrajectory ReadCoordinates(string path, FrameId frame, bool positionsOnly, bool legacy = false)
    {
        using FileStream file = AddonDependencies.OpenRead(path, 64L * 1024 * 1024);
        if (file.Length > 64L * 1024 * 1024)
            throw new InvalidDataException("Sampled trajectory exceeds 64 MiB.");
        using var reader = new StreamReader(file, new UTF8Encoding(false, true));
        var samples = new List<TimedState>();
        var times = new List<double>();
        double[] values = new double[positionsOnly ? 4 : 7];
        int field = 0;
        while (reader.ReadLine() is { } line)
        {
            int comment = line.IndexOf('#');
            string[] tokens = (comment < 0 ? line : line[..comment])
                .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (legacy && tokens.Length != 0 && tokens.Length != values.Length)
                throw new InvalidDataException("Each legacy trajectory line requires a complete sample.");
            foreach (string token in tokens)
            {
                values[field] = ReadValue(token, field, legacy);
                field++;
                if (field != values.Length)
                    continue;
                if (samples.Count == MaximumSamples)
                    throw new InvalidDataException("A trajectory contains too many samples.");
                samples.Add(new(JulianInstant.FromJulianDate(values[0], TimeScale.Tdb),
                    new(new(values[1], values[2], values[3]), positionsOnly ? default : new(values[4], values[5], values[6]))));
                times.Add(legacy ? values[0] : (values[0] - JulianInstant.J2000Day) * JulianInstant.SecondsPerDay);
                field = 0;
            }
        }
        if (field != 0)
            throw new InvalidDataException("A sampled trajectory contains an incomplete sample.");
        if (legacy && samples.Count == 0)
            throw new InvalidDataException("The legacy trajectory has no samples.");
        if (positionsOnly && samples.Count != 0)
        {
            if (!legacy && samples.Count < 2)
                throw new InvalidDataException("A nonempty XYZ file requires at least two positions.");
            if (samples.Count > 1)
                DeriveVelocities(samples, times, legacy ? JulianInstant.SecondsPerDay : 1);
        }
        return new([.. samples], [.. times], frame,
            legacy ? SampledEndpointPolicy.ClampPosition : SampledEndpointPolicy.ClampState,
            legacy ? SampledClockPolicy.JulianDays : SampledClockPolicy.SourceJulianDouble);
    }

    private static double ReadValue(string token, int field, bool legacy)
    {
        if (legacy && field != 0)
        {
            if (float.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out float value))
            {
                if (field >= 4)
                    value *= 86_400f; // Legacy velocities are stored as float kilometers per day.
                if (float.IsFinite(value))
                    return field >= 4 ? value / 86_400d : value;
            }
        }
        else if (double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out double value) && double.IsFinite(value))
            return value;

        throw new InvalidDataException("A sampled trajectory contains an invalid number.");
    }

    private static void DeriveVelocities(List<TimedState> samples, List<double> times, double secondsPerUnit)
    {
        Vector3d next = Slope(1);
        for (int i = 0; i < samples.Count; i++)
        {
            Vector3d previous = next;
            if (i > 0 && i < samples.Count - 1)
                next = Slope(i + 1);
            var state = samples[i].State with { VelocityKilometersPerSecond = previous * .5 + next * .5 };
            samples[i] = samples[i] with { State = state };
        }

        Vector3d Slope(int later)
        {
            double duration = (times[later] - times[later - 1]) * secondsPerUnit;
            if (duration <= 0)
                throw new InvalidDataException("XYZ source times must increase.");
            Vector3d slope = (samples[later].State.PositionKilometers - samples[later - 1].State.PositionKilometers) / duration;
            if (!slope.IsFinite)
                throw new InvalidDataException("XYZ position differences produce a nonfinite velocity.");
            return slope;
        }
    }

    public bool TrySample(JulianInstant epoch, out CartesianState state)
    {
        if (epoch.Scale != TimeScale.Tdb)
            throw new ArgumentException("Sampled trajectories require TDB.", nameof(epoch));
        state = default;
        if (_samples.Length == 0)
            return _clock == SampledClockPolicy.SourceJulianDouble;

        double sourceTime = _clock switch
        {
            SampledClockPolicy.SourceJulianDouble => SourceSeconds(epoch),
            SampledClockPolicy.JulianDays => epoch.ToJulianDate(),
            _ => 0
        };
        int Comparison(int index) => _clock == SampledClockPolicy.ExactNanoseconds
            ? epoch.CompareTo(_samples[index].Epoch)
            : sourceTime.CompareTo(_sourceTimes[index]);

        int first = Comparison(0), last = Comparison(_samples.Length - 1);
        if ((first < 0 || last > 0) && _endpoints == SampledEndpointPolicy.Reject)
            return false;
        if (first <= 0)
        {
            state = Endpoint(0);
            return true;
        }
        if (last > 0)
        {
            state = Endpoint(_samples.Length - 1);
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
        double duration, fraction;
        if (_clock == SampledClockPolicy.ExactNanoseconds)
        {
            duration = (double)_samples[lower].Epoch.DifferenceNanoseconds(_samples[previous].Epoch) /
                JulianInstant.NanosecondsPerSecond;
            double elapsed = (double)epoch.DifferenceNanoseconds(_samples[previous].Epoch) / JulianInstant.NanosecondsPerSecond;
            fraction = elapsed / duration;
        }
        else
        {
            double interval = _sourceTimes[lower] - _sourceTimes[previous];
            duration = interval * (_clock == SampledClockPolicy.JulianDays ? JulianInstant.SecondsPerDay : 1);
            fraction = (sourceTime - _sourceTimes[previous]) / interval;
        }
        state = Interpolate(_samples[previous].State, _samples[lower].State, duration, fraction);
        return state.PositionKilometers.IsFinite && state.VelocityKilometersPerSecond.IsFinite;

        CartesianState Endpoint(int index) => _endpoints == SampledEndpointPolicy.ClampPosition
            ? new(_samples[index].State.PositionKilometers, default)
            : _samples[index].State;
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
