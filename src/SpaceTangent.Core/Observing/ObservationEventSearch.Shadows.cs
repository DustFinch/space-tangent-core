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
            var sun = at.Sun - at.Receiver;
            var moon = at.Occluder - at.Receiver;
            double distance = sun.Length;
            if (!sun.IsFinite || !moon.IsFinite || distance <= SolarOccultation.SunRadiusKilometers) return null;
            double depth = Vector3d.Dot(moon, sun / distance);
            if (depth <= 0 || depth >= distance) return -receiverRadiusKilometers - moon.Length;
            double shadow = occluderRadiusKilometers - depth * (SolarOccultation.SunRadiusKilometers - occluderRadiusKilometers) / (distance - depth);
            if (shadow <= 0) return -receiverRadiusKilometers;
            double lateral = (moon - sun * (depth / distance)).Length;
            return receiverRadiusKilometers + shadow - lateral;
        }
    }
}
