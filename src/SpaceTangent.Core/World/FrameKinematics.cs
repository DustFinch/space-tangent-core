// SPDX-License-Identifier: GPL-2.0-or-later
using SpaceTangent.Contracts;

namespace SpaceTangent.Core.World;

/// <summary>Orientation and, when available, angular velocity expressed in the world frame.</summary>
public readonly record struct FrameKinematics(Basis3d ToWorld, Vector3d? AngularVelocityRadiansPerSecond)
{
    internal bool TryTransform(CartesianState local, out CartesianState world)
    {
        world = default;
        if (AngularVelocityRadiansPerSecond is not { } angularVelocity)
            return false;

        Vector3d position = ToWorld.Transform(local.PositionKilometers);
        Vector3d velocity = ToWorld.Transform(local.VelocityKilometersPerSecond) +
            Vector3d.Cross(angularVelocity, position);
        world = new(position, velocity);
        return position.IsFinite && velocity.IsFinite;
    }
}

public enum DirectionQuantity { Position, Velocity }
public enum FrameAxis { X, NegativeX, Y, NegativeY, Z, NegativeZ }

public readonly record struct RelativeDirection(ObjectId Observer, ObjectId Target, DirectionQuantity Quantity);
