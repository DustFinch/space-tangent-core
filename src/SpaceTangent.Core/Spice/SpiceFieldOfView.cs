// SPDX-License-Identifier: GPL-2.0-or-later
using System.Globalization;
using SpaceTangent.Contracts;

namespace SpaceTangent.Core.Spice;

/// <summary>Unit rays from a rectangular instrument, transformed into its spacecraft's CK frame.</summary>
internal sealed record SpiceFieldOfView(int InstrumentId, Vector3d Boresight, Vector3d[] Corners)
{
    internal static SpiceFieldOfView Load(SpiceTextKernel pool, string instrument, string bodyFrame)
    {
        int id = ResolveInstrument(pool, instrument);
        string prefix = "INS" + id.ToString(CultureInfo.InvariantCulture);
        string frame = Text(pool, prefix + "_FOV_FRAME");
        if (Text(pool, prefix + "_FOV_SHAPE") != "RECTANGLE")
            throw new NotSupportedException("Only rectangular SPICE instrument fields are supported.");

        Vector3d boresight = Unit(Vector(pool, prefix + "_BORESIGHT"));
        Vector3d[] corners;
        string spec = prefix + "_FOV_CLASS_SPEC";
        string mode = pool.Contains(spec) ? Text(pool, spec) : "CORNERS";
        switch (mode)
        {
            case "ANGLES":
                Vector3d reference = Unit(Vector(pool, prefix + "_FOV_REF_VECTOR"));
                Vector3d vertical = Unit(Vector3d.Cross(boresight, reference));
                Vector3d horizontal = Vector3d.Cross(vertical, boresight);
                double unit = SpiceFixedFrames.AngleUnit(Text(pool, prefix + "_FOV_ANGLE_UNITS"));
                double halfWidth = Numbers(pool, prefix + "_FOV_REF_ANGLE", 1)[0] * unit;
                double halfHeight = Numbers(pool, prefix + "_FOV_CROSS_ANGLE", 1)[0] * unit;
                if (halfWidth is <= 0 or >= Math.PI / 2 || halfHeight is <= 0 or >= Math.PI / 2)
                    throw new InvalidDataException("SPICE camera half angles must be between zero and ninety degrees.");
                horizontal *= Math.Tan(halfWidth);
                vertical *= Math.Tan(halfHeight);
                corners = [Unit(boresight + horizontal + vertical), Unit(boresight - horizontal + vertical),
                    Unit(boresight - horizontal - vertical), Unit(boresight + horizontal - vertical)];
                break;
            case "CORNERS":
                double[] values = Numbers(pool, prefix + "_FOV_BOUNDARY_CORNERS", 12);
                corners = new Vector3d[4];
                for (int i = 0; i < corners.Length; i++)
                    corners[i] = Unit(new(values[3 * i], values[3 * i + 1], values[3 * i + 2]));
                break;
            default:
                throw new NotSupportedException("Unknown SPICE field-of-view specification.");
        }
        Basis3d toBody = SpiceFixedFrames.Between(pool, frame, bodyFrame);
        return new(id, toBody.Transform(boresight), corners.Select(toBody.Transform).ToArray());
    }

    private static int ResolveInstrument(SpiceTextKernel pool, string name)
    {
        if (int.TryParse(name, NumberStyles.Integer, CultureInfo.InvariantCulture, out int number))
            return number;
        string[] names = pool.Get("NAIF_BODY_NAME"), ids = pool.Get("NAIF_BODY_CODE");
        if (names.Length != ids.Length)
            throw new InvalidDataException("SPICE instrument aliases and codes have different lengths.");
        for (int index = names.Length - 1; index >= 0; index--)
        {
            if (names[index].Equals(name, StringComparison.OrdinalIgnoreCase) &&
                int.TryParse(ids[index], NumberStyles.Integer, CultureInfo.InvariantCulture, out int id))
                return id;
        }
        throw new InvalidDataException($"Unknown SPICE instrument {name}.");
    }

    private static Vector3d Vector(SpiceTextKernel pool, string key)
    {
        double[] values = Numbers(pool, key, 3);
        return new(values[0], values[1], values[2]);
    }

    private static Vector3d Unit(Vector3d value)
    {
        double length = value.Length;
        if (!double.IsFinite(length) || length < 1e-15)
            throw new InvalidDataException("Invalid SPICE instrument direction.");
        return value / length;
    }

    private static string Text(SpiceTextKernel pool, string key)
    {
        string[] values = pool.Get(key);
        return values.Length == 1 ? values[0] : throw new InvalidDataException($"Expected one value in {key}.");
    }

    private static double[] Numbers(SpiceTextKernel pool, string key, int count)
    {
        double[] values = pool.GetNumbers(key);
        return values.Length == count ? values : throw new InvalidDataException($"Expected {count} numbers in {key}.");
    }
}
