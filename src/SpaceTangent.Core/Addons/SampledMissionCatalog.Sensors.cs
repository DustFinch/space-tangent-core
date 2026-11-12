// SPDX-License-Identifier: GPL-2.0-or-later
using System.Globalization;
using System.Text.Json;
using SpaceTangent.Contracts;

namespace SpaceTangent.Core.Addons;

public static partial class SampledMissionCatalog
{
    private static MissionSensor ReadSensor(JsonElement value, Func<string, ObjectId> resolve)
    {
        ObjectId target = resolve(Text(value, "target"));
        double range = value.TryGetProperty("range", out _) ? Distance(value, "range") : 1000;
        if (!(range > 0))
            throw new InvalidDataException("A sensor range must be positive.");
        var (color, opacity) = SensorStyle(value);
        bool rectangle = value.TryGetProperty("shape", out var shape) && shape.GetString() == "rectangular";
        double x = HalfExtent("horizontalFov"), y = HalfExtent("verticalFov");
        Basis3d basis = value.TryGetProperty("orientation", out var attitude) ? Quaternion(attitude) : Basis3d.Identity;
        const int rayCount = 48;
        var directions = new Vector3d[rayCount];
        for (int index = 0; index < directions.Length; index++)
        {
            double phase = index / (double)rayCount * Math.Tau;
            Vector3d ray = rectangle ? RectangleRay(index, x, y) : new(x * Math.Cos(phase), y * Math.Sin(phase), 1);
            directions[index] = basis.Transform(ray / ray.Length);
        }
        return new(target, range, color, opacity, basis.Z, Array.AsReadOnly(directions), rectangle ? 4 : 8);

        double HalfExtent(string field)
        {
            double radians = 5;
            if (value.TryGetProperty(field, out var input))
            {
                if (!double.TryParse(input.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double degrees) ||
                    !double.IsFinite(degrees))
                    throw new InvalidDataException("A sensor field angle must be finite.");
                radians = degrees * (Math.PI / 180);
            }
            return Math.Tan(radians / 2);
        }
    }

    private static (Vector3d Color, double Opacity) SensorStyle(JsonElement value)
    {
        Vector3d color = value.TryGetProperty("frustumColor", out var rgb) ? Vector(rgb) : new(1, 1, 1);
        double opacity = value.TryGetProperty("frustumOpacity", out var alpha) ? alpha.GetDouble() : .3;
        if (!double.IsFinite(opacity) || opacity is < 0 or > 1 ||
            color.X is < 0 or > 1 || color.Y is < 0 or > 1 || color.Z is < 0 or > 1)
            throw new InvalidDataException("Sensor colour and opacity require values between zero and one.");
        return (color, opacity);
    }

    private static Vector3d RectangleRay(int index, double x, double y)
    {
        const int side = 12;
        double along = index % side / (double)side - .5;
        return (index / side) switch
        {
            0 => new(along * x, -.5 * y, 1),
            1 => new(.5 * x, along * y, 1),
            2 => new(-along * x, .5 * y, 1),
            _ => new(-.5 * x, -along * y, 1)
        };
    }
}
