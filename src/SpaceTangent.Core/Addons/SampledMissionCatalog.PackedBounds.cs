// SPDX-License-Identifier: GPL-2.0-or-later
using System.Text.Json;
using SpaceTangent.Contracts;

namespace SpaceTangent.Core.Addons;

public static partial class SampledMissionCatalog
{
    private const string PackedMeshSize = "cosmographiaSize", PackedMeshScale = "cosmographiaScale";

    private static MissionGeometryBounds ReadPackedGeometryBounds(JsonElement terms, string root, string name,
        Action<string>? warning, CancellationToken cancellation)
    {
        if (terms.GetArrayLength() > 4096)
            throw new InvalidDataException("A mission geometry may contain at most 4096 bound terms.");
        var meshes = new List<MissionMeshBound>();
        double radius = 0;
        bool complete = true;
        foreach (var term in terms.EnumerateArray())
        {
            cancellation.ThrowIfCancellationRequested();
            string kind = Text(term, "kind");
            if (kind == "sphere")
            {
                double size = term.GetProperty("radiusKm").GetDouble();
                if (size < 0 || !float.IsFinite((float)size))
                    throw new InvalidDataException("Invalid packed sphere bound.");
                radius = Math.Max(radius, size);
                continue;
            }

            string file = Text(term, "file");
            try
            {
                switch (kind)
                {
                    case "mesh":
                        var offset = Vector(term.GetProperty("offset"));
                        if (!float.IsFinite((float)offset.X) || !float.IsFinite((float)offset.Y) || !float.IsFinite((float)offset.Z))
                            throw new InvalidDataException("Invalid packed mesh offset.");
                        double value = term.GetProperty("value").GetDouble();
                        string path = PackedAsset(root, "models", file);
                        meshes.Add(Text(term, "convention") switch
                        {
                            PackedMeshSize when value > 0 && double.IsFinite(value) => new(path, value, offset),
                            PackedMeshScale when float.IsFinite((float)value) => new(path, 0, offset) { Scale = value },
                            _ => throw new InvalidDataException("Invalid packed mesh bound convention or scale.")
                        });
                        break;
                    case "swarm":
                        string swarmPath = PackedAsset(root, "data", file);
                        var format = Text(term, "format") switch
                        {
                            "astorb" => MissionSwarmFormat.Astorb,
                            "binary" => MissionSwarmFormat.Binary,
                            "kepbin" => MissionSwarmFormat.KepBin,
                            _ => throw new InvalidDataException("Invalid packed swarm bound format.")
                        };
                        using (var stream = AddonDependencies.OpenRead(swarmPath, MissionSwarm.MaximumInputBytes))
                            radius = Math.Max(radius, MissionSwarm.Read(stream, format, cancellation).BoundingRadiusKilometers);
                        break;
                    default:
                        throw new InvalidDataException($"Unknown packed geometry bound kind {kind}.");
                }
            }
            catch (FileNotFoundException)
            {
                complete = false;
                warning?.Invoke($"{name}: geometry bound resource {file} is missing.");
            }
        }
        return new(radius, meshes.AsReadOnly(), complete);
    }
}
