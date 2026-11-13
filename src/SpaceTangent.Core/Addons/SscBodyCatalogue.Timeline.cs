// SPDX-License-Identifier: GPL-2.0-or-later
using System.Globalization;
using SpaceTangent.Contracts;
using SpaceTangent.Core.Orbits;
using SpaceTangent.Core.Time;
using SpaceTangent.Core.World;

namespace SpaceTangent.Core.Addons;

public static partial class SscBodyCatalogue
{
    private static readonly Basis3d TimelineEcliptic = new(new(1, 0, 0),
        SscSampledSpacecraft.ToEquatorial(new(0, 1, 0)), SscSampledSpacecraft.ToEquatorial(new(0, 0, 1)));
    private static readonly JulianInstant EarliestTimelineEpoch = new(0, 0, TimeScale.Tdb);
    private static readonly JulianInstant LatestTimelineEpoch =
        new(JulianInstant.MaximumDay, JulianInstant.NanosecondsPerDay - 1, TimeScale.Tdb);

    private static IReadOnlyList<Func<Func<string, ObjectId>, MissionArc>> ReadTimeline(SscDefinition item, SscValue value,
        SscResource resolve, CancellationToken cancellation)
    {
        if (value is not SscValue.List { Items.Length: >= 1 and <= 128 } phases)
            throw new InvalidDataException("An SSC Timeline needs one to 128 phases.");

        var curves = new Dictionary<string, SampledStateTrajectory>(OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        long totalBytes = 0;
        var result = new List<Func<Func<string, ObjectId>, MissionArc>>(phases.Items.Length);
        var previousEnd = EarliestTimelineEpoch;
        for (int index = 0; index < phases.Items.Length; index++)
        {
            cancellation.ThrowIfCancellationRequested();
            if (phases.Items[index] is not SscValue.Map phase)
                throw new InvalidDataException("SSC Timeline phases must be objects.");
            var start = previousEnd;
            if (phase.Fields.TryGetValue("Beginning", out var begins))
            {
                if (index != 0)
                    throw new InvalidDataException("Only the first SSC Timeline phase may declare Beginning.");
                start = ReadTimelineDate(begins.AsText());
            }
            var end = phase.Fields.TryGetValue("Ending", out var ends) ? ReadTimelineDate(ends.AsText()) :
                index == phases.Items.Length - 1 ? LatestTimelineEpoch :
                throw new InvalidDataException("A nonfinal SSC Timeline phase requires Ending.");
            if (end.CompareTo(start) <= 0)
                throw new InvalidDataException("SSC Timeline phases must have increasing bounds.");
            previousEnd = end;

            string center = item.Parent;
            MissionFrameFactory trajectory = _ => new MissionFrame.Inertial(
                center.Equals("Sol", StringComparison.OrdinalIgnoreCase) ? TimelineEcliptic : Basis3d.Identity);
            if (phase.Fields.ContainsKey("OrbitFrame"))
                trajectory = ReadTimelineFrame(phase.Object("OrbitFrame"), center, out center);

            var state = ReadTimelineState(phase, center, out double? orbitPeriodSeconds);
            MissionFrameFactory bodyFrame = _ => new MissionFrame.Inertial(TimelineEcliptic);
            if (phase.Fields.ContainsKey("BodyFrame"))
                bodyFrame = ReadTimelineFrame(phase.Object("BodyFrame"), item.Parent, out _);

            Func<JulianInstant, FrameKinematics?>? spin = null;
            if (phase.Fields.ContainsKey("PrecessingRotation"))
                spin = ReadUniformSpin(phase.Object("PrecessingRotation"), Basis3d.Identity, orbitPeriodSeconds, precessing: true);
            else if (phase.Fields.ContainsKey("UniformRotation"))
                spin = ReadUniformSpin(phase.Object("UniformRotation"), Basis3d.Identity, orbitPeriodSeconds);
            else if (phase.Fields.ContainsKey("FixedRotation"))
                spin = ReadUniformSpin(phase.Object("FixedRotation"), Basis3d.Identity, null, stationary: true);
            if (phase.Fields.Keys.Any(key => key is "CustomRotation" or "SpiceRotation" or "ScriptedRotation"
                or "SampledOrientation" or "FixedAttitude"))
                throw new NotSupportedException("This SSC Timeline rotation model is unavailable.");

            result.Add(find =>
            {
                MissionFrame body = bodyFrame(find);
                return new(start, end, find(center), state, trajectory(find),
                    spin is null ? body : new MissionFrame.Rotating(body, epoch => spin(epoch)!.Value))
                {
                    BodyReferenceFrame = body,
                    UsePhysicalBodyFixedVelocity = true
                };
            });
        }
        return result.AsReadOnly();

        Func<JulianInstant, CartesianState?> ReadTimelineState(SscValue.Map phase, string center, out double? orbitPeriodSeconds)
        {
            orbitPeriodSeconds = null;
            if (phase.Fields.Keys.Any(key => key is "CustomOrbit" or "SpiceOrbit" or "ScriptedOrbit" or "LongLat"))
                throw new NotSupportedException("This SSC Timeline orbit model is unavailable.");
            if (phase.Fields.TryGetValue("SampledTrajectory", out var data))
            {
                if (data is not SscValue.Map source ||
                    (source.Fields.TryGetValue("Interpolation", out var interpolation) && interpolation.AsText() != "cubic") ||
                    (source.Fields.TryGetValue("DoublePrecision", out var precision) && precision.AsText() != "true"))
                    throw new NotSupportedException("This SSC Timeline needs cubic double-precision samples.");
                string name = source.Text("Source");
                string path = resolve(item, "Timeline", AddonStage.Trajectory, name, Path.Combine("models", "trajectories"), "data", "trajectories");
                if (!curves.TryGetValue(path, out var curve))
                {
                    totalBytes += new FileInfo(path).Length;
                    if (totalBytes > 128L * 1024 * 1024)
                        throw new InvalidDataException("SSC Timeline trajectories exceed 128 MiB.");
                    curve = Path.GetExtension(path).ToLowerInvariant() switch
                    {
                        ".xyzv" => SampledStateTrajectory.ReadXyzv(path, Icrs),
                        ".xyz" => SampledStateTrajectory.ReadXyz(path, Icrs),
                        _ => throw new NotSupportedException("An SSC Timeline needs XYZ or XYZV samples.")
                    };
                    if (curve.Count == 0)
                        throw new InvalidDataException("An SSC Timeline trajectory has no samples.");
                    curves.Add(path, curve);
                }
                return epoch => curve.TrySample(epoch, out var sample) ? sample : null;
            }
            if (phase.Fields.TryGetValue("EllipticalOrbit", out var orbit))
            {
                bool heliocentric = center.Equals("Sol", StringComparison.OrdinalIgnoreCase);
                var context = new SscOrbitContext(Icrs, heliocentric ? AuKilometers : 1, heliocentric ? 31_557_600 : 86_400);
                var conic = SscConicOrbits.ReadOrbit(item, orbit, context).Orbit;
                orbitPeriodSeconds = Number(((SscValue.Map)orbit).Get("Period")) * context.SecondsPerPeriodUnit;
                return epoch => conic.Evaluate(epoch);
            }
            if (phase.Fields.TryGetValue("FixedPosition", out var fixedPosition))
            {
                if (fixedPosition is SscValue.Map coordinates)
                    fixedPosition = coordinates.Get("Rectangular");
                var state = new CartesianState(ReadVector(fixedPosition), default);
                return _ => state;
            }
            throw new NotSupportedException("This SSC Timeline has no supported trajectory.");
        }
    }

    private delegate MissionFrame MissionFrameFactory(Func<string, ObjectId> resolve);

    private static MissionFrameFactory ReadTimelineFrame(SscValue.Map frame, string defaultCenter, out string center)
    {
        if (frame.Fields.Count != 1 || frame.Fields.Single() is not (var kind, SscValue.Map parameters))
            throw new InvalidDataException("An SSC Timeline frame requires a single definition.");
        center = parameters.Fields.TryGetValue("Center", out var origin) ? origin.AsText() : defaultCenter;
        string reference = center;
        switch (kind)
        {
            case "EclipticJ2000":
                return _ => new MissionFrame.Inertial(TimelineEcliptic);
            case "EquatorJ2000":
                return _ => new MissionFrame.Inertial(Basis3d.Identity);
            case "BodyFixed":
                return find => new MissionFrame.BodyFixed(find(reference));
            case "TwoVector" when HasConstantAxes(parameters):
                var basis = SscConstantOrbitFrame.Read(parameters);
                return _ => new MissionFrame.Inertial(basis);
            case "TwoVector":
                var primary = ReadAxis(parameters.Object("Primary"));
                var secondary = ReadAxis(parameters.Object("Secondary"));
                return find => new MissionFrame.TwoVector(primary.Axis, Direction(primary, find), secondary.Axis, Direction(secondary, find));
            default:
                throw new NotSupportedException($"Unsupported SSC Timeline frame {kind}.");
        }

        static RelativeDirection Direction(SscBodyAxis axis, Func<string, ObjectId> find) =>
            new(find(axis.Observer), find(axis.Target), axis.Quantity);
    }

    private static JulianInstant ReadTimelineDate(string text)
    {
        var epoch = SscSampledCatalog.ReadDate(text);
        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
            return epoch;
        DateTime date = DateTime.UnixEpoch.AddTicks((long)(epoch.DifferenceNanoseconds(
            JulianInstant.FromJulianDate(2440587.5, TimeScale.Tdb)) / 100));
        DateOnly day = DateOnly.FromDateTime(date);
        if (day < LeapSecondTable.FirstDate)
        {
            var tt = new JulianInstant(epoch.Day, epoch.NanosecondsOfDay, TimeScale.Tt);
            return TimeConversion.ApproximateTdbForEarth(tt.AddSeconds(DeltaT.Seconds(tt, DeltaTModel.EspenakMeeus2006)));
        }
        var utc = new UtcDateTime(day, date.Hour, date.Minute, date.Second, (int)(date.Ticks % TimeSpan.TicksPerSecond) * 100);
        return TimeConversion.ApproximateTdbForEarth(TimeConversion.FromUtc(utc, TimeScale.Tt));
    }
}
