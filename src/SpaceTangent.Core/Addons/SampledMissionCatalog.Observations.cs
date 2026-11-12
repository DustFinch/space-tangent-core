// SPDX-License-Identifier: GPL-2.0-or-later
using System.Text.Json;
using SpaceTangent.Contracts;
using SpaceTangent.Core.World;

namespace SpaceTangent.Core.Addons;

public static partial class SampledMissionCatalog
{
    private static MissionObservation ReadObservation(JsonElement value, ObjectId target, Func<string, ObjectId> resolve)
    {
        var sensor = resolve(Text(value, "sensor"));
        var list = value.GetProperty("groups");
        if (list.GetArrayLength() is < 1 or > 16)
            throw new InvalidDataException("An observation requires one to sixteen groups.");
        var groups = new List<MissionObservationGroup>();
        foreach (var group in list.EnumerateArray())
        {
            _ = group.GetProperty("startTime");
            var start = Date(group, "startTime", Minimum);
            var end = Date(group, "endTime", start);
            double rate = group.TryGetProperty("obsRate", out var input) ? input.GetDouble() : 0;
            if (end.CompareTo(start) < 0 || !double.IsFinite(rate) || rate < 0)
                throw new InvalidDataException("An observation group needs ordered times and a nonnegative rate.");
            groups.Add(new(start, end, rate));
        }
        var color = value.TryGetProperty("footprintColor", out var rgb) ? Vector(rgb) : new(1, .5, 0);
        double opacity = value.TryGetProperty("footprintOpacity", out var alpha) ? alpha.GetDouble() : .4;
        int along = value.TryGetProperty("alongTrackDivisions", out var divisions) ? divisions.GetInt32() : 200;
        bool resolution = value.TryGetProperty("showResWithColor", out var byResolution) && byResolution.GetBoolean();
        if (!double.IsFinite(opacity) || color.X is < 0 or > 1 || color.Y is < 0 or > 1 || color.Z is < 0 or > 1)
            throw new InvalidDataException("An observation requires a finite opacity and a valid colour.");
        return new(target, sensor, groups.AsReadOnly(), Math.Clamp(along, 2, 500), color, Math.Clamp(opacity, 0, 1), resolution);
    }
}
