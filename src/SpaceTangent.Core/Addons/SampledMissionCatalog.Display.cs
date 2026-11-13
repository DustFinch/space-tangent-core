// SPDX-License-Identifier: GPL-2.0-or-later
using System.Text.Json;
using SpaceTangent.Contracts;

namespace SpaceTangent.Core.Addons;

public sealed record MissionDisplay(Vector3d? LabelColor, Vector3d? OrbitColor, double? OrbitDurationSeconds,
    double OrbitFade, double OrbitLeadSeconds);

public static partial class SampledMissionCatalog
{
    internal static MissionDisplay? ReadDisplay(JsonElement item, bool packed = false)
    {
        var display = packed && item.TryGetProperty("display", out var record) ? record : default;
        var label = packed ? display : item.TryGetProperty("label", out var given) ? given : default;
        var plot = packed ? display : item.TryGetProperty("trajectoryPlot", out var trajectory) ? trajectory : default;
        var labelColor = Color(label, packed ? "labelColor" : "color");
        var orbitColor = Color(plot, packed ? "orbitColor" : "color");
        double duration = Duration(packed ? "orbitDays" : "duration");
        double lead = Duration(packed ? "leadDays" : "lead");
        double fade = plot.ValueKind == JsonValueKind.Object && plot.TryGetProperty(packed ? "orbitFade" : "fade", out var alpha)
            ? alpha.GetDouble() : 0;
        if (!double.IsFinite(fade) || packed && (fade is < 0 or > 1 || lead < 0 ||
            plot.ValueKind == JsonValueKind.Object && plot.TryGetProperty("orbitDays", out _) && duration <= 0))
            throw new InvalidDataException("Invalid mission orbit display settings.");
        return labelColor is null && orbitColor is null && duration <= 0 && fade <= 0 ? null :
            new(labelColor, orbitColor, duration > 0 ? duration : null, Math.Clamp(fade, 0, 1), duration > 0 ? Math.Max(0, lead) : 0);

        double Duration(string field) => plot.ValueKind == JsonValueKind.Object ? Seconds(plot, field, 0) : 0;

        static Vector3d? Color(JsonElement node, string field)
        {
            if (node.ValueKind != JsonValueKind.Object || !node.TryGetProperty(field, out var value))
                return null;
            if (value.ValueKind == JsonValueKind.Array)
                return Vector(value);
            string color = value.GetString() ?? "";
            if (color.Length == 7 && color[0] == '#' &&
                uint.TryParse(color.AsSpan(1), System.Globalization.NumberStyles.HexNumber, null, out uint rgb))
                return new((rgb >> 16) / 255d, ((rgb >> 8) & 255) / 255d, (rgb & 255) / 255d);
            return color.ToLowerInvariant() switch
            {
                "white" => new(1, 1, 1), "black" => new(0, 0, 0),
                "red" => new(1, 0, 0), "green" => new(0, 1, 0), "blue" => new(0, 0, 1),
                "yellow" => new(1, 1, 0), "cyan" => new(0, 1, 1), "magenta" => new(1, 0, 1),
                _ => null
            };
        }
    }
}

