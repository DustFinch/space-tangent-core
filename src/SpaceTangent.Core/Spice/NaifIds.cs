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

    internal static int Resolve(string name, SpiceResources resources, bool allowNumericPrefix = false)
    {
        if (int.TryParse(name, NumberStyles.Integer, CultureInfo.InvariantCulture, out int direct))
            return direct;
        var pool = resources.Text;
        if (pool.Contains("NAIF_BODY_NAME") && pool.Contains("NAIF_BODY_CODE"))
        {
            string[] names = pool.Get("NAIF_BODY_NAME"), codes = pool.Get("NAIF_BODY_CODE");
            if (names.Length != codes.Length)
                throw new InvalidDataException("SPICE body names and codes have different lengths.");
            for (int index = names.Length - 1; index >= 0; index--)
                if (names[index].Equals(name, StringComparison.OrdinalIgnoreCase) &&
                    int.TryParse(codes[index], NumberStyles.Integer, CultureInfo.InvariantCulture, out int mapped))
                    return mapped;
        }
        if (allowNumericPrefix && TryReadNumericPrefix(name, out int prefix))
            return prefix;
        return Resolve(name);
    }

    private static bool TryReadNumericPrefix(string name, out int id)
    {
        var text = name.AsSpan().TrimStart();
        int start = !text.IsEmpty && text[0] is '+' or '-' ? 1 : 0;
        int end = start;
        while (end < text.Length && char.IsAsciiDigit(text[end]))
            end++;
        return int.TryParse(text[..end], NumberStyles.Integer, CultureInfo.InvariantCulture, out id);
    }

    internal static int Resolve(string name) => int.TryParse(name, NumberStyles.Integer, CultureInfo.InvariantCulture, out int id)
        ? id
        : Names.TryGetValue(name, out int mapped) ? mapped : throw new NotSupportedException($"Unknown NAIF object {name}.");
}
