// SPDX-License-Identifier: GPL-2.0-or-later
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SpaceTangent.Core.Addons;

/// <summary>Quadtree coordinates with two root columns and the first row at the south pole.</summary>
public readonly record struct SurfaceTileAddress(int Level, int Column, int Row);

public readonly record struct SurfaceTileRegion(string? File, float Left, float Bottom, float Right, float Top);

public sealed record SurfaceTilePyramid(int LevelCount, int TileSize, float Border, bool HasColumns, bool HasRows,
    bool PackedNormals, IReadOnlyDictionary<SurfaceTileAddress, string> Tiles)
{
    public int EffectiveTileSize => TileSize * 3 / 5;

    public SurfaceTileRegion FindRegion(SurfaceTileAddress address)
    {
        if (!IsAddress(address, 21))
            throw new ArgumentOutOfRangeException(nameof(address));
        float left = Border, bottom = Border, right = 1 - Border, top = 1 - Border;
        var at = address;
        while (at.Level >= 0)
        {
            var key = new SurfaceTileAddress(at.Level, HasColumns ? at.Column : 0, HasRows ? at.Row : 0);
            if (at.Level < LevelCount && Tiles.TryGetValue(key, out string? file))
                return new(file, left, bottom, right, top);
            at = new(at.Level - 1, at.Column / 2, at.Row / 2);
            float width = (right - left) * .5f, height = (top - bottom) * .5f;
            int mask = (1 << (address.Level - at.Level)) - 1;
            left = Border + width * (address.Column & mask);
            bottom = Border + height * (address.Row & mask);
            right = left + width;
            top = bottom + height;
        }
        return new(null, left, bottom, right, top);
    }

    internal static bool IsAddress(SurfaceTileAddress address, int levelCount) =>
        address.Level >= 0 && address.Level < levelCount && address.Column >= 0 &&
        address.Column < (2 << address.Level) && address.Row >= 0 && address.Row < (1 << address.Level);
}

public sealed record BodySurfaceMaps(string Name, SurfaceTilePyramid? Color, SurfaceTilePyramid? Normal);

/// <summary>Discovers local color and normal pyramids in original and previously packed world catalogues.</summary>
public static class LocalSurfaceMaps
{
    private const int MaximumCatalogueBytes = 16 * 1024 * 1024;
    private const int MaximumTiles = 16384;
    private static readonly Regex Tokens = new("%level|%column|%row", RegexOptions.CultureInvariant);

    public static IReadOnlyList<BodySurfaceMaps> Load(string root, CancellationToken cancellation = default)
    {
        root = Path.GetFullPath(root);
        if (!AddonDependencies.DirectoryExists(root))
            return [];
        string packedPath = Path.Combine(root, "pack.json");
        bool packed = AddonDependencies.FileExists(packedPath);
        string[] files = packed ? [packedPath] : AddonDependencies.ListFiles(root, "*.json");
        var results = new List<BodySurfaceMaps>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string path in files.OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            cancellation.ThrowIfCancellationRequested();
            AddonDiagnostics.Read(AddonStage.Texture, path, () =>
            {
                string file = LocalAddonFiles.Resolve(root, root, path);
                using var input = AddonDependencies.OpenRead(file, MaximumCatalogueBytes);
                if (input.Length > MaximumCatalogueBytes)
                    throw new InvalidDataException("The local surface catalogue exceeds 16 MiB.");
                using var document = JsonDocument.Parse(input, new JsonDocumentOptions
                    { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
                if (!Property(document.RootElement, packed ? "bodies" : "items", out var items) || items.ValueKind != JsonValueKind.Array)
                    return false;
                foreach (var item in items.EnumerateArray())
                {
                    cancellation.ThrowIfCancellationRequested();
                    if (!Property(item, packed ? "appearance" : "geometry", out var surface))
                        continue;
                    string? name = Text(item, "name");
                    if (string.IsNullOrWhiteSpace(name))
                        continue;
                    var color = ReadMap(surface, packed ? "tiledMap" : "baseMap", root, packed, cancellation);
                    var normal = ReadMap(surface, "normal" + (packed ? "TiledMap" : "Map"), root, packed, cancellation);
                    if (color is null && normal is null)
                        continue;
                    if (!names.Add(name))
                        throw new InvalidDataException($"More than one local tile declaration for {name}.");
                    results.Add(new(name, color, normal));
                }
                return true;
            });
        }
        return results.AsReadOnly();
    }

    private static SurfaceTilePyramid? ReadMap(JsonElement appearance, string key, string root, bool packed,
        CancellationToken cancellation)
    {
        if (!Property(appearance, key, out var map) || Text(map, "type") != "NameTemplate")
            return null;
        string template = Text(map, "template")?.Replace('\\', '/') ?? throw new InvalidDataException("Missing tile template.");
        if (string.IsNullOrWhiteSpace(template) || template.Length > 4096 || Path.IsPathRooted(template) ||
            template.Split('/').Any(part => part is "" or "." or ".."))
            throw new InvalidDataException("Tile templates must be inside the surface catalogue root.");
        int levels = (int)Math.Clamp(Unsigned(map, "levelCount"), 1u, 16u);
        int tileSize = (int)Math.Clamp(Unsigned(map, "tileSize"), 128u, 8192u);
        float border = Property(map, "tileBorderThickness", out var field) ?
            float.Parse(field.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture) : 0;
        if (!float.IsFinite(border) || border is < 0 or >= .5f)
            throw new InvalidDataException("A tile border must be a finite fraction below one half.");
        bool hasColumns = template.Contains("%column", StringComparison.Ordinal);
        bool hasRows = template.Contains("%row", StringComparison.Ordinal);
        var tiles = new Dictionary<SurfaceTileAddress, string>();
        if (Property(map, "localFiles", out var localFiles))
        {
            string textureDirectory = packed ? Path.Combine(root, "textures") : root;
            foreach (var entry in localFiles.EnumerateObject())
            {
                cancellation.ThrowIfCancellationRequested();
                var address = ParseAddress(entry.Name, levels);
                string relative = entry.Value.GetString() ?? throw new InvalidDataException("A local tile path is required.");
                string directory = packed && relative.Replace('\\', '/').StartsWith("textures/", StringComparison.OrdinalIgnoreCase)
                    ? root : textureDirectory;
                try
                {
                    tiles.Add(address, LocalAddonFiles.ResolveRelative(root, directory, relative));
                    CheckSize(tiles.Count);
                }
                catch (FileNotFoundException)
                {
                    // A sparse pack can use an ancestor for missing tiles.
                }
            }
        }
        else
        {
            ReadTemplate(root, template, levels, hasRows, tiles, cancellation);
        }
        bool packedNormals = template.EndsWith(".dds", StringComparison.OrdinalIgnoreCase) ||
            template.EndsWith(".dxt5nm", StringComparison.OrdinalIgnoreCase);
        return new(levels, tileSize, border, hasColumns, hasRows, packedNormals,
            new System.Collections.ObjectModel.ReadOnlyDictionary<SurfaceTileAddress, string>(tiles));
    }

    private static void ReadTemplate(string root, string template, int levels, bool hasRows,
        Dictionary<SurfaceTileAddress, string> tiles, CancellationToken cancellation)
    {
        var pieces = new List<(string Literal, int Token)>();
        int end = 0;
        foreach (Match token in Tokens.Matches(template))
        {
            if (token.Index > end)
                pieces.Add((template[end..token.Index], -1));
            pieces.Add(("", token.Value switch { "%level" => 0, "%column" => 1, _ => 2 }));
            end = token.Index + token.Length;
        }
        if (end < template.Length)
            pieces.Add((template[end..], -1));
        string[] templateParts = template.Split('/');
        foreach (string candidate in MatchPaths(root, templateParts, 0, cancellation))
        {
            string relative = Path.GetRelativePath(root, candidate).Replace('\\', '/');
            Parse(0, 0, null, null, null);

            void Parse(int piece, int offset, int? level, int? column, int? northRow)
            {
                if (piece == pieces.Count)
                {
                    if (offset != relative.Length)
                        return;
                    for (int at = level ?? 0; at < (level.HasValue ? level.Value + 1 : levels); at++)
                    {
                        var address = new SurfaceTileAddress(at, column ?? 0, hasRows ? (1 << at) - 1 - northRow!.Value : 0);
                        if (SurfaceTilePyramid.IsAddress(address, levels) && !tiles.ContainsKey(address))
                        {
                            tiles.Add(address, LocalAddonFiles.Resolve(root, root, candidate));
                            CheckSize(tiles.Count);
                        }
                    }
                    return;
                }
                var (literal, token) = pieces[piece];
                if (token < 0)
                {
                    if (relative.AsSpan(offset).StartsWith(literal, StringComparison.Ordinal))
                        Parse(piece + 1, offset + literal.Length, level, column, northRow);
                    return;
                }
                int? seen = token switch { 0 => level, 1 => column, _ => northRow };
                if (seen is int value)
                {
                    string digits = value.ToString(CultureInfo.InvariantCulture);
                    if (relative.AsSpan(offset).StartsWith(digits, StringComparison.Ordinal))
                        Parse(piece + 1, offset + digits.Length, level, column, northRow);
                    return;
                }
                int maximum = token switch { 0 => levels - 1, 1 => 65535, _ => 32767 };
                int number = 0;
                for (int i = offset; i < relative.Length; i++)
                {
                    char digit = relative[i];
                    if (!char.IsAsciiDigit(digit) || (i > offset && relative[offset] == '0'))
                        break;
                    number = number * 10 + digit - '0';
                    if (number > maximum)
                        break;
                    Parse(piece + 1, i + 1, token == 0 ? number : level, token == 1 ? number : column,
                        token == 2 ? number : northRow);
                }
            }
        }
    }

    private static IEnumerable<string> MatchPaths(string directory, string[] template, int index, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        if (!AddonDependencies.DirectoryExists(directory) ||
            (AddonDependencies.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
            yield break;
        string part = template[index];
        bool wildcard = Tokens.IsMatch(part);
        if (index == template.Length - 1)
        {
            string[] files = wildcard ? AddonDependencies.ListFiles(directory, "*") :
                AddonDependencies.FileExists(Path.Combine(directory, part)) ? [Path.Combine(directory, part)] : [];
            foreach (string file in files)
            {
                cancellation.ThrowIfCancellationRequested();
                yield return file;
            }
            yield break;
        }
        var directories = wildcard ? Directory.EnumerateDirectories(directory).OrderBy(path => path, StringComparer.Ordinal) :
            (IEnumerable<string>)[Path.Combine(directory, part)];
        foreach (string child in directories)
        foreach (string candidate in MatchPaths(child, template, index + 1, cancellation))
            yield return candidate;
    }

    private static SurfaceTileAddress ParseAddress(string path, int levels)
    {
        string[] parts = path.Split('/');
        if (parts.Length == 3 && int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out int level) &&
            int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out int column) &&
            int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out int row))
        {
            var address = new SurfaceTileAddress(level, column, row);
            if (SurfaceTilePyramid.IsAddress(address, levels))
                return address;
        }
        throw new InvalidDataException($"Invalid local tile address {path}.");
    }

    private static uint Unsigned(JsonElement obj, string name) => Property(obj, name, out var field) &&
        uint.TryParse(field.ToString(), NumberStyles.None, CultureInfo.InvariantCulture, out uint value) ? value :
        throw new InvalidDataException($"Invalid local tile value {name}.");

    private static string? Text(JsonElement obj, string name) =>
        Property(obj, name, out var field) && field.ValueKind == JsonValueKind.String ? field.GetString() : null;

    private static bool Property(JsonElement obj, string name, out JsonElement field)
    {
        field = default;
        return obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out field);
    }

    private static void CheckSize(int count)
    {
        if (count > MaximumTiles)
            throw new InvalidDataException($"A local map contains more than {MaximumTiles} tile addresses.");
    }
}
