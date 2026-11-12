// SPDX-License-Identifier: GPL-2.0-or-later
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using SpaceTangent.Contracts;
using SpaceTangent.Core.Addons;

namespace SpaceTangent.Core.Observing;

public sealed record TransientStar(ObjectId Id, string Name, string Designation, bool Supernova, string Type,
    Vector3d Direction, double PeakJulianDay, double PeakMagnitude, double QuietMagnitude, int T2, int T3, int T6, int T9)
{
    public double MagnitudeAt(double julianDay)
    {
        double age = julianDay - PeakJulianDay;
        return Supernova ? SupernovaMagnitude(age) : NovaMagnitude(age);
    }

    private double SupernovaMagnitude(double age)
    {
        if (Type.Contains("II", StringComparison.Ordinal))
        {
            return age switch
            {
                < -20 => 99,
                < 0 => PeakMagnitude - .75 * age,
                <= 30 => PeakMagnitude + .05 * age,
                <= 80 => PeakMagnitude + 1.5 + .013 * (age - 30),
                <= 100 => PeakMagnitude + 2.15 + .075 * (age - 80),
                _ => PeakMagnitude + 3.65 + .025 * (age - 100)
            };
        }
        return age switch
        {
            < -15 => 99,
            < 0 => PeakMagnitude - 1.13 * age,
            <= 25 => PeakMagnitude + .1 * age,
            _ => PeakMagnitude + 2.5 + .016 * (age - 25)
        };
    }

    private double NovaMagnitude(double age)
    {
        if (age < 0)
            return age < -3 ? QuietMagnitude : PeakMagnitude - (QuietMagnitude - PeakMagnitude) * (age / 3);
        double delta = age <= T2 ? 2 * age / T2 : age <= T3 ? 2 + (age - T2) / (T3 - T2) :
            age <= T6 ? 3 + 3 * (age - T3) / (T6 - T3) : age <= T9 ? 6 + 3 * (age - T6) / (T9 - T6) :
            9 + 9 * (age - T9) / T9;
        return Math.Min(QuietMagnitude, PeakMagnitude + delta);
    }
}

/// <summary>Local historical nova and supernova directions and light curves.</summary>
public sealed class TransientStarCatalogue
{
    private static readonly Regex NonId = new("[^a-z0-9]+", RegexOptions.CultureInvariant);
    private readonly Dictionary<ObjectId, TransientStar> _stars;

    private TransientStarCatalogue(Dictionary<ObjectId, TransientStar> stars)
    {
        _stars = stars;
        Stars = Array.AsReadOnly(stars.Values.ToArray());
    }

    public static TransientStarCatalogue Empty { get; } = new([]);
    public IReadOnlyList<TransientStar> Stars { get; }
    public TransientStar? Find(ObjectId id) => _stars.GetValueOrDefault(id);

    public static TransientStarCatalogue Load(string root, Action<string>? report = null, CancellationToken cancellation = default)
    {
        root = Path.GetFullPath(root);
        var stars = new Dictionary<ObjectId, TransientStar>();
        Read("supernovae.json", "supernova", true);
        Read("novae.json", "nova", false);
        return stars.Count == 0 ? Empty : new(stars);

        void Read(string file, string key, bool supernova)
        {
            string path = Path.Combine(root, file);
            if (!AddonDependencies.FileExists(path))
                return;
            try
            {
                const int maxBytes = 1024 * 1024;
                using var input = AddonDependencies.OpenRead(LocalAddonFiles.Resolve(root, root, path), maxBytes);
                if (input.Length > maxBytes)
                    throw new InvalidDataException("The transient catalogue exceeds 1 MiB.");
                using var document = JsonDocument.Parse(input);
                var parsed = new Dictionary<ObjectId, TransientStar>();
                foreach (var record in document.RootElement.GetProperty(key).EnumerateObject())
                {
                    cancellation.ThrowIfCancellationRequested();
                    TransientStar star = ReadStar(record, supernova);
                    parsed.Add(star.Id, star);
                }
                foreach (var (id, star) in parsed)
                    stars.Add(id, star);
            }
            catch (Exception error) when (report is not null && error is IOException or UnauthorizedAccessException or
                JsonException or FormatException or KeyNotFoundException or InvalidOperationException or ArgumentException or OverflowException)
            {
                report($"{file}: {error.Message}");
            }
        }
    }

    private static TransientStar ReadStar(JsonProperty entry, bool supernova)
    {
        JsonElement data = entry.Value;
        string designation = supernova ? "SN " + entry.Name : entry.Name;
        string name = data.TryGetProperty("name", out var label) ? label.GetString() ?? designation : designation;
        string type = data.GetProperty("type").GetString() ?? "";
        string slug = NonId.Replace(designation.ToLowerInvariant(), "-").Trim('-');
        double ra = ReadAngle(data.GetProperty(supernova ? "alpha" : "RA").GetString()) * 15;
        double dec = ReadAngle(data.GetProperty(supernova ? "delta" : "Dec").GetString());
        double peakDay = data.GetProperty("peakJD").GetDouble(), peak = data.GetProperty("maxMagnitude").GetDouble();
        double quiet = supernova ? 99 : data.GetProperty("minMagnitude").GetDouble();
        if (ra is < 0 or > 360 || dec is < -90 or > 90 || !double.IsFinite(peakDay) || peakDay < 0 ||
            peakDay >= JulianInstant.MaximumDay + 1d || !double.IsFinite(peak) || !double.IsFinite(quiet) || quiet < peak)
            throw new InvalidDataException($"Invalid transient position or magnitude for {designation}.");
        var (sinRa, cosRa) = Math.SinCos(ra * Math.PI / 180);
        var (sinDec, cosDec) = Math.SinCos(dec * Math.PI / 180);
        int t2 = Fade("m2", 10, 80, 200, 1);
        int t3 = Fade("m3", 30, 160, 300, t2 + 1);
        int t6 = Fade("m6", 100, 300, 1200, t3 + 1);
        int t9 = Fade("m9", 400, 1000, 3000, t6 + 1);
        return new(new("transient/" + slug), name, designation, supernova, type, new(cosDec * cosRa, cosDec * sinRa, sinDec),
            peakDay, peak, quiet, t2, t3, t6, t9);

        int Fade(string key, int fast, int slow, int verySlow, int minimum)
        {
            int fallback = type.Contains("NA", StringComparison.Ordinal) ? fast :
                type.Contains("NC", StringComparison.Ordinal) ? verySlow : slow;
            int time = data.TryGetProperty(key, out var value) && value.TryGetInt32(out int days) && days > 0 ? days : fallback;
            return Math.Clamp(time, minimum, minimum + 20_000);
        }
    }

    private static double ReadAngle(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidDataException("Missing transient coordinate.");
        string[] parts = value.Trim().Split(['h', 'd', 'm', 's'], StringSplitOptions.RemoveEmptyEntries);
        double scale = 1, result = 0;
        foreach (string part in parts)
        {
            double component = double.Parse(part, NumberStyles.Float, CultureInfo.InvariantCulture);
            if (!double.IsFinite(component))
                throw new InvalidDataException("Invalid transient coordinate.");
            result += Math.Abs(component) * scale;
            scale /= 60;
        }
        return value.TrimStart().StartsWith('-') ? -result : result;
    }
}
