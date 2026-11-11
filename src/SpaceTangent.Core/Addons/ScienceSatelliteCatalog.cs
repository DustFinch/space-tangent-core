// SPDX-License-Identifier: GPL-2.0-or-later
using System.Text.Json;
using SpaceTangent.Contracts;
using SpaceTangent.Core.Orbits;

namespace SpaceTangent.Core.Addons;

public static class ScienceSatelliteCatalog
{
    private const int MaximumCatalogBytes = 1024 * 1024;

    /// <summary>Reads the version 1 science-satellite catalogue inside an explicitly provided asset root.</summary>
    public static IReadOnlyList<ScienceSatellite> Load(string catalogPath, string assetRoot)
    {
        string root = Path.GetFullPath(assetRoot);
        string fullCatalogPath = ResolveLocalFile(root, root, catalogPath);
        byte[] bytes;
        using (FileStream input = File.OpenRead(fullCatalogPath))
        {
            if (input.Length > MaximumCatalogBytes)
                throw new InvalidDataException("Satellite catalogue exceeds 1 MiB.");
            bytes = new byte[(int)input.Length];
            input.ReadExactly(bytes);
        }

        using JsonDocument document = JsonDocument.Parse(bytes.AsMemory(HasUtf8Bom(bytes) ? 3 : 0));
        JsonElement catalog = document.RootElement;
        if (ReadString(catalog, "version") != "1.0")
            throw new InvalidDataException("Expected a version 1.0 science-satellite catalogue.");

        var satellites = new List<ScienceSatellite>();
        var ids = new HashSet<ObjectId>();
        foreach (JsonElement item in catalog.GetProperty("items").EnumerateArray())
        {
            try
            {
                ScienceSatellite satellite = ReadSatellite(item, root, Path.GetDirectoryName(fullCatalogPath)!);
                if (!ids.Add(satellite.Id))
                    throw new FormatException("Duplicate NORAD catalogue number.");
                satellites.Add(satellite);
            }
            catch (Exception error) when (error is FormatException or JsonException or KeyNotFoundException
                       or InvalidOperationException or IOException or InvalidDataException or ArgumentException or NotSupportedException)
            {
                throw new InvalidDataException($"Invalid satellite catalogue item {satellites.Count}: {error.Message}", error);
            }
        }
        return satellites.AsReadOnly();
    }

    private static ScienceSatellite ReadSatellite(JsonElement item, string root, string directory)
    {
        string name = ReadString(item, "name");
        Require(item, "center", "Earth");
        Require(item, "trajectoryFrame", "EquatorJ2000");

        JsonElement trajectory = item.GetProperty("trajectory");
        Require(trajectory, "type", "TLE");
        TwoLineElements elements = TwoLineElements.Parse(ReadString(trajectory, "line1"), ReadString(trajectory, "line2"));

        JsonElement frame = item.GetProperty("bodyFrame");
        Require(frame, "type", "TwoVector");
        SignedAxis nadir = SignedAxis.Parse(ReadString(frame, "primaryAxis"));
        SignedAxis velocity = SignedAxis.Parse(ReadString(frame, "secondaryAxis"));
        if (nadir.Index == velocity.Index)
            throw new FormatException("Two-vector frame requires different axes.");
        ReadDirection(frame.GetProperty("primary"), "RelativePosition", name);
        ReadDirection(frame.GetProperty("secondary"), "RelativeVelocity", name);

        JsonElement mesh = item.GetProperty("geometry");
        Require(mesh, "type", "Mesh");
        string source = ReadString(mesh, "source");
        if (Path.IsPathRooted(source))
            throw new FormatException("Mesh source must be a relative path.");
        string meshPath = ResolveLocalFile(root, directory, source);
        if (!Path.GetExtension(meshPath).Equals(".obj", StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException("Expected a local OBJ mesh.");
        double size = mesh.GetProperty("size").GetDouble();
        if (!double.IsFinite(size) || size <= 0)
            throw new FormatException("Mesh size must be finite and positive.");

        Basis3d fixedRotation = item.TryGetProperty("rotationModel", out JsonElement rotation)
            ? ReadFixedRotation(rotation)
            : Basis3d.Identity;
        return new(name, elements, nadir, velocity, fixedRotation, meshPath, size);
    }

    private static Basis3d ReadFixedRotation(JsonElement model)
    {
        Require(model, "type", "Fixed");
        JsonElement quaternion = model.GetProperty("quaternion");
        if (quaternion.GetArrayLength() != 4)
            throw new FormatException("Fixed rotation requires WXYZ coefficients.");
        double w = quaternion[0].GetDouble(), x = quaternion[1].GetDouble();
        double y = quaternion[2].GetDouble(), z = quaternion[3].GetDouble();
        if (!(double.IsFinite(w) && double.IsFinite(x) && double.IsFinite(y) && double.IsFinite(z)))
            throw new FormatException("Fixed rotation coefficients must be finite.");

        // Source files use the raw WXYZ matrix expression. Keep it distinct from the rigid two-vector basis.
        var basis = new Basis3d(new(1 - 2 * (y * y + z * z), 2 * (x * y + w * z), 2 * (x * z - w * y)),
            new(2 * (x * y - w * z), 1 - 2 * (x * x + z * z), 2 * (y * z + w * x)),
            new(2 * (x * z + w * y), 2 * (y * z - w * x), 1 - 2 * (x * x + y * y)));
        if (!(basis.X.IsFinite && basis.Y.IsFinite && basis.Z.IsFinite))
            throw new FormatException("Fixed rotation coefficients exceed the supported range.");
        return basis;
    }

    private static void ReadDirection(JsonElement element, string type, string name)
    {
        Require(element, "type", type);
        Require(element, "observer", name);
        Require(element, "target", "Earth");
    }

    private static void Require(JsonElement element, string property, string expected)
    {
        if (ReadString(element, property) != expected)
            throw new NotSupportedException($"Expected {property} to be {expected}.");
    }

    private static string ReadString(JsonElement element, string property)
    {
        string? value = element.GetProperty(property).GetString();
        return string.IsNullOrWhiteSpace(value) ? throw new FormatException($"Missing {property}.") : value;
    }

    private static string ResolveLocalFile(string root, string directory, string path)
    {
        string candidate = Path.GetFullPath(path, directory);
        string relative = Path.GetRelativePath(root, candidate);
        if (Path.IsPathRooted(relative) || relative == ".." ||
            relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new InvalidDataException("Catalogue asset is outside the declared add-on root.");
        if (!File.Exists(candidate))
            throw new FileNotFoundException("Catalogue asset is missing.", candidate);
        for (string cursor = candidate; ; cursor = Path.GetDirectoryName(cursor)!)
        {
            if ((File.GetAttributes(cursor) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Catalogue assets must not use symbolic links.");
            if (Path.GetRelativePath(root, cursor) == ".")
                return candidate;
        }
    }

    private static bool HasUtf8Bom(ReadOnlySpan<byte> bytes) => bytes.StartsWith("\uFEFF"u8);
}
