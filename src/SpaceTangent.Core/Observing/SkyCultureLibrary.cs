// SPDX-License-Identifier: GPL-2.0-or-later
using System.Globalization;
using System.Text;
using System.Text.Json;
using SpaceTangent.Contracts;
using SpaceTangent.Core.Addons;

namespace SpaceTangent.Core.Observing;

public enum SkyCultureNames { International, Native, Russian }

public sealed record SkyCultureFigure(string Id, string InternationalName, string NativeName, string RussianName,
    IReadOnlyList<IReadOnlyList<uint?>> Lines, SkyCultureArtwork? Artwork)
{
    public string DisplayName(SkyCultureNames style) => style switch
    {
        SkyCultureNames.Native => NativeName,
        SkyCultureNames.Russian => RussianName,
        _ => InternationalName
    };
}

public readonly record struct SkyArtAnchor(double X, double Y, uint Hipparcos);

public sealed record SkyCultureArtwork(string ImagePath, int Width, int Height,
    SkyArtAnchor First, SkyArtAnchor Second, SkyArtAnchor Third)
{
    public Vector3d? Direction(double x, double y, Vector3d first, Vector3d second, Vector3d third)
    {
        double bx = Second.X - First.X, by = Second.Y - First.Y;
        double cx = Third.X - First.X, cy = Third.Y - First.Y;
        double determinant = bx * cy - by * cx;
        if (Math.Abs(determinant) < 1e-8)
            return null;
        double px = x - First.X, py = y - First.Y;
        double b = (px * cy - py * cx) / determinant;
        double c = (bx * py - by * px) / determinant;
        Vector3d direction = first * (1 - b - c) + second * b + third * c;
        double length = direction.Length;
        return length > 1e-10 && double.IsFinite(length) ? direction / length : null;
    }
}

public sealed record SkyCultureDefinition(string Id, string Title, IReadOnlyList<SkyCultureFigure> Figures);

public sealed class SkyCultureLibrary
{
    private readonly Dictionary<string, SkyCultureDefinition> _index;
    private SkyCultureLibrary(IEnumerable<SkyCultureDefinition> entries) =>
        _index = entries.ToDictionary(culture => culture.Id, StringComparer.OrdinalIgnoreCase);

    public IReadOnlyCollection<SkyCultureDefinition> Cultures => _index.Values;
    public SkyCultureDefinition? Find(string? id) => id is null ? null : _index.GetValueOrDefault(id);

    public static bool ValidId(string value) => value.Length is > 0 and <= 64 &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-');

    public static SkyCultureLibrary Load(string directory, Action<string>? warning = null)
    {
        string root = Path.GetFullPath(directory);
        if (!Directory.Exists(root))
            return new([]);
        var cultures = new Dictionary<string, SkyCultureDefinition>(StringComparer.OrdinalIgnoreCase);
        string[] folders = Directory.EnumerateDirectories(root).Take(129).Order(StringComparer.Ordinal).ToArray();
        if (folders.Length > 128)
            throw new InvalidDataException("The sky culture directory exceeds 128 folders.");
        foreach (string folder in folders)
        {
            try
            {
                if (!File.Exists(Path.Combine(folder, "index.json")))
                    continue;
                var culture = ReadCulture(root, folder, warning);
                if (!cultures.TryAdd(culture.Id, culture))
                    throw new InvalidDataException("Duplicate sky culture identifier.");
            }
            catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or
                InvalidOperationException or KeyNotFoundException or ArgumentException)
            {
                warning?.Invoke($"{Path.GetFileName(folder)}: {error.Message}");
            }
        }
        return new(cultures.Values);
    }

    private static SkyCultureDefinition ReadCulture(string root, string folder, Action<string>? warning)
    {
        string index = LocalAddonFiles.ResolveRelative(root, folder, "index.json");
        using var document = JsonDocument.Parse(ReadText(index), new JsonDocumentOptions { MaxDepth = 20 });
        var data = document.RootElement;
        string id = Text(data, "id") ?? "";
        if (!ValidId(id) || !Path.GetFileName(folder).Equals(id, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("A sky culture requires a matching folder and identifier.");
        string? translation = OptionalText("ru.po") ?? OptionalText("po/ru.po");
        var russian = translation is null ? new Dictionary<(string, string), string>() : ReadTranslations(translation);
        string title = OptionalText("description.md")?.Split('\n').Select(line => line.Trim())
            .FirstOrDefault(line => line.StartsWith("# ", StringComparison.Ordinal))?[2..].Trim() ?? id;
        bool useLatin = data.TryGetProperty("fallback_to_international_names", out var fallback) && fallback.ValueKind == JsonValueKind.True;
        var figures = new List<SkyCultureFigure>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        JsonElement items = data.GetProperty("constellations");
        if (items.GetArrayLength() > 1024)
            throw new InvalidDataException("A sky culture exceeds 1024 figures.");
        foreach (var item in items.EnumerateArray())
        {
            string figureId = Text(item, "id") ?? throw new InvalidDataException("A constellation requires an identifier.");
            if (!ids.Add(figureId))
                throw new InvalidDataException("Duplicate constellation identifier.");
            var name = item.TryGetProperty("common_name", out var given) ? given : default;
            string english = Text(name, "english") ?? Text(name, "native") ?? figureId;
            string native = Text(name, "native") ?? english;
            string context = Text(name, "context")?.Trim() ?? "";
            var lines = new List<IReadOnlyList<uint?>>();
            if (item.TryGetProperty("lines", out var paths))
            {
                if (paths.GetArrayLength() > 1024)
                    throw new InvalidDataException("A constellation exceeds 1024 line chains.");
                foreach (var chain in paths.EnumerateArray())
                {
                    if (chain.GetArrayLength() > 1024)
                        throw new InvalidDataException("A constellation line exceeds 1024 vertices.");
                    uint?[] stars = chain.EnumerateArray().Where(point =>
                        point.ValueKind != JsonValueKind.String || point.GetString() is not ("thin" or "bold"))
                        .Select(Hipparcos).ToArray();
                    if (stars.Length >= 2)
                        lines.Add(Array.AsReadOnly(stars));
                }
            }
            var art = ReadArtwork(item, root, folder, warning);
            if (lines.Count == 0 && art is null)
                continue;
            string international = useLatin ? native : english;
            string translated = russian.GetValueOrDefault((context, english)) ?? russian.GetValueOrDefault(("", english)) ?? international;
            figures.Add(new(figureId, international, native, translated, lines.AsReadOnly(), art));
        }
        return new(id, russian.GetValueOrDefault(("", title)) ?? title, figures.AsReadOnly());

        string? OptionalText(string file) => File.Exists(Path.Combine(folder, file))
            ? ReadText(LocalAddonFiles.ResolveRelative(root, folder, file)) : null;
    }

    private static SkyCultureArtwork? ReadArtwork(JsonElement figure, string root, string folder, Action<string>? warning)
    {
        if (!figure.TryGetProperty("image", out var image) || image.ValueKind != JsonValueKind.Object ||
            Text(image, "file") is not { Length: > 0 } file || !image.TryGetProperty("size", out var size) ||
            size.GetArrayLength() != 2 || !size[0].TryGetInt32(out int width) || !size[1].TryGetInt32(out int height) ||
            width is < 1 or > 4096 || height is < 1 or > 4096 ||
            !image.TryGetProperty("anchors", out var anchors) || anchors.GetArrayLength() != 3)
            return null;
        var points = new SkyArtAnchor[3];
        for (int index = 0; index < points.Length; index++)
        {
            var anchor = anchors[index];
            if (!anchor.TryGetProperty("pos", out var at) || at.GetArrayLength() != 2 ||
                !at[0].TryGetDouble(out double x) || !at[1].TryGetDouble(out double y) ||
                !double.IsFinite(x) || !double.IsFinite(y) || Math.Abs(x) > 16384 || Math.Abs(y) > 16384 ||
                !anchor.TryGetProperty("hip", out var star) || Hipparcos(star) is not { } hip)
                return null;
            points[index] = new(x, y, hip);
        }
        string path;
        try
        {
            path = LocalAddonFiles.ResolveRelative(root, folder, file);
        }
        catch (FileNotFoundException)
        {
            warning?.Invoke($"Constellation artwork {file} is missing.");
            return null;
        }
        if (Path.GetExtension(path).ToLowerInvariant() is not (".webp" or ".png" or ".jpg" or ".jpeg"))
            return null;
        return new(path, width, height, points[0], points[1], points[2]);
    }

    private static string ReadText(string file)
    {
        const int maximum = 4 * 1024 * 1024;
        using FileStream input = AddonDependencies.OpenRead(file, maximum);
        if (input.Length > maximum)
            throw new InvalidDataException("Sky culture text exceeds 4 MiB.");
        using var reader = new StreamReader(input, new UTF8Encoding(false, true));
        return reader.ReadToEnd();
    }

    private static string? Text(JsonElement obj, string key) => obj.ValueKind == JsonValueKind.Object &&
        obj.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } text
        ? text : null;

    private static uint? Hipparcos(JsonElement value)
    {
        uint number = 0;
        bool parsed = value.ValueKind switch
        {
            JsonValueKind.Number => value.TryGetUInt32(out number),
            JsonValueKind.String => uint.TryParse(value.GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out number),
            _ => false
        };
        return parsed && number > 0 ? number : null;
    }

    private static Dictionary<(string Context, string Text), string> ReadTranslations(string source)
    {
        var translated = new Dictionary<(string, string), string>();
        var context = new StringBuilder();
        var original = new StringBuilder();
        var translation = new StringBuilder();
        StringBuilder? current = null;
        using var reader = new StringReader(source);
        while (reader.ReadLine() is { } raw)
        {
            string line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("msgctxt ", StringComparison.Ordinal) ||
                line.StartsWith("msgid ", StringComparison.Ordinal) && original.Length > 0)
                Flush();
            string? fragment = null;
            if (line.StartsWith("msgctxt ", StringComparison.Ordinal))
                (current, fragment) = (context, line[8..]);
            else if (line.StartsWith("msgid ", StringComparison.Ordinal))
                (current, fragment) = (original, line[6..]);
            else if (line.StartsWith("msgstr ", StringComparison.Ordinal))
                (current, fragment) = (translation, line[7..]);
            else if (line.StartsWith('"'))
                fragment = line;
            if (current is not null && fragment is { Length: >= 2 } && fragment[0] == '"' && fragment[^1] == '"')
            {
                for (int index = 1; index < fragment.Length - 1; index++)
                {
                    char c = fragment[index];
                    if (c == '\\' && index < fragment.Length - 2)
                        c = fragment[++index] switch { 'n' => '\n', 'r' => '\r', 't' => '\t', var escaped => escaped };
                    current.Append(c);
                }
            }
        }
        Flush();
        return translated;

        void Flush()
        {
            if (original.Length > 0 && translation.Length > 0)
                translated.TryAdd((context.ToString(), original.ToString()), translation.ToString());
            current = null;
            context.Clear();
            original.Clear();
            translation.Clear();
        }
    }
}
