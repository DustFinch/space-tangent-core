// SPDX-License-Identifier: GPL-2.0-or-later
using System.Globalization;
using SpaceTangent.Contracts;
using SpaceTangent.Core.Ephemerides;
using SpaceTangent.Core.Orbits;
using SpaceTangent.Core.World;

namespace SpaceTangent.Core.Addons;

public sealed class SscSampledSpacecraft
{
    internal sealed record Phase(JulianInstant Start, JulianInstant End, ObjectId Center, SampledStateTrajectory Curve);
    private readonly Phase[] _phases;
    private static readonly FrameId EclipticFrame = new("ecliptic-j2000");
    private const double Obliquity = 84381.448 / 3600 * Math.PI / 180;
    private static readonly double SinObliquity = Math.Sin(Obliquity), CosObliquity = Math.Cos(Obliquity);

    internal SscSampledSpacecraft(string name, ObjectId id, string mesh, double radius, Phase[] phases,
        FrameAxis primaryAxis, RelativeDirection primary, FrameAxis secondaryAxis, RelativeDirection secondary)
    {
        Name = name;
        Id = id;
        MeshPath = mesh;
        RadiusKilometers = radius;
        _phases = phases;
        PrimaryAxis = primaryAxis;
        Primary = primary;
        SecondaryAxis = secondaryAxis;
        Secondary = secondary;
    }

    public string Name { get; }
    public ObjectId Id { get; }
    public string MeshPath { get; }
    public double RadiusKilometers { get; }
    public JulianInstant Start => _phases[0].Start;
    public JulianInstant End => _phases[^1].End;
    public int PhaseCount => _phases.Length;
    internal IEnumerable<ObjectId> Centers => _phases.Select(phase => phase.Center).Distinct();
    internal FrameAxis PrimaryAxis { get; }
    internal RelativeDirection Primary { get; }
    internal FrameAxis SecondaryAxis { get; }
    internal RelativeDirection Secondary { get; }

    /// <summary>Returns the active phase's state in J2000 axes and its explicit center.</summary>
    public bool TrySampleRelative(JulianInstant epoch, out ObjectId center, out CartesianState state)
    {
        if (epoch.Scale != TimeScale.Tdb)
            throw new ArgumentException("SSC timelines require TDB.", nameof(epoch));
        center = default;
        state = default;
        if (epoch.CompareTo(Start) < 0 || epoch.CompareTo(End) >= 0)
            return false;
        int low = 0, high = _phases.Length - 1;
        while (low < high)
        {
            int middle = low + (high - low) / 2;
            if (epoch.CompareTo(_phases[middle].End) < 0)
                high = middle;
            else
                low = middle + 1;
        }
        Phase phase = _phases[low];
        if (!phase.Curve.TrySample(epoch, out var local))
            return false;
        center = phase.Center;
        state = new(ToEquatorial(local.PositionKilometers), ToEquatorial(local.VelocityKilometersPerSecond));
        return state.PositionKilometers.IsFinite && state.VelocityKilometersPerSecond.IsFinite;
    }

    internal static SampledStateTrajectory ReadCurve(string path) => SampledStateTrajectory.ReadXyzv(path, EclipticFrame);

    internal static Vector3d ToEquatorial(Vector3d value) =>
        new(value.X, CosObliquity * value.Y - SinObliquity * value.Z,
            SinObliquity * value.Y + CosObliquity * value.Z);
}

public static class SscSampledCatalog
{
    private static readonly string[] DateFormats = ["yyyy M d H:mm:ss.FFFFFFF", "yyyy M d H:mm:ss", "yyyy M d"];
    private static readonly JulianInstant UnixEpoch = JulianInstant.FromJulianDate(2440587.5, TimeScale.Tdb);

    public static SscSampledSpacecraft Load(string catalogPath, string assetRoot, string objectPath,
        CancellationToken cancellation = default)
        => Load([catalogPath], assetRoot, objectPath, cancellation);

    public static SscSampledSpacecraft Load(IEnumerable<string> catalogPaths, string assetRoot, string objectPath,
        CancellationToken cancellation = default)
    {
        var (root, definition) = ReadDefinition(catalogPaths, assetRoot, objectPath, cancellation);
        return AddonDiagnostics.Read(AddonStage.Catalog, definition.SourceFile, () => ReadTrack(root, definition, cancellation));
    }

    private static SscSampledSpacecraft ReadTrack(string root, SscDefinition definition, CancellationToken cancellation)
    {
        var (names, parent, fields, _, _) = definition;
        if (fields.Text("Class") != "spacecraft")
            throw new NotSupportedException("The sampled SSC track must be a spacecraft.");

        string name = names.Split(':')[0];
        string primaryPath = parent + "/" + name;
        var id = new ObjectId("ssc/" + primaryPath.ToLowerInvariant());
        string mesh = FindAsset(AddonStage.Mesh, root, definition.DirectoryFor("Mesh"), fields.Text("Mesh"), "models");
        if (!double.TryParse(fields.Text("Radius"), NumberStyles.Float, CultureInfo.InvariantCulture, out double radius) ||
            !double.IsFinite(radius) || radius <= 0)
            throw new InvalidDataException("SSC radius must be finite and positive.");
        if (fields.Get("Timeline") is not SscValue.List { Items.Length: >= 1 and <= 128 } timeline)
            throw new InvalidDataException("SSC Timeline must contain one to 128 phases.");
        string directory = definition.DirectoryFor("Timeline");

        var phases = new SscSampledSpacecraft.Phase[timeline.Items.Length];
        var curves = new Dictionary<string, SampledStateTrajectory>(OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        long sourceBytes = 0;
        (FrameAxis PrimaryAxis, RelativeDirection Primary, FrameAxis SecondaryAxis, RelativeDirection Secondary)? attitude = null;
        JulianInstant previousEnd = default;
        for (int index = 0; index < phases.Length; index++)
        {
            cancellation.ThrowIfCancellationRequested();
            if (timeline.Items[index] is not SscValue.Map phase)
                throw new InvalidDataException("SSC Timeline phases must be objects.");
            if (index > 0 && phase.Fields.ContainsKey("Beginning"))
                throw new InvalidDataException("SSC Beginning belongs in the first phase only.");
            JulianInstant start = index == 0 ? ReadDate(phase.Text("Beginning")) : previousEnd;
            JulianInstant end = ReadDate(phase.Text("Ending"));
            if (start.CompareTo(end) >= 0)
                throw new InvalidDataException("SSC phase ending must follow its beginning.");

            SscValue.Map frame = phase.Object("OrbitFrame").Object("EclipticJ2000");
            ObjectId center = ResolveObject(frame.Text("Center"), parent, names, id);
            SscValue.Map source = phase.Object("SampledTrajectory");
            if (source.Text("Interpolation") != "cubic" || source.Text("DoublePrecision") != "true")
                throw new NotSupportedException("The SSC track requires cubic double-precision samples.");
            string path = FindAsset(AddonStage.Trajectory, root, directory, source.Text("Source"),
                Path.Combine("models", "trajectories"), "data");
            if (!Path.GetExtension(path).Equals(".xyzv", StringComparison.OrdinalIgnoreCase))
                throw new NotSupportedException("The SSC track requires XYZV samples.");
            if (!curves.TryGetValue(path, out var curve))
            {
                sourceBytes += new FileInfo(path).Length;
                if (sourceBytes > 128L * 1024 * 1024)
                    throw new InvalidDataException("SSC timeline trajectories exceed 128 MiB.");
                curve = SscSampledSpacecraft.ReadCurve(path);
                if (curve.Count == 0)
                    throw new InvalidDataException("SSC phase has an empty trajectory.");
                curves.Add(path, curve);
            }

            if (phase.Object("FixedRotation").Fields.Count != 0)
                throw new NotSupportedException("This SSC track requires an identity rotation relative to the body frame.");
            SscValue.Map body = phase.Object("BodyFrame").Object("TwoVector");
            if (ResolveObject(body.Text("Center"), parent, names, id) != id)
                throw new NotSupportedException("The SSC body frame must be centered on the spacecraft.");
            var (primaryAxis, primary) = ReadDirection(body.Object("Primary"), parent, names, id);
            var (secondaryAxis, secondary) = ReadDirection(body.Object("Secondary"), parent, names, id);
            var directions = (primaryAxis, primary, secondaryAxis, secondary);
            if (attitude is { } previous && directions != previous)
                throw new NotSupportedException("This SSC track requires the same body frame in every phase.");
            attitude = directions;
            phases[index] = new(start, end, center, curve);
            previousEnd = end;
        }

        var result = attitude!.Value;
        return new(name, id, mesh, radius, phases, result.PrimaryAxis, result.Primary, result.SecondaryAxis, result.Secondary);
    }

    internal static (string Root, SscDefinition Definition) ReadDefinition(
        IEnumerable<string> catalogPaths, string assetRoot, string objectPath, CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(catalogPaths);
        string root = Path.GetFullPath(assetRoot);
        var definition = SscDefinitions.Load(catalogPaths, root, cancellation)
            .FirstOrDefault(item => item.Names.Split(':').Any(alias => SamePath(item.Parent + "/" + alias, objectPath)));
        return (root, definition ?? throw new InvalidDataException($"Expected an SSC definition for {objectPath}."));
    }

    private static (FrameAxis, RelativeDirection) ReadDirection(SscValue.Map vector, string parent, string names, ObjectId self)
    {
        SignedAxis axis = SignedAxis.Parse(vector.Text("Axis"));
        bool position = vector.Fields.ContainsKey("RelativePosition");
        if (position == vector.Fields.ContainsKey("RelativeVelocity"))
            throw new InvalidDataException("SSC frame vector requires exactly one relative direction.");
        SscValue.Map data = vector.Object(position ? "RelativePosition" : "RelativeVelocity");
        var direction = new RelativeDirection(ResolveObject(data.Text("Observer"), parent, names, self),
            ResolveObject(data.Text("Target"), parent, names, self), position ? DirectionQuantity.Position : DirectionQuantity.Velocity);
        return ((FrameAxis)(axis.Index * 2 + (axis.Sign < 0 ? 1 : 0)), direction);
    }

    private static ObjectId ResolveObject(string path, string parent, string names, ObjectId self)
    {
        if (names.Split(':').Any(alias => SamePath(path, parent + "/" + alias)))
            return self;
        return path.ToLowerInvariant() switch
        {
            "sol" => SolarSystemSample.SunId,
            "sol/earth" => SolarSystemSample.EarthId,
            "sol/saturn" => SolarSystemSample.SaturnBarycenterId,
            _ => throw new NotSupportedException($"Unsupported SSC frame object {path}.")
        };
    }

    private static bool SamePath(string left, string right) => left.Equals(right, StringComparison.OrdinalIgnoreCase);

    internal static JulianInstant ReadDate(string value)
    {
        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double day) && double.IsFinite(day))
            return JulianInstant.FromJulianDate(day, TimeScale.Tdb);
        if (!DateTime.TryParseExact(value, DateFormats, CultureInfo.InvariantCulture,
                DateTimeStyles.AllowWhiteSpaces, out var date))
            throw new InvalidDataException($"Invalid SSC phase date {value}.");
        return UnixEpoch.AddNanoseconds((Int128)(date.Ticks - DateTime.UnixEpoch.Ticks) * 100);
    }

    internal static string FindAsset(AddonStage stage, string root, string directory, string relative, params string[] fallbacks) =>
        AddonDiagnostics.Read(stage, Path.Combine(directory, relative), () => FindAssetCore(root, directory, relative, fallbacks));

    private static string FindAssetCore(string root, string directory, string relative, string[] fallbacks)
    {
        try
        {
            return LocalAddonFiles.ResolveRelative(root, directory, relative);
        }
        catch (FileNotFoundException)
        {
            string[] directories = SamePath(root, directory) ? [root] : [directory, root];
            foreach (string folder in fallbacks)
            {
                string candidate = Path.Combine(folder, relative);
                foreach (string baseDirectory in directories)
                {
                    try
                    {
                        return LocalAddonFiles.ResolveRelative(root, baseDirectory, candidate);
                    }
                    catch (FileNotFoundException)
                    {
                        // Try the next conventional catalogue resource directory.
                    }
                }
            }
            throw;
        }
    }
}
