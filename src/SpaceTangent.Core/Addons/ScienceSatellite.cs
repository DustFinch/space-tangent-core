// SPDX-License-Identifier: GPL-2.0-or-later
using SpaceTangent.Contracts;
using SpaceTangent.Core.Orbits;

namespace SpaceTangent.Core.Addons;

public readonly record struct ScienceSatelliteSample(CartesianState EarthRelativeState, Basis3d BodyToTrajectory,
    Basis3d MeshToBody)
{
    public Basis3d InstrumentToTrajectory { get; init; }
}

public sealed class ScienceSatellite
{
    private readonly Sgp4Orbit _orbit;
    private readonly SignedAxis _nadir;
    private readonly SignedAxis _relativeVelocity;
    private readonly Basis3d _meshToBody;
    private readonly Basis3d? _trajectoryAxes;

    internal ScienceSatellite(string name, TwoLineElements elements, SignedAxis nadir, SignedAxis relativeVelocity,
        Basis3d meshToBody, Basis3d instrumentToFrame, string meshPath, double sizeKilometers,
        Basis3d? trajectoryAxes = null, Basis3d? fixedBodyAxes = null)
    {
        Name = name;
        Id = new($"norad/{elements.CatalogNumber}");
        _orbit = new(elements);
        OrbitPeriodSeconds = Math.Tau * 60 / elements.MeanMotion;
        _nadir = nadir;
        _relativeVelocity = relativeVelocity;
        _meshToBody = meshToBody;
        _trajectoryAxes = trajectoryAxes;
        FixedBodyAxes = fixedBodyAxes;
        InstrumentToFrame = instrumentToFrame;
        MeshToOrientedBody = SampledMissionCatalog.Multiply(instrumentToFrame.Transpose(), meshToBody);
        MeshPath = meshPath;
        SizeKilometers = sizeKilometers;
    }

    public ObjectId Id { get; }
    public string Name { get; }
    public string MeshPath { get; }
    public double SizeKilometers { get; }
    public double OrbitPeriodSeconds { get; }
    public MissionDisplay? Display { get; internal init; }
    public Basis3d MeshToBody => _meshToBody;
    /// <summary>Mesh transform to apply after the world graph's physical spacecraft attitude.</summary>
    public Basis3d MeshToOrientedBody { get; }
    internal Basis3d InstrumentToFrame { get; }
    public JulianInstant Epoch => _orbit.Epoch;
    internal SignedAxis NadirAxis => _nadir;
    internal SignedAxis RelativeVelocityAxis => _relativeVelocity;

    internal Basis3d? FixedBodyAxes { get; }

    internal CartesianState? TryOrbit(JulianInstant epoch)
    {
        if (!_orbit.TrySample(epoch, out var state))
            return null;
        return _trajectoryAxes is { } axes
            ? new(axes.Transform(state.PositionKilometers), axes.Transform(state.VelocityKilometersPerSecond))
            : state;
    }

    /// <summary>The original catalogue interprets the raw SGP4 state in its declared frame.</summary>
    public static FrameId TrajectoryFrame { get; } = new("compatibility/equator-j2000");

    public bool TrySample(JulianInstant epoch, out ScienceSatelliteSample sample)
    {
        sample = default;
        if (TryOrbit(epoch) is not { } state)
            return false;
        Basis3d bodyToTrajectory = FixedBodyAxes.GetValueOrDefault();
        if (FixedBodyAxes is null && !TwoVectorBasis.TryCreate(_nadir, state.PositionKilometers * -1, _relativeVelocity,
            state.VelocityKilometersPerSecond * -1, out bodyToTrajectory))
            return false;

        sample = new(state, bodyToTrajectory, _meshToBody)
        {
            InstrumentToTrajectory = SampledMissionCatalog.Multiply(bodyToTrajectory, InstrumentToFrame)
        };
        return true;
    }
}
