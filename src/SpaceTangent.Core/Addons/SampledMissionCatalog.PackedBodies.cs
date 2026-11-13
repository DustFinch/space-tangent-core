// SPDX-License-Identifier: GPL-2.0-or-later
using System.Text.Json;
using SpaceTangent.Contracts;
using SpaceTangent.Core.Coordinates;
using SpaceTangent.Core.Orbits;
using SpaceTangent.Core.World;

namespace SpaceTangent.Core.Addons;

public static partial class SampledMissionCatalog
{
    private static SampledMission ReadPackedBody(JsonElement body, Func<string, ObjectId> resolve,
        string root, Action<string>? warning, CancellationToken cancellation)
    {
        string name = Text(body, "name");
        ObjectId id = resolve(Text(body, "path"));
        string parent = Text(body, "parent");
        var lifetime = body.TryGetProperty("lifetime", out var given) ? given : default;
        JulianInstant start = lifetime.ValueKind == JsonValueKind.Object ? Date(lifetime, "beginningJdTdb", Minimum) : Minimum;
        JulianInstant end = lifetime.ValueKind == JsonValueKind.Object ? Date(lifetime, "endingJdTdb", Maximum) : Maximum;
        if (end.CompareTo(start) <= 0)
            throw new InvalidDataException("A packed body has an empty lifetime.");

        var spin = body.GetProperty("rotation");
        bool follows = Text(spin, "kind") == "follow";
        var reference = follows ? new MissionFrame.BodyFixed(resolve(Text(spin, "body"))) :
            PackedFrame(spin.GetProperty("frame"), resolve, id);
        MissionFrame attitude;
        if (body.TryGetProperty("bodyFrame", out var axes))
        {
            var dynamic = PackedFrame(axes, resolve, id);
            if (reference is not MissionFrame.Inertial inertial)
                throw new NotSupportedException("A packed rotation in a two-vector body frame needs inertial local axes.");
            var relative = Multiply(Ecliptic.ToWorld.Transpose(), inertial.ToWorld);
            var local = PackedRotation(spin, new MissionFrame.Inertial(relative), root, cancellation);
            attitude = new MissionFrame.Rotating(dynamic, epoch => EvaluateLocalRotation(local, epoch));
            reference = new MissionFrame.Rotating(dynamic, _ => new(relative, default(Vector3d)));
        }
        else
            attitude = PackedRotation(spin, reference, root, cancellation);

        var arcs = new List<MissionArc>();
        if (body.TryGetProperty("timeline", out var phases))
        {
            if (phases.GetArrayLength() is < 1 or > 128)
                throw new InvalidDataException("A packed body needs one to 128 timeline phases.");
            var previous = start;
            foreach (var phase in phases.EnumerateArray())
            {
                cancellation.ThrowIfCancellationRequested();
                var until = Date(phase, "endingJdTdb", end);
                if (until.CompareTo(previous) <= 0 || until.CompareTo(end) > 0)
                    throw new InvalidDataException("Packed timeline ends must increase within the body's lifetime.");
                AddArc(previous, until, Text(phase, "center"), phase.GetProperty("orbit"));
                previous = until;
            }
        }
        else
            AddArc(start, end, parent, body.GetProperty("orbit"));

        double radius = PackedPositive(body, "radiusKm");
        MissionShape? shape = ReadPackedShape(body, root, name, radius, resolve, warning, cancellation);
        var shown = body.TryGetProperty("shown", out var interval) ? interval : default;
        var visibleFrom = shown.ValueKind == JsonValueKind.Object ? Date(shown, "fromJdTdb", start) : start;
        var visibleTo = shown.ValueKind == JsonValueKind.Object ? Date(shown, "untilJdTdb", end) : end;
        if (visibleFrom.CompareTo(start) < 0)
            visibleFrom = start;
        if (visibleTo.CompareTo(end) > 0)
            visibleTo = end;
        return new(id, name, shape?.MeshPath, radius, arcs.AsReadOnly())
        {
            Geometry = shape is null || visibleTo.CompareTo(visibleFrom) <= 0 ? [] : [new(visibleFrom, visibleTo, shape)]
        };

        void AddArc(JulianInstant from, JulianInstant until, string center, JsonElement orbit)
        {
            var trajectory = PackedTrajectory(orbit, center, resolve, id, root);
            arcs.Add(new(from, until, resolve(center), trajectory.Local, trajectory.Frame, attitude)
            {
                BodyReferenceFrame = reference, NativeVelocitySource = trajectory.NativeVelocitySource,
                UsePhysicalBodyFixedVelocity = trajectory.PhysicalVelocity
            });
        }
    }

    private sealed record PackedOrbit(Func<JulianInstant, CartesianState?> Local, MissionFrame Frame,
        Basis3d? NativeVelocitySource = null, bool PhysicalVelocity = false);

    private static PackedOrbit PackedTrajectory(JsonElement orbit, string center, Func<string, ObjectId> resolve,
        ObjectId owner, string root, bool nested = false)
    {
        string kind = Text(orbit, "kind");
        if (kind == "movingFrame")
        {
            if (nested)
                throw new InvalidDataException("Packed moving frames cannot be nested.");
            var trajectory = orbit.GetProperty("trajectory");
            if (!center.Equals(Text(trajectory, "center"), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The packed moving trajectory changed its centre.");
            var local = PackedTrajectory(trajectory.GetProperty("orbit"), center, resolve, owner, root, nested: true);
            if (local.Frame is not MissionFrame.Inertial source)
                throw new NotSupportedException("A packed moving frame needs an inertial local trajectory.");
            ObjectId body = resolve(Text(orbit, "body"));
            var axes = LegacyBodyAxes(body, Quaternion(orbit.GetProperty("axes")));
            var fromLocal = Multiply(Ecliptic.ToWorld.Transpose(), source.ToWorld);
            var frame = new MissionFrame.BodyFixed(body) { LocalAxes = axes };
            string policy = orbit.TryGetProperty("velocityConvention", out var given) ? given.GetString()! : "physical";
            return policy switch
            {
                "cosmographiaEntity" => new(Sample, frame, PackedIcrf.ToWorld),
                "physical" => new(Sample, frame, PhysicalVelocity: true),
                _ => throw new NotSupportedException($"Unknown packed moving-frame velocity convention {policy}.")
            };

            CartesianState? Sample(JulianInstant epoch) => local.Local(epoch) is { } value
                ? new(fromLocal.Transform(value.PositionKilometers), fromLocal.Transform(value.VelocityKilometersPerSecond)) : null;
        }

        MissionFrame axesFrame = PackedFrame(orbit.GetProperty("frame"), resolve, owner);
        return new(kind switch
        {
            "fixed" => Constant(Vector(orbit.GetProperty("positionKm"))),
            "kepler" => Keplerian(PackedPositive(orbit, "semiMajorAxisKm"), orbit.GetProperty("eccentricity").GetDouble(),
                PackedPositive(orbit, "periodDays") * JulianInstant.SecondsPerDay, Date(orbit, "epochJdTdb", J2000),
                Angle(orbit, "meanAnomalyDeg"), Angle(orbit, "inclinationDeg"), Angle(orbit, "ascendingNodeDeg"),
                Angle(orbit, "argOfPericenterDeg")),
            "sampled" => PackedSamples(orbit, root),
            _ => throw new NotSupportedException($"Unsupported packed mission trajectory {kind}.")
        }, axesFrame, PhysicalVelocity: true);
    }

    private static Func<JulianInstant, CartesianState?> PackedSamples(JsonElement orbit, string root)
    {
        string path = PackedAsset(root, "trajectories", Text(orbit, "file"));
        var samples = Text(orbit, "sampleConvention") switch
        {
            "cosmographiaXyz" => SampledStateTrajectory.ReadXyz(path, MissionSource),
            "cosmographiaXyzv" => SampledStateTrajectory.ReadXyzv(path, MissionSource),
            var kind => throw new NotSupportedException($"Unsupported packed mission sample convention {kind}.")
        };
        return epoch => samples.TrySample(epoch, out var state) ? state : null;
    }

    private static MissionFrame.Rotating PackedRotation(JsonElement spin, MissionFrame parent, string root, CancellationToken cancellation)
    {
        string kind = Text(spin, "kind");
        if (kind == "follow")
        {
            if (spin.TryGetProperty("localRotation", out var local) && local.ValueKind != JsonValueKind.Null)
                throw new NotSupportedException("A packed follow frame with a local spin is unavailable.");
            var sourceAxes = RotateX(-Math.PI / 2);
            var turn = Multiply(Multiply(sourceAxes.Transpose(), Quaternion(spin.GetProperty("turn"))), sourceAxes);
            return new MissionFrame.Rotating(parent, _ => new(turn, default(Vector3d)));
        }
        if (kind == "quaternion")
        {
            var basis = Quaternion(spin.GetProperty("quaternion"));
            return new MissionFrame.Rotating(parent, _ => new(basis, default(Vector3d)));
        }
        if (kind == "sampled")
        {
            if (Text(spin, "interpolation") != "cosmographiaNative")
                throw new NotSupportedException("A packed sampled attitude needs the native interpolation profile.");
            string path = PackedAsset(root, "trajectories", Text(spin, "file"));
            bool compatible = Text(spin, "convention") switch
            {
                "cosmographia" => false,
                "celestia" => true,
                var convention => throw new NotSupportedException($"Unsupported packed attitude convention {convention}.")
            };
            var samples = SampledMissionAttitude.Read(path, compatible, cancellation);
            return new MissionFrame.Rotating(parent, samples.Sample);
        }
        if (kind == "cosmographiaUniform")
        {
            double period = spin.GetProperty("periodDays").GetDouble() * JulianInstant.SecondsPerDay;
            if (!double.IsFinite(period) || period == 0)
                throw new InvalidDataException("A packed uniform attitude needs a finite nonzero period.");
            double rate = Math.Tau / period;
            var mean = Multiply(RotateZ(Angle(spin, "ascendingNodeDeg")), RotateX(Angle(spin, "inclinationDeg")));
            double meridian = Angle(spin, "meridianDeg") + Math.PI;
            double reference = Date(spin, "epochJdTdb", J2000).ToJulianDate();
            return new MissionFrame.Rotating(parent, epoch => new(Multiply(mean,
                RotateZ(meridian + (epoch.ToJulianDate() - reference) * JulianInstant.SecondsPerDay * rate)), mean.Z * rate));
        }
        throw new NotSupportedException($"Unsupported packed mission rotation {kind}.");
    }

    private static FrameKinematics EvaluateLocalRotation(MissionFrame.Rotating rotating, JulianInstant epoch)
    {
        var parent = ((MissionFrame.Inertial)rotating.Parent).ToWorld;
        FrameKinematics local = rotating.Local(epoch);
        return new(Multiply(parent, local.ToWorld), local.AngularVelocityRadiansPerSecond is { } rate ? parent.Transform(rate) : null);
    }

    private static MissionShape? ReadPackedShape(JsonElement body, string root, string name, double radius, Func<string, ObjectId> resolve, Action<string>? warning, CancellationToken cancellation)
    {
        if (body.TryGetProperty("swarm", out var swarm))
            return ReadSwarm(swarm, root, Path.Combine(root, "data"), cancellation, packed: true);
        if (body.TryGetProperty("plane", out var plane))
            return ReadVectorShape(plane, plane: true, resolve, packed: true);
        if (body.TryGetProperty("direction", out var direction))
            return ReadVectorShape(direction, plane: false, resolve, packed: true);
        if (body.TryGetProperty("axesGeometry", out var axes))
        {
            var kind = Text(axes, "kind") switch
            {
                "geometry" => MissionAxesKind.Geometry,
                "bodyVisualizer" => MissionAxesKind.BodyVisualizer,
                "frameVisualizer" => MissionAxesKind.FrameVisualizer,
                var unknown => throw new NotSupportedException($"Unknown packed axes type {unknown}.")
            };
            double size = PackedPositive(axes, "scale");
            if (!float.IsFinite((float)size))
                throw new InvalidDataException("A packed axes size exceeds its finite rendering range.");
            return AxesShape(new(kind, size, axes.GetProperty("naturalAxes").GetBoolean(), axes.GetProperty("catalogVisible").GetBoolean()));
        }
        if (!body.TryGetProperty("appearance", out var appearance))
            return null;
        if (appearance.TryGetProperty("model", out var model))
        {
            string file = Text(model, "file");
            string? path;
            try { path = PackedAsset(root, "models", file); }
            catch (FileNotFoundException) { path = null; warning?.Invoke($"{name}: mesh {file} is missing; the trajectory is available."); }
            double size = Text(model, "geometryConvention") switch
            {
                "cosmographiaSize" => PackedPositive(model, "geometryValue"),
                var kind => throw new NotSupportedException($"Unsupported packed mesh size convention {kind}.")
            };
            var orientation = Quaternion(model.GetProperty("orientation"));
            var sourceAxes = RotateX(-Math.PI / 2);
            var rotation = Multiply(sourceAxes.Transpose(), orientation.Transpose());
            var offset = model.TryGetProperty("offset", out var position) ? sourceAxes.Transpose().Transform(Vector(position)) : default;
            return new(path, size, rotation, offset);
        }
        var maps = ReadGlobeMaps(appearance, root, root, name, packed: true, cancellation);
        if (appearance.TryGetProperty("texture", out var texture) || maps is not null)
        {
            string? path = null;
            if (texture.ValueKind == JsonValueKind.String && texture.GetString() is { Length: > 0 } file)
            {
                try { path = PackedAsset(root, "textures", file); }
                catch (FileNotFoundException) { warning?.Invoke($"{name}: globe map {file} is missing; the shape is available."); }
            }
            return new(null, radius, Basis3d.Identity, default)
            {
                EllipsoidRadiiKilometers = new(radius, radius, PackedPositive(body, "polarRadiusKm")),
                GlobeTexturePath = path, SurfaceMaps = maps
            };
        }
        return null;
    }

    private static string PackedAsset(string root, string directory, string file) =>
        LocalAddonFiles.ResolveRelative(root, Path.Combine(root, directory), file);

    private static double PackedPositive(JsonElement value, string field)
    {
        double number = value.GetProperty(field).GetDouble();
        return double.IsFinite(number) && number > 0 ? number : throw new InvalidDataException($"Packed {field} must be finite and positive.");
    }
}
