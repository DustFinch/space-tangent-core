// SPDX-License-Identifier: GPL-2.0-or-later
using SpaceTangent.Contracts;
using SpaceTangent.Core.Spice;
using SpaceTangent.Core.World;

namespace SpaceTangent.Core.Addons;

public static partial class SscBodyCatalogue
{
    private sealed record SscSpiceInput(SpiceResources Pool,
        IReadOnlyDictionary<(SscValue.Map Properties, string Field), Exception> Unavailable)
    {
        internal SpiceResources ForModel(SscValue.Map properties, string field)
        {
            if (Unavailable.TryGetValue((properties, field), out var error))
            {
                string? file = AddonDiagnostics.Get(error)?.File;
                throw new InvalidDataException(file is null ? error.Message : $"{file}: {error.Message}", error);
            }
            return Pool;
        }
    }

    private static SscSpiceInput ReadSpiceResources(IReadOnlyList<SscDefinition> definitions,
        Func<string, string> root, SscResource resolve, CancellationToken cancellation,
        string[] roots, IReadOnlyList<PackedKernel> furnished,
        IReadOnlyList<SpiceResources.KernelGroup>? missionLoads, IReadOnlyDictionary<string, int>? kernelOrder)
    {
        var order = roots.Select((path, index) => (path, index)).ToLookup(item => item.path, item => item.index);
        var unavailable = new Dictionary<(SscValue.Map Properties, string Field), Exception>();
        var supplied = furnished.Select(item => (Group: new SpiceResources.KernelGroup(item.Root, [(item.Directory, item.Name)]), item.Order));
        var pool = new SpiceResources(supplied.Concat(Sources()).OrderBy(item => order[item.Group.Root].First())
            .ThenBy(item => item.Order).Select(item => item.Group).Concat(missionLoads ?? []));
        return new(pool, unavailable);

        IEnumerable<(SpiceResources.KernelGroup Group, int Order)> Sources()
        {
            foreach (var item in definitions)
            foreach (var (properties, timeline) in ModelScopes(item.Properties))
            foreach (string field in new[] { "SpiceOrbit", "SpiceRotation" })
            {
                cancellation.ThrowIfCancellationRequested();
                if (!properties.Fields.TryGetValue(field, out var value) || value is not SscValue.Map parameters ||
                    !parameters.Fields.TryGetValue("Kernel", out var kernel))
                    continue;
                string ownerField = timeline ? "Timeline" : field;
                string directory = item.DirectoryFor(ownerField);
                string owner = root(directory);
                var key = (properties, field);
                bool optional = HasSpiceAlternative(properties, field, timeline);
                string[] authored = (kernel is SscValue.List list ? list.Items : [kernel]).Select(name => name.AsText()).ToArray();
                yield return (new SpiceResources.KernelGroup(owner, authored.Select(name => (directory, name)).ToArray(),
                    optional ? error => unavailable.Add(key, error) : null, SkipRepeatedExplicit: true)
                    { ResolveExplicit = name => resolve(item, ownerField, AddonStage.Kernel, name, "data") },
                    kernelOrder?.GetValueOrDefault(directory) ?? 0);
            }
        }
    }

    private static IEnumerable<(SscValue.Map Properties, bool Timeline)> ModelScopes(SscValue.Map properties)
    {
        yield return (properties, false);
        if (properties.Fields.TryGetValue("Timeline", out var timeline) && timeline is SscValue.List phases)
            foreach (var phase in phases.Items.OfType<SscValue.Map>())
                yield return (phase, true);
    }

    private static bool HasSpiceAlternative(SscValue.Map properties, string field, bool timeline = false)
    {
        string[] keys = field == "SpiceOrbit"
            ? ["EllipticalOrbit", "FixedPosition", "SampledTrajectory", "SampledOrbit"]
            : ["SampledOrientation", "PrecessingRotation", "UniformRotation", "FixedRotation"];
        if (keys.Any(properties.Fields.ContainsKey))
            return true;
        return !timeline && (field == "SpiceOrbit"
            ? properties.Fields.ContainsKey("ScriptedOrbit")
            : properties.Fields.ContainsKey("ScriptedRotation") || properties.Fields.ContainsKey("CustomRotation"));
    }

    private static T? TrySpiceModel<T>(SscValue.Map fields, SscSpiceInput spice, string name,
        Func<SscValue.Map, SpiceResources, T> read, Action<string> warn, bool timeline = false) where T : class
    {
        if (!fields.Fields.ContainsKey(name))
            return null;
        try
        {
            return read(fields.Object(name), spice.ForModel(fields, name));
        }
        catch (Exception error) when (HasSpiceAlternative(fields, name, timeline) &&
            error is InvalidDataException or NotSupportedException or FormatException or ArgumentException or FileNotFoundException)
        {
            warn($"{name} initialization failed. {error.Message}");
            return null;
        }
    }

    private static Func<JulianInstant, CartesianState?> ReadSpiceOrbit(SscValue.Map orbit, SpiceResources resources, bool preflight)
    {
        int target = NaifIds.Resolve(orbit.Text("Target"), resources, allowNumericPrefix: true);
        int origin = NaifIds.Resolve(orbit.Text("Origin"), resources, allowNumericPrefix: true);
        if (Number(orbit.Get("BoundingRadius")) <= 0)
            throw new InvalidDataException("SSC SpiceOrbit requires a positive bounding radius.");
        var coverage = SpiceCoverage(orbit);
        if (target == 0 && origin == 0)
            return _ => default(CartesianState);

        var states = resources.States;
        bool bounded = coverage is { Beginning: not null, Ending: not null };
        if (bounded)
            coverage = (Inset(coverage.Beginning!.Value, .001), Inset(coverage.Ending!.Value, -.001));
        else if (target != 0 && states.FirstTargetInterval(target) is { } interval)
            coverage = (Inset(interval.Start, .001), Inset(interval.End, -.001));
        else if (preflight && target != 0)
            throw new InvalidDataException("No SPK coverage for the requested target.");

        if (preflight)
        {
            var first = coverage.Beginning ?? states.FirstTargetInterval(origin)?.Start;
            if (bounded && target != 0 && !states.CoversTarget(target, coverage.Beginning!.Value, coverage.Ending!.Value))
                throw new InvalidDataException("The specified SSC interval is not entirely covered by SPK.");
            if (first is null || !states.TryState(target, origin, first.Value, out _))
                throw new InvalidDataException("No connected SPICE state at interval beginning.");
        }

        var j2000ToEcliptic = SpiceReferenceFrames.InertialBasis(SpiceReferenceFrames.Id("ECLIPJ2000"))!.Value.Transpose();
        return Sample;

        CartesianState? Sample(JulianInstant epoch)
        {
            if (coverage.Beginning is { } first && epoch.CompareTo(first) < 0)
                epoch = first;
            else if (coverage.Ending is { } last && epoch.CompareTo(last) > 0)
                epoch = last;
            if (!states.TryNearestState(target, origin, epoch, out var state))
                return null;
            return new(j2000ToEcliptic.Transform(state.PositionKilometers), j2000ToEcliptic.Transform(state.VelocityKilometersPerSecond));
        }

        // The authored SSC interval is in Julian days, with the native model's millisecond inset.
        static JulianInstant Inset(JulianInstant epoch, double seconds) =>
            JulianInstant.FromJulianDate(epoch.ToJulianDate() + seconds / JulianInstant.SecondsPerDay, TimeScale.Tdb);
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
