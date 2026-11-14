// SPDX-License-Identifier: GPL-2.0-or-later
using System.Text.Json;
using SpaceTangent.Contracts;

namespace SpaceTangent.Core.Addons;

public sealed record MissionSwarmStyle(string SourcePath, MissionSwarm Elements, Vector3d Color,
    float Opacity, float PointSize, float FadeSize, bool Visible)
{
    public bool ReverseEquatorialAxes { get; init; }
}

public static partial class SampledMissionCatalog
{
    private static MissionShape ReadSwarm(JsonElement item, string root, string directory,
        CancellationToken cancellation, bool packed = false)
    {
        var format = Text(item, "format") switch
        {
            "astorb" => MissionSwarmFormat.Astorb,
            "binary" => MissionSwarmFormat.Binary,
            "kepbin" => MissionSwarmFormat.KepBin,
            var name => throw new NotSupportedException($"Unsupported mission swarm format {name}.")
        };
        if (packed && !UsesStandardAxes(item) && (!item.TryGetProperty("convention", out var convention) || convention.GetString() != "cosmographia"))
            throw new NotSupportedException("The packed swarm has a different orbital convention.");
        string path = LocalAddonFiles.ResolveRelative(root, directory, Text(item, "source"));
        var swarm = AddonDiagnostics.Read(AddonStage.Trajectory, path, () =>
        {
            using var file = AddonDependencies.OpenRead(path, MissionSwarm.MaximumInputBytes);
            return MissionSwarm.Read(file, format, cancellation);
        });
        double opacity = Number("opacity", 1);
        double size = Number(packed ? "pointSize" : "particleSize", 3);
        double fade = Number("fadeSize", 50);
        if (opacity is < 0 or > 1 || !(size > 0) || !float.IsFinite((float)size) ||
            fade < 0 || !float.IsFinite((float)fade))
            throw new InvalidDataException("A mission swarm has invalid opacity, point size or fade size.");
        var color = item.TryGetProperty("color", out var rgb) ? ParticleColor(rgb, opacity).Color : new(1, 1, 1);
        bool visible = !item.TryGetProperty("catalogVisible", out var show) || show.GetBoolean();
        return new(null, swarm.BoundingRadiusKilometers, Basis3d.Identity, default)
        {
            Swarm = new(path, swarm, color, (float)opacity, (float)size, (float)fade, visible)
            {
                // Packed natural attitudes include the half-turn of the body's prime meridian.
                ReverseEquatorialAxes = packed && item.TryGetProperty("naturalAxes", out var natural) && natural.GetBoolean()
            }
        };

        static bool UsesStandardAxes(JsonElement value) =>
            !value.TryGetProperty("convention", out var profile) || profile.GetString() == "standard";

        double Number(string field, double fallback) =>
            item.TryGetProperty(field, out var number) ? ParticleNumber(number) : fallback;
    }
}
