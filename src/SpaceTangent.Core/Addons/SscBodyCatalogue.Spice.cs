// SPDX-License-Identifier: GPL-2.0-or-later
using SpaceTangent.Contracts;
using SpaceTangent.Core.Spice;
using SpaceTangent.Core.World;

namespace SpaceTangent.Core.Addons;

public static partial class SscBodyCatalogue
{
    private sealed record SscSpiceInput(SpiceResources Pool,
        IReadOnlyDictionary<(SscValue.Map Properties, string Field), Exception> Unavailable,
        IReadOnlyDictionary<(SscValue.Map Properties, string Field), int> Prefixes)
    {
        internal SpiceResources ForModel(SscValue.Map properties, string field, out SpiceResources initial)
        {
            if (Unavailable.TryGetValue((properties, field), out var error))
            {
                string? file = AddonDiagnostics.Get(error)?.File;
                throw new InvalidDataException(file is null ? error.Message : $"{file}: {error.Message}", error);
            }
            initial = Prefixes.TryGetValue((properties, field), out int count) ? Pool.Prefix(count) : Pool;
            return Pool;
        }
    }

    private static SscSpiceInput ReadSpiceResources(IReadOnlyList<SscDefinition> definitions,
        IReadOnlyList<SscDefinition> declarations,
        Func<string, string> root, SscResource resolve, CancellationToken cancellation,
        string[] roots, IReadOnlyList<PackedKernel> furnished,
        IReadOnlyList<SpiceResources.KernelGroup>? missionLoads, IReadOnlyDictionary<string, int>? kernelOrder, SscCustomModels? custom)
    {
        var order = roots.Select((path, index) => (path, index)).ToLookup(item => item.path, item => item.index);
        var unavailable = new Dictionary<(SscValue.Map Properties, string Field), Exception>();
        var prefixes = new Dictionary<(SscValue.Map Properties, string Field), int>();
        var effective = new Dictionary<SscValue.Map, (SscValue.Map Fields, bool Optional)>(ReferenceEqualityComparer.Instance);
        foreach (var item in definitions)
        foreach (var (fields, timeline) in ModelScopes(item.Properties))
        foreach (string field in new[] { "SpiceOrbit", "SpiceRotation" })
            if (fields.Fields.GetValueOrDefault(field) is SscValue.Map block)
                effective.Add(block, (fields, HasSpiceAlternative(fields, field, timeline)));

        var supplied = furnished.Select(item => (Group: new SpiceResources.KernelGroup(item.Root, [(item.Directory, item.Name)]), item.Order, Sequence: 0));
        var pool = new SpiceResources(supplied.Concat(Sources()).OrderBy(item => order[item.Group.Root].First())
            .ThenBy(item => item.Order).ThenBy(item => item.Sequence).Select(item => item.Group).Concat(missionLoads ?? []));
        return new(pool, unavailable, prefixes);

        IEnumerable<(SpiceResources.KernelGroup Group, int Order, int Sequence)> Sources()
        {
            foreach (var item in declarations)
            foreach (var (properties, timeline) in ModelScopes(item.Properties))
            foreach (string field in new[] { "SpiceOrbit", "SpiceRotation" })
            {
                cancellation.ThrowIfCancellationRequested();
                if (!properties.Fields.TryGetValue(field, out var value) || value is not SscValue.Map parameters)
                    continue;
                if (custom is not null && PreferCustom(properties, field, custom))
                    continue;
                string ownerField = timeline ? "Timeline" : field;
                string directory = item.DirectoryFor(ownerField);
                string owner = root(directory);
                bool retained = effective.TryGetValue(parameters, out var model);
                bool hasKernel = parameters.Fields.TryGetValue("Kernel", out var kernel);
                if (!retained && !hasKernel)
                    continue;
                try
                {
                    _ = SpiceCoverage(parameters);
                }
                catch (InvalidDataException error)
                {
                    if (retained)
                        unavailable.Add((model.Fields, field), error);
                    continue;
                }
                string[] authored = hasKernel
                    ? (kernel is SscValue.List list ? list.Items : [kernel!]).Select(name => name.AsText()).ToArray() : [];
                var key = (model.Fields, field);
                Action<Exception>? unavailableModel = retained && !model.Optional ? null : OnUnavailable;
                yield return (new SpiceResources.KernelGroup(owner, authored.Select(name => (directory, name)).ToArray(),
                    unavailableModel, SkipRepeatedExplicit: true)
                    {
                        ResolveExplicit = name => resolve(item, ownerField, AddonStage.Kernel, name, "data"),
                        RecordPrefix = retained ? count => prefixes.Add(key, count) : null
                    }, kernelOrder?.GetValueOrDefault(directory) ?? 0, item.FieldOrder[ownerField]);

                void OnUnavailable(Exception error)
                {
                    if (retained)
                        unavailable.Add(key, error);
                }
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

    private static bool PreferCustom(SscValue.Map properties, string spiceField, SscCustomModels custom)
    {
        if (!properties.Fields.TryGetValue(spiceField == "SpiceOrbit" ? "CustomOrbit" : "CustomRotation", out var model))
            return false;
        return spiceField == "SpiceOrbit" ? custom.Orbit(model.AsText()) is not null : custom.Rotation(model.AsText()) is not null;
    }

    private static bool HasSpiceAlternative(SscValue.Map properties, string field, bool timeline = false)
    {
        string[] keys = field == "SpiceOrbit"
            ? ["EllipticalOrbit", "FixedPosition", "SampledTrajectory", "SampledOrbit"]
            : ["SampledOrientation", "PrecessingRotation", "UniformRotation", "FixedRotation"];
        if (keys.Any(properties.Fields.ContainsKey) || field == "SpiceRotation" && HasLegacyRotationPeriod(properties))
            return true;
        return field == "SpiceOrbit" ? properties.Fields.ContainsKey("ScriptedOrbit")
            : properties.Fields.ContainsKey("ScriptedRotation") || !timeline && properties.Fields.ContainsKey("CustomRotation");
    }

    private static T? TrySpiceModel<T>(SscValue.Map fields, SscSpiceInput spice, string name,
        Func<SscValue.Map, SpiceResources, SpiceResources, T> read, Action<string> warn, bool timeline = false) where T : class
    {
        if (!fields.Fields.ContainsKey(name))
            return null;
        try
        {
            var pool = spice.ForModel(fields, name, out var initial);
            return read(fields.Object(name), pool, initial);
        }
        catch (Exception error) when (HasSpiceAlternative(fields, name, timeline) &&
            error is InvalidDataException or NotSupportedException or FormatException or ArgumentException or FileNotFoundException)
        {
            warn($"{name} initialization failed. {error.Message}");
            return null;
        }
    }

    private static Func<JulianInstant, CartesianState?> ReadSpiceOrbit(SscValue.Map orbit, SpiceResources resources, SpiceResources initial, bool preflight, Action<string> warn, double periodUnit, out double? periodSeconds)
    {
        int target = ResolveOrbitId(orbit.Text("Target"));
        int origin = ResolveOrbitId(orbit.Text("Origin"));
        if (Number(orbit.Get("BoundingRadius")) <= 0)
            throw new InvalidDataException("SSC SpiceOrbit requires a positive bounding radius.");
        var coverage = SpiceCoverage(orbit);
        double? authoredPeriod = ReadTimeQuantity(orbit, "Period", periodUnit);
        if (target == 0 && origin == 0)
        {
            periodSeconds = authoredPeriod;
            return _ => default(CartesianState);
        }

        var initialStates = initial.StatesAtInitialization;
        var intervals = initialStates.SscTargetIntervals(target);
        // A nonzero self-orbit still needs a furnished ephemeris, even though its state is zero.
        if (!preflight && target == origin && intervals.Count == 0)
            intervals = resources.StatesAtInitialization.SscTargetIntervals(target);
        if (intervals.Count == 0 && target != 0 && (preflight || target == origin))
            throw new InvalidDataException("No SPK coverage for the requested target.");

        bool bounded = coverage is { Beginning: not null, Ending: not null };
        if (bounded)
            coverage = (Inset(coverage.Beginning!.Value, .001), Inset(coverage.Ending!.Value, -.001));
        else if (intervals.Count > 0)
            coverage = (Inset(intervals[0].Start, .001), Inset(intervals[0].End, -.001));

        if ((preflight || target == origin) && bounded && target != 0 && !intervals.Any(span =>
            coverage.Beginning!.Value.CompareTo(span.Start) >= 0 && coverage.Ending!.Value.CompareTo(span.End) <= 0))
            throw new InvalidDataException("The specified SSC interval is not entirely covered by SPK.");

        if (preflight)
        {
            var first = coverage.Beginning ?? (target == 0 ? initialStates.FirstTargetEpoch(origin) : null);
            if (first is null || !initialStates.TryState(target, origin, first.Value, out _))
                throw new InvalidDataException("No connected SPICE state at interval beginning.");
        }

        var states = resources.States;
        periodSeconds = authoredPeriod;
        if (periodSeconds is null or 0 && coverage is { Beginning: { } begin, Ending: { } end })
            periodSeconds = (end.ToJulianDate() - begin.ToJulianDate()) * JulianInstant.SecondsPerDay;

        var j2000ToEcliptic = SpiceReferenceFrames.InertialBasis(SpiceReferenceFrames.Id("ECLIPJ2000"))!.Value.Transpose();
        int reportedFailure = 0;
        return Sample;

        CartesianState? Sample(JulianInstant epoch)
        {
            epoch = ClampSpiceEpoch(epoch, coverage);
            bool found = states.TryNearestState(target, origin, epoch, out var state, out var error);
            if (error is not null && Interlocked.Exchange(ref reportedFailure, 1) == 0)
                warn($"SpiceOrbit state failed for {target} relative to {origin}. {error.Message}");
            if (!found)
                return null;
            return new(j2000ToEcliptic.Transform(state.PositionKilometers), j2000ToEcliptic.Transform(state.VelocityKilometersPerSecond));
        }

        int ResolveOrbitId(string name)
        {
            try
            {
                return NaifIds.Resolve(name, initial, allowNumericPrefix: true);
            }
            catch (NotSupportedException) when (!preflight)
            {
                // A required orbit may name a body defined by a separately installed kernel.
                return NaifIds.Resolve(name, resources, allowNumericPrefix: true);
            }
        }
    }

    private static Func<JulianInstant, FrameKinematics?> ReadSpiceRotation(SscValue.Map rotation,
        SpiceResources resources, SpiceResources initial, Basis3d spinFrame, bool optional)
    {
        var frames = resources.Frames;
        string frame = rotation.Text("Frame");
        string baseFrame = rotation.Fields.TryGetValue("BaseFrame", out var value) ? value.AsText() : "ECLIPJ2000";
        int frameId = initial.Frames.Id(frame), baseId = initial.Frames.Id(baseFrame);
        if (!optional)
        {
            // Required pointing can be supplied by a separately installed mission.
            if (frameId == 0) frameId = frames.Id(frame);
            if (baseId == 0) baseId = frames.Id(baseFrame);
        }
        if (frameId == 0 || baseId == 0)
            throw new InvalidDataException($"Unknown SSC SPICE frame {frame} or {baseFrame}.");
        frames.RequireDefinition(frameId);
        frames.RequireDefinition(baseId);
        var bounds = SpiceCoverage(rotation);
        var coverage = (bounds.Beginning is { } first ? Inset(first, .001) : (JulianInstant?)null,
            bounds.Ending is { } last ? Inset(last, -.001) : (JulianInstant?)null);
        return Sample;

        FrameKinematics? Sample(JulianInstant epoch)
        {
            var sample = ClampSpiceEpoch(epoch, coverage);
            if (!frames.TrySample(frameId, sample, false, out var pointing) ||
                !frames.TrySample(baseId, sample, false, out var reference))
                return null;
            Basis3d toWorld = SpiceFixedFrames.Compose(spinFrame, reference.ToWorld.Transpose());
            Vector3d? velocity = sample != epoch ? default(Vector3d) : pointing.AngularVelocityRadiansPerSecond is { } bodyRate &&
                reference.AngularVelocityRadiansPerSecond is { } referenceRate ? toWorld.Transform(bodyRate - referenceRate) : null;
            return new(SpiceFixedFrames.Compose(toWorld, pointing.ToWorld), velocity);
        }
    }

    private static (JulianInstant? Beginning, JulianInstant? Ending) SpiceCoverage(SscValue.Map block)
    {
        if (block.Fields.ContainsKey("Beginning") != block.Fields.ContainsKey("Ending"))
            throw new InvalidDataException("SSC SPICE coverage requires both Beginning and Ending.");
        return (Date("Beginning"), Date("Ending"));

        JulianInstant? Date(string name) => block.Fields.TryGetValue(name, out var value)
            ? ReadCatalogueDate(value.AsText()) : null;
    }

    // The authored SSC interval is in Julian days, with the native model's millisecond inset.
    private static JulianInstant Inset(JulianInstant epoch, double seconds) =>
        JulianInstant.FromJulianDate(epoch.ToJulianDate() + seconds / JulianInstant.SecondsPerDay, TimeScale.Tdb);

    private static JulianInstant ClampSpiceEpoch(JulianInstant epoch,
        (JulianInstant? Beginning, JulianInstant? Ending) coverage)
    {
        if (coverage.Beginning is { } first && epoch.CompareTo(first) < 0)
            return first;
        if (coverage.Ending is { } last && epoch.CompareTo(last) > 0)
            return last;
        return epoch;
    }
}
