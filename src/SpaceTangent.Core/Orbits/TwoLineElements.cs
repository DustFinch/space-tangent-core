// SPDX-License-Identifier: GPL-2.0-or-later
using System.Globalization;
using SpaceTangent.Contracts;
using SpaceTangent.Core.Time;

namespace SpaceTangent.Core.Orbits;

/// <summary>Mean elements from a NORAD TLE, in radians and radians per minute.</summary>
public sealed class TwoLineElements
{
    public string CatalogNumber { get; }
    public UtcDateTime UtcEpoch { get; }
    internal double Inclination { get; }
    internal double AscendingNode { get; }
    internal double Eccentricity { get; }
    internal double ArgumentOfPerigee { get; }
    internal double MeanAnomaly { get; }
    internal double MeanMotion { get; }
    internal double BStar { get; }

    private TwoLineElements(string catalogNumber, UtcDateTime utcEpoch, double inclination, double ascendingNode,
        double eccentricity, double argumentOfPerigee, double meanAnomaly, double meanMotion, double bStar)
    {
        CatalogNumber = catalogNumber;
        UtcEpoch = utcEpoch;
        Inclination = inclination;
        AscendingNode = ascendingNode;
        Eccentricity = eccentricity;
        ArgumentOfPerigee = argumentOfPerigee;
        MeanAnomaly = meanAnomaly;
        MeanMotion = meanMotion;
        BStar = bStar;
    }

    public static TwoLineElements Parse(string first, string second)
    {
        CheckLine(first, '1');
        CheckLine(second, '2');
        if (!first.AsSpan(2, 5).SequenceEqual(second.AsSpan(2, 5)))
            throw new FormatException("TLE lines must use the same catalogue number.");
        string catalogNumber = first.Substring(2, 5).TrimStart();
        if (catalogNumber.Length == 0 || (!char.IsAsciiDigit(catalogNumber[0]) &&
            (catalogNumber.Length != 5 || catalogNumber[0] is < 'A' or > 'Z' or 'I' or 'O')) ||
            catalogNumber.AsSpan(1).ContainsAnyExceptInRange('0', '9'))
            throw new FormatException("Invalid TLE catalogue number.");

        const double radiansPerDegree = Math.PI / 180;
        double inclination = ParseDecimal(second.AsSpan(8, 8));
        double node = ParseDecimal(second.AsSpan(17, 8));
        double eccentricity = ParseUnsigned(second.AsSpan(26, 7)) / 10_000_000d;
        double perigee = ParseDecimal(second.AsSpan(34, 8));
        double anomaly = ParseDecimal(second.AsSpan(43, 8));
        double revolutionsPerDay = ParseDecimal(second.AsSpan(52, 11));
        if (inclination is < 0 or > 180 || node is < 0 or > 360 || perigee is < 0 or > 360 ||
            anomaly is < 0 or > 360 || revolutionsPerDay <= 0)
            throw new FormatException("TLE orbital elements are outside the supported range.");

        return new(catalogNumber, ParseEpoch(first.AsSpan(18, 14)), inclination * radiansPerDegree,
            node * radiansPerDegree, eccentricity, perigee * radiansPerDegree, anomaly * radiansPerDegree,
            revolutionsPerDay * Math.Tau / 1440, ParseBStar(first.AsSpan(53, 8)));
    }

    private static void CheckLine(string line, char number)
    {
        ArgumentNullException.ThrowIfNull(line);
        if (line.Length != 69 || line[0] != number || line[1] != ' ' || !char.IsAsciiDigit(line[68]))
            throw new FormatException($"Invalid TLE line {number}.");

        int checksum = 0;
        foreach (char character in line.AsSpan(0, 68))
        {
            if (character is < ' ' or > '~')
                throw new FormatException($"Invalid TLE line {number} character.");
            if (char.IsAsciiDigit(character))
                checksum += character - '0';
            else if (character == '-')
                checksum++;
        }

        if (checksum % 10 != line[68] - '0')
            throw new FormatException($"Invalid TLE line {number} checksum.");
    }

    private static UtcDateTime ParseEpoch(ReadOnlySpan<char> value)
    {
        int shortYear = ParseUnsigned(value[..2]);
        int year = shortYear >= 57 ? 1900 + shortYear : 2000 + shortYear;
        int day = ParseUnsigned(value.Slice(2, 3));
        if (value[5] != '.' || day < 1 || day > (DateTime.IsLeapYear(year) ? 366 : 365) || year < 1972)
            throw new FormatException("TLE epoch is outside the supported UTC calendar.");

        const long nanosecondsPerDayDigit = JulianInstant.NanosecondsPerDay / 100_000_000;
        long nanoseconds = ParseUnsigned(value[6..]) * nanosecondsPerDayDigit;
        return UtcDateTime.FromDayOffset(new DateOnly(year, 1, 1).AddDays(day - 1), nanoseconds);
    }

    private static double ParseBStar(ReadOnlySpan<char> value)
    {
        if (value[0] is not (' ' or '+' or '-') || value[6] is not ('+' or '-'))
            throw new FormatException("Invalid TLE B* field.");

        int exponent = (value[6] == '-' ? -1 : 1) * ParseUnsigned(value[7..]);
        int mantissa = (value[0] == '-' ? -1 : 1) * ParseUnsigned(value.Slice(1, 5));
        return mantissa * Math.Pow(10, exponent - 5);
    }

    private static int ParseUnsigned(ReadOnlySpan<char> value) =>
        int.Parse(value, NumberStyles.None, CultureInfo.InvariantCulture);

    private static double ParseDecimal(ReadOnlySpan<char> value) =>
        double.Parse(value, NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingWhite, CultureInfo.InvariantCulture);
}
