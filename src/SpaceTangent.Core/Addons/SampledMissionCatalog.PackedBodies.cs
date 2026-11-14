// SPDX-License-Identifier: GPL-2.0-or-later
using System.Text.Json;
using SpaceTangent.Contracts;
using SpaceTangent.Core.Coordinates;
using SpaceTangent.Core.Orbits;
using SpaceTangent.Core.Observing;
using SpaceTangent.Core.Spice;
using SpaceTangent.Core.World;

namespace SpaceTangent.Core.Addons;

public static partial class SampledMissionCatalog
{
    private static SampledMission ReadPackedBody(JsonElement body, Func<string, ObjectId> resolve,
        string root, SpiceResources resources, Action<string>? warning, CancellationToken cancellation, IReadOnlyList<JsonElement>? children)
    {
        string name = Text(body, "name");
        ObjectId id = resolve(Text(body, "path"));
        string parent = Text(body, "parent");
        var lifetime = body.TryGetProperty("lifetime", out var given) ? given : default;
        JulianInstant start = lifetime.ValueKind == JsonValueKind.Object ? Date(lifetime, "beginningJdTdb", Minimum) : Minimum;
        JulianInstant end = lifetime.ValueKind == JsonValueKind.Object ? Date(lifetime, "endingJdTdb", Maximum) : Maximum;
        if (end.CompareTo(start) <= 0)
            throw new InvalidDataException("A packed body has an empty lifetime.");

        var rotations = Text(body.GetProperty("rotation"), "kind") == "timeline"
            ? ReadPhases(body.GetProperty("rotation").GetProperty("phases"), "rotation")
            : [(end, body.GetProperty("rotation"))];
        var bodyFrames = body.TryGetProperty("bodyFrames", out var framePhases)
            ? ReadPhases(framePhases, "frame")
            : [(end, body.TryGetProperty("bodyFrame", out var frame) ? frame : default)];
        var attitudes = new Dictionary<(int Rotation, int Frame), (MissionFrame Attitude, MissionFrame Reference)>();
        int rotationIndex = 0, frameIndex = 0;
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
        MissionShape? shape = body.TryGetProperty("sensor", out var sensor)
            ? sensor.TryGetProperty("instrument", out var instrument) && instrument.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(instrument.GetString())
                ? ReadPackedSpiceSensor(sensor, body, resources, resolve)
                : new(null, .001, Basis3d.Identity, default, ReadSensor(sensor, resolve, packed: true))
            : ReadPackedShape(body, root, name, radius, resolve, warning, cancellation);
        var geometry = new List<MissionGeometrySpan>();
        AddGeometry(body, shape);
        var bounds = body.TryGetProperty("geometryBounds", out var terms)
            ? ReadPackedGeometryBounds(terms, root, name, warning, cancellation) : null;
        foreach (var child in children ?? [])
        {
            cancellation.ThrowIfCancellationRequested();
            double childRadius = PackedPositive(child, "radiusKm");
            var childShape = ReadPackedShape(child, root, name, childRadius, resolve, warning, cancellation);
            if (childShape is null && (!child.TryGetProperty("visible", out var visible) || visible.GetBoolean()))
                childShape = new(null, childRadius, Basis3d.Identity, default)
                {
                    EllipsoidRadiiKilometers = new(childRadius, childRadius, PackedPositive(child, "polarRadiusKm"))
                };
            AddGeometry(child, childShape);
        }
        return new(id, name, shape?.MeshPath, radius, arcs.AsReadOnly())
        {
            Geometry = geometry.OrderBy(span => span.Start).ToArray(),
            GeometryBounds = bounds,
            Observation = body.TryGetProperty("observation", out var observation)
                ? ReadObservation(observation, resolve(parent), resolve, packed: true) : null
        };

        void AddGeometry(JsonElement item, MissionShape? visibleShape)
        {
            if (visibleShape is null)
                return;
            var shown = item.TryGetProperty("shown", out var interval) ? interval : default;
            var from = shown.ValueKind == JsonValueKind.Object ? Date(shown, "fromJdTdb", start) : start;
            var until = shown.ValueKind == JsonValueKind.Object ? Date(shown, "untilJdTdb", end) : end;
            if (from.CompareTo(start) < 0)
                from = start;
            if (until.CompareTo(end) > 0)
                until = end;
            if (until.CompareTo(from) > 0)
                geometry.Add(new(from, until, visibleShape));
        }

        void AddArc(JulianInstant from, JulianInstant until, string center, JsonElement orbit)
        {
            var trajectory = PackedTrajectory(orbit, center, resolve, id, root, resources, cancellation);
            ObjectId centerId = resolve(center);
            while (from.CompareTo(until) < 0)
            {
                cancellation.ThrowIfCancellationRequested();
                while (rotationIndex + 1 < rotations.Length && rotations[rotationIndex].Until.CompareTo(from) <= 0)
                    rotationIndex++;
                while (frameIndex + 1 < bodyFrames.Length && bodyFrames[frameIndex].Until.CompareTo(from) <= 0)
                    frameIndex++;
                JulianInstant next = until;
                if (rotationIndex + 1 < rotations.Length && rotations[rotationIndex].Until.CompareTo(next) < 0)
                    next = rotations[rotationIndex].Until;
                if (frameIndex + 1 < bodyFrames.Length && bodyFrames[frameIndex].Until.CompareTo(next) < 0)
                    next = bodyFrames[frameIndex].Until;
                var key = (rotationIndex, frameIndex);
                if (!attitudes.TryGetValue(key, out var orientation))
                {
                    orientation = ReadAttitude(rotations[rotationIndex].Value, bodyFrames[frameIndex].Value);
                    attitudes.Add(key, orientation);
                }
                arcs.Add(new(from, next, centerId, trajectory.Local, trajectory.Frame, orientation.Attitude)
                {
                    BodyReferenceFrame = orientation.Reference, NativeVelocitySource = trajectory.NativeVelocitySource,
                    UsePhysicalBodyFixedVelocity = trajectory.PhysicalVelocity
                });
                from = next;
            }
        }

        (MissionFrame Attitude, MissionFrame Reference) ReadAttitude(JsonElement spin, JsonElement axes)
        {
            string kind = Text(spin, "kind");
            if (kind == "spice")
                return (ReadPackedSpiceAttitude(spin, resources), PackedSpiceAxes(spin, "outputFrame"));
            var reference = kind == "follow" ? new MissionFrame.BodyFixed(resolve(Text(spin, "body"))) :
                PackedFrame(spin.GetProperty("frame"), resolve, id);
            if (kind == "follow" && spin.TryGetProperty("localRotation", out var own) && own.ValueKind != JsonValueKind.Null)
            {
                if (axes.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null) ||
                    PackedFrame(own.GetProperty("frame"), resolve, id) is not MissionFrame.Inertial ownAxes)
                    throw new NotSupportedException("A packed follow spin requires inertial local axes.");
                var turn = PackedFollowTurn(spin);
                var turned = new MissionFrame.Rotating(reference, _ => new(turn, default(Vector3d)));
                var relativeAxes = new MissionFrame.Inertial(Multiply(Ecliptic.ToWorld.Transpose(), ownAxes.ToWorld));
                var ownModel = PackedRotation(own, relativeAxes, root, cancellation);
                return (new MissionFrame.RelativeAttitude(turned, epoch => EvaluateLocalRotation(ownModel, epoch))
                    { LocalRateOnly = true }, turned);
            }
            if (axes.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
                return (PackedRotation(spin, reference, root, cancellation), reference);
            var dynamicFrame = PackedFrame(axes, resolve, id);
            if (reference is not MissionFrame.Inertial inertial)
                throw new NotSupportedException("A packed rotation in a two-vector body frame needs inertial local axes.");
            var relative = Multiply(Ecliptic.ToWorld.Transpose(), inertial.ToWorld);
            var local = PackedRotation(spin, new MissionFrame.Inertial(relative), root, cancellation);
            return (new MissionFrame.Rotating(dynamicFrame, epoch => EvaluateLocalRotation(local, epoch)),
                new MissionFrame.Rotating(dynamicFrame, _ => new(relative, default(Vector3d))));
        }

        (JulianInstant Until, JsonElement Value)[] ReadPhases(JsonElement phases, string property)
        {
            if (phases.GetArrayLength() is < 1 or > 128)
                throw new InvalidDataException("A packed orientation needs one to 128 phases.");
            var result = new List<(JulianInstant, JsonElement)>();
            JulianInstant previous = Minimum;
            foreach (var phase in phases.EnumerateArray())
            {
                JulianInstant until = Date(phase, "untilJdTdb", Maximum);
                if (until.CompareTo(previous) <= 0)
                    throw new InvalidDataException("Packed orientation phase ends must increase.");
                result.Add((until, phase.TryGetProperty(property, out var value) ? value : default));
                previous = until;
            }
            return result.ToArray();
        }
    }

    private sealed record PackedOrbit(Func<JulianInstant, CartesianState?> Local, MissionFrame Frame,
        Basis3d? NativeVelocitySource = null, bool PhysicalVelocity = false);

    private const string PackedBuiltinKind = "cosmographiaBuiltin";

    private static PackedOrbit PackedTrajectory(JsonElement orbit, string center, Func<string, ObjectId> resolve,
        ObjectId owner, string root, SpiceResources resources, CancellationToken cancellation, int depth = 0, bool framed = false)
    {
        cancellation.ThrowIfCancellationRequested();
        if (depth >= 16)
            throw new InvalidDataException("A packed trajectory exceeds 16 nested levels.");
        string kind = Text(orbit, "kind");
        if (kind == "spice")
            return ReadPackedSpiceOrbit(orbit, resources, resolve, owner);
        if (kind == "twoVectorFrame")
        {
            if (framed || Text(orbit, "velocityPolicy") != "cosmographia-native-zero")
                throw new InvalidDataException("Invalid packed two-vector trajectory or velocity policy.");
            var trajectory = orbit.GetProperty("trajectory");
            if (!center.Equals(Text(trajectory, "center"), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The packed two-vector trajectory changed its centre.");
            var local = PackedTrajectory(trajectory.GetProperty("orbit"), center, resolve, owner, root, resources, cancellation, depth + 1, framed: true);
            if (local.Frame is not MissionFrame.Inertial source ||
                PackedFrame(orbit.GetProperty("frame"), resolve, owner) is not MissionFrame.TwoVector axes)
                throw new NotSupportedException("A packed two-vector frame needs an inertial local trajectory and two directions.");
            return new(Sample, axes);

            CartesianState? Sample(JulianInstant epoch) => local.Local(epoch) is { } state
                ? new(source.ToWorld.Transform(state.PositionKilometers), source.ToWorld.Transform(state.VelocityKilometersPerSecond)) : null;
        }
        if (kind == "movingFrame")
        {
            if (framed)
                throw new InvalidDataException("Packed moving frames cannot be nested.");
            var trajectory = orbit.GetProperty("trajectory");
            if (!center.Equals(Text(trajectory, "center"), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The packed moving trajectory changed its centre.");
            var local = PackedTrajectory(trajectory.GetProperty("orbit"), center, resolve, owner, root, resources, cancellation, depth + 1, framed: true);
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

        MissionFrame axesFrame = IsFixedEarthMeanEquator(orbit)
            ? new MissionFrame.Rotating(Equatorial, epoch => new(new(
                ObservationEventSearch.DateEquatorToJ2000(new(1, 0, 0), epoch),
                ObservationEventSearch.DateEquatorToJ2000(new(0, 1, 0), epoch),
                ObservationEventSearch.DateEquatorToJ2000(new(0, 0, 1), epoch)), default(Vector3d)))
            : PackedFrame(orbit.GetProperty("frame"), resolve, owner);
        return new(kind switch
        {
            "fixed" => Constant(Vector(orbit.GetProperty("positionKm"))),
            "kepler" => Keplerian(PackedPositive(orbit, "semiMajorAxisKm"), orbit.GetProperty("eccentricity").GetDouble(),
                PackedPositive(orbit, "periodDays") * JulianInstant.SecondsPerDay, Date(orbit, "epochJdTdb", J2000),
                Angle(orbit, "meanAnomalyDeg"), Angle(orbit, "inclinationDeg"), Angle(orbit, "ascendingNodeDeg"),
                Angle(orbit, "argOfPericenterDeg")),
            "sampled" => PackedSamples(orbit, root),
            "chebyshev" => PackedChebyshev(orbit, root),
            "combination" => PackedCombination(orbit, center, resolve, owner, root, resources, axesFrame, cancellation, depth, framed),
            PackedBuiltinKind => BuiltinTrajectory(Text(orbit, "name"), axesFrame),
            _ => throw new NotSupportedException($"Unsupported packed mission trajectory {kind}.")
        }, axesFrame, PhysicalVelocity: true);
    }

    private static Func<JulianInstant, CartesianState?> PackedChebyshev(JsonElement orbit, string root)
    {
        var terms = orbit.GetProperty("terms");
        if (terms.GetArrayLength() is < 1 or > 16)
            throw new InvalidDataException("A packed Chebyshev trajectory requires one to 16 terms.");
        return WeightedTrajectory(terms.EnumerateArray().Select(term =>
        {
            string path = SscSampledCatalog.FindAsset(AddonStage.Trajectory, root, root, Text(term, "file"), "data", "trajectories");
            var series = AddonDiagnostics.Read(AddonStage.Trajectory, path, () => ChebyshevTrajectory.Load(path));
            Func<JulianInstant, CartesianState?> sample = epoch => series.Sample(epoch);
            return (sample, term.GetProperty("weight").GetDouble());
        }).ToArray());
    }

    private static Func<JulianInstant, CartesianState?> PackedCombination(JsonElement orbit, string center,
        Func<string, ObjectId> resolve, ObjectId owner, string root, SpiceResources resources, MissionFrame axes,
        CancellationToken cancellation, int depth, bool framed)
    {
        var terms = orbit.GetProperty("terms");
        if (terms.GetArrayLength() is < 2 or > 16)
            throw new InvalidDataException("A packed combination requires two to 16 terms.");
        return WeightedTrajectory(terms.EnumerateArray().Select(term =>
            (ReadTerm(term.GetProperty("phases")), term.GetProperty("weight").GetDouble())).ToArray());

        Func<JulianInstant, CartesianState?> ReadTerm(JsonElement phases)
        {
            if (phases.GetArrayLength() is < 1 or > 128)
                throw new InvalidDataException("A packed trajectory term requires one to 128 phases.");
            var segments = new List<(JulianInstant End, Func<JulianInstant, CartesianState?> Sample, Basis3d? ToCombined)>();
            JulianInstant previous = Minimum;
            foreach (var phase in phases.EnumerateArray())
            {
                var until = Date(phase, "endingJdTdb", Maximum);
                if (until.CompareTo(previous) <= 0 || resolve(Text(phase, "center")) != resolve(center))
                    throw new InvalidDataException("Packed combination phases require increasing ends and a common centre.");
                var child = PackedTrajectory(phase.GetProperty("orbit"), center, resolve, owner, root, resources,
                    cancellation, depth + 1, framed);
                Basis3d? toCombined = child.Frame == axes ? null :
                    axes is MissionFrame.Inertial combined && child.Frame is MissionFrame.Inertial component
                        ? Multiply(combined.ToWorld.Transpose(), component.ToWorld)
                        : throw new NotSupportedException("Packed combination terms require compatible trajectory axes.");
                segments.Add((until, child.Local, toCombined));
                previous = until;
            }
            return epoch =>
            {
                var segment = segments[^1];
                foreach (var candidate in segments)
                {
                    if (epoch.CompareTo(candidate.End) >= 0)
                        continue;
                    segment = candidate;
                    break;
                }
                var local = segment.Sample(epoch);
                return local is { } state && segment.ToCombined is { } basis
                    ? new(basis.Transform(state.PositionKilometers), basis.Transform(state.VelocityKilometersPerSecond)) : local;
            };
        }
    }

    private static Func<JulianInstant, CartesianState?> PackedSamples(JsonElement orbit, string root)
    {
        string path = AddonDiagnostics.Read(AddonStage.Trajectory, null,
            () => PackedAsset(root, "trajectories", Text(orbit, "file")));
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
            var turn = PackedFollowTurn(spin);
            return new MissionFrame.Rotating(parent, _ => new(turn, default(Vector3d)));
        }
        if (kind == "quaternion")
        {
            var basis = Quaternion(spin.GetProperty("quaternion"));
            return new MissionFrame.Rotating(parent, _ => new(basis, default(Vector3d)));
        }
        if (kind == "sampled")
        {
            bool legacy = !spin.TryGetProperty("interpolation", out var profile) || profile.GetString() == "legacy";
            string path = PackedAsset(root, "trajectories", Text(spin, "file"));
            bool compatible = Text(spin, "convention") switch
            {
                "cosmographia" => false,
                "celestia" => true,
                var convention => throw new NotSupportedException($"Unsupported packed attitude convention {convention}.")
            };
            if (legacy)
            {
                var legacySamples = SampledMissionAttitude.ReadLegacy(path, compatible, cancellation);
                return new MissionFrame.Rotating(parent, legacySamples.Sample);
            }
            if (Text(spin, "interpolation") != "cosmographiaNative")
                throw new NotSupportedException("A packed sampled attitude needs the native interpolation profile.");
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
        if (kind is "fixed" or "uniform" && (kind == "fixed" || !spin.TryGetProperty("periodDays", out _)))
        {
            var basis = Multiply(Multiply(RotateZ(Angle(spin, "ascendingNodeDeg")), RotateX(Angle(spin, "inclinationDeg"))),
                RotateZ(Math.PI + Angle(spin, "meridianDeg")));
            return new MissionFrame.Rotating(parent, _ => new(basis, default(Vector3d)));
        }
        throw new NotSupportedException($"Unsupported packed mission rotation {kind}.");
    }

    private static Basis3d PackedFollowTurn(JsonElement spin)
    {
        var sourceAxes = RotateX(-Math.PI / 2);
        return Multiply(Multiply(sourceAxes.Transpose(), Quaternion(spin.GetProperty("turn"))), sourceAxes);
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
            try { path = SscSampledCatalog.FindAsset(AddonStage.Mesh, root, root, file, "models"); }
            catch (FileNotFoundException) { path = null; warning?.Invoke($"{name}: mesh {file} is missing; the trajectory is available."); }
            string convention = model.TryGetProperty("geometryConvention", out var mode)
                ? mode.GetString() ?? throw new InvalidDataException("Missing packed mesh geometry convention.")
                : PackedMeshSize;
            double value = model.TryGetProperty("geometryValue", out var size) ? size.GetDouble() : radius;
            double? scale = convention switch
            {
                PackedMeshSize when value > 0 && double.IsFinite(value) => null,
                PackedMeshScale when float.IsFinite((float)value) => value,
                _ => throw new NotSupportedException($"Unsupported packed mesh size convention or value {convention}.")
            };
            var orientation = Quaternion(model.GetProperty("orientation"));
            var sourceAxes = RotateX(-Math.PI / 2);
            var rotation = Multiply(sourceAxes.Transpose(), orientation.Transpose());
            var offset = model.TryGetProperty("offset", out var position) ? sourceAxes.Transpose().Transform(Vector(position)) : default;
            return new(path, scale is null ? value : radius, rotation, offset) { MeshScale = scale };
        }
        var maps = ReadGlobeMaps(appearance, root, root, name, packed: true, cancellation);
        if (appearance.TryGetProperty("texture", out var texture) || maps is not null)
        {
            string? path = null;
            if (texture.ValueKind == JsonValueKind.String && texture.GetString() is { Length: > 0 } file)
            {
                try { path = SscSampledCatalog.FindAsset(AddonStage.Texture, root, root, file, "textures"); }
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
