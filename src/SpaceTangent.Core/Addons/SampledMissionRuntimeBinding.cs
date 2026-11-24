// SPDX-License-Identifier: GPL-2.0-or-later
using System.Text.Json;
using SpaceTangent.Contracts;
using SpaceTangent.Core.Spice;

namespace SpaceTangent.Core.Addons;

public static partial class SampledMissionCatalog
{
    // Binds already-read definitions against the ordered installation pool.
    private sealed class MissionRuntimeBinding(MissionDocumentSet documents)
    {
        internal SampledMissionContent Bind(Func<IReadOnlyList<SpiceResources.KernelGroup>, SpiceResources>? bindPool)
        {
            var kernelLoads = documents.KernelLoads;
            var definitions = documents.Definitions;
            var timedChildren = documents.TimedChildren;
            var instrumentFrames = documents.InstrumentFrames;
            var annotations = documents.Annotations;
            var viewpointItems = documents.Viewpoints;
            var cancellation = documents.Cancellation;
            var warning = documents.Warning;
            var resources = bindPool is null ? new SpiceResources(kernelLoads) : bindPool(kernelLoads);
            var rejected = new HashSet<ObjectId>();
            var loaded = new List<(SampledMission Mission, HashSet<ObjectId> References)>();

            foreach (var definition in definitions)
            {
                cancellation.ThrowIfCancellationRequested();
                string directory = Path.GetDirectoryName(definition.Source)!;
                if (definition.Packed && IsTimedComponent(definition.Item) &&
                    timedChildren.ContainsKey((definition.Source, Text(definition.Item, "parent"))))
                    continue;
                var references = new HashSet<ObjectId>();
                try
                {
                    var mission = AddonDiagnostics.Read(AddonStage.Catalog, definition.Source, () =>
                        definition.Packed ? definition.Item.TryGetProperty("particles", out _)
                            ? ReadPackedParticleMission(definition.Item, TrackReference, directory, warning)
                            : ReadPackedBody(definition.Item, TrackReference, directory, resources, warning, cancellation,
                                timedChildren.GetValueOrDefault((definition.Source, Text(definition.Item, "path"))))
                        : ReadItem(definition.Item, Resolve, definition.Owner, directory, resources,
                            name => instrumentFrames.GetValueOrDefault(name), warning, cancellation));
                    var display = ReadDisplay(definition.Item, definition.Packed);
                    loaded.Add((mission with { Display = display }, references));
                }
                catch (Exception error) when (definition.Packed && !definition.Item.TryGetProperty("particles", out _) &&
                    (error is NotSupportedException ||
                    error is FileNotFoundException && AddonDiagnostics.Get(error)?.Stage != AddonStage.Trajectory ||
                    AddonDiagnostics.Get(error) is { Stage: AddonStage.Trajectory, Code: AddonIssue.InvalidInput }))
                {
                    string name = Text(definition.Item, "name");
                    rejected.Add(ResolvePacked(Text(definition.Item, "path")));
                    warning?.Invoke($"{name}: {error.Message}");
                }

                ObjectId TrackReference(string path)
                {
                    ObjectId id = ResolvePacked(path);
                    references.Add(id);
                    return id;
                }
            }

            bool removed;
            do
            {
                removed = false;
                foreach (var (mission, references) in loaded)
                {
                    if (references.Overlaps(rejected) && rejected.Add(mission.Id))
                    {
                        warning?.Invoke($"{mission.Name}: depends on an unavailable packed mission body.");
                        removed = true;
                    }
                }
            } while (removed);
            var missions = loaded.Where(item => !rejected.Contains(item.Mission.Id)).Select(item => item.Mission).ToArray();
            var sensors = missions.Where(mission => mission.Geometry?.Any(span => span.Shape.Sensor is not null) == true)
                .Select(mission => mission.Id).ToHashSet();
            foreach (var mission in missions)
                if (mission.Observation is { } observation && !sensors.Contains(observation.Sensor))
                    throw new InvalidDataException($"{mission.Name}: observation requires an installed sensor field.");
            var viewpoints = new List<MissionViewpoint>();
            foreach (var (item, packed) in viewpointItems)
            {
                cancellation.ThrowIfCancellationRequested();
                try
                {
                    viewpoints.Add(ReadViewpoint(item, packed, ResolveViewpoint));
                }
                catch (Exception error) when (error is InvalidDataException or KeyNotFoundException or FormatException or InvalidOperationException)
                {
                    warning?.Invoke($"Mission viewpoint: {error.Message}");
                }

                ObjectId ResolveViewpoint(string body)
                {
                    ObjectId id = packed ? ResolvePacked(body) : Resolve(body);
                    return rejected.Contains(id) ? throw new InvalidDataException($"Unavailable viewpoint body {body}.") : id;
                }
            }
            return new(Array.AsReadOnly(missions), annotations.AsReadOnly(), viewpoints.AsReadOnly());

            ObjectId Resolve(string name) => documents.Resolve(name);
            ObjectId ResolvePacked(string path) => documents.ResolvePacked(path);
        }
    }
}
