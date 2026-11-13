// SPDX-License-Identifier: GPL-2.0-or-later
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using SpaceTangent.Contracts;
using SpaceTangent.Core.Addons;
using SpaceTangent.Core.Ephemerides;

namespace SpaceTangent.Core.Observing;

/// <summary>Deep-sky objects and stable designations from local DSC catalogues.</summary>
public sealed class DeepSkyIndex
{
    private const int MaximumCatalogueBytes = 16 * 1024 * 1024;
    private const int MaximumObjects = 100_000;
    private static readonly Regex Messier = new(@"^M\s*([0-9]+)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex NonId = new("[^a-z0-9]+", RegexOptions.CultureInvariant);
    private readonly Dictionary<ObjectId, DeepSkyObject> _byId;

    private DeepSkyIndex(Dictionary<ObjectId, DeepSkyObject> objects)
    {
        _byId = objects;
        Objects = Array.AsReadOnly(objects.Values.ToArray());
    }

    public static DeepSkyIndex Default { get; } = new(DeepSkyCatalogue.Objects.ToDictionary(body => body.Id));
    public IReadOnlyList<DeepSkyObject> Objects { get; }
    public DeepSkyObject? Find(ObjectId id) => _byId.GetValueOrDefault(id);

    public static string ResolveMesh(string root, DeepSkyObject item)
    {
        if (item.Mesh is not { Length: > 0 } name || item.CatalogueFile is not { } catalogue)
            throw new ArgumentException("The deep-sky object has no local model reference.", nameof(item));
        return SscSampledCatalog.FindAsset(AddonStage.Mesh, Path.GetFullPath(root),
            Path.GetDirectoryName(catalogue)!, name, "models");
    }

    public static DeepSkyIndex Load(string root, CancellationToken cancellation = default)
    {
        root = Path.GetFullPath(root);
        if (!AddonDependencies.DirectoryExists(root))
            return Default;
        string[] files = SkyCataloguePack.List(root, ".dsc");
        if (files.Length == 0)
            return Default;
        if (files.Length > 64)
            throw new InvalidDataException("A local deep-sky source contains more than 64 DSC catalogues.");
        var objects = new Dictionary<ObjectId, DeepSkyObject>(Default._byId);
        var primaryKeys = new Dictionary<ObjectId, string>();
        var duplicates = new Dictionary<ObjectId, DeepSkyObject>();
        int records = 0;
        foreach (string path in files)
        {
            cancellation.ThrowIfCancellationRequested();
            AddonDiagnostics.Read(AddonStage.Catalog, path, () =>
            {
                string file = LocalAddonFiles.Resolve(root, root, path);
                using var stream = AddonDependencies.OpenRead(file, MaximumCatalogueBytes);
                if (stream.Length > MaximumCatalogueBytes)
                    throw new InvalidDataException("A DSC catalogue exceeds 16 MiB.");
                using var reader = new StreamReader(stream, new UTF8Encoding(false, true));
                foreach (var (kind, names, properties) in new SscReader(reader.ReadToEnd(), file).ReadTypedRecords("Galaxy"))
                {
                    cancellation.ThrowIfCancellationRequested();
                    if (++records > MaximumObjects)
                        throw new InvalidDataException("The local deep-sky source exceeds 100,000 objects.");
                    DeepSkyObject item = ReadObject(kind, names, properties) with { CatalogueFile = file };
                    ObjectId id = item.Id;
                    string key = FormattableString.Invariant($"{kind}\0{names}\0{item.RightAscensionDegrees:R}\0{item.DeclinationDegrees:R}\0{item.DistanceLightYears:R}");
                    if (primaryKeys.TryAdd(id, key))
                    {
                        objects[id] = item;
                        continue;
                    }

                    string primary = primaryKeys[id];
                    int order = StringComparer.Ordinal.Compare(key, primary);
                    if (order == 0)
                        continue;
                    if (order < 0)
                    {
                        (item, objects[id]) = (objects[id], item);
                        (key, primaryKeys[id]) = (primary, key);
                    }
                    var distinctId = new ObjectId(id.Value + "-" + ShortHash(key));
                    duplicates.TryAdd(distinctId, item with { Id = distinctId });
                }
                return true;
            });
        }
        foreach (var (id, duplicate) in duplicates)
        {
            if (!objects.TryAdd(id, duplicate))
                throw new InvalidDataException($"Conflicting DSC identifier {id.Value}.");
        }
        return new(objects);
    }

    private static DeepSkyObject ReadObject(string kind, string names, SscValue.Map fields)
    {
        DeepSkyKind type = kind switch
        {
            "Galaxy" => DeepSkyKind.Galaxy, "Nebula" => DeepSkyKind.Nebula,
            "OpenCluster" => DeepSkyKind.OpenCluster, "Globular" => DeepSkyKind.GlobularCluster,
            _ => throw new InvalidDataException($"Unknown DSC object kind {kind}.")
        };
        string[] aliases = names.Split(':', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (aliases.Length == 0)
            throw new InvalidDataException("A DSC object needs a name.");
        string name = aliases[0];
        string? messier = aliases.Select(alias => Messier.Match(alias)).FirstOrDefault(match => match.Success)?.Groups[1].Value;
        string designation = messier is null ? name : "M" + messier;
        string slug = NonId.Replace(designation.ToLowerInvariant(), "-").Trim('-');
        if (slug.Length is 0 or > 80)
            slug = ShortHash(designation);
        double ra = Number("RA", 0), dec = Number("Dec", 0), distance = Number("Distance", 1), radius = Number("Radius", 1);
        if (ra is < 0 or >= 24.0001 || dec is < -90 or > 90 || distance is <= 0 or > 1e12 || radius is <= 0 or > 1e11)
            throw new InvalidDataException($"DSC position or size out of range for {name}.");
        return new(new("dso/" + slug), designation, name, type, ra * 15 % 360, dec, distance, radius)
        {
            Aliases = Array.AsReadOnly(aliases.Append(designation).Distinct(StringComparer.OrdinalIgnoreCase).ToArray()),
            AbsoluteMagnitude = fields.Fields.ContainsKey("AbsMag") ? Number("AbsMag", 0) : null,
            Morphology = Text("Type"),
            Orientation = Orientation(fields, Number("Angle", 0)),
            KingConcentration = Number("KingConcentration", 1.5),
            CoreRadiusArcminutes = Number("CoreRadius", 0),
            Mesh = Text("Mesh")
        };

        string? Text(string key) => fields.Fields.TryGetValue(key, out SscValue? value) ? value.AsText() : null;
        double Number(string key, double fallback) => Text(key) is { } token ? Parse(token, key) : fallback;
    }

    private static Basis3d Orientation(SscValue.Map fields, double degrees)
    {
        if (degrees == 0)
            return Basis3d.Identity;
        Vector3d axis = new(1, 0, 0);
        if (fields.Fields.TryGetValue("Axis", out var field))
        {
            if (field is not SscValue.List { Items.Length: 3 } vector)
                throw new InvalidDataException("The DSC axis needs three coordinates.");
            // Convert the source ecliptic Y-up direction to ICRF.
            double[] v = vector.Items.Select(part => Parse(part.AsText(), "Axis")).ToArray();
            Vector3d native = new(v[0], -v[2], v[1]);
            if (native.Length > 0)
                axis = HistoricalSolarEphemeris.Rotate(native / native.Length);
        }
        var (sin, cos) = Math.SinCos(degrees * Math.PI / 180);
        return new(Rotate(new(1, 0, 0)), Rotate(new(0, 1, 0)), Rotate(new(0, 0, 1)));
        Vector3d Rotate(Vector3d value) => value * cos + Vector3d.Cross(axis, value) * sin + axis * (Vector3d.Dot(axis, value) * (1 - cos));
    }

    private static double Parse(string value, string key) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double result) && double.IsFinite(result)
            ? result : throw new InvalidDataException($"Invalid DSC {key}.");

    private static string ShortHash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..12].ToLowerInvariant();
}
