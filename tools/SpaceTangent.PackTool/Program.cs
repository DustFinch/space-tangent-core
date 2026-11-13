// SPDX-License-Identifier: GPL-2.0-or-later
using SpaceTangent.Core.Addons;

try
{
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
        if (!Path.GetFileName(manifest).Equals("pack.json", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Supply a pack.json manifest.");
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
    Console.Error.WriteLine("Usage: import <SSC source folder> --pack <new folder> | check <pack.json> [--strict]");
    return 2;
}
catch (Exception error) when (AddonDiagnostics.Annotate(error, AddonStage.Catalog))
{
    var issue = AddonDiagnostics.Get(error)!;
    Console.Error.WriteLine($"{issue.Stage}/{issue.Code}: {issue.File} {issue.Detail}".Trim());
    return 1;
}

static void Warn(string message) => Console.Error.WriteLine($"Warning: {message}");
