// SPDX-License-Identifier: GPL-2.0-or-later
using SpaceTangent.Contracts;
using SpaceTangent.Core.World;

namespace SpaceTangent.Core.Addons;

public sealed record MissionObservation(ObjectId Target, ObjectId Sensor, IReadOnlyList<MissionObservationGroup> Groups,
    int AlongTrackDivisions, Vector3d Color, double Opacity, bool ByResolution);

public readonly record struct MissionObservationGroup(JulianInstant Start, JulianInstant End, double RateSeconds);

public sealed record MissionFootprint(JulianInstant Epoch, int Group, bool Swath, IReadOnlyList<Vector3d> Points,
    double MeanRangeKilometers);

public static class MissionFootprints
{
    public const int MaximumPerGroup = 2000;

    public static int SampleCount(MissionObservation observation) =>
        observation.Groups.Sum(group => Sampling(group, observation.AlongTrackDivisions).Count);

    /// <summary>Samples past footprints in the rotating target's own axes. The entire field must land within range.</summary>
    public static IReadOnlyList<MissionFootprint> Calculate(MissionObservation observation, SampledMission sensorTrack,
        Vector3d axes, WorldGraph world, CancellationToken cancellation = default)
    {
        if (sensorTrack.Id != observation.Sensor)
            throw new ArgumentException("The observation must refer to the supplied sensor.", nameof(sensorTrack));
        if (sensorTrack.Geometry is not { Count: > 0 } spans)
            return [];
        var output = new List<MissionFootprint>();
        for (int group = 0; group < observation.Groups.Count; group++)
        {
            var item = observation.Groups[group];
            bool swath = item.RateSeconds == 0;
            var (count, step) = Sampling(item, observation.AlongTrackDivisions);
            for (int index = 0; index < count; index++)
            {
                cancellation.ThrowIfCancellationRequested();
                var epoch = item.Start.AddSeconds(step * index);
                var active = spans.FirstOrDefault(span =>
                    span.Start.CompareTo(epoch) <= 0 && epoch.CompareTo(span.End) < 0)?.Shape.Sensor;
                if (active is null)
                    continue;
                var snapshot = world.Evaluate(epoch);
                if (!snapshot.TryState(sensorTrack.Id, out var craft) ||
                    !snapshot.TryOrientation(sensorTrack.Id, out var sensorFrame) ||
                    !snapshot.TryState(observation.Target, out var target) ||
                    !snapshot.TryOrientation(observation.Target, out var targetFrame))
                    continue;
                Basis3d toFixed = targetFrame.ToWorld.Transpose();
                Vector3d origin = toFixed.Transform((craft - target).PositionKilometers);
                var points = new Vector3d[active.Boundary.Count];
                int hits = 0;
                double totalRange = 0;
                foreach (Vector3d local in active.Boundary)
                {
                    Vector3d ray = toFixed.Transform(sensorFrame.ToWorld.Transform(local));
                    if (!MissionSensorProjection.TryCrossing(origin, ray, axes, out double range) ||
                        range > active.RangeKilometers)
                        break;
                    points[hits++] = origin + ray * range;
                    totalRange += range;
                }
                if (hits == points.Length)
                    output.Add(new(epoch, group, swath, Array.AsReadOnly(points), totalRange / hits));
            }
        }
        return Array.AsReadOnly(output.OrderBy(point => point.Epoch).ToArray());
    }

    private static (int Count, double Step) Sampling(MissionObservationGroup item, int alongTrackDivisions)
    {
        bool swath = item.RateSeconds == 0;
        double duration = (double)item.End.DifferenceNanoseconds(item.Start) / JulianInstant.NanosecondsPerSecond;
        double natural = swath ? (duration == 0 ? 1 : alongTrackDivisions) : Math.Floor(duration / item.RateSeconds) + 1;
        int count = (int)Math.Min(natural, MaximumPerGroup);
        double step = count < 2 ? 0 : swath || natural > count ? duration / (count - 1) : item.RateSeconds;
        return (count, step);
    }
}
