// SPDX-License-Identifier: GPL-2.0-or-later
using SpaceTangent.Contracts;
using SpaceTangent.Core.Spice;

namespace SpaceTangent.Core.Addons;

public sealed class SpiceSpacecraft
{
    private readonly SpkKernel[] _kernels;
    private readonly SpiceAttitude? _attitude;

    internal SpiceSpacecraft(string name, string parent, int target, int center, string meshPath, double radius,
        string? attitudeFrame, string? attitudeBaseFrame, SpiceResources resources)
    {
        Name = name;
        Parent = parent;
        Target = target;
        Center = center;
        MeshPath = meshPath;
        RadiusKilometers = radius;
        AttitudeFrame = attitudeFrame;
        AttitudeBaseFrame = attitudeBaseFrame;
        _kernels = resources.TrajectoryKernels();
        _attitude = attitudeFrame is null ? null : new SpiceAttitude(attitudeFrame, attitudeBaseFrame!, resources.Files);
    }

    public string Name { get; }
    public string Parent { get; }
    public int Target { get; }
    public int Center { get; }
    public string MeshPath { get; }
    public double RadiusKilometers { get; }
    public string? AttitudeFrame { get; }
    public string? AttitudeBaseFrame { get; }
    public static FrameId Frame { get; } = new("icrf3");

    public bool TrySample(JulianInstant tdb, out CartesianState state)
    {
        for (int index = _kernels.Length - 1; index >= 0; index--)
        {
            if (_kernels[index].TryState(Target, Center, tdb, out state))
                return true;
        }
        state = default;
        return false;
    }

    public bool TryAttitude(JulianInstant tdb, out Basis3d bodyToReference)
    {
        if (_attitude is { } attitude)
            return attitude.TrySample(tdb, out bodyToReference);
        bodyToReference = default;
        return false;
    }
}
