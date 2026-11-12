// SPDX-License-Identifier: GPL-2.0-or-later
using SpaceTangent.Contracts;

namespace SpaceTangent.Core.Ephemerides;

public interface ISolarSystemEphemeris
{
    JulianInstant Start { get; }
    JulianInstant End { get; }
    SolarSystemSample Sample(JulianInstant tdb);
}
