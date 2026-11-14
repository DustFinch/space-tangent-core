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
    private readonly (ConicOrbit? Before, ConicOrbit? After) _continuations;
    private readonly bool _sequentialArchive;
    private const int ContinuationBoundarySeconds = 365 * 86_400;

    internal ScienceSatellite(string name, TwoLineElements elements, SignedAxis nadir, SignedAxis relativeVelocity,
        Basis3d meshToBody, Basis3d instrumentToFrame, string meshPath, double sizeKilometers,
        Basis3d? trajectoryAxes = null, Basis3d? fixedBodyAxes = null, bool continueOutsideCoverage = false)
    {
        Name = name;
        Id = new($"norad/{elements.CatalogNumber}");
        _orbit = new(elements);
        _sequentialArchive = continueOutsideCoverage && _orbit.DeepSpace;
        OrbitPeriodSeconds = Math.Tau * 60 / elements.MeanMotion;
        _nadir = nadir;
        _relativeVelocity = relativeVelocity;
        _meshToBody = meshToBody;
        _trajectoryAxes = trajectoryAxes;
        FixedBodyAxes = fixedBodyAxes;
        if (continueOutsideCoverage)
            _continuations = (ContinuationAt(-ContinuationBoundarySeconds), ContinuationAt(ContinuationBoundarySeconds));
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

    internal Sgp4Orbit.Evaluation? CreateOrbitalHistory()
    {
        if (!_sequentialArchive)
            return null;
        var history = _orbit.CreateEvaluation();
        history.TrySample(Epoch.AddSeconds(-ContinuationBoundarySeconds), out _);
        history.TrySample(Epoch.AddSeconds(ContinuationBoundarySeconds), out _);
        return history;
    }

    internal CartesianState? TryOrbit(JulianInstant epoch, Sgp4Orbit.Evaluation? history = null)
    {
        bool available = history is null ? _orbit.TrySample(epoch, out var state) : history.TrySample(epoch, out state);
        if (!available)
        {
            double elapsed = (double)epoch.DifferenceNanoseconds(Epoch) / JulianInstant.NanosecondsPerSecond;
            var continuation = elapsed > ContinuationBoundarySeconds ? _continuations.After :
                elapsed < -ContinuationBoundarySeconds ? _continuations.Before : null;
            if (continuation is null)
                return null;
            state = continuation.Evaluate(epoch);
        }
        return _trajectoryAxes is { } axes
            ? new(axes.Transform(state.PositionKilometers), axes.Transform(state.VelocityKilometersPerSecond))
            : state;
    }

    private ConicOrbit? ContinuationAt(int seconds)
    {
        const double earthMu = 398600.4418;
        var epoch = Epoch.AddSeconds(seconds);
        if (!_orbit.TrySample(epoch, out var state))
            return null;

        var position = state.PositionKilometers;
        var velocity = state.VelocityKilometersPerSecond;
        double radius = position.Length;
        var angularMomentum = Vector3d.Cross(position, velocity);
        var pole = angularMomentum / angularMomentum.Length;
        var eccentricityVector = Vector3d.Cross(velocity, angularMomentum) / earthMu - position / radius;
        double eccentricity = eccentricityVector.Length;
        double inverseAxis = 2 / radius - Vector3d.Dot(velocity, velocity) / earthMu;
        if (!(inverseAxis > 0 && eccentricity < 1 && pole.IsFinite))
            return null;

        double inclination = Math.Atan2(Math.Sqrt(pole.X * pole.X + pole.Y * pole.Y), pole.Z);
        double node = Math.Atan2(pole.X, -pole.Y);
        var (sinNode, cosNode) = Math.SinCos(node);
        var ascending = new Vector3d(cosNode, sinNode, 0);
        var transverse = Vector3d.Cross(pole, ascending);
        var periapsis = eccentricity > 1e-12 ? eccentricityVector / eccentricity : ascending;
        double argument = Math.Atan2(Vector3d.Dot(periapsis, transverse), Vector3d.Dot(periapsis, ascending));
        double minorAxisRatio = Math.Sqrt(1 - eccentricity * eccentricity);
        double axis = 1 / inverseAxis;
        double anomaly = Math.Atan2(Vector3d.Dot(position, Vector3d.Cross(pole, periapsis)) / minorAxisRatio,
            Vector3d.Dot(position, periapsis) + axis * eccentricity);
        double meanAnomaly = anomaly - eccentricity * Math.Sin(anomaly);
        double motion = Math.Sqrt(earthMu / axis) / axis;
        return new(new(axis * (1 - eccentricity), eccentricity, inclination, node, argument,
            epoch.AddSeconds(-meanAnomaly / motion), earthMu, TrajectoryFrame));
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
