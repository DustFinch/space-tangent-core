// SPDX-License-Identifier: GPL-2.0-or-later
using System.Globalization;
using SpaceTangent.Contracts;
using SpaceTangent.Core.Orbits;
using SpaceTangent.Core.Spice;
using SpaceTangent.Core.World;

namespace SpaceTangent.Core.Addons;

public sealed record SscBodyAxis(FrameAxis Axis, string Observer, string Target, DirectionQuantity Quantity);
public sealed record SscBodyOrientation(SscBodyAxis Primary, SscBodyAxis Secondary);

public sealed record SscRings(double InnerKilometers, double OuterKilometers, string TexturePath);

public sealed record SscExtrasolarConic(Func<JulianInstant, CartesianState?> Sample, IReadOnlyList<Vector3d> Points);

public delegate SampledMission SscTimelineBinder(ObjectId id, Func<string, ObjectId> resolve,
    Func<ObjectId, double>? radiusKilometers = null);

public sealed record SscBodyDefinition(string Names, string Parent, string Center, string Class,
    double RadiusKilometers, Vector3d Color, string? Mesh, Vector3d MeshCenter, Basis3d MeshRotation,
    SscBodyOrientation? Orientation, Func<JulianInstant, CartesianState?> Sample)
{
    public Func<JulianInstant, FrameKinematics?>? SampleRotation { get; init; }
    public Vector3d? EllipsoidRadiiKilometers { get; init; }
    public string? SurfaceMap { get; init; }
    public double? GeometricAlbedo { get; init; }
    /// <summary>The apoapsis distance of the resolved bound catalogue conic, when available.</summary>
    public double? OrbitApoapsisKilometers { get; init; }
    public SscRings? Rings { get; init; }
    public string? MeshRoot { get; init; }
    public Func<bool, SscExtrasolarConic>? BindExtrasolarOrbit { get; init; }
    public SscTimelineBinder? BindTimeline { get; init; }

    public string Name => Names.Split(':')[0];
}

/// <summary>Local SSC bodies with conic or cubic XYZV orbits and static or two-vector attitudes.</summary>
public static partial class SscBodyCatalogue
{
    private const double AuKilometers = 149_597_870.7;
    private static readonly FrameId Icrs = new("icrf3");
    private static readonly Basis3d Ecliptic = Rotation(new(1, 0, 0), 23.4392911);

    public static IReadOnlyList<SscBodyDefinition> Load(string root, CancellationToken cancellation = default,
        ISscScriptModels? scripts = null, Action<string>? warning = null)
    {
        root = Path.GetFullPath(root);
        if (!AddonDependencies.DirectoryExists(root))
            return [];
        string pack = Path.Combine(root, "pack.json");
        if (AddonDependencies.FileExists(pack))
            return AddonDiagnostics.Read(AddonStage.Catalog, pack, () => ReadPackedSsc(root, pack, scripts, warning, cancellation));
        var source = ReadLooseDocuments(root);
        return ReadBodyDefinitions(SscDefinitions.Load(source.Documents, cancellation), _ => root,
            scripts, warning, source.Resolve, cancellation, [root], source.Kernels);
    }

    private static SscSource ReadLooseDocuments(string root)
    {
        string data = Path.Combine(root, "data");
        var paths = AddonDependencies.ListFiles(root, "*.ssc").AsEnumerable();
        if (AddonDependencies.DirectoryExists(data))
            paths = paths.Concat(AddonDependencies.ListFiles(data, "*.ssc"));
        string[] files = paths.OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray();
        if (files.Length > 64)
            throw new InvalidDataException("A body catalogue source contains more than 64 SSC files.");
        return new(files.Select(file => new SscDocument(file, root)).ToArray(),
            (body, field, stage, asset, fallbacks) => SscSampledCatalog.FindAsset(stage, root,
                body.DirectoryFor(field), asset, fallbacks), []);
    }

    private static IReadOnlyList<SscBodyDefinition> ReadBodyDefinitions(IReadOnlyList<SscDefinition> definitions,
        Func<string, string> root, ISscScriptModels? scripts, Action<string>? warning, SscResource resolve, CancellationToken cancellation,
        string[] roots, IReadOnlyList<PackedKernel> kernels, IReadOnlyDictionary<string, int>? kernelOrder = null) =>
        ReadBodyDefinitions(definitions, root, scripts, warning, resolve, cancellation, roots, kernels, out _, null, kernelOrder);

    private static IReadOnlyList<SscBodyDefinition> ReadBodyDefinitions(IReadOnlyList<SscDefinition> definitions,
        Func<string, string> root, ISscScriptModels? scripts, Action<string>? warning, SscResource resolve, CancellationToken cancellation,
        string[] roots, IReadOnlyList<PackedKernel> kernels, out SpiceResources resources,
        IReadOnlyList<SpiceResources.KernelGroup>? missionLoads, IReadOnlyDictionary<string, int>? kernelOrder)
    {
        if (definitions.Count > 512)
            throw new InvalidDataException("A body catalogue source contains more than 512 objects.");
        var spice = ReadSpiceResources(definitions, root, resolve, cancellation, roots, kernels, missionLoads, kernelOrder);
        resources = spice.Pool;
        var byPath = definitions.ToDictionary(item => item.Parent + "/" + item.Name, StringComparer.OrdinalIgnoreCase);
        var result = new List<SscBodyDefinition>();
        var visited = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        var reading = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in definitions)
            Read(item);
        return result.AsReadOnly();

        bool Read(SscDefinition item)
        {
            cancellation.ThrowIfCancellationRequested();
            string path = item.Parent + "/" + item.Name;
            if (visited.TryGetValue(path, out bool installed))
                return installed;
            if (!reading.Add(path))
                throw new InvalidDataException($"SSC parent cycle at {path}.");
            if (!byPath.TryGetValue(item.Parent, out var parent) || Read(parent))
            {
                var body = AddonDiagnostics.Read(AddonStage.Catalog, item.SourceFile, () => ReadBody(item, root, scripts, warning, resolve, spice, cancellation));
                installed = body is not null;
                if (body is not null)
                    result.Add(body);
            }
            reading.Remove(path);
            visited[path] = installed;
            return installed;
        }
    }

    /// <summary>Reads a local body pack and its optional fixed-column MPC selections.</summary>
    public static IReadOnlyList<SscBodyDefinition> LoadWithMpc(string root, Action<string> warning,
        CancellationToken cancellation = default, ISscScriptModels? scripts = null)
    {
        ArgumentNullException.ThrowIfNull(warning);
        root = Path.GetFullPath(root);
        var bodies = Load(root, cancellation, scripts, warning).ToList();
        AppendMpc(root, bodies, warning, cancellation);
        return bodies.AsReadOnly();
    }

    private static void AppendMpc(string root, List<SscBodyDefinition> bodies, Action<string> warning, CancellationToken cancellation)
    {
        if (!AddonDependencies.DirectoryExists(root))
            return;

        string data = Path.Combine(root, "data");
        var paths = AddonDependencies.ListFiles(root, "*.mpc").AsEnumerable();
        if (AddonDependencies.DirectoryExists(data))
            paths = paths.Concat(AddonDependencies.ListFiles(data, "*.mpc"));
        string[] files = paths.OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray();
        if (files.Length > 64)
            throw new InvalidDataException("A body catalogue source contains more than 64 MPC selections.");
        var known = new HashSet<string>(bodies.Where(body => body.Parent.Equals("Sol", StringComparison.OrdinalIgnoreCase))
            .SelectMany(body => body.Names.Split(':')), StringComparer.OrdinalIgnoreCase);
        foreach (string path in files)
        {
            cancellation.ThrowIfCancellationRequested();
            var catalogue = AddonDiagnostics.Read(AddonStage.Catalog, path,
                () => MpcCatalogue.Load(LocalAddonFiles.Resolve(root, root, path), cancellation));
            string label = Path.GetFileName(path);
            foreach (string message in catalogue.Warnings)
                warning($"{label}: {message}");
            foreach (MpcOrbit orbit in catalogue.Orbits)
            {
                cancellation.ThrowIfCancellationRequested();
                if (known.Contains(orbit.Name) || known.Contains(orbit.Designation))
                {
                    warning($"{label}: {orbit.Designation} already has a catalogue entry.");
                    continue;
                }
                if (bodies.Count == 512)
                {
                    warning($"{label}: the body pack has reached 512 objects. Later MPC records were left out.");
                    return;
                }
                known.Add(orbit.Name);
                known.Add(orbit.Designation);
                string aliases = orbit.Name == orbit.Designation ? orbit.Name : $"{orbit.Name}:{orbit.Designation}";
                bodies.Add(new(aliases, "Sol", "Sol", orbit.IsComet ? "comet" : "asteroid", orbit.RadiusKilometers,
                    new(.70, .65, .60), null, default, Basis3d.Identity, null, epoch => orbit.Sample(epoch)));
            }
        }
    }

    public static string ResolveTexture(string root, string mesh, string relative)
    {
        string directory = Path.GetDirectoryName(mesh)!;
        if (relative.EndsWith(".*", StringComparison.Ordinal))
        {
            foreach (string name in TextureNames(relative))
            {
                try { return Find(name); }
                catch (FileNotFoundException) { }
            }
        }
        return Find(relative);

        string Find(string name) => SscSampledCatalog.FindAsset(AddonStage.Texture, root, directory, name,
            Path.Combine("textures", "medres"), Path.Combine("textures", "lores"), "textures");
    }

    private static string[] TextureNames(string name) => name.EndsWith(".*", StringComparison.Ordinal)
        ? [name[..^1] + "png", name[..^1] + "jpg", name[..^1] + "dds"] : [name];

    private static SscBodyDefinition? ReadBody(SscDefinition item, Func<string, string> root, ISscScriptModels? scripts,
        Action<string>? warning, SscResource resolve, SscSpiceInput spice, CancellationToken cancellation)
    {
        SscValue.Map fields = item.Properties;
        var reference = SscConicOrbits.ReadReference(item);
        bool starScale = reference.Center.Equals("Sol", StringComparison.OrdinalIgnoreCase) || reference.Center.Equals("SSB", StringComparison.OrdinalIgnoreCase);
        Basis3d orbitFrame = reference.Axes switch
        {
            SscConicAxes.EclipticJ2000 => Ecliptic,
            SscConicAxes.EquatorJ2000 => Basis3d.Identity,
            SscConicAxes.ParentDefault => starScale ? Ecliptic : Basis3d.Identity,
            SscConicAxes.ConstantTwoVector => SscConstantOrbitFrame.Read(fields.Object("OrbitFrame").Object("TwoVector")),
            _ => throw new NotSupportedException($"The SSC orbital frame {reference.Axes} is unavailable.")
        };
        JulianInstant? beginning = Text("Beginning") is { } begin ? SscSampledCatalog.ReadDate(begin) : null;
        JulianInstant? ending = Text("Ending") is { } end ? SscSampledCatalog.ReadDate(end) : null;
        if (beginning is { } first && ending is { } last && first.CompareTo(last) >= 0)
            throw new InvalidDataException("SSC ending must follow its beginning.");
        Func<JulianInstant, CartesianState?> raw;
        double? orbitPeriodSeconds = null;
        double? orbitApoapsis = null;
        Func<bool, SscExtrasolarConic>? extrasolarOrbit = null;
        var timeline = fields.Fields.TryGetValue("Timeline", out var timelineValue)
            ? ReadTimeline(item, timelineValue, resolve, spice, Warn, cancellation) : null;
        bool usesScriptOrbit = fields.Fields.ContainsKey("ScriptedOrbit");
        if (timeline is not null)
            raw = _ => null; // A timeline needs its bound graph to select a center and frame.
        else if (TrySpiceModel(fields, spice, "SpiceOrbit", ReadSpiceOrbit, Warn) is { } spiceSample)
            raw = spiceSample;
        else if (usesScriptOrbit &&
            TryFactory<SscScriptOrbit>("ScriptedOrbit", scripts is null ? null : scripts.CreateOrbit) is { } scripted)
            raw = ScriptOrbit(scripted);
        else if (ReadLegacySampledOrbit(item, fields, "SampledOrbit", resolve, Warn) is { } legacyCurve)
            raw = epoch => legacyCurve.TrySample(epoch, out var state) ? state : null;
        else if (fields.Fields.TryGetValue("EllipticalOrbit", out SscValue? orbit))
        {
            var context = new SscOrbitContext(Icrs, starScale ? AuKilometers : 1,
                starScale ? 31_557_600 : 86_400);
            var conic = SscConicOrbits.ReadOrbit(item, orbit, context);
            orbitPeriodSeconds = conic.CharacteristicPeriodSeconds;
            var elements = conic.Orbit.Elements;
            if (elements.Eccentricity < 1)
                orbitApoapsis = elements.PeriapsisKilometers * ((1 + elements.Eccentricity) / (1 - elements.Eccentricity));
            raw = epoch => conic.Orbit.Evaluate(epoch);
            if (!starScale && !item.Parent.StartsWith("Sol/", StringComparison.OrdinalIgnoreCase))
            {
                extrasolarOrbit = orbitsStar =>
                {
                    var units = new SscOrbitContext(Icrs, orbitsStar ? AuKilometers : 1,
                        orbitsStar ? 31_557_600 : 86_400);
                    var external = SscConicOrbits.ReadOrbit(item, orbit, units);
                    var externalOrbit = external.Orbit;
                    var axes = reference.Axes == SscConicAxes.ParentDefault ? Ecliptic : orbitFrame;
                    double period = external.CharacteristicPeriodSeconds;
                    double eccentricity = externalOrbit.Elements.Eccentricity;
                    const int segments = 120;
                    var points = new Vector3d[segments + 1];
                    for (int index = 0; index <= segments; index++)
                    {
                        double fraction = (double)index / segments;
                        double elapsed = period * fraction;
                        if (eccentricity > 1)
                        {
                            double anomaly = 6 * fraction - 3;
                            elapsed = (eccentricity * Math.Sinh(anomaly) - anomaly) / Math.Tau * period;
                        }
                        points[index] = axes.Transform(externalOrbit.EvaluateElapsedSeconds(elapsed).PositionKilometers);
                    }
                    if (eccentricity < 1)
                        points[segments] = points[0];
                    return new(SampleExternal, Array.AsReadOnly(points));

                    CartesianState? SampleExternal(JulianInstant epoch)
                    {
                        if (beginning is { } start && epoch.CompareTo(start) < 0 ||
                            ending is { } end && epoch.CompareTo(end) > 0)
                            return null;
                        var state = externalOrbit.Evaluate(epoch);
                        return new(axes.Transform(state.PositionKilometers), axes.Transform(state.VelocityKilometersPerSecond));
                    }
                };
            }
        }
        else if (fields.Fields.TryGetValue("FixedPosition", out var position))
        {
            var state = new CartesianState(ReadVector(position), default);
            raw = _ => state;
        }
        else
        {
            const string key = "SampledTrajectory";
            if (!fields.Fields.ContainsKey(key) && (usesScriptOrbit || fields.Fields.ContainsKey("SampledOrbit")))
                return null;
            SscValue data = fields.Get(key);
            string name;
            if (data is SscValue.Map sample)
            {
                if (sample.Fields.TryGetValue("Interpolation", out SscValue? interpolation) && interpolation.AsText() != "cubic")
                    throw new NotSupportedException("Only cubic sampled trajectory interpolation is available.");
                name = sample.Text("Source");
            }
            else
                name = data.AsText();
            string file;
            try
            {
                file = resolve(item, key, AddonStage.Trajectory, name, Path.Combine("models", "trajectories"), "data", "trajectories");
            }
            catch (FileNotFoundException) when (usesScriptOrbit)
            {
                Warn($"The alternate {key} resource is unavailable.");
                return null;
            }
            if (!Path.GetExtension(file).Equals(".xyzv", StringComparison.OrdinalIgnoreCase))
                throw new NotSupportedException("This SSC body needs position and velocity samples in XYZV.");
            var curve = SampledStateTrajectory.ReadXyzv(file, Icrs);
            if (curve.Count == 0)
                throw new InvalidDataException("The SSC trajectory contains no samples.");
            raw = epoch => epoch.CompareTo(curve.Start!.Value) >= 0 && epoch.CompareTo(curve.End!.Value) <= 0 &&
                curve.TrySample(epoch, out CartesianState state) ? state : null;
        }

        Vector3d? radii = null;
        if (fields.Fields.TryGetValue("SemiAxes", out var dimensions))
        {
            var axes = ReadVector(dimensions);
            if (axes.X <= 0 || axes.Y <= 0 || axes.Z <= 0 || Math.Max(axes.X, Math.Max(axes.Y, axes.Z)) > 2_000_000)
                throw new InvalidDataException("SSC semiaxes must be positive and within the size limit.");
            radii = axes;
        }
        double radius = fields.Fields.TryGetValue("Radius", out var radiusValue) ? Number(radiusValue) :
            radii is { } ellipsoid ? Math.Max(ellipsoid.X, Math.Max(ellipsoid.Y, ellipsoid.Z)) : 1;
        if (radius is <= 0 or > 2_000_000)
            throw new InvalidDataException("SSC body radius is out of range.");

        Vector3d color = fields.Fields.TryGetValue("Color", out var colorValue) ? ReadVector(colorValue) : new(.8, .8, .8);
        if (color.X is < 0 or > 1 || color.Y is < 0 or > 1 || color.Z is < 0 or > 1)
            throw new InvalidDataException("SSC color components must be between zero and one.");
        var meshCenter = fields.Fields.TryGetValue("MeshCenter", out var centerValue) ? ReadVector(centerValue) : default;
        Basis3d meshRotation = Basis3d.Identity;
        if (fields.Fields.TryGetValue("Orientation", out var rotationValue))
        {
            double[] rotation = Numbers(rotationValue, 4);
            meshRotation = Rotation(new(rotation[1], rotation[2], rotation[3]), rotation[0]);
        }
        SscBodyOrientation? orientation = null;
        Basis3d spinFrame = Ecliptic;
        if (fields.Fields.ContainsKey("BodyFrame"))
        {
            var frame = fields.Object("BodyFrame");
            if (frame.Fields.Count != 1)
                throw new InvalidDataException("SSC BodyFrame requires one frame definition.");
            if (frame.Fields.ContainsKey("TwoVector"))
            {
                var vectors = frame.Object("TwoVector");
                if (HasConstantAxes(vectors))
                    spinFrame = SscConstantOrbitFrame.Read(vectors);
                else
                    orientation = new(ReadAxis(vectors.Object("Primary")), ReadAxis(vectors.Object("Secondary")));
            }
            else if (frame.Fields.ContainsKey("EquatorJ2000"))
                spinFrame = Basis3d.Identity;
            else if (!frame.Fields.ContainsKey("EclipticJ2000"))
                throw new NotSupportedException("This SSC body frame is unavailable.");
        }

        Func<JulianInstant, FrameKinematics?>? spin = null;
        bool usesScriptRotation = fields.Fields.ContainsKey("ScriptedRotation");
        if (TrySpiceModel(fields, spice, "SpiceRotation", (block, pool) => ReadSpiceRotation(block, pool, spinFrame), Warn) is { } spiceSpin)
            spin = spiceSpin;
        else if (usesScriptRotation &&
            TryFactory<SscScriptRotation>("ScriptedRotation", scripts is null ? null : scripts.CreateRotation) is { } scriptedRotation)
            spin = ScriptRotation(scriptedRotation, spinFrame);
        else if (fields.Fields.ContainsKey("SampledOrientation"))
        {
            string name = Text("SampledOrientation")!;
            try
            {
                string file = resolve(item, "SampledOrientation", AddonStage.Trajectory, name, "data", "trajectories");
                spin = ReadSampledSpin(file, spinFrame, cancellation);
            }
            catch (FileNotFoundException) when (usesScriptRotation)
            {
                Warn("The alternate sampled orientation is unavailable.");
                return null;
            }
        }
        else if (Text("CustomRotation") is "iau-amalthea" or "iau-thebe")
        {
            int naif = Text("CustomRotation") == "iau-amalthea" ? 505 : 514;
            string rotationRoot = root(item.DirectoryFor("CustomRotation"));
            string local = Path.Combine(rotationRoot, "pck00011.tpc");
            if (AddonDependencies.FileExists(local))
            {
                var model = new SpicePolynomialBodyFrame(LocalAddonFiles.Resolve(rotationRoot, rotationRoot, local), naif);
                spin = epoch => model.Sample(epoch);
            }
            else
            {
                Warn("Local PCK is absent; using synchronous rotation.");
                spin = ReadUniformSpin(new SscValue.Map(new Dictionary<string, SscValue>()), spinFrame, orbitPeriodSeconds);
            }
        }
        else if (fields.Fields.ContainsKey("PrecessingRotation"))
            spin = ReadUniformSpin(fields.Object("PrecessingRotation"), spinFrame, orbitPeriodSeconds, precessing: true);
        else if (fields.Fields.ContainsKey("UniformRotation"))
            spin = ReadUniformSpin(fields.Object("UniformRotation"), spinFrame, orbitPeriodSeconds);
        else if (fields.Fields.TryGetValue("FixedRotation", out var fixedRotation))
        {
            if (fixedRotation is not SscValue.Map fixedSpin)
                throw new InvalidDataException("Expected a fixed rotation definition.");
            if (fixedSpin.Fields.Values.Any(value => Number(value) != 0))
                spin = ReadUniformSpin(fixedSpin, spinFrame, orbitPeriodSeconds, stationary: true);
        }
        else if (usesScriptRotation)
            return null;
        else if (fields.Fields.ContainsKey("CustomRotation"))
            throw new NotSupportedException("This body catalogue needs a different rotation model.");
        else if (ReadTimeQuantity(fields, "RotationPeriod", 3_600) is { } legacyPeriod)
            spin = ReadUniformSpin(new SscValue.Map(new Dictionary<string, SscValue>()), spinFrame, legacyPeriod);
        if (spin is not null && orientation is not null)
            throw new NotSupportedException("This SSC spin model currently needs an inertial body frame.");

        string? mesh = Text("Mesh") is { } fileName ? resolve(item, "Mesh", AddonStage.Mesh, fileName, "models") : null;
        string? surfaceMap = null;
        if (Text("Texture") is { } texture)
        {
            foreach (string candidate in TextureNames(texture))
            {
                try
                {
                    surfaceMap = resolve(item, "Texture", AddonStage.Texture, candidate,
                        Path.Combine("textures", "medres"), Path.Combine("textures", "lores"), "textures");
                    break;
                }
                catch (FileNotFoundException) { }
            }
            if (surfaceMap is null)
                Warn($"Texture {texture} is unavailable; using the catalogue color.");
        }
        SscRings? rings = null;
        if (fields.Fields.ContainsKey("Rings"))
        {
            var ring = fields.Object("Rings");
            double inner = Number(ring.Get("Inner")), outer = Number(ring.Get("Outer"));
            if (inner <= 0 || outer <= inner || outer > 10_000_000)
                throw new InvalidDataException("SSC ring radii must be positive and increasing.");
            if (ring.Fields.TryGetValue("Texture", out var ringTexture))
            {
                string name = ringTexture.AsText();
                foreach (string candidate in TextureNames(name))
                {
                    try
                    {
                        string path = resolve(item, "Rings", AddonStage.Texture, candidate,
                            Path.Combine("textures", "medres"), Path.Combine("textures", "lores"), "textures");
                        rings = new(inner, outer, path);
                        break;
                    }
                    catch (FileNotFoundException) { }
                }
                if (rings is null)
                    Warn($"Ring texture {name} is unavailable.");
            }
        }
        double? albedo = Text("GeomAlbedo") is { } text &&
            double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double coefficient) &&
            double.IsFinite(coefficient) && coefficient >= 0 ? coefficient : null;
        return new(item.Names, item.Parent, reference.Center, Text("Class") ?? "asteroid", radius, color,
            mesh, meshCenter, meshRotation, orientation, Sample)
        {
            SampleRotation = spin, EllipsoidRadiiKilometers = radii, SurfaceMap = surfaceMap,
            Rings = rings, GeometricAlbedo = albedo, BindExtrasolarOrbit = extrasolarOrbit,
            OrbitApoapsisKilometers = orbitApoapsis is { } distance && double.IsFinite(distance) ? distance : null,
            MeshRoot = mesh is null ? null : root(item.DirectoryFor("Mesh")),
            BindTimeline = timeline is null ? null : (id, find, radiusKilometers) =>
                new(id, item.Name, mesh, radius, Array.AsReadOnly(timeline.Select(phase => phase(find, radiusKilometers)).ToArray()))
        };

        T? TryFactory<T>(string field, Func<SscScriptRequest, T?>? make) where T : class
        {
            if (make is not null)
            {
                try
                {
                    if (make(ScriptRequest(item, field, root(item.DirectoryFor(field)), resolve)) is { } model)
                        return model;
                }
                catch (Exception error) when (error is InvalidDataException or IOException or ArgumentException)
                {
                    Warn($"{field} initialization failed. {error.Message}");
                    return null;
                }
            }
            Warn($"{field} initialization failed: factory unavailable or rejected its model.");
            return null;
        }

        void Warn(string message) => warning?.Invoke($"{item.Parent}/{item.Name}: {message}");

        string? Text(string field) => fields.Fields.TryGetValue(field, out SscValue? value) ? value.AsText() : null;

        CartesianState? Sample(JulianInstant epoch)
        {
            if ((beginning is { } start && epoch.CompareTo(start) < 0) ||
                (ending is { } stop && epoch.CompareTo(stop) > 0) ||
                raw(epoch) is not { } state)
                return null;
            return new(orbitFrame.Transform(state.PositionKilometers), orbitFrame.Transform(state.VelocityKilometersPerSecond));
        }
    }

    private static SampledStateTrajectory? ReadLegacySampledOrbit(SscDefinition item, SscValue.Map fields,
        string field, SscResource resolve, Action<string> warn,
        Func<string, SampledStateTrajectory>? read = null)
    {
        if (fields.Fields.ContainsKey("SampledTrajectory") || !fields.Fields.TryGetValue("SampledOrbit", out var data))
            return null;

        string name = data is SscValue.Map source ? source.Text("Source") : data.AsText();
        try
        {
            if (data is SscValue.Map options && options.Fields.TryGetValue("Interpolation", out var interpolation) &&
                interpolation.AsText() != "cubic")
                throw new NotSupportedException("Only cubic SampledOrbit interpolation is available.");
            string file = resolve(item, field, AddonStage.Trajectory, name, Path.Combine("models", "trajectories"),
                "data", "trajectories");
            return read is null ? SampledStateTrajectory.ReadLegacy(file, Icrs) : read(file);
        }
        catch (Exception error) when (error is InvalidDataException or IOException or UnauthorizedAccessException or
                                      FormatException or ArgumentException or OverflowException or NotSupportedException)
        {
            warn($"SampledOrbit initialization failed: {name}: {error.Message}");
            return null;
        }
    }

    private static Func<JulianInstant, FrameKinematics?> ReadUniformSpin(SscValue.Map fields, Basis3d frame,
        double? orbitPeriodSeconds, bool stationary = false, bool precessing = false)
    {
        double period = stationary ? 0 : ReadTimeQuantity(fields, "Period", 3_600) ?? orbitPeriodSeconds ?? 0;
        double precessionPeriod = precessing ? ReadTimeQuantity(fields, "PrecessionPeriod", 31_557_600) ?? 0 : 0;
        var epoch = JulianInstant.FromJulianDate(Get("Epoch", JulianInstant.J2000Day), TimeScale.Tdb);
        Vector3d pole = new(0, 0, 1);
        double node = Angle("AscendingNode");
        var inclination = Rotation(new(1, 0, 0), Angle("Inclination"));
        var meanAtEpoch = MeanEquator(node);
        double spinRate = period == 0 ? 0 : Math.Tau / period;
        double nodeRate = period == 0 || precessionPeriod == 0 ? 0 : -Math.Tau / precessionPeriod;
        if (!double.IsFinite(spinRate) || !double.IsFinite(nodeRate))
            throw new InvalidDataException("SSC rotation rates must be finite.");
        double meridian = 180 + Angle("MeridianAngle");
        return time =>
        {
            double seconds = (double)time.DifferenceNanoseconds(epoch) / JulianInstant.NanosecondsPerSecond;
            double angle = meridian + (period == 0 ? 0 : 360 * (Math.IEEERemainder(seconds, period) / period));
            var mean = nodeRate == 0 ? meanAtEpoch :
                MeanEquator(node - 360 * (Math.IEEERemainder(seconds, precessionPeriod) / precessionPeriod));
            return new FrameKinematics(SampledMissionCatalog.Multiply(mean, Rotation(pole, angle)),
                mean.Z * spinRate + frame.Z * nodeRate);
        };

        Basis3d MeanEquator(double ascendingNode) => SampledMissionCatalog.Multiply(frame,
            SampledMissionCatalog.Multiply(Rotation(pole, ascendingNode), inclination));

        double Get(string key, double fallback) => fields.Fields.TryGetValue(key, out var value) ? Number(value) : fallback;
        double Angle(string key)
        {
            double degrees = Get(key, 0);
            return period == 0 ? degrees : (float)(degrees / 180 * Math.PI) * (180 / Math.PI);
        }
    }

    internal static double? ReadTimeQuantity(SscValue.Map fields, string name, double defaultScale)
    {
        double? seconds = null;
        foreach (var (key, value) in fields.Fields)
        {
            if (key != name && !key.StartsWith(name + "<", StringComparison.Ordinal))
                continue;
            if (seconds is not null)
                throw new InvalidDataException($"Duplicate SSC time field {name}.");
            double scale = key == name ? defaultScale : key[name.Length..] switch
            {
                "<s>" => 1,
                "<min>" => 60,
                "<h>" => 3_600,
                "<d>" => 86_400,
                "<y>" => 31_557_600,
                _ => throw new InvalidDataException($"Unknown SSC time unit in {key}.")
            };
            seconds = Number(value) * scale;
            if (!double.IsFinite(seconds.Value))
                throw new InvalidDataException($"SSC time field {name} must be finite.");
        }
        return seconds;
    }

    private static bool HasConstantAxes(SscValue.Map vectors) =>
        vectors.Object("Primary").Fields.ContainsKey("ConstantVector") &&
        vectors.Object("Secondary").Fields.ContainsKey("ConstantVector");

    private static SscBodyAxis ReadAxis(SscValue.Map frame)
    {
        FrameAxis axis = frame.Text("Axis") switch
        {
            "x" => FrameAxis.X, "-x" => FrameAxis.NegativeX, "y" => FrameAxis.Y,
            "-y" => FrameAxis.NegativeY, "z" => FrameAxis.Z, "-z" => FrameAxis.NegativeZ,
            _ => throw new InvalidDataException("Unknown SSC body-frame axis.")
        };
        bool velocity = frame.Fields.ContainsKey("RelativeVelocity");
        var direction = frame.Object(velocity ? "RelativeVelocity" : "RelativePosition");
        return new(axis, direction.Text("Observer"), direction.Text("Target"),
            velocity ? DirectionQuantity.Velocity : DirectionQuantity.Position);
    }

    private static double Number(SscValue value) =>
        double.TryParse(value.AsText(), NumberStyles.Float, CultureInfo.InvariantCulture, out double number) && double.IsFinite(number)
            ? number : throw new InvalidDataException("Invalid SSC number.");

    private static double[] Numbers(SscValue value, int count) =>
        value is SscValue.List list && list.Items.Length == count ? list.Items.Select(Number).ToArray() :
            throw new InvalidDataException("Invalid SSC vector.");

    internal static Vector3d ReadVector(SscValue value)
    {
        double[] vector = Numbers(value, 3);
        return new(vector[0], vector[1], vector[2]);
    }

    private static Basis3d Rotation(Vector3d axis, double degrees)
    {
        if (axis.Length <= 0 || !axis.IsFinite)
            throw new InvalidDataException("An SSC rotation needs a finite axis.");
        axis /= axis.Length;
        var (sin, cos) = Math.SinCos(degrees * Math.PI / 180);
        return new(Apply(new(1, 0, 0)), Apply(new(0, 1, 0)), Apply(new(0, 0, 1)));
        Vector3d Apply(Vector3d value) => value * cos + Vector3d.Cross(axis, value) * sin +
            axis * (Vector3d.Dot(axis, value) * (1 - cos));
    }
}
