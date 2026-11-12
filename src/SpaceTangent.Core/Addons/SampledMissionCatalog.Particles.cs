// SPDX-License-Identifier: GPL-2.0-or-later
using System.Globalization;
using System.Text.Json;
using SpaceTangent.Contracts;

namespace SpaceTangent.Core.Addons;

public static partial class SampledMissionCatalog
{
    private static MissionShape ReadParticles(JsonElement geometry, string root, string directory, Action<string>? warning)
    {
        var list = geometry.GetProperty("emitters");
        if (list.GetArrayLength() is < 1 or > 32)
            throw new InvalidDataException("A particle system requires one to 32 emitters.");
        MissionParticleEmitter[] emitters = list.EnumerateArray().Select(emitter => ReadParticleEmitter(emitter, root, directory, warning)).ToArray();
        return ParticleShape(emitters);
    }

    private static MissionShape ParticleShape(MissionParticleEmitter[] emitters)
    {
        double radius = .001;
        foreach (MissionParticleEmitter emitter in emitters)
        {
            double emitterRadius = emitter.BoundingRadiusKilometers;
            if (!(emitter.SpawnRate > 0) || !(emitter.LifetimeSeconds > 0) ||
                emitter.SpawnRate * emitter.LifetimeSeconds > MissionParticleEmitter.MaximumLiveParticles ||
                emitter.LifetimeSeconds is < 1e-20 or > 1e15 ||
                emitter.StartJulianDate > emitter.EndJulianDate || emitter.StartSize < 0 || emitter.EndSize < 0 ||
                emitter.TraceSeconds is < 0 or > 1e15 || emitter.VelocityVariation < 0 || emitter.DiscRadiusKilometers < 0 ||
                emitter.StartSize > 1e15 || emitter.EndSize > 1e15 || emitterRadius is < 0 or > 1e15 ||
                !double.IsFinite(emitterRadius))
                throw new InvalidDataException("Particle timing, size or generator exceeds the supported finite range.");
            radius = Math.Max(radius, emitterRadius);
        }
        return new(null, radius, Basis3d.Identity, default) { ParticleEmitters = Array.AsReadOnly(emitters) };
    }

    private static MissionParticleEmitter ReadParticleEmitter(JsonElement emitter, string root, string directory, Action<string>? warning)
    {
        var generator = emitter.GetProperty("generator");
        MissionParticleGenerator kind = ParticleGeneratorKind(Text(generator, "type"));
        MissionParticleBirth[] strip = kind == MissionParticleGenerator.Strip
            ? ParticleStrip(generator.GetProperty("states")) : [];
        MissionParticleArc[] arcs = kind == MissionParticleGenerator.ArcStrip
            ? ParticleArcs(generator.GetProperty("arcs")) : [];
        return new MissionParticleEmitter
        {
            Generator = kind,
            Center = ReadOptionalVector(generator, kind == MissionParticleGenerator.Point ? "position" : "center"),
            Sides = ReadOptionalVector(generator, "sides"),
            Velocity = ReadOptionalVector(generator, "velocity"),
            Force = ReadOptionalVector(emitter, "force"),
            DiscRadiusKilometers = ReadOptionalNumber(generator, "radius", 0),
            Strip = Array.AsReadOnly(strip),
            Arcs = Array.AsReadOnly(arcs),
            Colors = ReadParticleColors(emitter),
            SpawnRate = ReadOptionalNumber(emitter, "spawnRate", 0),
            LifetimeSeconds = ReadOptionalNumber(emitter, "lifetime", 0),
            StartSize = emitter.TryGetProperty("startSize", out _) ? Distance(emitter, "startSize") : 0,
            EndSize = emitter.TryGetProperty("endSize", out _) ? Distance(emitter, "endSize") : 1,
            TraceSeconds = ReadOptionalNumber(emitter, "trace", 0),
            VelocityVariation = ReadOptionalNumber(emitter, "velocityVariation", 0),
            StartJulianDate = Date(emitter, "startTime", J2000).ToJulianDate(),
            EndJulianDate = Date(emitter, "endTime", J2000).ToJulianDate(),
            TexturePath = ReadParticleTexture(emitter, root, directory, warning)
        };
    }

    private static MissionParticleGenerator ParticleGeneratorKind(string value)
    {
        if (!Enum.TryParse(value, ignoreCase: true, out MissionParticleGenerator result) || !Enum.IsDefined(result) ||
            !value.Equals(result.ToString(), StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException($"Unknown mission particle generator {value}.");
        return result;
    }

    private static MissionParticleBirth[] ParticleStrip(JsonElement array)
    {
        double[] values = ParticleNumbers(array, 6);
        var points = new MissionParticleBirth[values.Length / 6];
        for (int index = 0; index < points.Length; index++)
        {
            int p = index * 6;
            points[index] = new(new(values[p], values[p + 1], values[p + 2]),
                new(values[p + 3], values[p + 4], values[p + 5]));
        }
        return points;
    }

    private static MissionParticleArc[] ParticleArcs(JsonElement array)
    {
        double[] values = ParticleNumbers(array, 4);
        var points = new MissionParticleArc[values.Length / 4];
        for (int index = 0; index < points.Length; index++)
        {
            int p = index * 4;
            float latitude = (float)(values[p] * (Math.PI / 180)), longitude = (float)(values[p + 1] * (Math.PI / 180));
            float radius = (float)values[p + 2];
            if (radius <= 0 || Math.Abs(values[p]) > 90)
                throw new InvalidDataException("A particle arc requires a positive radius and valid latitude.");
            points[index] = new(new(MathF.Cos(latitude) * MathF.Cos(longitude) * radius,
                MathF.Cos(latitude) * MathF.Sin(longitude) * radius, MathF.Sin(latitude) * radius), (float)values[p + 3]);
        }
        return points;
    }

    private static double[] ParticleNumbers(JsonElement array, int stride)
    {
        int length = array.GetArrayLength();
        if (length < 2 * stride || length > 2048 * stride || length % stride != 0)
            throw new InvalidDataException("A particle strip requires two to 2048 complete generator points.");
        return array.EnumerateArray().Select(ParticleNumber).ToArray();
    }

    private static IReadOnlyList<MissionParticleColor> ReadParticleColors(JsonElement emitter)
    {
        if (!emitter.TryGetProperty("colors", out var colors) || colors.GetArrayLength() == 0)
            return [new(new(1, 1, 1), 1)];
        int length = colors.GetArrayLength();
        if (length % 2 != 0 || length > 10)
            throw new InvalidDataException("Particle colours require at most five colour and opacity pairs.");
        int count = length / 2;
        if (count == 5)
            count = 1; // The source keeps one active key when setColorCount receives five.
        var keys = new MissionParticleColor[count];
        for (int index = 0; index < keys.Length; index++)
            keys[index] = ParticleColor(colors[2 * index], ParticleNumber(colors[2 * index + 1]));
        return Array.AsReadOnly(keys);
    }

    private static MissionParticleColor ParticleColor(JsonElement value, double opacity)
    {
        Vector3d color;
        if (value.ValueKind == JsonValueKind.Array)
            color = Vector(value);
        else
        {
            string text = value.GetString() ?? "";
            if (text.Length == 7 && text[0] == '#' && int.TryParse(text.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int rgb))
                color = new((rgb >> 16) / 255d, ((rgb >> 8) & 255) / 255d, (rgb & 255) / 255d);
            else
            {
                var named = System.Drawing.Color.FromName(text);
                if (!named.IsKnownColor)
                    throw new InvalidDataException($"Unknown particle colour {text}.");
                color = new(named.R / 255d, named.G / 255d, named.B / 255d);
            }
        }
        if (opacity is < 0 or > 1 || color.X is < 0 or > 1 || color.Y is < 0 or > 1 || color.Z is < 0 or > 1)
            throw new InvalidDataException("Particle colour and opacity require values between zero and one.");
        return new(color, opacity);
    }

    private static string? ReadParticleTexture(JsonElement emitter, string root, string directory, Action<string>? warning)
    {
        if (!emitter.TryGetProperty("texture", out var value) || value.GetString() is not { Length: > 0 } relative)
            return null;
        if (Path.GetExtension(relative).ToLowerInvariant() is not (".png" or ".jpg" or ".jpeg"))
            throw new InvalidDataException("A local particle texture must be PNG or JPEG.");
        try { return LocalAddonFiles.ResolveRelative(root, directory, relative); }
        catch (FileNotFoundException)
        {
            warning?.Invoke($"Particle texture {relative} is missing; using the default spot.");
            return null;
        }
    }

    private static Vector3d ReadOptionalVector(JsonElement value, string key) =>
        value.TryGetProperty(key, out var vector) ? Vector(vector) : default;

    private static double ReadOptionalNumber(JsonElement value, string key, double fallback) =>
        value.TryGetProperty(key, out var number) ? ParticleNumber(number) : fallback;

    private static double ParticleNumber(JsonElement value)
    {
        double number = value.GetDouble();
        return Math.Abs(number) <= 1e15 ? number : throw new InvalidDataException("A particle number is outside the supported finite range.");
    }
}
