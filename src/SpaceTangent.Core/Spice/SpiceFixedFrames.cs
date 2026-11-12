// SPDX-License-Identifier: GPL-2.0-or-later
using System.Globalization;
using SpaceTangent.Contracts;
using SpaceTangent.Core.Coordinates;

namespace SpaceTangent.Core.Spice;

internal static class SpiceFixedFrames
{
    internal static (string Root, Basis3d ToRoot) Resolve(SpiceTextKernel pool, string frame)
    {
        var visited = new HashSet<int>();
        var basis = Basis3d.Identity;
        while (!frame.Equals("J2000", StringComparison.OrdinalIgnoreCase))
        {
            int id = pool.GetInteger("FRAME_" + frame);
            string key = "FRAME_" + id.ToString(CultureInfo.InvariantCulture);
            if (pool.GetInteger(key + "_CLASS") != 4)
                break;
            if (visited.Count == 12 || !visited.Add(id))
                throw new InvalidDataException("A fixed SPICE frame chain contains a cycle or exceeds twelve frames.");
            int classId = pool.GetInteger(key + "_CLASS_ID");
            string local = "TKFRAME_" + classId.ToString(CultureInfo.InvariantCulture);
            basis = Compose(Transform(pool, local), basis);
            frame = Text(pool, local + "_RELATIVE");
        }
        return (frame, basis);
    }

    internal static Basis3d Between(SpiceTextKernel pool, string from, string to)
    {
        if (from.Equals(to, StringComparison.OrdinalIgnoreCase))
            return Basis3d.Identity;
        var (first, source) = Resolve(pool, from);
        var (second, destination) = Resolve(pool, to);
        if (!first.Equals(second, StringComparison.OrdinalIgnoreCase))
        {
            string firstKey = Key(first), secondKey = Key(second);
            if (pool.GetInteger(firstKey + "_CLASS") != 3 || pool.GetInteger(secondKey + "_CLASS") != 3 ||
                pool.GetInteger(firstKey + "_CLASS_ID") != pool.GetInteger(secondKey + "_CLASS_ID"))
                throw new NotSupportedException("SPICE instrument frames must share a fixed or CK reference.");
        }
        return Compose(destination.Transpose(), source);

        string Key(string name) => "FRAME_" + pool.GetInteger("FRAME_" + name).ToString(CultureInfo.InvariantCulture);
    }

    internal static Basis3d Compose(Basis3d parent, Basis3d local) =>
        new(parent.Transform(local.X), parent.Transform(local.Y), parent.Transform(local.Z));

    internal static Basis3d Transform(SpiceTextKernel pool, string key)
    {
        string kind = Text(pool, key + "_SPEC");
        if (kind == "ANGLES")
        {
            double scale = AngleUnit(Text(pool, key + "_UNITS"));
            double[] angles = Numbers(pool, key + "_ANGLES", 3), axes = Numbers(pool, key + "_AXES", 3);
            return SpiceEuler.Compose([angles[0] * scale, angles[1] * scale, angles[2] * scale], axes);
        }
        if (kind == "MATRIX")
        {
            double[] m = Numbers(pool, key + "_MATRIX", 9);
            var basis = new Basis3d(new(m[0], m[1], m[2]), new(m[3], m[4], m[5]), new(m[6], m[7], m[8]));
            if (Math.Abs(basis.X.Length - 1) > 1e-5 || Math.Abs(basis.Y.Length - 1) > 1e-5 ||
                (Vector3d.Cross(basis.X, basis.Y) - basis.Z).Length > 1e-5)
                throw new InvalidDataException("A fixed SPICE frame needs a proper rotation matrix.");
            return basis;
        }
        if (kind == "QUATERNION")
        {
            double[] q = Numbers(pool, key + "_Q", 4);
            var normalized = new RigidQuaternion(q[0], q[1], q[2], q[3]).Normalize();
            return QuaternionBasis.FromRaw(normalized.W, normalized.X, normalized.Y, normalized.Z);
        }
        throw new NotSupportedException($"Unsupported fixed SPICE frame specification {kind}.");
    }

    internal static double AngleUnit(string unit) => unit switch
    {
        "DEGREES" => Math.PI / 180,
        "RADIANS" => 1,
        "ARCSECONDS" => Math.PI / 648000,
        "ARCMINUTES" => Math.PI / 10800,
        "HOURANGLE" => Math.PI / 12,
        "MINUTEANGLE" => Math.PI / 720,
        "SECONDANGLE" => Math.PI / 43200,
        _ => throw new NotSupportedException($"Unsupported SPICE angular unit {unit}.")
    };

    private static string Text(SpiceTextKernel pool, string key) => pool.Get(key) is [string value] ? value :
        throw new InvalidDataException($"Expected one SPICE value in {key}.");

    private static double[] Numbers(SpiceTextKernel pool, string key, int count) => pool.GetNumbers(key) is { } values &&
        values.Length == count ? values : throw new InvalidDataException($"Expected {count} SPICE numbers in {key}.");
}
