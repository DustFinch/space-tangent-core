// SPDX-License-Identifier: GPL-2.0-or-later
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using SpaceTangent.Contracts;
using SpaceTangent.Core.Addons;

namespace SpaceTangent.Core.Observing;

public sealed record StellarImport(StellarCatalogue Catalogue, int AppliedRecords, int DiagnosticCount, IReadOnlyList<string> Warnings);

public sealed partial class StellarCatalogue
{
    private const int MaximumStcBytes = 16 * 1024 * 1024;
    private const double AuKilometers = 149_597_870.7, SolarRadius = 695_700, Degrees = Math.PI / 180;
    private static readonly StringComparer Names = StringComparer.OrdinalIgnoreCase;

    private sealed record StcDraft(string Kind, string[] Aliases, SscValue.Map Fields, string File);

    /// <summary>Applies local STC records, resolving binary systems after all catalogue files have been read.</summary>
    public StellarImport WithStc(string root, CancellationToken cancellation = default)
    {
        root = Path.GetFullPath(root);
        if (!AddonDependencies.DirectoryExists(root))
            return new(this, 0, 0, []);
        string[] files = AddonDependencies.ListFiles(root, "*.stc");
        if (files.Length == 0)
            return new(this, 0, 0, []);
        if (files.Length > 64)
            throw new InvalidDataException("A stellar source contains more than 64 STC files.");
        var aliases = new Dictionary<string, ObjectId>(Names);
        foreach (StellarEntry star in Stars)
        {
            aliases.TryAdd(star.Name, star.Id);
            foreach (string name in star.Aliases)
                aliases.TryAdd(name, star.Id);
        }
        var drafts = new Dictionary<ObjectId, StcDraft>();
        var warnings = new List<string>();
        int applied = 0, omitted = 0;
        foreach (string path in files.OrderBy(path => path, Names))
        {
            cancellation.ThrowIfCancellationRequested();
            AddonDiagnostics.Read(AddonStage.Catalog, path, () =>
            {
                string file = LocalAddonFiles.Resolve(root, root, path);
                using var input = AddonDependencies.OpenRead(file, MaximumStcBytes);
                if (input.Length > MaximumStcBytes)
                    throw new InvalidDataException("An STC catalogue exceeds 16 MiB.");
                using var reader = new StreamReader(input, new UTF8Encoding(false, true));
                foreach (var (disposition, kind, number, name, fields) in new SscReader(reader.ReadToEnd(), file).ReadCatalogueRecords("Star"))
                {
                    cancellation.ThrowIfCancellationRequested();
                    string[] incoming = name?.Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) ?? [];
                    if (number == 0 || incoming.Contains("Solar System Barycenter", Names))
                        continue; // The solar ephemeris supplies both solar reference objects.
                    if (kind is not ("Star" or "Barycenter") || (number is null && incoming.Length == 0))
                    {
                        Warn(file, $"Unsupported record {name ?? kind}.");
                        continue;
                    }
                    ObjectId id = number is { } numeric ? CatalogId(numeric) : NamedRecordId(incoming, kind, disposition);
                    if (drafts.Count == 100_000 && !drafts.ContainsKey(id))
                        throw new InvalidDataException("The enabled STC files exceed 100,000 records.");
                    drafts.TryGetValue(id, out var previous);
                    var original = Find(id);
                    string[] names = incoming.Concat(previous?.Aliases ?? []).Concat(original?.Aliases ?? [])
                        .Concat(original is null ? [] : [original.Name])
                        .Append(number is { } designation ? CatalogDesignation(designation) : incoming[0])
                        .Distinct(Names).Take(128).ToArray();
                    var properties = fields;
                    if (previous is not null && disposition != SscDisposition.Replace)
                    {
                        var merged = new Dictionary<string, SscValue>(previous.Fields.Fields, StringComparer.Ordinal);
                        foreach (var (key, value) in fields.Fields)
                        {
                            int suffix = key.IndexOf('<');
                            string scalar = suffix >= 0 ? key[..suffix] : key;
                            foreach (string alias in merged.Keys.Where(name => name == scalar || name.StartsWith(scalar + "<", StringComparison.Ordinal)).ToArray())
                                merged.Remove(alias);
                            merged[key] = value;
                        }
                        properties = new(merged);
                    }
                    drafts[id] = new(kind == "Barycenter" || (disposition == SscDisposition.Modify &&
                        (previous?.Kind == "Barycenter" || original?.IsBarycenter == true)) ? "Barycenter" : "Star",
                        names, properties, file);
                    foreach (string alias in names)
                        if (!aliases.TryGetValue(alias, out var owner) || owner == id || IsBarycenter(id) || !IsBarycenter(owner))
                            aliases[alias] = id;
                }
                return true;
            });
        }

        var resolved = new Dictionary<ObjectId, StellarEntry>(_index.Count + drafts.Count);
        var visiting = new HashSet<ObjectId>();
        var failed = new HashSet<ObjectId>();
        foreach (ObjectId id in _index.Keys.Concat(drafts.Keys).Distinct())
        {
            cancellation.ThrowIfCancellationRequested();
            Resolve(id, 0);
        }
        var extents = new Dictionary<ObjectId, double>();
        foreach (StellarEntry star in resolved.Values.Where(star => star.BarycenterId.HasValue))
        {
            StellarEntry at = star;
            double extent = star.RadiusKilometers;
            for (int depth = 0; at.BarycenterId is { } parentId; depth++)
            {
                if (depth >= 8 || !resolved.TryGetValue(parentId, out var parent))
                    break;
                if (at.Orbit?.Curve.Elements is { } curve)
                    extent += curve.PeriapsisKilometers * (1 + curve.Eccentricity) / (1 - curve.Eccentricity);
                extents[parentId] = Math.Max(extent, extents.GetValueOrDefault(parentId));
                at = parent;
            }
        }
        foreach (var (id, extent) in extents)
            if (resolved[id].SystemRadiusKilometers < extent)
                resolved[id] = resolved[id] with { SystemRadiusKilometers = extent };
        return new(new(resolved), applied, omitted, warnings.AsReadOnly());

        ObjectId NamedRecordId(string[] names, string kind, SscDisposition disposition)
        {
            foreach (string name in names)
            {
                if (FindExisting(name) is not { } candidate)
                    continue;
                // Shared designations can name both a system and its primary star.
                if (disposition != SscDisposition.Add || IsBarycenter(candidate) == (kind == "Barycenter"))
                    return candidate;
            }
            return NamedId(names[0]);
        }

        bool IsBarycenter(ObjectId id) => drafts.TryGetValue(id, out var draft) ? draft.Kind == "Barycenter" : Find(id)?.IsBarycenter == true;

        ObjectId? FindExisting(string name)
        {
            if (aliases.TryGetValue(name, out ObjectId id))
                return id;
            if (uint.TryParse(name, NumberStyles.None, CultureInfo.InvariantCulture, out uint number))
                return CatalogId(number);
            if (name.StartsWith("HIP ", StringComparison.OrdinalIgnoreCase) &&
                uint.TryParse(name.AsSpan(4), NumberStyles.None, CultureInfo.InvariantCulture, out number))
                return CatalogId(number);
            return null;
        }

        StellarEntry? Resolve(ObjectId id, int depth)
        {
            if (resolved.TryGetValue(id, out var ready))
                return ready;
            if (failed.Contains(id))
                return null;
            StellarEntry? previous = Find(id);
            if (depth > 8 || !visiting.Add(id))
                return null;
            try
            {
                StellarEntry? current = previous;
                if (drafts.TryGetValue(id, out var draft))
                {
                    try
                    {
                        string? reference = StcText(draft.Fields, "OrbitBarycenter");
                        StellarEntry? parent = reference is { } name && FindExisting(name) is { } parentId
                            ? Resolve(parentId, depth + 1) : null;
                        if (reference is null && previous?.BarycenterId is { } priorParent)
                            parent = Resolve(priorParent, depth + 1);
                        int chain = 0;
                        for (StellarEntry? at = parent; at is not null; at = at.Barycenter)
                            chain++;
                        if (chain > 8)
                            parent = null;
                        current = BuildStar(id, draft, previous, parent);
                        if (current is null)
                        {
                            Warn(draft.File, $"No positioned stellar record for {draft.Aliases[0]}.");
                            current = previous;
                        }
                        else
                        {
                            if (reference is not null && parent is null)
                                Warn(draft.File, $"{current.Name} uses its recorded position because {reference} is unavailable.");
                            applied++;
                        }
                    }
                    catch (Exception error) when (error is FormatException or InvalidDataException or ArgumentOutOfRangeException or NotSupportedException)
                    {
                        Warn(draft.File, $"{draft.Aliases[0]}: {error.Message}");
                    }
                }
                else if (previous?.BarycenterId is { } oldParent && Resolve(oldParent, depth + 1) is { } updated &&
                    !ReferenceEquals(previous.Barycenter, updated))
                {
                    current = previous with { Barycenter = updated };
                }
                if (current is not null)
                    resolved.Add(id, current);
                else
                    failed.Add(id);
                return current;
            }
            finally
            {
                visiting.Remove(id);
            }
        }

        void Warn(string path, string message)
        {
            omitted++;
            if (warnings.Count < 80)
                warnings.Add($"{Path.GetFileName(path)}: {message}");
        }
    }

    private static StellarEntry? BuildStar(ObjectId id, StcDraft draft, StellarEntry? previous, StellarEntry? parent)
    {
        SscValue.Map fields = draft.Fields;
        bool barycenter = draft.Kind == "Barycenter";
        double? ra = StcNumber(fields, "RA"), dec = StcNumber(fields, "Dec");
        double? distanceLy = StcNumber(fields, "Distance", ("pc", 1 / ParsecsPerLightYear), ("ly", 1));
        bool hasPosition = ra is not null && dec is not null && distanceLy is > 0;
        Vector3d? position = hasPosition ? Equatorial(ra!.Value, dec!.Value, distanceLy!.Value * ParsecsPerLightYear) : previous?.PositionParsecs;
        StellarConic? orbit = parent is null ? null : fields.Fields.TryGetValue("EllipticalOrbit", out var orbitValue)
            ? ReadStcOrbit(orbitValue) : previous?.Orbit;
        if (parent is not null && (!hasPosition || orbit is not null))
            position = parent.PositionParsecs;
        else
            parent = null;
        if (position is not { } coordinates || !coordinates.IsFinite || coordinates.Length is <= 0 or > 1e9)
            return null;
        double distance = coordinates.Length;
        double correction = 5 * Math.Log10(distance / 10);
        double? apparent = StcNumber(fields, "AppMag"), absolute = StcNumber(fields, "AbsMag");
        if (absolute is null && apparent is null && previous is not null)
            absolute = previous.AbsoluteMagnitude ?? previous.VisualMagnitude - 5 * Math.Log10(previous.DistanceParsecs / 10);
        absolute ??= apparent - correction;
        if (absolute is null && !barycenter)
            return null;
        double visualMagnitude = apparent ?? (absolute ?? 30) + correction;
        string? spectral = StcText(fields, "SpectralType");
        double temperature = StcNumber(fields, "Temperature") ?? Temperature(spectral);
        if (temperature <= 0)
            throw new InvalidDataException("The star has a nonpositive temperature.");
        double? radius = StcNumber(fields, "Radius", ("rS", SolarRadius), ("rJ", 71_492), ("cm", .00001), ("km", 1));
        if (radius is < 0 or > 3e9)
            throw new InvalidDataException("The star radius is out of range.");
        double estimated = previous is not null && radius is null && spectral is null && !fields.Fields.ContainsKey("Temperature")
            ? previous.RadiusKilometers : EstimateRadius(absolute ?? 30, temperature);
        return new(id, draft.Aliases[0], distance, coordinates, previous?.MotionParsecsPerYear ?? default,
            visualMagnitude, spectral is null && !fields.Fields.ContainsKey("Temperature") && previous is not null ? previous.ColorIndex :
                Math.Clamp(.65 + 1.45 * (5772 / temperature - 1), -.4, 2.5), barycenter ? 0 : radius ?? estimated)
        {
            Aliases = Array.AsReadOnly(draft.Aliases),
            AbsoluteMagnitude = absolute,
            ColorSrgb = spectral is null && !fields.Fields.ContainsKey("Temperature") ? previous?.ColorSrgb : null,
            IsBarycenter = barycenter,
            SystemRadiusKilometers = previous?.SystemRadiusKilometers ?? 0,
            Barycenter = parent,
            Orbit = orbit
        };
    }

    private static StellarConic ReadStcOrbit(SscValue value)
    {
        if (value is not SscValue.Map fields)
            throw new InvalidDataException("Expected an STC elliptical orbit.");
        double e = StcNumber(fields, "Eccentricity") ?? 0;
        double axis = StcNumber(fields, "SemiMajorAxis", ("km", 1 / AuKilometers), ("au", 1)) ??
            (StcNumber(fields, "PericenterDistance") ?? 0) / (1 - e);
        double period = StcNumber(fields, "Period", ("d", 1 / 365.25), ("min", 60d / 31_557_600), ("yr", 1)) ?? 0;
        double node = StcNumber(fields, "AscendingNode") ?? 0;
        double argument = StcNumber(fields, "ArgOfPericenter") ?? ((StcNumber(fields, "LongOfPericenter") ?? node) - node);
        double mean = StcNumber(fields, "MeanAnomaly") ?? ((StcNumber(fields, "MeanLongitude") ?? (node + argument)) - (node + argument));
        return StellarOrbit(axis * AuKilometers, e, period * 31_557_600, (StcNumber(fields, "Inclination") ?? 0) * Degrees,
            node * Degrees, argument * Degrees, mean * Degrees, StcNumber(fields, "Epoch") ?? JulianInstant.J2000Day);
    }

    private static Vector3d Equatorial(double ra, double dec, double distance)
    {
        if (ra is < 0 or > 360 || dec is < -90 or > 90)
            throw new InvalidDataException("STC right ascension or declination is out of range.");
        var (sinRa, cosRa) = Math.SinCos(ra * Degrees);
        var (sinDec, cosDec) = Math.SinCos(dec * Degrees);
        return new Vector3d(cosDec * cosRa, cosDec * sinRa, sinDec) * distance;
    }

    private static string? StcText(SscValue.Map fields, string key) =>
        fields.Fields.TryGetValue(key, out var value) ? value.AsText() : null;

    private static double? StcNumber(SscValue.Map fields, string key, params (string Unit, double Scale)[] units)
    {
        if (StcText(fields, key) is { } token)
            return ParseStcNumber(token, key);
        foreach (var (unit, scale) in units)
            if (StcText(fields, key + "<" + unit + ">") is { } qualified)
                return ParseStcNumber(qualified, key) * scale;
        return null;
    }

    private static double ParseStcNumber(string token, string key) =>
        double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out double number) && double.IsFinite(number)
            ? number : throw new InvalidDataException($"Invalid STC number {key}.");

    private static double Temperature(string? spectral) => spectral?.FirstOrDefault() switch
    {
        'O' or 'W' => 32_000, 'B' => 15_000, 'A' => 8_900, 'F' => 6_900, 'K' => 4_600,
        'M' => 3_100, 'L' => 1_900, 'T' or 'Y' => 900, 'D' => 11_000, _ => 5_772
    };

    private static double EstimateRadius(double absoluteMagnitude, double temperature)
    {
        double kelvin = Math.Clamp(temperature, 1000, 200_000), log = Math.Log10(kelvin) - 4;
        double correction = -.438 + log * (-3.901 + log * (-8.131 + log * (13.421 - log * 8.499)));
        return Math.Clamp(SolarRadius * Math.Pow(10, -.2 * (absoluteMagnitude + correction - 4.74)) *
            Math.Pow(5772 / kelvin, 2), .001, 3e9);
    }

    private static ObjectId NamedId(string name) => new("stc/" +
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(name.ToUpperInvariant())))[..16].ToLowerInvariant());
}
