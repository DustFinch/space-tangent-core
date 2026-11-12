// SPDX-License-Identifier: GPL-2.0-or-later
using System.Text.Json;
using SpaceTangent.Contracts;
using SpaceTangent.Core.World;

namespace SpaceTangent.Core.Addons;

public static partial class SampledMissionCatalog
{
    private static readonly Basis3d LegacyNaturalBodyAxes = new(new(-1, 0, 0), new(0, -1, 0), new(0, 0, 1));

    private static SampledMission ReadPackedParticleMission(JsonElement body, Func<string, ObjectId> resolve,
        string directory, Action<string>? warning)
    {
        string name = Text(body, "name");
        ObjectId id = resolve(name);
        var particles = body.GetProperty("particles");
        var list = particles.GetProperty("emitters");
        if (list.GetArrayLength() is < 1 or > 32)
            throw new InvalidDataException("A packed particle system requires one to 32 emitters.");
        var emitters = list.EnumerateArray().Select(emitter => PackedEmitter(emitter, directory, warning)).ToArray();
        MissionShape shape = ParticleShape(emitters);

        var rotation = body.GetProperty("rotation");
        Basis3d spin = Quaternion(rotation.GetProperty("quaternion"));
        if (Text(rotation, "kind") != "quaternion" || spin != Basis3d.Identity)
            throw new NotSupportedException($"{name}: packed particles currently require an unrotated source body frame.");
        var timeline = body.GetProperty("timeline");
        if (timeline.GetArrayLength() != 1)
            throw new NotSupportedException($"{name}: packed particles currently require one stationary trajectory.");
        var phase = timeline[0];
        string centerPath = Text(phase, "center");
        ObjectId center = resolve(centerPath);
        JulianInstant start = Minimum, end = Maximum;
        if (body.TryGetProperty("lifetime", out var lifetime))
        {
            start = Date(lifetime, "beginningJdTdb", start);
            end = Date(lifetime, "endingJdTdb", end);
            if (end.CompareTo(start) <= 0)
                throw new InvalidDataException($"{name}: packed particle availability is empty.");
        }
        var phaseEnd = Date(phase, "endingJdTdb", end);
        if (phaseEnd.CompareTo(end) < 0)
            end = phaseEnd;
        if (end.CompareTo(start) <= 0)
            throw new InvalidDataException($"{name}: packed particle trajectory is empty.");

        var orbit = phase.GetProperty("orbit");
        MissionFrame? trajectoryFrame = null;
        Basis3d axes = Basis3d.Identity;
        if (Text(orbit, "kind") == "movingFrame")
        {
            ObjectId rotatingBody = resolve(Text(orbit, "body"));
            trajectoryFrame = new MissionFrame.BodyFixed(rotatingBody);
            axes = LegacyBodyAxes(rotatingBody, Quaternion(orbit.GetProperty("axes")));
            var local = orbit.GetProperty("trajectory");
            if (Text(local, "center") != centerPath)
                throw new InvalidDataException($"{name}: the packed rotating trajectory changed its centre.");
            orbit = local.GetProperty("orbit");
        }
        if (Text(orbit, "kind") != "fixed")
            throw new NotSupportedException($"{name}: packed particles currently require a fixed trajectory.");
        Vector3d position = Vector(orbit.GetProperty("positionKm"));
        if (trajectoryFrame is null)
        {
            var orbitFrame = orbit.GetProperty("frame");
            trajectoryFrame = PackedFrame(orbitFrame, resolve, id);
            if (trajectoryFrame is MissionFrame.BodyFixed direct)
                axes = LegacyBodyAxes(direct.Body, axes);
        }
        MissionFrame attitude = particles.TryGetProperty("frameBody", out var emitterFrame)
            ? new MissionFrame.BodyFixed(resolve(emitterFrame.GetString()!))
            : body.TryGetProperty("bodyFrame", out var bodyFrame) ? PackedFrame(bodyFrame, resolve, id)
            : PackedFrame(rotation.GetProperty("frame"), resolve, id);
        return new(id, name, null, shape.RadiusKilometers,
            [new(start, end, center, Constant(axes.Transform(position)), trajectoryFrame, attitude)])
            { Geometry = [new(start, end, shape)] };
    }

    // A legacy pack stores its natural-body prime meridian on the opposite X axis.
    private static Basis3d LegacyBodyAxes(ObjectId id, Basis3d local) => id.Value.StartsWith("sol/", StringComparison.Ordinal)
        ? Multiply(LegacyNaturalBodyAxes, local) : local;

    private static MissionParticleEmitter PackedEmitter(JsonElement emitter, string directory, Action<string>? warning)
    {
        if (Text(emitter, "convention") != "cosmographia" || Text(emitter, "sourceAxes") is not ("astronomical" or "natural"))
            throw new NotSupportedException("A packed particle emitter requires its original source axes and convention.");
        var generator = ParticleGeneratorKind(Text(emitter, "generator"));
        var strip = emitter.TryGetProperty("strip", out var states)
            ? PackedList(states, 2048, item => new MissionParticleBirth(Vector(item.GetProperty("positionKm")), Vector(item.GetProperty("velocityKmS"))))
            : [];
        var arcs = emitter.TryGetProperty("arcs", out var points)
            ? PackedList(points, 2048, item => new MissionParticleArc(Vector(item.GetProperty("positionKm")), ParticleNumber(item.GetProperty("speedKmS"))))
            : [];
        if (generator == MissionParticleGenerator.Strip && strip.Length < 2 ||
            generator == MissionParticleGenerator.ArcStrip && (arcs.Length < 2 || arcs.Any(arc => !(arc.Position.Length > 0))))
            throw new InvalidDataException("A packed particle strip requires two complete generator points.");
        var colors = PackedList(emitter.GetProperty("colors"), 4,
            key => ParticleColor(key.GetProperty("color"), ParticleNumber(key.GetProperty("opacity"))));
        return new()
        {
            Generator = generator,
            Center = ReadOptionalVector(emitter, "centerKm"),
            Sides = ReadOptionalVector(emitter, "sidesKm"),
            Velocity = ReadOptionalVector(emitter, "velocityKmS"),
            Force = ReadOptionalVector(emitter, "forceKmS2"),
            DiscRadiusKilometers = ReadOptionalNumber(emitter, "discRadiusKm", 0),
            Strip = Array.AsReadOnly(strip),
            Arcs = Array.AsReadOnly(arcs),
            Colors = Array.AsReadOnly(colors),
            SpawnRate = ParticleNumber(emitter.GetProperty("spawnRatePerS")),
            LifetimeSeconds = ParticleNumber(emitter.GetProperty("lifetimeS")),
            StartSize = ParticleNumber(emitter.GetProperty("startSizeKm")),
            EndSize = ParticleNumber(emitter.GetProperty("endSizeKm")),
            TraceSeconds = ReadOptionalNumber(emitter, "traceS", 0),
            VelocityVariation = ReadOptionalNumber(emitter, "velocityVariationKmS", 0),
            StartJulianDate = Date(emitter, "startJdTdb", J2000).ToJulianDate(),
            EndJulianDate = Date(emitter, "endJdTdb", J2000).ToJulianDate(),
            TexturePath = ReadParticleTexture(emitter, directory, Path.Combine(directory, "textures"), warning)
        };
    }

    private static T[] PackedList<T>(JsonElement list, int maximum, Func<JsonElement, T> read)
    {
        if (list.GetArrayLength() > maximum)
            throw new InvalidDataException($"A packed particle list has more than {maximum} entries.");
        return list.EnumerateArray().Select(read).ToArray();
    }
}
