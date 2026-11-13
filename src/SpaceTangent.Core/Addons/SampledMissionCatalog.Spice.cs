// SPDX-License-Identifier: GPL-2.0-or-later
using System.Text.Json;
using SpaceTangent.Contracts;
using SpaceTangent.Core.Spice;
using SpaceTangent.Core.World;

namespace SpaceTangent.Core.Addons;

public static partial class SampledMissionCatalog
{
    private static void ReadSpiceLoads(JsonElement document, string directory, List<(string Directory, string Name)> output)
    {
        if (!document.TryGetProperty("spiceKernels", out var values))
            return;
        if (values.GetArrayLength() > 256 - output.Count)
            throw new InvalidDataException("The sampled mission catalogues declare too many SPICE kernels.");
        foreach (var value in values.EnumerateArray())
        {
            if (value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()))
                throw new InvalidDataException("A SPICE kernel load requires a local path.");
            output.Add((directory, value.GetString()!));
        }
    }

    private static Func<JulianInstant, CartesianState?> SpiceTrajectory(JsonElement value, SpiceResources resources,
        string arcCenter, bool packed = false)
    {
        if (!packed && value.TryGetProperty("frame", out var frame) &&
            !string.Equals(frame.GetString(), "J2000", StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException("A sampled SPICE trajectory requires a J2000 output frame.");
        int target = ResolveSpiceId(value.GetProperty("target"), resources);
        int center = value.TryGetProperty(packed ? "spiceCenter" : "center", out var selected) ? ResolveSpiceId(selected, resources) :
            NaifIds.Resolve(arcCenter, resources);
        var states = resources.States;
        return epoch => states.TryState(target, center, epoch, out var state) ? state : null;
    }

    private static int ResolveSpiceId(JsonElement value, SpiceResources resources) => value.ValueKind switch
    {
        JsonValueKind.Number => value.GetInt32(),
        JsonValueKind.String => NaifIds.Resolve(value.GetString()!, resources),
        _ => throw new InvalidDataException("A SPICE target or center requires an object name or integer code.")
    };

    private static MissionFrame.Spice SpiceOrientation(string frame, SpiceResources resources)
    {
        var attitude = new SpiceAttitude(frame, "J2000", resources);
        return new(epoch => attitude.TrySample(epoch, out var orientation) ? orientation : null);
    }

    private static SampledMission ReadSpiceInstrument(JsonElement item, JsonElement geometry, JulianInstant start,
        JulianInstant end, string name, Func<string, ObjectId> resolve, SpiceResources resources,
        Func<string, string?> findFrame)
    {
        string parent = Text(item, "center");
        if (item.TryGetProperty("parent", out var declared) && !string.Equals(parent, declared.GetString(), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("A SPICE instrument must be centered on its parent.");
        string frame = findFrame(parent) ?? throw new InvalidDataException("The SPICE instrument requires a parent attitude.");
        var field = SpiceFieldOfView.Load(resources.Text, Text(geometry, "instrName"), frame);
        var target = resolve(Text(geometry, "target"));
        double range = Distance(geometry, "range");
        var (color, opacity) = SensorStyle(geometry);
        if (!(range > 0) || end.CompareTo(start) <= 0)
            throw new InvalidDataException("A SPICE sensor requires a positive range and an increasing availability window.");
        var sensor = new MissionSensor(target, range, color, opacity, field.Boresight,
            field.Curved ? Array.AsReadOnly(field.Corners) : SensorEdgeRays(field.Corners), field.Curved ? 8 : field.Corners.Length)
        {
            FootprintOpacity = ReadFootprintOpacity(geometry),
            FootprintCorners = field.Curved ? 0 : field.Corners.Length
        };
        ObjectId center = resolve(parent);
        return new(resolve(name), name, null, .001,
            [new(start, end, center, _ => new CartesianState(default, default), Equatorial, new MissionFrame.BodyFixed(center))])
            { Geometry = [new(start, end, new(null, .001, Basis3d.Identity, default, sensor))] };
    }
    private static double ReadFootprintOpacity(JsonElement geometry)
    {
        double opacity = geometry.TryGetProperty("footprintOpacity", out var value) ? value.GetDouble() : 1;
        return opacity is >= 0 and <= 1 ? opacity :
            throw new InvalidDataException("A SPICE footprint opacity must be between zero and one.");
    }

    private static IReadOnlyList<Vector3d> SensorEdgeRays(IReadOnlyList<Vector3d> corners)
    {
        const int samplesPerEdge = 12;
        var rays = new Vector3d[corners.Count * samplesPerEdge];
        for (int side = 0; side < corners.Count; side++)
        {
            Vector3d first = corners[side], next = corners[(side + 1) % corners.Count];
            for (int sample = 0; sample < samplesPerEdge; sample++)
            {
                Vector3d ray = first + (next - first) * (sample / (double)samplesPerEdge);
                rays[side * samplesPerEdge + sample] = ray / ray.Length;
            }
        }
        return Array.AsReadOnly(rays);
    }
}
