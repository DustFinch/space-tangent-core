// SPDX-License-Identifier: GPL-2.0-or-later
using SpaceTangent.Contracts;
using SpaceTangent.Core.Orbits;

namespace SpaceTangent.Core.Addons;

internal static class SscConstantOrbitFrame
{
    private static readonly Basis3d Ecliptic = CreateEcliptic();

    internal static Basis3d Read(SscValue.Map definition)
    {
        var (primaryAxis, primary) = Side(definition.Object("Primary"));
        var (secondaryAxis, secondary) = Side(definition.Object("Secondary"));
        if (primaryAxis.Index == secondaryAxis.Index ||
            !TwoVectorBasis.TryCreate(primaryAxis, primary, secondaryAxis, secondary, out var basis))
            throw new InvalidDataException("SSC orbit axes require two independent constant directions.");
        return basis;
    }

    private static (SignedAxis Axis, Vector3d Direction) Side(SscValue.Map side)
    {
        if (!side.Fields.ContainsKey("ConstantVector"))
            throw new NotSupportedException("This SSC orbit frame requires constant directions.");
        var constant = side.Object("ConstantVector");
        var direction = SscBodyCatalogue.ReadVector(constant.Get("Vector"));
        if (constant.Fields.ContainsKey("Frame"))
        {
            var frames = constant.Object("Frame");
            if (frames.Fields.Count != 1)
                throw new InvalidDataException("An SSC orbit direction requires one reference frame.");
            direction = frames.Fields.Keys.Single() switch
            {
                "EquatorJ2000" => direction,
                "EclipticJ2000" => Ecliptic.Transform(direction),
                var frame => throw new NotSupportedException($"Unsupported SSC orbit vector frame {frame}.")
            };
        }
        else
            direction = Ecliptic.Transform(direction);
        return (SignedAxis.Parse(side.Text("Axis")), direction);
    }

    private static Basis3d CreateEcliptic()
    {
        var (sin, cos) = Math.SinCos(23.4392911 * Math.PI / 180);
        return new(new(1, 0, 0), new(0, cos, sin), new(0, -sin, cos));
    }
}
