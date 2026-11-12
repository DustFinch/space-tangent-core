// SPDX-License-Identifier: GPL-2.0-or-later
using System.Text;
using SpaceTangent.Contracts;
using SpaceTangent.Core.Observing;
using SpaceTangent.Core.Orbits;
using SpaceTangent.Core.Time;

namespace SpaceTangent.Core.Addons;

public sealed class LocalTleSatellite
{
    private static readonly JulianInstant UtcStart = TimeConversion.FromUtc(new(LeapSecondTable.FirstDate), TimeScale.Tdb);
    private readonly Sgp4Orbit _orbit;

    internal LocalTleSatellite(string name, TwoLineElements elements, string[] aliases)
    {
        Name = name;
        Id = new($"norad/{elements.CatalogNumber}");
        _orbit = new(elements);
        Aliases = Array.AsReadOnly(aliases);
    }

    public ObjectId Id { get; }
    public string Name { get; }
    public IReadOnlyList<string> Aliases { get; }
    public JulianInstant Epoch => _orbit.Epoch;
    public bool DeepSpace => _orbit.DeepSpace;
    /// <summary>Returns the Earth-centered state in TEME.</summary>
    public CartesianState? Sample(JulianInstant epoch) => _orbit.TrySample(epoch, out var state) ? state : null;

    /// <summary>Rotates the inertial components through the reference Earth axes used by the simulator.</summary>
    public CartesianState? SampleInWorld(JulianInstant epoch, DeltaTModel model = DeltaTModel.MorrisonStephenson2004)
    {
        if (epoch.CompareTo(UtcStart) < 0 || Sample(epoch) is not { } state)
            return null;
        UtcDateTime utc = TimeConversion.ToUtc(epoch);
        double days = utc.Date.DayNumber + (1_721_425.5 - JulianInstant.J2000Day) +
            utc.NanosecondsSinceMidnight / (double)JulianInstant.NanosecondsPerDay;
        double centuries = days / 36_525;
        double temeSidereal = 67310.54841 + (876600 * 3600.0 + 8640184.812866) * centuries +
            centuries * centuries * (.093104 - 6.2e-6 * centuries);
        double angle = EarthSurfaceLighting.GreenwichSiderealAngle(epoch, model) -
            (temeSidereal % JulianInstant.SecondsPerDay) * (Math.Tau / JulianInstant.SecondsPerDay);
        (double sin, double cos) = Math.SinCos(angle);
        return new(Rotate(state.PositionKilometers), Rotate(state.VelocityKilometersPerSecond));

        Vector3d Rotate(Vector3d value) => new(cos * value.X - sin * value.Y, sin * value.X + cos * value.Y, value.Z);
    }
}

public static class LocalTleCatalogue
{
    private const int MaximumFiles = 64;
    private const int MaximumRecords = 16_384;
    private const int MaximumBytes = 4 * 1024 * 1024;
    private sealed record Entry(string Name, string Group, TwoLineElements Elements);

    public static IReadOnlyList<LocalTleSatellite> Load(string directory, Action<string>? warning = null,
        CancellationToken cancellation = default)
    {
        string root = Path.GetFullPath(directory);
        if (!AddonDependencies.DirectoryExists(root))
            return [];
        string[] files = [.. AddonDependencies.ListFiles(root, "*.tle"), .. AddonDependencies.ListFiles(root, "*.txt")];
        if (files.Length > MaximumFiles)
            throw new InvalidDataException("A TLE directory may contain at most 64 catalogues.");
        var entries = new List<Entry>();
        foreach (string path in files.Order(StringComparer.Ordinal))
            AddonDiagnostics.Read(AddonStage.Catalog, path, () =>
            {
                ReadFile(LocalAddonFiles.Resolve(root, root, path), entries, warning, cancellation);
                return true;
            });

        var satellites = new List<LocalTleSatellite>();
        foreach (var group in entries.GroupBy(entry => entry.Elements.CatalogNumber, StringComparer.Ordinal))
        {
            cancellation.ThrowIfCancellationRequested();
            string[] aliases = group.SelectMany(entry => new[] { entry.Name, entry.Group })
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            foreach (var candidate in group.OrderByDescending(entry => entry.Elements.UtcEpoch.Date)
                         .ThenByDescending(entry => entry.Elements.UtcEpoch.NanosecondsSinceMidnight))
            {
                try
                {
                    satellites.Add(new(candidate.Name, candidate.Elements,
                        aliases.Where(alias => !alias.Equals(candidate.Name, StringComparison.OrdinalIgnoreCase)).ToArray()));
                    break;
                }
                catch (ArgumentException error)
                {
                    warning?.Invoke($"{candidate.Group}: {candidate.Name}: {error.Message}");
                }
            }
        }
        if (entries.Count != 0 && satellites.Count == 0)
            throw new InvalidDataException("No supported orbits were found in the TLE directory.");
        return satellites.AsReadOnly();
    }

    private static void ReadFile(string path, List<Entry> entries, Action<string>? warning, CancellationToken cancellation)
    {
        using FileStream file = AddonDependencies.OpenRead(path, MaximumBytes);
        if (file.Length > MaximumBytes)
            throw new InvalidDataException("A TLE file exceeds 4 MiB.");
        using var reader = new StreamReader(file, new UTF8Encoding(false, true));
        string group = Path.GetFileNameWithoutExtension(path);
        int line = 0, initialCount = entries.Count;
        bool nonempty = false;
        string? name = null, first = null;
        while (reader.ReadLine() is { } raw)
        {
            line++;
            cancellation.ThrowIfCancellationRequested();
            string text = raw.Trim();
            if (text.Length == 0 || text.StartsWith('#'))
                continue;
            nonempty = true;
            if (text.StartsWith("1 ", StringComparison.Ordinal))
            {
                if (first is not null)
                    Report("The preceding TLE is missing its second line.");
                first = text;
                continue;
            }
            if (text.StartsWith("2 ", StringComparison.Ordinal))
            {
                if (first is null)
                    Report("A second line has no matching first line.");
                else
                {
                    try
                    {
                        var elements = TwoLineElements.Parse(first, text);
                        if (entries.Count == MaximumRecords)
                            throw new InvalidDataException("The local TLE catalogues exceed 16,384 records.");
                        entries.Add(new(name ?? $"NORAD {elements.CatalogNumber}", group, elements));
                    }
                    catch (FormatException error) { Report(error.Message); }
                }
                name = first = null;
                continue;
            }
            if (first is not null)
                Report("The preceding TLE is missing its second line.");
            first = null;
            name = text.StartsWith("0 ", StringComparison.Ordinal) ? text[2..].Trim() : text;
            if (name.Length > 120)
                throw new InvalidDataException("A TLE name exceeds 120 characters.");
        }
        if (first is not null)
            Report("The final TLE is missing its second line.");
        if (nonempty && entries.Count == initialCount)
            throw new InvalidDataException($"No valid TLE pairs in {Path.GetFileName(path)}.");

        void Report(string issue) => warning?.Invoke($"{Path.GetFileName(path)}:{line}: {issue}");
    }
}
