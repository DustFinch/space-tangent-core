// SPDX-License-Identifier: GPL-2.0-or-later
using SpaceTangent.Contracts;
using SpaceTangent.Core.Orbits;

namespace SpaceTangent.Core.Addons;

public readonly record struct ScienceSatelliteSample(CartesianState EarthRelativeState, Basis3d BodyToTrajectory,
    Basis3d MeshToBody);

public sealed class ScienceSatellite
{
    private readonly Sgp4Orbit _orbit;
    private readonly SignedAxis _nadir;
    private readonly SignedAxis _relativeVelocity;
    private readonly Basis3d _meshToBody;

    internal ScienceSatellite(string name, TwoLineElements elements, SignedAxis nadir, SignedAxis relativeVelocity,
        Basis3d meshToBody, string meshPath, double sizeKilometers)
    {
        Name = name;
        Id = new($"norad/{elements.CatalogNumber}");
        _orbit = new(elements);
        _nadir = nadir;
        _relativeVelocity = relativeVelocity;
        _meshToBody = meshToBody;
        MeshPath = meshPath;
        SizeKilometers = sizeKilometers;
    }

    public ObjectId Id { get; }
    public string Name { get; }
    public string MeshPath { get; }
    public double SizeKilometers { get; }
    public JulianInstant Epoch => _orbit.Epoch;

    /// <summary>The original catalogue interprets the raw SGP4 state in its declared frame.</summary>
    public static FrameId TrajectoryFrame { get; } = new("compatibility/equator-j2000");

    public bool TrySample(JulianInstant epoch, out ScienceSatelliteSample sample)
    {
        sample = default;
        if (!_orbit.TrySample(epoch, out var state) ||
            !TwoVectorBasis.TryCreate(_nadir, state.PositionKilometers * -1, _relativeVelocity,
                state.VelocityKilometersPerSecond * -1, out var bodyToTrajectory))
            return false;

        sample = new(state, bodyToTrajectory, _meshToBody);
        return true;
    }
}
