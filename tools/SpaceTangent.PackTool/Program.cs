// SPDX-License-Identifier: GPL-2.0-or-later
using System.Text.Json;
using SpaceTangent.Contracts;
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
    if (args is ["import-missions", var missionSource, "--pack", var missionDestination, .. var missionBodyOptions])
    {
        var (missions, annotations, viewpoints) = MissionCataloguePack.Build(missionSource, missionDestination,
            MissionReferences(missionBodyOptions), Warn);
        Console.WriteLine($"{missions} missions, {annotations} annotations, {viewpoints} viewpoints -> {Path.GetFullPath(missionDestination)}");
        return 0;
    }
    if (args is ["import", var source, "--pack", var destination])
    {
        int bodies = SscBodyCatalogue.BuildPack(source, destination, Warn);
        Console.WriteLine($"{bodies} SSC bodies -> {Path.GetFullPath(destination)}");
        return 0;
    }
    if (args is ["check", var input, .. var options])
    {
        bool strict = options is ["--strict", ..];
        string[] bodyOptions = strict ? options[1..] : options;
        string manifest = Path.GetFullPath(input);
        if (!File.Exists(manifest))
            throw new FileNotFoundException("Pack manifest not found.", manifest);
        string name = Path.GetFileName(manifest);
        string root = Path.GetDirectoryName(manifest)!;
        if (name.Equals(SkyCataloguePack.ManifestName, StringComparison.OrdinalIgnoreCase))
        {
            if (bodyOptions.Length != 0)
                throw new InvalidDataException("Sky catalogue packs do not take body references.");
            var stars = StellarCatalogue.Empty.WithStc(root);
            var deep = DeepSkyIndex.Load(root);
            foreach (string warning in stars.Warnings)
                Warn(warning);
            Console.WriteLine($"{stars.AppliedRecords} effective STC records, {deep.Objects.Count} DSC objects including defaults, {stars.DiagnosticCount} STC diagnostics without a base catalogue");
            return strict && stars.DiagnosticCount != 0 ? 1 : 0;
        }
        if (!name.Equals("pack.json", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Supply a pack.json or sky-pack.json manifest.");
        int warnings = 0;
        const int maximumManifestBytes = 16 * 1024 * 1024;
        using var inputFile = File.OpenRead(manifest);
        if (inputFile.Length > maximumManifestBytes)
            throw new InvalidDataException("The pack manifest exceeds 16 MiB.");
        using var document = JsonDocument.Parse(inputFile,
            new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
        JsonElement pack = document.RootElement;
        if (pack.ValueKind == JsonValueKind.Object && pack.TryGetProperty("corrections", out _))
        {
            if (bodyOptions is not ["--stellar-archive", var stellarRoot])
                throw new InvalidDataException("Supply the base stellar catalogue directory as --stellar-archive <folder>.");
            var result = StellarCatalogue.LoadArchive(stellarRoot).WithDistanceCorrectionPack(manifest);
            foreach (string warning in result.Warnings)
                Warn(warning);
            Console.WriteLine($"{result.AppliedRecords} stellar distance corrections, {result.DiagnosticCount} warnings");
            return strict && result.DiagnosticCount != 0 ? 1 : 0;
        }
        bool archivedMission = pack.ValueKind == JsonValueKind.Object && pack.TryGetProperty("formatVersion", out _) &&
            !pack.TryGetProperty("sscCatalogs", out _) &&
            (pack.TryGetProperty("bodies", out _) || pack.TryGetProperty("annotations", out _) || pack.TryGetProperty("viewpoints", out _));
        if (archivedMission || MissionCataloguePack.IsManifest(manifest))
        {
            var content = SampledMissionCatalog.LoadContent([manifest], root, MissionReferences(bodyOptions), message =>
            {
                warnings++;
                Warn(message);
            });
            foreach (var mission in content.Missions)
                Console.WriteLine(mission.Name);
            Console.WriteLine($"{content.Missions.Count} missions, {content.Annotations.Count} annotations, {content.Viewpoints.Count} viewpoints, {warnings} warnings");
            if (archivedMission && content.Missions.Count + content.Annotations.Count + content.Viewpoints.Count == 0)
                throw new InvalidDataException("The mission reader did not find any supported content in this archived pack.");
            return strict && warnings != 0 ? 1 : 0;
        }
        if (bodyOptions.Length != 0)
            throw new InvalidDataException("SSC packs do not take body references.");
        var bodies = SscBodyCatalogue.LoadWithMpc(Path.GetDirectoryName(manifest)!, message =>
        {
            warnings++;
            Warn(message);
        });
        foreach (var body in bodies)
            Console.WriteLine($"{body.Parent}/{body.Name}");
        Console.WriteLine($"{bodies.Count} SSC/MPC bodies, {warnings} warnings");
        return strict && warnings != 0 ? 1 : 0;
    }
    Console.Error.WriteLine("Usage: import <SSC source folder> --pack <new folder> | import-sky <STC/DSC source folder> --pack <new folder> | import-missions <JSON source folder> --pack <new folder> [--body <name>]... | check <pack.json|sky-pack.json> [--strict] [--body <name>]... [--stellar-archive <folder>]");
    return 2;
}
catch (Exception error) when (AddonDiagnostics.Annotate(error, AddonStage.Catalog))
{
    var issue = AddonDiagnostics.Get(error)!;
    Console.Error.WriteLine($"{issue.Stage}/{issue.Code}: {issue.File} {issue.Detail}".Trim());
    return 1;
}

static Func<string, ObjectId?> MissionReferences(string[] options)
{
    string[] solar = ["Sun", "Mercury", "Venus", "Earth", "Moon", "Mars", "Jupiter", "Saturn", "Uranus", "Neptune", "Pluto"];
    var ids = solar.ToDictionary(name => name, name => new ObjectId("sol/" + name.ToLowerInvariant()),
        StringComparer.OrdinalIgnoreCase);
    ids.Add("Sol", ids["Sun"]);
    for (int index = 0; index < options.Length; index += 2)
    {
        if (options[index] != "--body" || index + 1 == options.Length || string.IsNullOrWhiteSpace(options[index + 1]))
            throw new InvalidDataException("Supply installed mission references as --body <name>.");
        ids.TryAdd(options[index + 1], new ObjectId($"installed/body-{index / 2}"));
    }
    return name => ids.TryGetValue(name, out var id) ? id : null;
}

static void Warn(string message) => Console.Error.WriteLine($"Warning: {message}");
