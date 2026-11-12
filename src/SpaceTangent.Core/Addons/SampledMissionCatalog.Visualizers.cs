// SPDX-License-Identifier: GPL-2.0-or-later
using System.Text.Json;
using SpaceTangent.Contracts;
using SpaceTangent.Core.World;

namespace SpaceTangent.Core.Addons;

/// <summary>A grid in the mission body's XY plane or a line toward a target sampled at the observer's epoch.</summary>
public sealed record MissionVectorStyle(double SizeKilometers, Vector3d Color, double Opacity, int Subdivisions,
    ObjectId? Target, bool Visible)
{
    public bool TryDirection(WorldSnapshot world, Vector3d origin, out Vector3d direction)
    {
        direction = default;
        if (Target is not { } id || !world.TryState(id, out var state))
            return false;
        Vector3d difference = state.PositionKilometers - origin;
        double distance = difference.Length;
        if (!(distance > 0) || !double.IsFinite(distance))
            return false;
        direction = difference / distance;
        return true;
    }
}

public static partial class SampledMissionCatalog
{
    private static MissionShape ReadVectorShape(JsonElement value, bool plane, Func<string, ObjectId> resolve,
        bool packed = false)
    {
        string sizeField = packed ? "sizeKm" : "size";
        double size = value.TryGetProperty(sizeField, out _) ? Distance(value, sizeField) : plane ? 1000 : 1;
        if (!(size > 0) || !float.IsFinite((float)size))
            throw new InvalidDataException("A mission visualizer requires a finite positive size.");
        double opacity = value.TryGetProperty("opacity", out var alpha) ? ParticleNumber(alpha) : plane ? .2 : 1;
        Vector3d color = value.TryGetProperty("color", out var rgb) ? ParticleColor(rgb, opacity).Color : new(1, 1, 1);
        int subdivisions = value.TryGetProperty(packed ? "subdivisions" : "gridSubdivision", out var grid)
            ? (int)Math.Clamp(ParticleNumber(grid), 1, 100) : 10;
        ObjectId? target = plane ? null : resolve(Text(value, "target"));
        bool visible = !value.TryGetProperty("visible", out var show) || show.GetBoolean();
        return new(null, size, Basis3d.Identity, default)
        {
            VectorOverlay = new(size, color, Math.Clamp(opacity, 0, 1), subdivisions, target, visible)
        };
    }
}
