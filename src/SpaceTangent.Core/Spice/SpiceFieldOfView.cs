// SPDX-License-Identifier: GPL-2.0-or-later
using System.Globalization;
using SpaceTangent.Contracts;

namespace SpaceTangent.Core.Spice;

/// <summary>Unit rays along the instrument boundary in its spacecraft's body frame.</summary>
internal sealed record SpiceFieldOfView(int InstrumentId, Vector3d Boresight, Vector3d[] Corners, bool Curved)
{
    private const int RimSamples = 32;
    private const int MaximumPolygonVertices = 64;

    internal static SpiceFieldOfView Load(SpiceTextKernel pool, string instrument, string bodyFrame)
    {
        int id = ResolveInstrument(pool, instrument);
        string prefix = "INS" + id.ToString(CultureInfo.InvariantCulture);
        string frame = Text(pool, prefix + "_FOV_FRAME");
        string shape = Text(pool, prefix + "_FOV_SHAPE").ToUpperInvariant();
        bool curved = shape is "CIRCLE" or "ELLIPSE";
        if (!curved && shape is not ("RECTANGLE" or "POLYGON"))
            throw new NotSupportedException($"Unsupported SPICE instrument field shape {shape}.");

        Vector3d boresight = Unit(Vector(pool, prefix + "_BORESIGHT"));
        string spec = prefix + "_FOV_CLASS_SPEC";
        string mode = pool.Contains(spec) ? Text(pool, spec).ToUpperInvariant() : "CORNERS";
        Vector3d[] boundary = mode switch
        {
            "ANGLES" when shape != "POLYGON" => FromAngles(),
            "CORNERS" => FromCorners(),
            _ => throw new NotSupportedException($"Unsupported SPICE field specification {mode} for {shape}.")
        };
        Basis3d toBody = SpiceFixedFrames.Between(pool, frame, bodyFrame);
        return new(id, toBody.Transform(boresight), boundary.Select(toBody.Transform).ToArray(), curved);

        Vector3d[] FromAngles()
        {
            Vector3d reference = Unit(Vector(pool, prefix + "_FOV_REF_VECTOR"));
            Vector3d vertical = Unit(Vector3d.Cross(boresight, reference));
            Vector3d horizontal = Vector3d.Cross(vertical, boresight);
            string units = prefix + "_FOV_ANGLE_UNITS";
            double unit = pool.Contains(units) ? SpiceFixedFrames.AngleUnit(Text(pool, units).ToUpperInvariant()) : 1;
            double width = HalfAngle("_FOV_REF_ANGLE");
            double height = shape == "CIRCLE" ? width : HalfAngle("_FOV_CROSS_ANGLE");
            if (shape == "CIRCLE")
                return Rim(Ray(width, horizontal), Ray(width, vertical));
            if (shape == "ELLIPSE")
                return Rim(Ray(width, horizontal), Ray(height, vertical));
            horizontal *= Math.Tan(width);
            vertical *= Math.Tan(height);
            return [Unit(boresight + horizontal + vertical), Unit(boresight - horizontal + vertical),
                Unit(boresight - horizontal - vertical), Unit(boresight + horizontal - vertical)];

            double HalfAngle(string key)
            {
                double radians = Numbers(pool, prefix + key, 1)[0] * unit;
                if (radians <= 0 || radians > Math.PI / 2 || (shape != "CIRCLE" && radians == Math.PI / 2))
                    throw new InvalidDataException("Invalid SPICE instrument half angle.");
                return radians;
            }

            Vector3d Ray(double angle, Vector3d axis) => boresight * Math.Cos(angle) + axis * Math.Sin(angle);
        }

        Vector3d[] FromCorners()
        {
            double[] values = pool.GetNumbers(prefix + "_FOV_BOUNDARY_CORNERS");
            int count = values.Length / 3;
            bool valid = shape switch
            {
                "CIRCLE" => count == 1,
                "ELLIPSE" => count == 2,
                "RECTANGLE" => count == 4,
                _ => count is >= 3 and <= MaximumPolygonVertices
            };
            if (!valid || values.Length % 3 != 0)
                throw new InvalidDataException($"Invalid SPICE {shape} boundary vector count.");
            var rays = new Vector3d[count];
            for (int i = 0; i < count; i++)
                rays[i] = Unit(new(values[3 * i], values[3 * i + 1], values[3 * i + 2]));
            if (!curved)
                return rays;
            Vector3d first = rays[0];
            Vector3d second = shape == "CIRCLE"
                ? boresight * Vector3d.Dot(first, boresight) + Vector3d.Cross(boresight, first)
                : rays[1];
            return Rim(first, second);
        }

        Vector3d[] Rim(Vector3d first, Vector3d second)
        {
            double forward = Vector3d.Dot(first, boresight), crossForward = Vector3d.Dot(second, boresight);
            Vector3d across = first - boresight * forward, cross = second - boresight * crossForward;
            if (forward < -1e-14 || crossForward < -1e-14 || Vector3d.Cross(across, cross).Length < 1e-15 ||
                (shape == "ELLIPSE" && (forward <= 0 || crossForward <= 0)))
                throw new InvalidDataException("Invalid SPICE instrument rim axes.");
            // Scale the tangent-plane expression to retain the circular rim at a 90-degree half angle.
            if (shape == "CIRCLE")
                return Enumerable.Range(0, RimSamples).Select(i =>
                {
                    var (sin, cos) = Math.SinCos(i * Math.Tau / RimSamples);
                    return Unit(boresight * forward + across * cos + cross * sin);
                }).ToArray();
            return Enumerable.Range(0, RimSamples).Select(i =>
            {
                var (sin, cos) = Math.SinCos(i * Math.Tau / RimSamples);
                return Unit(boresight + across * (cos / forward) + cross * (sin / crossForward));
            }).ToArray();
        }
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
