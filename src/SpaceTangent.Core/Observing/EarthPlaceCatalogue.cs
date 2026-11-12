// SPDX-License-Identifier: GPL-2.0-or-later
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using SpaceTangent.Contracts;
using SpaceTangent.Core.Addons;

namespace SpaceTangent.Core.Observing;

public sealed record EarthPlace(ObjectId Id, string Name, string EnglishName, string Region, char Kind,
    double PopulationThousands, double LatitudeDegrees, double LongitudeDegrees, double AltitudeMeters,
    int? Bortle, string TimeZone, double? FixedOffsetHours = null)
{
    public EarthSite ObserverSite => new(LatitudeDegrees, LongitudeDegrees, Math.Max(0, AltitudeMeters / 1000));

    public double OffsetHours(DateTimeOffset instant)
    {
        if (TimeZone.Length != 0 && TimeZoneInfo.TryFindSystemTimeZoneById(TimeZone, out var zone))
            return zone.GetUtcOffset(instant).TotalHours;
        return FixedOffsetHours ?? Math.Round(LongitudeDegrees / 15);
    }
}

public sealed class EarthPlaceCatalogue
{
    private const int MaximumBytes = 4 * 1024 * 1024;
    private readonly Dictionary<ObjectId, EarthPlace> _byId;

    private EarthPlaceCatalogue(IEnumerable<EarthPlace> places)
    {
        _byId = places.ToDictionary(place => place.Id);
        Places = Array.AsReadOnly(_byId.Values.ToArray());
    }

    public static EarthPlaceCatalogue Empty { get; } = new([]);
    public IReadOnlyList<EarthPlace> Places { get; }
    public EarthPlace? Find(ObjectId id) => _byId.GetValueOrDefault(id);

    public static EarthPlaceCatalogue Load(string archivePath, string? savedPath = null,
        Action<string>? warning = null, CancellationToken cancellation = default)
    {
        return LoadWithSavedSources(archivePath, string.IsNullOrEmpty(savedPath) ? [] : [savedPath], warning, cancellation);
    }

    public static EarthPlaceCatalogue LoadWithSavedSources(string archivePath, IEnumerable<string> savedPaths,
        Action<string>? warning = null, CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(savedPaths);
        var places = new Dictionary<ObjectId, EarthPlace>();
        Read(archivePath, saved: false);
        foreach (string savedPath in savedPaths)
            Read(savedPath, saved: true);
        return new(places.Values);

        void Read(string path, bool saved)
        {
            if (!AddonDependencies.FileExists(path))
                return;
            AddonDiagnostics.Read(AddonStage.Catalog, path, () =>
            {
                using var file = AddonDependencies.OpenRead(path, MaximumBytes);
                if (file.Length > MaximumBytes)
                    throw new InvalidDataException("An Earth place list exceeds 4 MiB.");
                using var reader = new StreamReader(file, new UTF8Encoding(false, true));
                int rejected = 0;
                while (reader.ReadLine() is { } line)
                {
                    cancellation.ThrowIfCancellationRequested();
                    if (line.Length == 0 || line.StartsWith('#'))
                        continue;
                    var columns = line.Split('\t');
                    EarthPlace? place = saved ? ParseSaved(columns) : ParseArchive(columns);
                    if (place is null)
                    {
                        rejected++;
                        continue;
                    }
                    if (places.Count >= 16_384 && !places.ContainsKey(place.Id))
                        throw new InvalidDataException("The Earth place lists exceed 16,384 distinct entries.");
                    places[place.Id] = place;
                }
                if (rejected != 0)
                    warning?.Invoke($"{Path.GetFileName(path)}: ignored {rejected} invalid Earth places.");
                return true;
            });
        }
    }

    public EarthPlace? Nearest(double latitudeDegrees, double longitudeDegrees, double limitDegrees)
    {
        if (!(latitudeDegrees is >= -90 and <= 90) || !(longitudeDegrees is >= -180 and <= 180) ||
            !(limitDegrees is >= 0 and <= 180))
            throw new ArgumentOutOfRangeException(nameof(limitDegrees));
        Vector3d point = Direction(latitudeDegrees, longitudeDegrees);
        double nearest = Math.Cos(limitDegrees * Math.PI / 180);
        EarthPlace? result = null;
        foreach (var place in Places)
        {
            double dot = Vector3d.Dot(point, Direction(place.LatitudeDegrees, place.LongitudeDegrees));
            if (dot >= nearest)
            {
                nearest = dot;
                result = place;
            }
        }
        return result;
    }

    private static EarthPlace? ParseArchive(string[] row)
    {
        if (row.Length < 10 || string.IsNullOrWhiteSpace(row[0]) || row[3].Length != 1 ||
            !Number(row[4], out double population) || !Coordinates(row, 5, out double latitude, out double longitude, out double altitude))
            return null;
        string english = row[1].Length == 0 ? row[0] : row[1];
        int? bortle = int.TryParse(row[8], NumberStyles.None, CultureInfo.InvariantCulture, out int value) && value is >= 1 and <= 9 ? value : null;
        string identity = FormattableString.Invariant($"{english.ToUpperInvariant()}|{row[2].ToUpperInvariant()}|{latitude:R}|{longitude:R}");
        return new(Id("archive", identity), row[0], english, row[2], row[3][0], population,
            latitude, longitude, altitude, bortle, row[9] == "-" ? "" : row[9]);
    }

    public static IReadOnlyList<EarthPlace> ParseSavedText(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return [];
        if (Encoding.UTF8.GetByteCount(text) > MaximumBytes)
            throw new InvalidDataException("A saved Earth place list exceeds 4 MiB.");
        var places = new Dictionary<ObjectId, EarthPlace>();
        using var reader = new StringReader(text);
        while (reader.ReadLine() is { } line)
        {
            if (line.Length == 0 || line.StartsWith('#') || ParseSaved(line.Split('\t')) is not { } place)
                continue;
            if (places.Count == 16_384 && !places.ContainsKey(place.Id))
                throw new InvalidDataException("A saved Earth place list exceeds 16,384 distinct entries.");
            places[place.Id] = place;
        }
        return Array.AsReadOnly(places.Values.ToArray());
    }

    private static EarthPlace? ParseSaved(string[] row)
    {
        if (row.Length is not (5 or 6) || string.IsNullOrWhiteSpace(row[0]) ||
            !Coordinates(row, 1, out double latitude, out double longitude, out double altitude) ||
            !Number(row[4], out double offset) || offset is < -14 or > 14)
            return null;
        string name = row[0].Trim();
        return new(Id("saved", name.ToUpperInvariant()), name, name, "", 'U', 0, latitude, longitude,
            altitude, null, row.Length == 6 ? row[5] : "", offset);
    }

    private static bool Coordinates(string[] row, int start, out double latitude, out double longitude, out double altitude)
    {
        bool parsedLatitude = Number(row[start], out latitude);
        bool parsedLongitude = Number(row[start + 1], out longitude);
        bool parsedAltitude = Number(row[start + 2], out altitude);
        return parsedLatitude && parsedLongitude && parsedAltitude &&
            latitude is >= -90 and <= 90 && longitude is >= -180 and <= 180 && altitude is >= -100_000 and <= 100_000;
    }

    private static ObjectId Id(string source, string key)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(key));
        return new($"site/earth/{source}/{Convert.ToHexString(hash.AsSpan(0, 12)).ToLowerInvariant()}");
    }

    private static Vector3d Direction(double latitude, double longitude)
    {
        var (sinLat, cosLat) = Math.SinCos(latitude * Math.PI / 180);
        var (sinLon, cosLon) = Math.SinCos(longitude * Math.PI / 180);
        return new(cosLat * cosLon, cosLat * sinLon, sinLat);
    }

    private static bool Number(string value, out double number) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out number) && double.IsFinite(number);
}
