// SPDX-License-Identifier: GPL-2.0-or-later
namespace SpaceTangent.Contracts;

/// <summary>Geometric state in a common Cartesian basis at a common epoch.</summary>
public readonly record struct CartesianState(Vector3d PositionKilometers, Vector3d VelocityKilometersPerSecond)
{
    public static CartesianState operator +(CartesianState left, CartesianState right) =>
        new(left.PositionKilometers + right.PositionKilometers,
            left.VelocityKilometersPerSecond + right.VelocityKilometersPerSecond);

    public static CartesianState operator -(CartesianState left, CartesianState right) =>
        new(left.PositionKilometers - right.PositionKilometers,
            left.VelocityKilometersPerSecond - right.VelocityKilometersPerSecond);

    public static CartesianState operator *(CartesianState state, double scale) =>
        new(state.PositionKilometers * scale, state.VelocityKilometersPerSecond * scale);
}
