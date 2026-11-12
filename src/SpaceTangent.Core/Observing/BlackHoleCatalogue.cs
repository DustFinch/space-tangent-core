// SPDX-License-Identifier: GPL-2.0-or-later
using System.Globalization;
using SpaceTangent.Contracts;

namespace SpaceTangent.Core.Observing;

public sealed record BlackHole(ObjectId Id, string Name, IReadOnlyList<string> Aliases,
    Vector3d Direction, double DistanceParsecs, double MassSolar, string? Host,
    double? DiskInclinationDegrees, double Spin, string Reference)
{
    private const double SchwarzschildKilometersPerSolarMass = 2 * 1.3271244e20 / (299792458.0 * 299792458.0) / 1000;
    public double SchwarzschildRadiusKilometers => MassSolar * SchwarzschildKilometersPerSolarMass;
    public Vector3d PositionKilometers => Direction * (DistanceParsecs * StellarEntry.KilometersPerParsec);

    /// <summary>The catalogue inclination, with position angle zero measured toward celestial north.</summary>
    public Vector3d? DiskAxis()
    {
        if (DiskInclinationDegrees is not { } degrees)
            return null;
        Vector3d near = Direction * -1;
        Vector3d north = new Vector3d(0, 0, 1) - near * near.Z;
        if (north.Length < 1e-9)
            north = Vector3d.Cross(near, new(1, 0, 0));
        var (sin, cos) = Math.SinCos(degrees * Math.PI / 180);
        return near * cos + north * (sin / north.Length);
    }
}

/// <summary>The authored compact object catalogue in equatorial J2000 coordinates.</summary>
public sealed class BlackHoleCatalogue
{
    private readonly Dictionary<ObjectId, BlackHole> _byId;
    private static readonly Lazy<BlackHoleCatalogue> BuiltIn = new(() =>
    {
        using var resource = typeof(BlackHoleCatalogue).Assembly.GetManifestResourceStream("SpaceTangent.Core.Data.blackholes.txt") ??
            throw new InvalidOperationException("The built-in black hole catalogue is missing.");
        using var reader = new StreamReader(resource);
        return Read(reader);
    });

    private BlackHoleCatalogue(List<BlackHole> entries)
    {
        Entries = entries.AsReadOnly();
        _byId = entries.ToDictionary(entry => entry.Id);
    }

    public static BlackHoleCatalogue Default => BuiltIn.Value;
    public IReadOnlyList<BlackHole> Entries { get; }
    public BlackHole? Find(ObjectId id) => _byId.GetValueOrDefault(id);

    public static BlackHoleCatalogue Read(TextReader input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var entries = new List<BlackHole>();
        var keys = new HashSet<string>(StringComparer.Ordinal);
        int line = 0;
        while (input.ReadLine() is { } raw)
        {
            line++;
            string text = raw.Trim();
            if (text.Length == 0 || text[0] == '#')
                continue;
            string[] fields = text.Split('|', StringSplitOptions.TrimEntries);
            if (text.Length > 2048 || fields.Length is not (10 or 11) || entries.Count >= 1024)
                throw Invalid("invalid entry or catalogue limit");
            string key = fields[0];
            if (key.Length is 0 or > 32 || !key.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '-') || !keys.Add(key))
                throw Invalid("invalid or repeated identifier");
            string name = fields[1];
            string[] aliases = fields[2].Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            if (name.Length is 0 or > 64 || aliases.Length > 16 || aliases.Any(alias => alias.Length > 64))
                throw Invalid("invalid name or alias");
            double ra = Sexagesimal(fields[3], 24, false) * 15 * Math.PI / 180;
            double dec = Sexagesimal(fields[4], 90, true) * Math.PI / 180;
            double distance = Number(fields[5], 0, 1e10), mass = Number(fields[6], 0, 1e12);
            if (distance <= 0 || mass <= 0)
                throw Invalid("distance and mass must be positive");
            string? host = fields[7] == "-" ? null : fields[7];
            double? tilt = fields[8] == "-" ? null : Number(fields[8], 0, 90);
            double spin = fields.Length == 11 && fields[9] != "-" ? Number(fields[9], 0, .998) : 0;
            if (host is { Length: 0 or > 64 } || fields[^1].Length == 0)
                throw Invalid("invalid host or reference");
            var (sinRa, cosRa) = Math.SinCos(ra);
            var (sinDec, cosDec) = Math.SinCos(dec);
            entries.Add(new(new("black-hole/" + key), name, Array.AsReadOnly(aliases),
                new(cosRa * cosDec, sinRa * cosDec, sinDec), distance, mass, host, tilt, spin, fields[^1]));
        }
        return new(entries);

        InvalidDataException Invalid(string reason) => new($"Black hole catalogue line {line}: {reason}.");
        double Number(string value, double minimum, double maximum) =>
            double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double result) &&
                result >= minimum && result <= maximum ? result : throw Invalid("invalid number");

        double Sexagesimal(string value, int limit, bool signed)
        {
            string[] parts = value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 3)
                throw Invalid("invalid sexagesimal coordinate");
            bool negative = parts[0].StartsWith('-');
            double whole = Number(parts[0].TrimStart('+', '-'), 0, limit);
            double minutes = Number(parts[1], 0, 60);
            double seconds = Number(parts[2], 0, 60);
            double result = whole + minutes / 60 + seconds / 3600;
            if (minutes == 60 || seconds == 60 || result > limit || (!signed && (negative || result == limit)))
                throw Invalid("coordinate out of range");
            return negative ? -result : result;
        }
    }
}
