// SPDX-License-Identifier: GPL-2.0-or-later
using SpaceTangent.Contracts;

namespace SpaceTangent.Core.World;

public sealed partial class ProceduralTerrain
{
    public const int HorizonAzimuths = 8;
    private const int HorizonSteps = 12;

    /// <summary>Computes the greatest terrain elevation in eight directions around a body-local point.</summary>
    public void SampleHorizon(Vector3d positionKilometers, double spacingKilometers, Span<float> elevationSines)
    {
        if (elevationSines.Length != HorizonAzimuths)
            throw new ArgumentException("Eight horizon values are required.", nameof(elevationSines));
        if (!(positionKilometers.Length > 0) || !double.IsFinite(spacingKilometers) || spacingKilometers <= 0)
            throw new ArgumentOutOfRangeException(nameof(spacingKilometers));

        var up = positionKilometers / positionKilometers.Length;
        var east = Vector3d.Cross(new(0, 1, 0), up);
        east = east.Length > 1e-5 ? east / east.Length : new(1, 0, 0);
        var north = Vector3d.Cross(up, east);
        double reach = Math.Min(200, .3 * _radius);
        double first = Math.Min(2 * spacingKilometers, reach / 4);
        double growth = Math.Pow(reach / first, 1.0 / (HorizonSteps - 1));
        for (int azimuth = 0; azimuth < HorizonAzimuths; azimuth++)
        {
            var (sin, cos) = Math.SinCos(azimuth * Math.Tau / HorizonAzimuths);
            var horizontal = east * cos + north * sin;
            double highest = -.25, distance = first;
            for (int step = 0; step < HorizonSteps; step++, distance *= growth)
            {
                var (s, c) = Math.SinCos(distance / _radius);
                var direction = up * c + horizontal * s;
                double radius = EllipsoidRadius(direction) + VisibleHeight(direction, Math.Max(2 * spacingKilometers, distance / 2));
                var offset = direction * radius - positionKilometers;
                double length = offset.Length;
                if (length > 0)
                    highest = Math.Max(highest, Vector3d.Dot(offset, up) / length);
            }
            elevationSines[azimuth] = (float)highest;
        }
    }
}
