// SPDX-License-Identifier: GPL-2.0-or-later
using System.Text.Json;
using SpaceTangent.Contracts;

namespace SpaceTangent.Core.Addons;

public sealed record SampledMissionContent(IReadOnlyList<SampledMission> Missions, IReadOnlyList<MissionAnnotation> Annotations);

public sealed record MissionAnnotation(string Name, double X, double Y, string Alignment, string Size,
    Vector3d Color, IReadOnlyList<MissionAnnotationLine> Lines);

public sealed record MissionAnnotationLine(JulianInstant Start, JulianInstant End, int Line, string Text, Vector3d? Color)
{
    public bool VisibleAt(JulianInstant epoch) => epoch.CompareTo(Start) >= 0 && epoch.CompareTo(End) <= 0;
}

public static partial class SampledMissionCatalog
{
    private static MissionAnnotation ReadAnnotation(JsonElement value, bool packed, CancellationToken cancellation)
    {
        string name = OptionalText(value, "name", "");
        var lines = new List<MissionAnnotationLine>();
        Vector3d color = value.TryGetProperty("color", out var rgb) ? ParticleColor(rgb, 1).Color : new(1, 1, 1);
        double x = .05, y = .9;
        if (value.TryGetProperty(packed ? "position" : "textPosition", out var position))
        {
            if (position.GetArrayLength() != 2)
                throw new InvalidDataException("An annotation position must have two components.");
            x = ParticleNumber(position[0]);
            y = ParticleNumber(position[1]);
        }
        string alignment = OptionalText(value, packed ? "alignment" : "textAlignment", "left").ToLowerInvariant();
        string size = OptionalText(value, packed ? "size" : "textSize", "medium").ToLowerInvariant();
        if (value.TryGetProperty(packed ? "lines" : "script", out var entries))
        {
            if (entries.GetArrayLength() > 1024)
                throw new InvalidDataException("An annotation track supports at most 1024 lines.");
            foreach (var entry in entries.EnumerateArray())
            {
                cancellation.ThrowIfCancellationRequested();
                string startKey = packed ? "startJdTdb" : "startTime";
                if (!entry.TryGetProperty(startKey, out _))
                    throw new InvalidDataException("An annotation line requires a start time.");
                var start = Date(entry, startKey, Minimum);
                var end = Date(entry, packed ? "endJdTdb" : "endTime", Maximum);
                string text = OptionalText(entry, "text", "");
                double row = entry.TryGetProperty(packed ? "line" : "lineIndex", out var index) ? ParticleNumber(index) : 0;
                if (end.CompareTo(start) < 0 || text.Length > 4096 || Math.Abs(row) > 4096)
                    throw new InvalidDataException("An annotation line has invalid time, text or row.");
                Vector3d? lineColor = entry.TryGetProperty("color", out var tint) ? ParticleColor(tint, 1).Color : null;
                lines.Add(new(start, end, (int)row, text, lineColor));
            }
        }
        return new(name, x, y, alignment, size, color, lines.AsReadOnly());

        static string OptionalText(JsonElement item, string field, string fallback) =>
            item.TryGetProperty(field, out var found) ? found.GetString() ?? fallback : fallback;
    }
}
