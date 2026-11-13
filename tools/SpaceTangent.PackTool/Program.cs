// SPDX-License-Identifier: GPL-2.0-or-later
using SpaceTangent.Core.Addons;
using SpaceTangent.Core.Observing;

try
{
    if (args is ["import-sky", var skySource, "--pack", var skyDestination])
    {
        var (stc, dsc) = SkyCataloguePack.Build(skySource, skyDestination);
        Console.WriteLine($"{stc} STC and {dsc} DSC catalogues -> {Path.GetFullPath(skyDestination)}");
        return 0;
    }
    if (args is ["import", var source, "--pack", var destination])
    {
        int bodies = SscBodyCatalogue.BuildPack(source, destination, Warn);
        Console.WriteLine($"{bodies} SSC bodies -> {Path.GetFullPath(destination)}");
        return 0;
    }
    if (args is ["check", var input, .. var options] && (options is [] or ["--strict"]))
    {
        string manifest = Path.GetFullPath(input);
        if (!File.Exists(manifest))
            throw new FileNotFoundException("Pack manifest not found.", manifest);
        string name = Path.GetFileName(manifest);
        string root = Path.GetDirectoryName(manifest)!;
        if (name.Equals(SkyCataloguePack.ManifestName, StringComparison.OrdinalIgnoreCase))
        {
            var stars = StellarCatalogue.Empty.WithStc(root);
            var deep = DeepSkyIndex.Load(root);
            foreach (string warning in stars.Warnings)
                Warn(warning);
            Console.WriteLine($"{stars.AppliedRecords} effective STC records, {deep.Objects.Count} DSC objects including defaults, {stars.DiagnosticCount} STC diagnostics without a base catalogue");
            return options.Length != 0 && stars.DiagnosticCount != 0 ? 1 : 0;
        }
        if (!name.Equals("pack.json", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Supply a pack.json or sky-pack.json manifest.");
        int warnings = 0;
        var bodies = SscBodyCatalogue.LoadWithMpc(Path.GetDirectoryName(manifest)!, message =>
        {
            warnings++;
            Warn(message);
        });
        foreach (var body in bodies)
            Console.WriteLine($"{body.Parent}/{body.Name}");
        Console.WriteLine($"{bodies.Count} SSC/MPC bodies, {warnings} warnings");
        return options.Length != 0 && warnings != 0 ? 1 : 0;
    }
    Console.Error.WriteLine("Usage: import <SSC source folder> --pack <new folder> | import-sky <STC/DSC source folder> --pack <new folder> | check <pack.json|sky-pack.json> [--strict]");
    return 2;
}
catch (Exception error) when (AddonDiagnostics.Annotate(error, AddonStage.Catalog))
{
    var issue = AddonDiagnostics.Get(error)!;
    Console.Error.WriteLine($"{issue.Stage}/{issue.Code}: {issue.File} {issue.Detail}".Trim());
    return 1;
}

static void Warn(string message) => Console.Error.WriteLine($"Warning: {message}");
