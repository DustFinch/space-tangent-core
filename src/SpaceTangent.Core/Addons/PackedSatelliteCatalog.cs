// SPDX-License-Identifier: GPL-2.0-or-later
using System.Text.Json;
using SpaceTangent.Contracts;
using SpaceTangent.Core.Coordinates;
using SpaceTangent.Core.Orbits;

namespace SpaceTangent.Core.Addons;

public static class PackedSatelliteCatalog
{
    private const int MaximumBytes = 4 * 1024 * 1024;
    private const int LatestPackVersion = 19;
    private static readonly Basis3d NativeToAstronomical = new(new(1, 0, 0), new(0, 0, 1), new(0, -1, 0));

    /// <summary>Reads modeled Earth satellites from a legacy versioned pack with local resources.</summary>
    public static IReadOnlyList<ScienceSatellite> Load(string assetRoot, Action<string>? warning = null)
    {
        string root = Path.GetFullPath(assetRoot);
        string path = Path.Combine(root, "pack.json");
        return AddonDiagnostics.Read(AddonStage.Catalog, path, () => Read(root, path, warning));
    }

    private static IReadOnlyList<ScienceSatellite> Read(string root, string path, Action<string>? warning)
    {
        path = LocalAddonFiles.Resolve(root, root, path);
        using var input = AddonDependencies.OpenRead(path, MaximumBytes);
        if (input.Length > MaximumBytes)
            throw new InvalidDataException("Packed satellite catalogue exceeds 4 MiB.");
        using var reader = new StreamReader(input);
        using var json = JsonDocument.Parse(reader.ReadToEnd());
        JsonElement pack = json.RootElement;
        int version = pack.GetProperty("formatVersion").GetInt32();
        if (version is < 1 or > LatestPackVersion)
            throw new NotSupportedException($"Packed satellite format version {version} is outside 1–{LatestPackVersion}.");
        _ = Text(pack, "id");
        _ = Text(pack, "name");
        JsonElement bodies = pack.GetProperty("bodies");
        if (bodies.GetArrayLength() is < 1 or > 512)
            throw new InvalidDataException("Packed satellite catalogue must contain one to 512 bodies.");
        var ids = new HashSet<ObjectId>();
        var satellites = new List<ScienceSatellite>();
        foreach (JsonElement body in bodies.EnumerateArray())
        {
            string name = Text(body, "name");
            if (!body.TryGetProperty("timeline", out var timeline) || timeline.GetArrayLength() != 1 ||
                !timeline[0].TryGetProperty("orbit", out var orbit) || Text(orbit, "kind") != "tle" ||
                Text(timeline[0], "center") != "Sol/Earth" || Text(body, "class") != "spacecraft")
            {
                warning?.Invoke($"{name}: not a modeled Earth satellite in this pack.");
                continue;
            }
            var satellite = ReadSatellite(body, orbit, root);
            if (!ids.Add(satellite.Id))
                throw new InvalidDataException($"Duplicate packed satellite {satellite.Id.Value}.");
            satellites.Add(satellite);
        }
        if (satellites.Count == 0)
            throw new InvalidDataException("The pack contains no supported modeled Earth satellites.");
        return satellites.AsReadOnly();
    }

    private static ScienceSatellite ReadSatellite(JsonElement body, JsonElement orbit, string root)
    {
        string name = Text(body, "name");
        var elements = TwoLineElements.Parse(Text(orbit, "line1"), Text(orbit, "line2"));
        var bodyFrame = body.GetProperty("bodyFrame");
        if (Text(bodyFrame, "kind") != "cosmographiaTwoVector")
            throw new NotSupportedException($"{name}: unsupported packed body frame.");
        var nadir = ReadAxis(bodyFrame.GetProperty("primary"), "relativePosition");
        var velocity = ReadAxis(bodyFrame.GetProperty("secondary"), "relativeVelocity");
        if (nadir.Index == velocity.Index)
            throw new InvalidDataException($"{name}: two-vector frame axes must differ.");

        var model = body.GetProperty("appearance").GetProperty("model");
        string asset = Text(model, "file");
        string mesh = SscSampledCatalog.FindAsset(AddonStage.Mesh, root, root, asset, "models");
        if (Path.GetExtension(mesh).ToLowerInvariant() is not (".glb" or ".obj"))
            throw new NotSupportedException($"{name}: expected an OBJ or packed GLB model.");
        var offset = Vector(model.GetProperty("centre"), 3);
        if (offset.Any(component => component != 0))
            throw new NotSupportedException($"{name}: this packed model has an unsupported center offset.");
        var modelBasis = Quaternion(model.GetProperty("orientation")).Transpose();
        var rotation = body.GetProperty("rotation");
        if (Text(rotation, "kind") != "quaternion" || Text(rotation.GetProperty("frame"), "kind") != "eclipticJ2000")
            throw new NotSupportedException($"{name}: unsupported packed satellite rotation.");
        var fixedBasis = Quaternion(rotation.GetProperty("quaternion"));
        var meshToBody = new Basis3d(Compose(modelBasis.X), Compose(modelBasis.Y), Compose(modelBasis.Z));

        double radius = body.GetProperty("radiusKm").GetDouble();
        if (!(radius > 0) || !double.IsFinite(radius))
            throw new InvalidDataException($"{name}: the packed satellite radius is invalid.");
        return new(name, elements, nadir, velocity, meshToBody, fixedBasis, mesh, radius);

        Vector3d Compose(Vector3d value) => fixedBasis.Transform(NativeToAstronomical.Transform(value));
    }

    private static SignedAxis ReadAxis(JsonElement item, string kind)
    {
        var direction = item.GetProperty("direction");
        if (Text(direction, "kind") != kind || Text(direction, "target") != "Sol/Earth")
            throw new NotSupportedException("A packed satellite axis must refer to the Earth's position or relative velocity.");
        return SignedAxis.Parse(Text(item, "axis"));
    }

    private static Basis3d Quaternion(JsonElement item)
    {
        double[] q = Vector(item, 4);
        double length = Math.Sqrt(q.Sum(component => component * component));
        if (!(length > 0) || !double.IsFinite(length))
            throw new InvalidDataException("The packed rotation quaternion is invalid.");
        return QuaternionBasis.FromRaw(q[0] / length, q[1] / length, q[2] / length, q[3] / length);
    }

    private static double[] Vector(JsonElement value, int count)
    {
        if (value.GetArrayLength() != count)
            throw new InvalidDataException($"A packed vector requires {count} components.");
        double[] result = value.EnumerateArray().Select(item => item.GetDouble()).ToArray();
        if (result.Any(component => !double.IsFinite(component)))
            throw new InvalidDataException("A packed vector contains a nonfinite value.");
        return result;
    }

    private static string Text(JsonElement item, string property) =>
        item.GetProperty(property).GetString() is { Length: > 0 } value ? value :
            throw new InvalidDataException($"Missing packed satellite field {property}.");
}
