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
            result.Add(AddonDiagnostics.Read(AddonStage.Catalog, definition.SourceFile, () => ReadSpacecraft(root, definition, new SpiceResources(root, ReadKernels(definition, requireOrbitKernel: true)))));
        return result.AsReadOnly();
    }

    internal static Func<SpiceResources, SpiceCatalogResult> Read(IEnumerable<string> catalogPaths, string assetRoot,
        out IReadOnlyList<SpiceResources.KernelGroup> loads)
    {
        string root = Path.GetFullPath(assetRoot);
        var definitions = SscDefinitions.Load(catalogPaths, root);
        loads = definitions.SelectMany(definition => ReadKernels(definition, requireOrbitKernel: false))
            .Select(file => new SpiceResources.KernelGroup(root, [file])).ToArray();
        return resources => new(Array.AsReadOnly(definitions.Select(definition =>
            AddonDiagnostics.Read(AddonStage.Catalog, definition.SourceFile, () => ReadSpacecraft(root, definition, resources))).ToArray()), []);
    }

    private static SpiceSpacecraft ReadSpacecraft(string root, SscDefinition definition, SpiceResources resources)
    {
        var fields = definition.Properties;
        if (fields.Text("Class") != "spacecraft" || fields.Fields.ContainsKey("Timeline"))
            throw new NotSupportedException("This SSC adapter expects spacecraft with a SpiceOrbit.");
        string meshDirectory = definition.DirectoryFor("Mesh");
        string meshName = fields.Text("Mesh");
        string mesh = AddonDiagnostics.Read(AddonStage.Mesh, Path.Combine(meshDirectory, meshName),
            () => LocalAddonFiles.ResolveRelative(root, meshDirectory, meshName));
        if (!double.TryParse(fields.Text("Radius"), NumberStyles.Float, CultureInfo.InvariantCulture, out double radius) ||
            !double.IsFinite(radius) || radius <= 0)
            throw new InvalidDataException("SSC radius must be finite and positive.");

        SscValue.Map orbit = fields.Object("SpiceOrbit");
        int target = NaifIds.Resolve(orbit.Text("Target"));
        int center = NaifIds.Resolve(orbit.Text("Origin"));


        string? attitude = null, baseFrame = null;
        if (fields.Fields.TryGetValue("SpiceRotation", out var rotation))
        {
            if (rotation is not SscValue.Map pointing)
                throw new InvalidDataException("SSC SpiceRotation must be an object.");
            attitude = pointing.Text("Frame");
            baseFrame = pointing.Text("BaseFrame");

        }
        return new(definition.Names, definition.Parent, target, center, mesh, radius, attitude, baseFrame,
            resources);
    }

    private static IEnumerable<(string Directory, string Name)> ReadKernels(SscDefinition definition, bool requireOrbitKernel)
    {
        var fields = definition.Properties;
        if (fields.Text("Class") != "spacecraft" || fields.Fields.ContainsKey("Timeline"))
            throw new NotSupportedException("This SSC adapter expects spacecraft with a SpiceOrbit.");
        var orbit = fields.Object("SpiceOrbit");
        if (orbit.Fields.TryGetValue("Kernel", out var orbitKernels) || requireOrbitKernel)
            foreach (var file in KernelSources(definition.DirectoryFor("SpiceOrbit"), orbitKernels ?? orbit.Get("Kernel")))
                yield return file;
        if (fields.Fields.TryGetValue("SpiceRotation", out var rotation) && rotation is SscValue.Map pointing &&
            pointing.Fields.TryGetValue("Kernel", out var pointingKernels))
            foreach (var file in KernelSources(definition.DirectoryFor("SpiceRotation"), pointingKernels))
                yield return file;
    }

    private static IEnumerable<(string Directory, string Name)> KernelSources(string directory, SscValue source) =>
        (source is SscValue.List list ? list.Items : [source]).Select(item => (directory, item.AsText()));
}
