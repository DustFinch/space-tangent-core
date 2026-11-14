// SPDX-License-Identifier: GPL-2.0-or-later
using System.Globalization;
using System.Text.Json;
using SpaceTangent.Contracts;
using SpaceTangent.Core.Addons;

namespace SpaceTangent.Core.Observing;

public sealed partial class StellarCatalogue
{
    private const int MaximumCorrectionPackBytes = 1024 * 1024;
    private static readonly JulianInstant CorrectionEpoch = JulianInstant.FromJulianDate(JulianInstant.J2000Day, TimeScale.Tdb);

    /// <summary>Applies archived version 1 distance corrections after the stellar catalogues have been merged.</summary>
    public StellarImport WithDistanceCorrectionPacks(string root, CancellationToken cancellation = default)
    {
        root = Path.GetFullPath(root);
        if (!AddonDependencies.DirectoryExists(root))
            return new(this, 0, 0, []);

        string[] directories = AddonDependencies.ListDirectories(root);
        if (directories.Length > 64)
            throw new InvalidDataException("A stellar correction source contains more than 64 pack folders.");

        var manifests = directories.Order(StringComparer.OrdinalIgnoreCase).ThenBy(path => path, StringComparer.Ordinal)
            .Prepend(root).Select(directory => Path.Combine(directory, "pack.json")).Where(AddonDependencies.FileExists);
        return ApplyDistanceCorrectionPacks(root, manifests, cancellation);
    }

    /// <summary>Applies the specified archived version 1 correction manifest.</summary>
    public StellarImport WithDistanceCorrectionPack(string manifest, CancellationToken cancellation = default)
    {
        manifest = Path.GetFullPath(manifest);
        return ApplyDistanceCorrectionPacks(Path.GetDirectoryName(manifest)!, [manifest], cancellation);
    }

    private StellarImport ApplyDistanceCorrectionPacks(string root, IEnumerable<string> manifests, CancellationToken cancellation)
    {
        var corrections = new Dictionary<ObjectId, double>();
        var warnings = new List<string>();
        int omitted = 0;
        foreach (string path in manifests)
        {
            cancellation.ThrowIfCancellationRequested();
            AddonDiagnostics.Read(AddonStage.Catalog, path, () =>
            {
                using FileStream input = AddonDependencies.OpenRead(LocalAddonFiles.Resolve(root, root, path), MaximumCorrectionPackBytes);
                if (input.Length > MaximumCorrectionPackBytes)
                    throw new InvalidDataException("A stellar correction pack exceeds 1 MiB.");
                using var document = JsonDocument.Parse(input, new JsonDocumentOptions { MaxDepth = 12 });
                var manifest = document.RootElement;
                if (manifest.ValueKind != JsonValueKind.Object ||
                    !manifest.TryGetProperty("formatVersion", out var version) || version.ValueKind != JsonValueKind.Number ||
                    !version.TryGetInt32(out int number) || number != 1 ||
                    !manifest.TryGetProperty("corrections", out var entries) || entries.ValueKind != JsonValueKind.Array ||
                    entries.GetArrayLength() > 4096)
                    throw new InvalidDataException("Expected an archived version 1 stellar correction pack.");
                foreach (JsonElement entry in entries.EnumerateArray())
                {
                    cancellation.ThrowIfCancellationRequested();
                    var target = Required(entry, "target", JsonValueKind.Object);
                    string name = Required(target, "id", JsonValueKind.String).GetString()!;
                    var values = Required(entry, "set", JsonValueKind.Object);
                    double distance = Required(values, "distanceLy", JsonValueKind.Number).GetDouble() * ParsecsPerLightYear;
                    if (Required(target, "kind", JsonValueKind.String).GetString() != "star" ||
                        values.EnumerateObject().Count() != 1 || string.IsNullOrWhiteSpace(name) ||
                        string.IsNullOrWhiteSpace(Required(entry, "source", JsonValueKind.String).GetString()) ||
                        !double.IsFinite(distance) || distance <= 0 || distance > 1e9)
                        throw new InvalidDataException("Invalid archived stellar distance correction.");
                    if (CorrectionTarget(name) is { } star)
                        corrections[star.Id] = distance;
                    else
                    {
                        omitted++;
                        if (warnings.Count < 80)
                            warnings.Add($"{Path.GetFileName(Path.GetDirectoryName(path))}/pack.json: no star named {name}.");
                    }
                }
                return true;
            });
        }

        if (corrections.Count == 0)
            return new(this, 0, omitted, warnings.AsReadOnly());

        var roots = new Dictionary<ObjectId, StellarEntry>();
        var physical = new Dictionary<ObjectId, (double Scale, double Absolute)>();
        foreach (var (id, distance) in corrections)
        {
            cancellation.ThrowIfCancellationRequested();
            StellarEntry original = _index[id];
            StellarEntry center = original;
            while (center.Barycenter is { } parent)
                center = parent;
            StellarEntry current = roots.GetValueOrDefault(center.Id) ?? center;
            CartesianState state = original.StateAt(CorrectionEpoch);
            Vector3d orbitalPosition = state.PositionKilometers / StellarEntry.KilometersPerParsec - center.PositionParsecs;
            double secondsPerYear = 365.25 * JulianInstant.SecondsPerDay;
            Vector3d orbitalMotion = state.VelocityKilometersPerSecond *
                (secondsPerYear / StellarEntry.KilometersPerParsec) - center.MotionParsecsPerYear;
            Vector3d position = current.PositionParsecs + orbitalPosition;
            double scale = distance / position.Length;
            if (!double.IsFinite(scale) || scale <= 0)
                throw new InvalidDataException($"The corrected distance for {id} cannot be applied.");
            Vector3d direction = position / position.Length;
            Vector3d motion = current.MotionParsecsPerYear + orbitalMotion;
            Vector3d radial = direction * Vector3d.Dot(motion, direction);
            roots[center.Id] = center with
            {
                PositionParsecs = position * scale - orbitalPosition,
                MotionParsecsPerYear = current.MotionParsecsPerYear == default
                    ? default : radial + (motion - radial) * scale - orbitalMotion
            };
            double absolute = original.AbsoluteMagnitude ??
                original.VisualMagnitude - 5 * Math.Log10(original.DistanceParsecs / 10);
            physical[id] = (scale, absolute - 5 * Math.Log10(scale));
        }

        var updated = new Dictionary<ObjectId, StellarEntry>(_index.Count);
        foreach (StellarEntry star in Stars)
        {
            cancellation.ThrowIfCancellationRequested();
            Update(star);
        }
        return new(new(updated), corrections.Count, omitted, warnings.AsReadOnly());

        StellarEntry Update(StellarEntry original)
        {
            if (updated.TryGetValue(original.Id, out var existing))
                return existing;
            var parent = original.Barycenter is { } oldParent ? Update(oldParent) : null;
            StellarEntry star = roots.GetValueOrDefault(original.Id) ?? original;
            if (!ReferenceEquals(parent, original.Barycenter))
                star = star with { Barycenter = parent, PositionParsecs = parent!.PositionParsecs };
            bool corrected = physical.TryGetValue(star.Id, out var values);
            if (!ReferenceEquals(star, original) || corrected)
            {
                double distance = star.StateAt(CorrectionEpoch).PositionKilometers.Length / StellarEntry.KilometersPerParsec;
                double absolute = corrected ? values.Absolute : original.AbsoluteMagnitude ??
                    original.VisualMagnitude - 5 * Math.Log10(original.DistanceParsecs / 10);
                star = star with
                {
                    DistanceParsecs = distance,
                    VisualMagnitude = absolute + 5 * Math.Log10(distance / 10),
                    RadiusKilometers = corrected ? star.RadiusKilometers * values.Scale : star.RadiusKilometers,
                    AbsoluteMagnitude = corrected ? absolute : star.AbsoluteMagnitude
                };
            }
            updated.Add(star.Id, star);
            return star;
        }
    }

    private StellarEntry? CorrectionTarget(string name)
    {
        string[] parts = name.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts is [var catalogue, var identifier])
        {
            if (catalogue.Equals("HIP", StringComparison.OrdinalIgnoreCase) &&
                uint.TryParse(identifier, NumberStyles.None,
                    CultureInfo.InvariantCulture, out uint number) && number is > 0 and <= 999_999)
                return Find(CatalogId(number));
            if (catalogue.Equals("TYC", StringComparison.OrdinalIgnoreCase) && identifier.Length <= 16 &&
                identifier.All(character => char.IsAsciiDigit(character) || character == '-'))
                return Find(new ObjectId("tyc/" + identifier));
        }
        return FindByName(name);
    }

    private static JsonElement Required(JsonElement source, string property, JsonValueKind kind) =>
        source.ValueKind == JsonValueKind.Object && source.TryGetProperty(property, out var value) && value.ValueKind == kind
            ? value : throw new InvalidDataException($"A stellar correction pack requires {property}.");
}
