// SPDX-License-Identifier: GPL-2.0-or-later
using System.Text.Json;
using SpaceTangent.Contracts;
using SpaceTangent.Core.Spice;
using SpaceTangent.Core.World;

namespace SpaceTangent.Core.Addons;

public static partial class SampledMissionCatalog
{
    private static bool HasPackedSpice(JsonElement body) =>
        body.TryGetProperty("rotation", out var spin) && IsSpice(spin) ||
        body.TryGetProperty("timeline", out var phases) &&
        phases.EnumerateArray().Any(phase => IsSpice(phase.GetProperty("orbit"))) ||
        body.TryGetProperty("orbit", out var orbit) && IsSpice(orbit) ||
        body.TryGetProperty("sensor", out var sensor) && sensor.TryGetProperty("instrument", out _);

    private static bool IsSpice(JsonElement item) =>
        item.TryGetProperty("kind", out var kind) && kind.ValueEquals("spice");

    private static void ReadPackedKernelLoads(JsonElement document, string root, string directory,
        List<(string Directory, string Name)> output)
    {
        if (document.TryGetProperty("spiceLoads", out var loads))
        {
            foreach (var load in loads.EnumerateArray())
            {
                string policy = Text(load, "policy");
                if (policy != "furnish")
                    continue;
                foreach (var file in load.GetProperty("files").EnumerateArray())
                    Add(file.GetString() ?? throw new InvalidDataException("Missing packed SPICE kernel filename."));
            }
            return;
        }

        if (!document.TryGetProperty("bodies", out var bodies))
            return;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var body in bodies.EnumerateArray())
        {
            if (body.TryGetProperty("timeline", out var phases))
                foreach (var phase in phases.EnumerateArray())
                    Read(phase.GetProperty("orbit"));
            if (body.TryGetProperty("orbit", out var orbit))
                Read(orbit);
            if (body.TryGetProperty("rotation", out var spin))
                Read(spin);
            if (body.TryGetProperty("sensor", out var sensor))
                Read(sensor);
        }

        void Read(JsonElement item)
        {
            if (!item.TryGetProperty("kernel", out var kernel) || kernel.ValueKind != JsonValueKind.String)
                return;
            foreach (string file in kernel.GetString()!.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                if (seen.Add(file))
                    Add(file);
        }

        void Add(string name)
        {
            if (output.Count >= 256)
                throw new InvalidDataException("The sampled mission catalogues declare too many SPICE kernels.");
            string path = SscSampledCatalog.FindAsset(AddonStage.Kernel, root, directory, name, "data");
            output.Add((Path.GetDirectoryName(path)!, Path.GetFileName(path)));
        }
    }

    private static PackedOrbit ReadPackedSpiceOrbit(JsonElement orbit, SpiceResources resources,
        Func<string, ObjectId> resolve, ObjectId owner)
    {
        var frame = orbit.TryGetProperty("frame", out var axes) && axes.ValueKind != JsonValueKind.Null
            ? PackedFrame(axes, resolve, owner) : Equatorial;
        if (frame is not MissionFrame.Inertial inertial)
            throw new NotSupportedException("Packed SPICE trajectories require inertial output axes.");
        var sample = SpiceTrajectory(orbit, resources, "spiceCenter", packed: true);
        var start = OptionalDate("beginningJdTdb", Minimum);
        var end = OptionalDate("endingJdTdb", Maximum);
        if (end.CompareTo(start) <= 0)
            throw new InvalidDataException("Packed SPICE coverage must be increasing.");
        var basis = inertial.ToWorld.Transpose();
        return new(epoch => epoch.CompareTo(start) < 0 || epoch.CompareTo(end) > 0 || sample(epoch) is not { } state
            ? null : new(basis.Transform(state.PositionKilometers), basis.Transform(state.VelocityKilometersPerSecond)),
            inertial, PhysicalVelocity: true);

        JulianInstant OptionalDate(string name, JulianInstant fallback) =>
            orbit.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null
                ? Date(orbit, name, fallback) : fallback;
    }

    private static MissionFrame PackedSpiceAxes(JsonElement spin, string field)
    {
        if (!spin.TryGetProperty(field, out var axes) || axes.GetString() is not { Length: > 0 } name)
            return Equatorial;
        string key = char.ToLowerInvariant(name[0]) + name[1..];
        return PackedFrame(JsonSerializer.SerializeToElement(new { kind = key }),
            _ => throw new InvalidDataException("Packed SPICE output axes must be inertial."), default) is MissionFrame.Inertial inertial
            ? inertial : throw new InvalidDataException("Packed SPICE output axes must be inertial.");
    }

    private static MissionFrame.Spice ReadPackedSpiceAttitude(JsonElement spin, SpiceResources resources)
    {
        // The kernel pool exposes physical J2000 attitudes, including instrument frames.
        var model = new SpiceAttitude(Text(spin, "frame"), Text(spin, "baseFrame"), resources);
        return new(epoch => model.TrySample(epoch, out var orientation) ? orientation : null);
    }

    private static MissionShape ReadPackedSpiceSensor(JsonElement sensor, JsonElement body,
        SpiceResources resources, Func<string, ObjectId> resolve)
    {
        string instrument = Text(sensor, "instrument");
        string frame = Text(body.GetProperty("rotation"), "frame");
        var field = SpiceFieldOfView.Load(resources.Text, instrument, frame);
        Vector3d color = Vector(sensor.GetProperty("color"));
        double opacity = sensor.GetProperty("frustumOpacity").GetDouble();
        var rays = SensorEdgeRays(field.Corners);
        return new(null, .001, Basis3d.Identity, default,
            new(resolve(Text(sensor, "target")), PackedPositive(sensor, "rangeKm"), color, opacity,
                field.Boresight, rays, field.Corners.Length));
    }
}
