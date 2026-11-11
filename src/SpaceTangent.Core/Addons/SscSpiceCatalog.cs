// SPDX-License-Identifier: GPL-2.0-or-later
using System.Globalization;
using SpaceTangent.Core.Spice;

namespace SpaceTangent.Core.Addons;

public static class SscSpiceCatalog
{
    public static IReadOnlyList<SpiceSpacecraft> Load(string catalogPath, string assetRoot)
        => Load([catalogPath], assetRoot);

    public static IReadOnlyList<SpiceSpacecraft> Load(IEnumerable<string> catalogPaths, string assetRoot)
    {
        ArgumentNullException.ThrowIfNull(catalogPaths);
        string root = Path.GetFullPath(assetRoot);
        var result = new List<SpiceSpacecraft>();
        foreach (var definition in SscDefinitions.Load(catalogPaths, root))
        {
            var fields = definition.Properties;
            if (fields.Text("Class") != "spacecraft" || fields.Fields.ContainsKey("Timeline"))
                throw new NotSupportedException("This SSC adapter expects spacecraft with a SpiceOrbit.");
            string mesh = LocalAddonFiles.ResolveRelative(root, definition.DirectoryFor("Mesh"), fields.Text("Mesh"));
            if (!double.TryParse(fields.Text("Radius"), NumberStyles.Float, CultureInfo.InvariantCulture, out double radius) ||
                !double.IsFinite(radius) || radius <= 0)
                throw new InvalidDataException("SSC radius must be finite and positive.");

            SscValue.Map orbit = fields.Object("SpiceOrbit");
            int target = NaifIds.Resolve(orbit.Text("Target"));
            int center = NaifIds.Resolve(orbit.Text("Origin"));
            var kernels = KernelSources(definition.DirectoryFor("SpiceOrbit"), orbit.Get("Kernel"));

            string? attitude = null, baseFrame = null;
            if (fields.Fields.TryGetValue("SpiceRotation", out var rotation))
            {
                if (rotation is not SscValue.Map pointing)
                    throw new InvalidDataException("SSC SpiceRotation must be an object.");
                attitude = pointing.Text("Frame");
                baseFrame = pointing.Text("BaseFrame");
                if (pointing.Fields.TryGetValue("Kernel", out var pointingKernels))
                    kernels = kernels.Concat(KernelSources(definition.DirectoryFor("SpiceRotation"), pointingKernels));
            }
            result.Add(new(definition.Names, definition.Parent, target, center, mesh, radius, attitude, baseFrame,
                new SpiceResources(root, kernels)));
        }
        return result.AsReadOnly();
    }

    private static IEnumerable<(string Directory, string Name)> KernelSources(string directory, SscValue source) =>
        (source is SscValue.List list ? list.Items : [source]).Select(item => (directory, item.AsText()));
}
