// SPDX-License-Identifier: GPL-2.0-or-later
using SpaceTangent.Contracts;

namespace SpaceTangent.Core.Ephemerides;

/// <summary>Planetary-system states in the source ICRF3 basis, with solar-system barycentric origin.</summary>
public readonly record struct SolarSystemSample(JulianInstant Epoch, CartesianState Sun, CartesianState Earth,
    CartesianState Moon, CartesianState SaturnBarycenter)
{
    public static ObjectId SunId { get; } = new("sol/sun");
    public static ObjectId EarthId { get; } = new("sol/earth");
    public static ObjectId MoonId { get; } = new("sol/moon");
    public static ObjectId MercuryId { get; } = new("sol/mercury");
    public static ObjectId VenusId { get; } = new("sol/venus");
    public static ObjectId EarthMoonBarycenterId { get; } = new("sol/earth-moon-barycenter");
    public static ObjectId MarsBarycenterId { get; } = new("sol/mars-barycenter");
    public static ObjectId JupiterBarycenterId { get; } = new("sol/jupiter-barycenter");
    public static ObjectId UranusBarycenterId { get; } = new("sol/uranus-barycenter");
    public static ObjectId NeptuneBarycenterId { get; } = new("sol/neptune-barycenter");
    public static ObjectId PlutoBarycenterId { get; } = new("sol/pluto-barycenter");
    public static ObjectId SaturnBarycenterId { get; } = new("sol/saturn-barycenter");
    public static ObjectId BarycenterId { get; } = new("sol/barycenter");
    internal static IReadOnlyList<ObjectId> StateIds { get; } = Array.AsReadOnly(new[]
    {
        BarycenterId, SunId, MercuryId, VenusId, EarthMoonBarycenterId, EarthId, MoonId, MarsBarycenterId,
        JupiterBarycenterId, SaturnBarycenterId, UranusBarycenterId, NeptuneBarycenterId, PlutoBarycenterId
    });
    public CartesianState Mercury { get; init; }
    public CartesianState Venus { get; init; }
    public CartesianState EarthMoonBarycenter { get; init; }
    public CartesianState MarsBarycenter { get; init; }
    public CartesianState JupiterBarycenter { get; init; }
    public CartesianState UranusBarycenter { get; init; }
    public CartesianState NeptuneBarycenter { get; init; }
    public CartesianState PlutoBarycenter { get; init; }
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
        if (id == MercuryId)
            return Mercury;
        if (id == VenusId)
            return Venus;
        if (id == EarthMoonBarycenterId)
            return EarthMoonBarycenter;
        if (id == MarsBarycenterId)
            return MarsBarycenter;
        if (id == JupiterBarycenterId)
            return JupiterBarycenter;
        if (id == SaturnBarycenterId)
            return SaturnBarycenter;
        if (id == UranusBarycenterId)
            return UranusBarycenter;
        if (id == NeptuneBarycenterId)
            return NeptuneBarycenter;
        if (id == PlutoBarycenterId)
            return PlutoBarycenter;
        if (id == BarycenterId)
            return default;
        throw new ArgumentOutOfRangeException(nameof(id), id, "Object is outside this ephemeris.");
    }
}
