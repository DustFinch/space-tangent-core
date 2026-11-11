// SPDX-License-Identifier: GPL-2.0-or-later
using SpaceTangent.Contracts;

namespace SpaceTangent.Core.Spice;

internal static class SpiceEuler
{
    internal static Basis3d Compose(ReadOnlySpan<double> angles, ReadOnlySpan<double> axes)
    {
        var result = Basis3d.Identity;
        for (int index = 2; index >= 0; index--)
        {
            double angle = angles[index];
            double sin = Math.Sin(angle), cos = Math.Cos(angle);
            Basis3d rotation = axes[index] switch
            {
                1 => new(new(1, 0, 0), new(0, cos, -sin), new(0, sin, cos)),
                2 => new(new(cos, 0, sin), new(0, 1, 0), new(-sin, 0, cos)),
                3 => new(new(cos, -sin, 0), new(sin, cos, 0), new(0, 0, 1)),
                _ => throw new InvalidDataException("Invalid SPICE Euler rotation axis.")
            };
            result = new(rotation.Transform(result.X), rotation.Transform(result.Y), rotation.Transform(result.Z));
        }
        return result;
    }
}
