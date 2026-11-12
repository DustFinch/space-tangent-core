// SPDX-License-Identifier: GPL-2.0-or-later
using System.Text.Json;
using SpaceTangent.Contracts;
using SpaceTangent.Core.Coordinates;
using SpaceTangent.Core.World;

namespace SpaceTangent.Core.Addons;

public static partial class SampledMissionCatalog
{
    private static readonly MissionFrame.Inertial PackedIcrf = new(PackedIcrfAxes());
    private static readonly MissionFrame.Inertial PackedB1950 = new(PackedB1950Axes());

    private static Basis3d PackedIcrfAxes()
    {
        // The positive-trace branch of the original Vesta frame-bias matrix, in astronomical axes.
        const double arcsec = Math.PI / (180 * 3600);
        double xi = -.0166170 * arcsec, eta = -.0068192 * arcsec, da = -.01460 * arcsec;
        double xx = 1 - .5 * (da * da + xi * xi), yy = 1 - .5 * (da * da + eta * eta), zz = 1 - .5 * (eta * eta + xi * xi);
        double trace = Math.Sqrt(((xx + yy) + zz) + 1), scale = .5 / trace;
        return QuaternionBasis.FromRaw(.5 * trace, -2 * eta * scale, 2 * xi * scale, 2 * da * scale);
    }

    private static Basis3d PackedB1950Axes()
    {
        // Vesta constructs this matrix column-first and retains the ecliptic rotation on the right.
        double trace = Math.Sqrt(((.9999256794956877 + .9999374848933135) + .9999881946023742) + 1), scale = .5 / trace;
        var bias = QuaternionBasis.FromRaw(.5 * trace, (-.0000271702937440 - (-.0000271625947142)) * scale,
            -(.0111814832391717 - (-.0111814832204662)) * scale, (-.0048590038153592 - .0048590037723143) * scale);
        return Multiply(bias, RotateX(23.4392911 * (Math.PI / 180)));
    }

    private static MissionFrame PackedFrame(JsonElement value, Func<string, ObjectId> resolve, ObjectId owner, int depth = 0)
    {
        if (depth >= 16)
            throw new InvalidDataException("Packed mission frame definitions exceed 16 nested levels.");
        return Text(value, "kind") switch
        {
            "bodyFixed" => new MissionFrame.BodyFixed(resolve(Text(value, "body"))),
            "eclipticJ2000" => Ecliptic,
            "equatorJ2000" => Equatorial,
            "cosmographiaIcrf" => PackedIcrf,
            "cosmographiaEquatorB1950" => PackedB1950,
            "cosmographiaTwoVector" or "twoVector" => TwoVector(),
            var kind => throw new NotSupportedException($"Unknown packed mission frame {kind}.")
        };

        MissionFrame TwoVector()
        {
            var primary = value.GetProperty("primary");
            var secondary = value.GetProperty("secondary");
            return new MissionFrame.TwoVector(Axis(Text(primary, "axis")), Side(primary), Axis(Text(secondary, "axis")), Side(secondary));
        }

        MissionDirection Side(JsonElement side)
        {
            var direction = side.GetProperty("direction");
            string kind = Text(direction, "kind");
            if (kind == "constant")
            {
                var frame = PackedFrame(direction.GetProperty("frame"), resolve, owner, depth + 1);
                var vector = Vector(direction.GetProperty("vector"));
                if (frame is MissionFrame.BodyFixed fixedFrame)
                    vector = LegacyBodyAxes(fixedFrame.Body, Basis3d.Identity).Transform(vector);
                return new MissionDirection.Constant(vector, frame);
            }
            var observer = direction.TryGetProperty("observer", out var from) ? resolve(from.GetString()!) : owner;
            var target = resolve(Text(direction, "target"));
            return new MissionDirection.Relative(new(observer, target, kind switch
            {
                "relativePosition" or "relativePositionFrom" => DirectionQuantity.Position,
                "relativeVelocity" or "relativeVelocityFrom" => DirectionQuantity.Velocity,
                _ => throw new NotSupportedException($"Unknown packed mission direction {kind}.")
            }));
        }
    }
}
