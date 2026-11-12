// SPDX-License-Identifier: GPL-2.0-or-later
using System.Globalization;

namespace SpaceTangent.Core.Spice;

internal static class NaifIds
{
    private static readonly Dictionary<string, int> Names = new(StringComparer.OrdinalIgnoreCase)
    {
        ["SOLAR SYSTEM BARYCENTER"] = 0,
        ["SSB"] = 0,
        ["SUN"] = 10,
        ["SOL"] = 10,
        ["MERCURY"] = 199,
        ["VENUS"] = 299,
        ["MARS"] = 499,
        ["JUPITER"] = 599,
        ["URANUS"] = 799,
        ["NEPTUNE"] = 899,
        ["PLUTO"] = 999,
        ["EARTH"] = 399,
        ["MOON"] = 301,
        ["SATURN BARYCENTER"] = 6,
        ["SATURN"] = 699,
        ["CASSINI"] = -82
    };

    internal static int Resolve(string name) => int.TryParse(name, NumberStyles.Integer, CultureInfo.InvariantCulture, out int id)
        ? id
        : Names.TryGetValue(name, out int mapped) ? mapped : throw new NotSupportedException($"Unknown NAIF object {name}.");
}
