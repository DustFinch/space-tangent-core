// SPDX-License-Identifier: GPL-2.0-or-later
using SpaceTangent.Contracts;
using SpaceTangent.Core.Spice;
using SpaceTangent.Core.World;

namespace SpaceTangent.Core.Addons;

public static partial class SscBodyCatalogue
{
    private static SpiceResources ReadSpiceResources(IReadOnlyList<SscDefinition> definitions,
        Func<string, string> root, SscResource resolve, CancellationToken cancellation,
        string[] roots, IReadOnlyList<(string Root, string Directory, string Name)> furnished)
    {
        var order = roots.Select((path, index) => (path, index)).ToLookup(item => item.path, item => item.index);
        return new SpiceResources(furnished.Concat(Sources()).OrderBy(item => order[item.Root].First()));

        IEnumerable<(string Root, string Directory, string Name)> Sources()
        {
            foreach (var item in definitions)
            foreach (string field in new[] { "SpiceOrbit", "SpiceRotation" })
            {
                cancellation.ThrowIfCancellationRequested();
                if (!item.Properties.Fields.TryGetValue(field, out var value) || value is not SscValue.Map parameters ||
                    !parameters.Fields.TryGetValue("Kernel", out var kernel))
                    continue;
                string owner = root(item.DirectoryFor(field));
                foreach (var name in kernel is SscValue.List list ? list.Items : [kernel])
                {
                    string file = resolve(item, field, AddonStage.Kernel, name.AsText(), "data");
                    yield return (owner, Path.GetDirectoryName(file)!, Path.GetFileName(file));
                }
            }
        }
    }

    private static Func<JulianInstant, CartesianState?> ReadSpiceOrbit(SscValue.Map orbit, SpiceResources resources)
    {
        int target = NaifIds.Resolve(orbit.Text("Target")), origin = NaifIds.Resolve(orbit.Text("Origin"));
        if (Number(orbit.Get("BoundingRadius")) <= 0)
            throw new InvalidDataException("SSC SpiceOrbit requires a positive bounding radius.");
        var coverage = SpiceCoverage(orbit);
        var states = resources.States;
        var j2000ToEcliptic = SpiceReferenceFrames.InertialBasis(SpiceReferenceFrames.Id("ECLIPJ2000"))!.Value.Transpose();
        return epoch => InCoverage(epoch, coverage) && states.TryState(target, origin, epoch, out var state)
            ? new(j2000ToEcliptic.Transform(state.PositionKilometers), j2000ToEcliptic.Transform(state.VelocityKilometersPerSecond))
            : null;
    }

    private static Func<JulianInstant, FrameKinematics?> ReadSpiceRotation(SscValue.Map rotation,
        SpiceResources resources, Basis3d spinFrame)
    {
        var frames = resources.Frames;
        string frame = rotation.Text("Frame");
        string baseFrame = rotation.Fields.TryGetValue("BaseFrame", out var value) ? value.AsText() : "ECLIPJ2000";
        int frameId = frames.Id(frame), baseId = frames.Id(baseFrame);
        if (frameId == 0 || baseId == 0)
            throw new InvalidDataException($"Unknown SSC SPICE frame {frame} or {baseFrame}.");
        frames.RequireDefinition(frameId);
        frames.RequireDefinition(baseId);
        var coverage = SpiceCoverage(rotation);
        return Sample;

        FrameKinematics? Sample(JulianInstant epoch)
        {
            if (!InCoverage(epoch, coverage) || !frames.TrySample(frameId, epoch, false, out var pointing) ||
                !frames.TrySample(baseId, epoch, false, out var reference))
                return null;
            Basis3d toWorld = SpiceFixedFrames.Compose(spinFrame, reference.ToWorld.Transpose());
            Vector3d? velocity = pointing.AngularVelocityRadiansPerSecond is { } bodyRate &&
                reference.AngularVelocityRadiansPerSecond is { } referenceRate ? toWorld.Transform(bodyRate - referenceRate) : null;
            return new(SpiceFixedFrames.Compose(toWorld, pointing.ToWorld), velocity);
        }
    }

    private static (JulianInstant? Beginning, JulianInstant? Ending) SpiceCoverage(SscValue.Map block)
    {
        JulianInstant? beginning = Date("Beginning"), ending = Date("Ending");
        if (beginning is { } first && ending is { } last && first.CompareTo(last) >= 0)
            throw new InvalidDataException("SSC SPICE ending must follow its beginning.");
        return (beginning, ending);

        JulianInstant? Date(string name) => block.Fields.TryGetValue(name, out var value)
            ? SscSampledCatalog.ReadDate(value.AsText()) : null;
    }

    private static bool InCoverage(JulianInstant epoch, (JulianInstant? Beginning, JulianInstant? Ending) coverage) =>
        (coverage.Beginning is not { } first || epoch.CompareTo(first) >= 0) &&
        (coverage.Ending is not { } last || epoch.CompareTo(last) <= 0);
}
