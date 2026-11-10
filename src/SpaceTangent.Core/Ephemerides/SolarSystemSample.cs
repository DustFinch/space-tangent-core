// SPDX-License-Identifier: GPL-2.0-or-later
using SpaceTangent.Contracts;

namespace SpaceTangent.Core.Ephemerides;

/// <summary>Sun, Earth and Moon states in the source ICRF3 basis, with solar-system barycentric origin.</summary>
public readonly record struct SolarSystemSample(JulianInstant Epoch, CartesianState Sun, CartesianState Earth,
    CartesianState Moon)
{
    public static ObjectId SunId { get; } = new("sol/sun");
    public static ObjectId EarthId { get; } = new("sol/earth");
    public static ObjectId MoonId { get; } = new("sol/moon");
    public static ObjectId BarycenterId { get; } = new("sol/barycenter");
    public FrameId Frame => Icrf3;
    private static FrameId Icrf3 { get; } = new("icrf3");

    public CartesianState RelativeState(ObjectId target, ObjectId center) =>
        BarycentricState(target) - BarycentricState(center);

    private CartesianState BarycentricState(ObjectId id)
    {
        if (id == SunId)
            return Sun;
        if (id == EarthId)
            return Earth;
        if (id == MoonId)
            return Moon;
        if (id == BarycenterId)
            return default;
        throw new ArgumentOutOfRangeException(nameof(id), id, "Object is outside this ephemeris.");
    }
}
