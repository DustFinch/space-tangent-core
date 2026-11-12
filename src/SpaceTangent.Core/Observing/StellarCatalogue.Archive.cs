// SPDX-License-Identifier: GPL-2.0-or-later
using System.Globalization;
using System.Text;
using SpaceTangent.Contracts;
using SpaceTangent.Core.Addons;
using SpaceTangent.Core.Ephemerides;
using SpaceTangent.Core.Orbits;

namespace SpaceTangent.Core.Observing;

public sealed partial class StellarCatalogue
{
    private const byte ArchiveBarycenter = 254, ArchiveDarkObject = 255;
    private const int ArchiveStarLimit = 3_000_000;
    private const int ArchiveStarBytes = 28, ArchiveOrbitBytes = 72;
    private const double ParsecsPerLightYear = StellarEntry.KilometersPerLightYear / StellarEntry.KilometersPerParsec;

    private readonly record struct ArchiveStar(Vector3d Position, double AbsoluteMagnitude, Vector3d Color, byte Surface,
        double Radius);
    private sealed record ArchiveOrbit(uint Parent, StellarConic Curve, double Apoapsis);

    /// <summary>Reads the archived nearby-star binary, its aliases and analytic binary-star orbits.</summary>
    public static StellarCatalogue LoadArchive(string root, StellarCatalogue? astrometry = null,
        CancellationToken cancellation = default)
    {
        root = Path.GetFullPath(root);
        string path = LocalAddonFiles.ResolveRelative(root, root, "stars.bin");
        const int maximumBytes = 128 * 1024 * 1024;
        using var file = AddonDependencies.OpenRead(path, maximumBytes);
        using var reader = new BinaryReader(file, Encoding.UTF8, leaveOpen: true);
        if (file.Length < 28 || file.Length > maximumBytes || !reader.ReadBytes(8).AsSpan().SequenceEqual("CELWSTAR"u8) ||
            reader.ReadUInt16() != 4 || reader.ReadUInt16() != 0)
            throw new InvalidDataException("Unsupported archived stellar catalogue header.");
        int count = reader.ReadInt32();
        float magnitude = reader.ReadSingle(), completeness = reader.ReadSingle();
        if (count is <= 0 or > ArchiveStarLimit || file.Length < 28L + (long)count * ArchiveStarBytes ||
            !float.IsFinite(magnitude) || !float.IsFinite(completeness) || completeness < 0)
            throw new InvalidDataException("Invalid archived stellar catalogue length or coverage.");

        var records = new Dictionary<uint, ArchiveStar>(count);
        for (int i = 0; i < count; i++)
        {
            cancellation.ThrowIfCancellationRequested();
            uint number = reader.ReadUInt32();
            Vector3d raw = new(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
            double absolute = reader.ReadSingle();
            Vector3d color = new Vector3d(reader.ReadByte(), reader.ReadByte(), reader.ReadByte()) / 255;
            byte surface = reader.ReadByte();
            double radius = reader.ReadSingle();
            bool validSurface = surface < 7 ? radius > 0 && double.IsFinite(radius) :
                surface is ArchiveBarycenter or ArchiveDarkObject && radius == 0;
            if (number == 0 || !raw.IsFinite || raw.Length <= 0 || !double.IsFinite(absolute) || !validSurface ||
                !records.TryAdd(number, new(RotateArchiveVector(raw), absolute, color, surface, radius)))
                throw new InvalidDataException($"Invalid archived stellar record {number}.");
        }
        int orbitCount = reader.ReadInt32();
        if (orbitCount < 0 || orbitCount > count || file.Length != file.Position + (long)orbitCount * ArchiveOrbitBytes)
            throw new InvalidDataException("Invalid archived stellar orbit table length.");
        var orbits = new Dictionary<uint, ArchiveOrbit>(orbitCount);
        for (int i = 0; i < orbitCount; i++)
        {
            cancellation.ThrowIfCancellationRequested();
            uint star = reader.ReadUInt32(), parent = reader.ReadUInt32();
            double period = reader.ReadDouble(), axis = reader.ReadDouble(), e = reader.ReadDouble();
            double inclination = reader.ReadDouble(), node = reader.ReadDouble(), argument = reader.ReadDouble();
            double mean = reader.ReadDouble(), epoch = reader.ReadDouble();
            if (star == parent || !records.ContainsKey(star) || !records.ContainsKey(parent) || orbits.ContainsKey(star))
                throw new InvalidDataException($"Invalid archived barycenter assignment for {star}.");
            StellarConic curve = StellarOrbit(axis, e, period * 86400, inclination, node, argument, mean, epoch);
            orbits.Add(star, new(parent, curve, axis * (1 + e)));
        }

        var extents = new Dictionary<uint, double>();
        foreach (uint star in orbits.Keys)
        {
            uint at = star;
            double extent = records[star].Radius;
            for (int depth = 0; orbits.TryGetValue(at, out var parent); depth++)
            {
                if (depth == 8)
                    throw new InvalidDataException("An archived star has a cyclic or excessively deep barycenter chain.");
                extent += parent.Apoapsis;
                at = parent.Parent;
                extents[at] = Math.Max(extent, extents.GetValueOrDefault(at));
            }
        }
        var aliases = ReadArchiveNames(root, records, cancellation);
        var assembled = new Dictionary<ObjectId, StellarEntry>(count);
        foreach (uint number in records.Keys)
        {
            cancellation.ThrowIfCancellationRequested();
            Build(number);
        }
        return new(assembled);

        StellarEntry Build(uint number)
        {
            ObjectId id = CatalogId(number);
            if (assembled.TryGetValue(id, out var existing))
                return existing;
            ArchiveStar star = records[number];
            var orbit = orbits.GetValueOrDefault(number);
            var parent = orbit is null ? null : Build(orbit.Parent);
            double distance = star.Position.Length * ParsecsPerLightYear;
            StellarEntry? precise = astrometry?.Find(id);
            string[] names = aliases.GetValueOrDefault(number) ?? [];
            string designation = CatalogDesignation(number);
            var entry = new StellarEntry(id, names.FirstOrDefault() ?? precise?.Name ?? designation, distance,
                star.Position * ParsecsPerLightYear, precise?.MotionParsecsPerYear ?? default,
                star.AbsoluteMagnitude + 5 * Math.Log10(distance / 10), precise?.ColorIndex ?? .65, star.Radius)
            {
                Aliases = names.Length == 0 && precise is null ? [] : Array.AsReadOnly(names.Append(designation)
                    .Append(precise?.Name ?? designation).Distinct(StringComparer.OrdinalIgnoreCase).ToArray()),
                AbsoluteMagnitude = star.AbsoluteMagnitude,
                ColorSrgb = star.Color,
                IsBarycenter = star.Surface == ArchiveBarycenter,
                SystemRadiusKilometers = extents.GetValueOrDefault(number),
                Barycenter = parent,
                Orbit = orbit?.Curve
            };
            assembled.Add(id, entry);
            return entry;
        }
    }

    private static Dictionary<uint, string[]> ReadArchiveNames(string root, IReadOnlyDictionary<uint, ArchiveStar> records,
        CancellationToken cancellation)
    {
        var result = new Dictionary<uint, string[]>();
        string path = Path.Combine(root, "stars-names.txt");
        if (!AddonDependencies.FileExists(path))
            return result;
        const int maximumBytes = 16 * 1024 * 1024;
        using var input = AddonDependencies.OpenRead(LocalAddonFiles.Resolve(root, root, path), maximumBytes);
        if (input.Length > maximumBytes)
            throw new InvalidDataException("Archived stellar names exceed 16 MiB.");
        using var reader = new StreamReader(input, new UTF8Encoding(false, true));
        while (reader.ReadLine() is { } line)
        {
            cancellation.ThrowIfCancellationRequested();
            if (line.Length == 0)
                continue;
            string[] fields = line.Split(':');
            if (fields.Length is < 2 or > 65 || !uint.TryParse(fields[0], NumberStyles.None, CultureInfo.InvariantCulture, out uint number) ||
                !records.ContainsKey(number) || fields.Skip(1).Any(name => name.Length == 0 || name.Length > 64 || name.Any(char.IsControl)) ||
                !result.TryAdd(number, fields[1..]))
                throw new InvalidDataException("An archived stellar name record is invalid.");
        }
        return result;
    }

    internal static StellarConic StellarOrbit(double axis, double eccentricity, double periodSeconds, double inclination,
        double node, double argument, double mean, double epoch)
    {
        if (!(axis > 0 && double.IsFinite(axis) && periodSeconds > 0 && double.IsFinite(periodSeconds) &&
            eccentricity is >= 0 and < 1 && double.IsFinite(mean) && double.IsFinite(epoch)))
            throw new InvalidDataException("Invalid binary-star orbital elements.");
        double rate = Math.Tau / periodSeconds;
        var curve = new ConicOrbit(new(axis * (1 - eccentricity), eccentricity, inclination, node, argument,
            new(JulianInstant.J2000Day, 0, TimeScale.Tdb), rate * rate * axis * axis * axis, new("j2000-ecliptic")));
        return new(curve, epoch, Math.IEEERemainder(mean, Math.Tau) / rate);
    }

    private static Vector3d RotateArchiveVector(Vector3d native) => HistoricalSolarEphemeris.Rotate(new(native.X, -native.Z, native.Y));

    internal static ObjectId CatalogId(uint number)
    {
        if (number is > 0 and <= 999_999)
            return new(FormattableString.Invariant($"hip/{number}"));
        uint component = number / 1_000_000_000, rest = number % 1_000_000_000;
        return component is >= 1 and <= 3 && rest % 10_000 != 0
            ? new(FormattableString.Invariant($"tyc/{rest % 10_000}-{rest / 10_000}-{component}"))
            : new(FormattableString.Invariant($"stc/{number}"));
    }

    internal static string CatalogDesignation(uint number)
    {
        ObjectId id = CatalogId(number);
        return id.Value.StartsWith("hip/", StringComparison.Ordinal) ? "HIP " + id.Value[4..] :
            id.Value.StartsWith("tyc/", StringComparison.Ordinal) ? "TYC " + id.Value[4..] : "#" + id.Value[4..];
    }
}

/// <summary>A periodic conic whose reference epoch can lie beyond the simulator calendar.</summary>
internal sealed record StellarConic(ConicOrbit Curve, double ReferenceJulianDay, double PhaseSeconds)
{
    internal CartesianState Evaluate(JulianInstant tdb)
    {
        if (tdb.Scale != TimeScale.Tdb)
            throw new ArgumentException("A stellar orbit requires TDB.", nameof(tdb));
        double elapsedDays = (tdb.Day - ReferenceJulianDay) + tdb.NanosecondsOfDay / (double)JulianInstant.NanosecondsPerDay;
        return Curve.EvaluateElapsedSeconds(elapsedDays * JulianInstant.SecondsPerDay + PhaseSeconds);
    }
}
