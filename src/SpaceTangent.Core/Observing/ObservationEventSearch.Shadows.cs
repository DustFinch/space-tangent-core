// SPDX-License-Identifier: GPL-2.0-or-later
using SpaceTangent.Contracts;

namespace SpaceTangent.Core.Observing;

public static partial class ObservationEventSearch
{
    /// <summary>Global umbral-shadow transits on a spherical receiver, not visibility from a particular observing site.</summary>
    public static IReadOnlyList<ObservationInterval> FindShadowTransits(
        Func<JulianInstant, (Vector3d Sun, Vector3d Receiver, Vector3d Occluder)?> positions,
        double receiverRadiusKilometers, double occluderRadiusKilometers, JulianInstant from, JulianInstant to,
        CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(positions);
        if (!(receiverRadiusKilometers > 0) || !(occluderRadiusKilometers > 0) ||
            !double.IsFinite(receiverRadiusKilometers + occluderRadiusKilometers))
            throw new ArgumentOutOfRangeException(nameof(receiverRadiusKilometers));
        double duration = ValidateWindow(from, to, 600);
        return FindPeaks(duration, 600, Score, true, 0, cancellation)
            .Select(peak => Contacts(from, duration, peak.Seconds, 600, Score, cancellation)).ToArray();

        double? Score(double seconds)
        {
            if (positions(from.AddSeconds(seconds)) is not { } at) return null;
            var source = at.Occluder - at.Sun;
            var receiver = at.Receiver - at.Occluder;
            double distance = source.Length;
            if (!source.IsFinite || !receiver.IsFinite || distance <= SolarOccultation.SunRadiusKilometers + occluderRadiusKilometers) return null;
            var axis = source / distance;
            double depth = Vector3d.Dot(receiver, axis);
            if (depth <= 0) return -receiverRadiusKilometers - receiver.Length;
            double lateral = (receiver - axis * depth).Length;
            double sine = (SolarOccultation.SunRadiusKilometers - occluderRadiusKilometers) / distance;
            double cosine = Math.Sqrt(1 - sine * sine);
            double slope = sine / cosine, baseRadius = occluderRadiusKilometers / cosine;
            // Distance to the solid umbral cone in its axial section. Clamp the side projection to the apex:
            // a hybrid eclipse can touch the receiver before an apex that lies short of its centre.
            double projected = Math.Max(0, (depth - slope * (lateral - baseRadius)) / (1 + slope * slope));
            if (slope > 0) projected = Math.Min(projected, baseRadius / slope);
            double dx = depth - projected, dy = lateral - (baseRadius - slope * projected);
            double separation = Math.Sqrt(dx * dx + dy * dy);
            bool inside = lateral <= baseRadius - slope * depth;
            return receiverRadiusKilometers + (inside ? separation : -separation);
        }
    }
}
