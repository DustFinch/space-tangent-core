// SPDX-License-Identifier: GPL-2.0-or-later
using System.Globalization;
using SpaceTangent.Contracts;
using SpaceTangent.Core.Orbits;
using SpaceTangent.Core.World;

namespace SpaceTangent.Core.Addons;

public sealed record SscBodyAxis(FrameAxis Axis, string Observer, string Target, DirectionQuantity Quantity);
public sealed record SscBodyOrientation(SscBodyAxis Primary, SscBodyAxis Secondary);

public sealed record SscBodyDefinition(string Names, string Parent, string Center, string Class,
    double RadiusKilometers, Vector3d Color, string? Mesh, Vector3d MeshCenter, Basis3d MeshRotation,
    SscBodyOrientation? Orientation, Func<JulianInstant, CartesianState?> Sample)
{
    public Func<JulianInstant, FrameKinematics?>? SampleRotation { get; init; }

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
        string data = Path.Combine(root, "data");
        var paths = AddonDependencies.ListFiles(root, "*.ssc").AsEnumerable();
        if (AddonDependencies.DirectoryExists(data))
            paths = paths.Concat(AddonDependencies.ListFiles(data, "*.ssc"));
        string[] files = paths.OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray();
        if (files.Length > 64)
            throw new InvalidDataException("A body catalogue source contains more than 64 SSC files.");
        var definitions = SscDefinitions.Load(files, root, cancellation);
        if (definitions.Count > 512)
            throw new InvalidDataException("A body catalogue source contains more than 512 objects.");
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
                var body = AddonDiagnostics.Read(AddonStage.Catalog, item.SourceFile, () => ReadBody(item, root, scripts, warning));
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
        if (!AddonDependencies.DirectoryExists(root))
            return bodies.AsReadOnly();

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
                    return bodies.AsReadOnly();
                }
                known.Add(orbit.Name);
                known.Add(orbit.Designation);
                string aliases = orbit.Name == orbit.Designation ? orbit.Name : $"{orbit.Name}:{orbit.Designation}";
                bodies.Add(new(aliases, "Sol", "Sol", orbit.IsComet ? "comet" : "asteroid", orbit.RadiusKilometers,
                    new(.70, .65, .60), null, default, Basis3d.Identity, null, epoch => orbit.Sample(epoch)));
            }
        }
        return bodies.AsReadOnly();
    }

    public static string ResolveTexture(string root, string mesh, string relative) =>
        SscSampledCatalog.FindAsset(AddonStage.Texture, root, Path.GetDirectoryName(mesh)!, relative,
            Path.Combine("textures", "medres"), Path.Combine("textures", "lores"), "textures");

    private static SscBodyDefinition? ReadBody(SscDefinition item, string root, ISscScriptModels? scripts, Action<string>? warning)
    {
        SscValue.Map fields = item.Properties;
        var reference = SscConicOrbits.ReadReference(item);
        bool heliocentric = reference.Center.Equals("Sol", StringComparison.OrdinalIgnoreCase);
        bool ecliptic = reference.Axes switch
        {
            SscConicAxes.EclipticJ2000 => true,
            SscConicAxes.EquatorJ2000 => false,
            SscConicAxes.ParentDefault => heliocentric,
            _ => throw new NotSupportedException($"The SSC orbital frame {reference.Axes} is unavailable.")
        };
        JulianInstant? beginning = Text("Beginning") is { } begin ? SscSampledCatalog.ReadDate(begin) : null;
        JulianInstant? ending = Text("Ending") is { } end ? SscSampledCatalog.ReadDate(end) : null;
        if (beginning is { } first && ending is { } last && first.CompareTo(last) >= 0)
            throw new InvalidDataException("SSC ending must follow its beginning.");
        Func<JulianInstant, CartesianState?> raw;
        bool usesScriptOrbit = fields.Fields.ContainsKey("ScriptedOrbit");
        SscScriptOrbit? scripted = usesScriptOrbit
            ? TryFactory<SscScriptOrbit>("ScriptedOrbit", scripts is null ? null : scripts.CreateOrbit) : null;
        if (scripted is not null)
            raw = ScriptOrbit(scripted);
        else if (fields.Fields.TryGetValue("EllipticalOrbit", out SscValue? orbit))
        {
            var context = new SscOrbitContext(Icrs, heliocentric ? AuKilometers : 1,
                heliocentric ? 31_557_600 : 86_400);
            var curve = SscConicOrbits.ReadOrbit(item, orbit, context).Orbit;
            raw = epoch => curve.Evaluate(epoch);
        }
        else if (fields.Fields.TryGetValue("FixedPosition", out var position))
        {
            var state = new CartesianState(ReadVector(position), default);
            raw = _ => state;
        }
        else
        {
            string key = fields.Fields.ContainsKey("SampledTrajectory") ? "SampledTrajectory" : "SampledOrbit";
            if (usesScriptOrbit && !fields.Fields.ContainsKey(key))
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
                file = SscSampledCatalog.FindAsset(AddonStage.Trajectory, root, item.DirectoryFor(key), name,
                    Path.Combine("models", "trajectories"), "data", "trajectories");
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

        double radius = fields.Fields.TryGetValue("Radius", out var radiusValue) ? Number(radiusValue) : 1;
        if (radius is <= 0 or > 2_000_000)
            throw new InvalidDataException("SSC body radius is out of range.");
        string? mesh = Text("Mesh") is { } fileName
            ? SscSampledCatalog.FindAsset(AddonStage.Mesh, root, item.DirectoryFor("Mesh"), fileName, "models") : null;
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
                orientation = new(ReadAxis(vectors.Object("Primary")), ReadAxis(vectors.Object("Secondary")));
            }
            else if (frame.Fields.ContainsKey("EquatorJ2000"))
                spinFrame = Basis3d.Identity;
            else if (!frame.Fields.ContainsKey("EclipticJ2000"))
                throw new NotSupportedException("This SSC body frame is unavailable.");
        }

        Func<JulianInstant, FrameKinematics?>? spin = null;
        bool usesScriptRotation = fields.Fields.ContainsKey("ScriptedRotation");
        if (usesScriptRotation &&
            TryFactory<SscScriptRotation>("ScriptedRotation", scripts is null ? null : scripts.CreateRotation) is { } scriptedRotation)
            spin = ScriptRotation(scriptedRotation, spinFrame);
        else if (fields.Fields.ContainsKey("SampledOrientation"))
        {
            string name = Text("SampledOrientation")!;
            try
            {
                string file = SscSampledCatalog.FindAsset(AddonStage.Trajectory, root,
                    item.DirectoryFor("SampledOrientation"), name, "data", "trajectories");
                spin = ReadSampledSpin(file, spinFrame);
            }
            catch (FileNotFoundException) when (usesScriptRotation)
            {
                Warn("The alternate sampled orientation is unavailable.");
                return null;
            }
        }
        else if (fields.Fields.TryGetValue("FixedRotation", out var fixedRotation))
        {
            if (fixedRotation is not SscValue.Map fixedSpin || fixedSpin.Fields.Any(pair => Number(pair.Value) != 0))
                throw new NotSupportedException("Only an identity fixed rotation is available for SSC body frames.");
        }
        else if (usesScriptRotation)
            return null;
        else if (fields.Fields.Keys.Any(key => key is "UniformRotation" or "CustomRotation" or "SpiceRotation"))
            throw new NotSupportedException("This body catalogue needs a different rotation model.");
        if (spin is not null && orientation is not null)
            throw new NotSupportedException("Scripted and sampled rotations currently need an inertial body frame.");

        return new(item.Names, item.Parent, reference.Center, Text("Class") ?? "asteroid", radius, color,
            mesh, meshCenter, meshRotation, orientation, Sample) { SampleRotation = spin };

        T? TryFactory<T>(string field, Func<SscScriptRequest, T?>? make) where T : class
        {
            if (make is not null)
            {
                try
                {
                    if (make(ScriptRequest(item, field, root)) is { } model)
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
            return ecliptic ? new(Ecliptic.Transform(state.PositionKilometers),
                Ecliptic.Transform(state.VelocityKilometersPerSecond)) : state;
        }
    }

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

    private static Vector3d ReadVector(SscValue value)
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
