// SPDX-License-Identifier: GPL-2.0-or-later
using System.Globalization;
using System.Text;
using SpaceTangent.Contracts;
using SpaceTangent.Core.Spice;

namespace SpaceTangent.Core.Addons;

public sealed class SscSpiceObject
{
    private readonly SpkKernel[] _kernels;

    internal SscSpiceObject(string name, string parent, int target, int center, string meshPath, double radius,
        string? attitudeFrame, string? attitudeBaseFrame, SpkKernel[] kernels)
    {
        Name = name;
        Parent = parent;
        Target = target;
        Center = center;
        MeshPath = meshPath;
        RadiusKilometers = radius;
        AttitudeFrame = attitudeFrame;
        AttitudeBaseFrame = attitudeBaseFrame;
        _kernels = kernels;
    }

    public string Name { get; }
    public string Parent { get; }
    public int Target { get; }
    public int Center { get; }
    public string MeshPath { get; }
    public double RadiusKilometers { get; }
    public string? AttitudeFrame { get; }
    public string? AttitudeBaseFrame { get; }
    public static FrameId Frame { get; } = new("icrf3");

    public bool TrySample(JulianInstant tdb, out CartesianState state)
    {
        for (int index = _kernels.Length - 1; index >= 0; index--)
        {
            if (_kernels[index].TryState(Target, Center, tdb, out state))
                return true;
        }
        state = default;
        return false;
    }
}

public static class SscSpiceCatalog
{
    private const int MaximumCatalogBytes = 1024 * 1024;

    public static IReadOnlyList<SscSpiceObject> Load(string catalogPath, string assetRoot)
    {
        string root = Path.GetFullPath(assetRoot);
        string catalog = LocalAddonFiles.Resolve(root, root, catalogPath);
        string directory = Path.GetDirectoryName(catalog)!;
        using FileStream stream = File.OpenRead(catalog);
        if (stream.Length > MaximumCatalogBytes)
            throw new InvalidDataException("SSC catalogue exceeds 1 MiB.");
        using var reader = new StreamReader(stream, new UTF8Encoding(false, true));
        var parser = new SscReader(reader.ReadToEnd());
        var kernels = new Dictionary<string, SpkKernel>(OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal);
        var result = new List<SscSpiceObject>();
        foreach (var (name, parent, fields) in parser.ReadDefinitions())
        {
            if (fields.Text("Class") != "spacecraft")
                throw new NotSupportedException("This SSC adapter expects spacecraft with a SpiceOrbit.");
            if (name.Length == 0 || parent.Length == 0)
                throw new InvalidDataException("SSC name and parent are required.");
            string mesh = ResolveRelative(root, directory, fields.Text("Mesh"));
            if (!double.TryParse(fields.Text("Radius"), NumberStyles.Float, CultureInfo.InvariantCulture, out double radius) ||
                !double.IsFinite(radius) || radius <= 0)
                throw new InvalidDataException("SSC radius must be finite and positive.");

            SscValue.Map orbit = fields.Object("SpiceOrbit");
            int target = NaifIds.Resolve(orbit.Text("Target"));
            int center = NaifIds.Resolve(orbit.Text("Origin"));
            SscValue entry = orbit.Get("Kernel");
            SscValue[] entries = entry is SscValue.List list ? list.Items : [entry];
            var spk = new List<SpkKernel>();
            foreach (SscValue source in entries)
            {
                string path = ResolveRelative(root, directory, source.AsText());
                if (!Path.GetExtension(path).Equals(".bsp", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (!kernels.TryGetValue(path, out var kernel))
                    kernels.Add(path, kernel = new SpkKernel(path));
                spk.Add(kernel);
            }
            if (spk.Count == 0)
                throw new InvalidDataException("SSC SpiceOrbit has no SPK file.");

            string? attitude = null, baseFrame = null;
            if (fields.Fields.TryGetValue("SpiceRotation", out var rotation))
            {
                if (rotation is not SscValue.Map definition)
                    throw new InvalidDataException("SSC SpiceRotation must be an object.");
                attitude = definition.Text("Frame");
                baseFrame = definition.Text("BaseFrame");
            }
            result.Add(new(name, parent, target, center, mesh, radius, attitude, baseFrame, [.. spk]));
        }
        return result.AsReadOnly();
    }

    private static string ResolveRelative(string root, string directory, string path)
    {
        if (Path.IsPathRooted(path))
            throw new InvalidDataException("SSC resources must use relative paths.");
        return LocalAddonFiles.Resolve(root, directory, path);
    }
}
