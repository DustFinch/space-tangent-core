// SPDX-License-Identifier: GPL-2.0-or-later
using System.Text.Json;
using SpaceTangent.Contracts;
using SpaceTangent.Core.Coordinates;
using SpaceTangent.Core.World;
using SpaceTangent.Core.Spice;

namespace SpaceTangent.Core.Addons;

public sealed record MissionShape(string? MeshPath, double RadiusKilometers, Basis3d MeshRotation, Vector3d MeshOffset, MissionSensor? Sensor = null)
{
    public IReadOnlyList<MissionParticleEmitter>? ParticleEmitters { get; init; }
    public Vector3d? EllipsoidRadiiKilometers { get; init; }
    public string? GlobeTexturePath { get; init; }
    public MissionAxesStyle? Axes { get; init; }
    public MissionVectorStyle? VectorOverlay { get; init; }
    public MissionSwarmStyle? Swarm { get; init; }
}
public sealed record MissionGeometrySpan(JulianInstant Start, JulianInstant End, MissionShape Shape);

public static partial class SampledMissionCatalog
{
    private static IReadOnlyList<MissionGeometrySpan> ReadGeometry(JsonElement value, string root, string directory,
        string name, JulianInstant start, JulianInstant end, Func<string, ObjectId> resolve, Action<string>? warning, CancellationToken cancellation, int depth = 0)
    {
        if (depth >= 16)
            throw new InvalidDataException("Mission geometry exceeds 16 nested levels.");
        string type = Text(value, "type");
        if (type == "Axes")
        {
            var axes = SourceAxes(value, MissionAxesKind.Geometry, "scale");
            return [new(start, end, AxesShape(axes))];
        }
        if (type is "PlaneVisualizer" or "DirectionVisualizer")
            return [new(start, end, ReadVectorShape(value, type == "PlaneVisualizer", resolve))];
        if (type == "Sensor")
            return [new(start, end, new(null, .001, Basis3d.Identity, default, ReadSensor(value, resolve)))];
        if (type == "KeplerianSwarm")
            return [new(start, end, ReadSwarm(value, root, directory, cancellation))];
        if (type == "ParticleSystem")
            return [new(start, end, ReadParticles(value, root, directory, warning))];
        if (type == "Mesh")
        {
            var mesh = ReadMesh(value, root, directory, name, warning);
            return [new(start, end, mesh)];
        }
        if (type == "Globe")
            return [new(start, end, ReadGlobe(value, root, directory, name, warning))];
        if (type != "TimeSwitched")
        {
            warning?.Invoke($"{name}: {type} geometry is not supported by the sampled mission reader.");
            return [];
        }

        var sequence = value.GetProperty("sequence");
        int count = sequence.GetArrayLength();
        if (count == 0)
            return [];
        if (count > 128)
            throw new InvalidDataException("A mission geometry sequence may contain at most 128 steps.");
        var times = new JulianInstant[count];
        var shapes = new IReadOnlyList<MissionGeometrySpan>[count];
        for (int index = 0; index < count; index++)
        {
            var step = sequence[index];
            if (!step.TryGetProperty("startTime", out _))
                throw new InvalidDataException("A mission geometry step is missing its start time.");
            times[index] = Date(step, "startTime", start);
            shapes[index] = step.TryGetProperty("geometry", out var geometry) && geometry.ValueKind == JsonValueKind.Object
                ? ReadGeometry(geometry, root, directory, name, start, end, resolve, warning, cancellation, depth + 1) : [];
        }

        // The source selects the first matching adjacent interval, or its last item when none match.
        JulianInstant[] boundaries = times.Where(time => time.CompareTo(start) > 0 && time.CompareTo(end) < 0)
            .Append(start).Append(end).Distinct().Order().ToArray();
        var spans = new List<MissionGeometrySpan>();
        for (int part = 0; part < boundaries.Length - 1; part++)
        {
            JulianInstant from = boundaries[part], until = boundaries[part + 1];
            int selected = count - 1;
            for (int index = 1; index < count; index++)
            {
                if (from.CompareTo(times[index - 1]) >= 0 && from.CompareTo(times[index]) < 0)
                {
                    selected = index - 1;
                    break;
                }
            }
            foreach (MissionGeometrySpan child in shapes[selected])
            {
                JulianInstant begin = from.CompareTo(child.Start) > 0 ? from : child.Start;
                JulianInstant finish = until.CompareTo(child.End) < 0 ? until : child.End;
                if (finish.CompareTo(begin) <= 0)
                    continue;
                if (spans.Count > 0 && spans[^1].End == begin && spans[^1].Shape == child.Shape)
                    spans[^1] = spans[^1] with { End = finish };
                else
                    spans.Add(new(begin, finish, child.Shape));
                if (spans.Count > 1024)
                    throw new InvalidDataException("A mission has too many distinct geometry spans.");
            }
        }
        return spans.AsReadOnly();
    }

    private static MissionShape AxesShape(MissionAxesStyle axes) => new(null,
        axes.ScaleKilometers * MissionAxesMesh.Build(axes.Kind).BoundingRadius, Basis3d.Identity, default) { Axes = axes };

    private static MissionAxesStyle SourceAxes(JsonElement style, MissionAxesKind kind, string field)
    {
        double size = style.TryGetProperty(field, out _) ? PackedPositive(style, field) : 1;
        if (!float.IsFinite((float)size))
            throw new InvalidDataException("A mission axes size exceeds its finite rendering range.");
        if (kind == MissionAxesKind.Geometry)
            size = (float)size;
        bool visible = !style.TryGetProperty("visible", out var shown) || shown.GetBoolean();
        return new(kind, size, false, visible);
    }

    private static bool IsVisualizer(JsonElement value) => value.TryGetProperty("type", out var type) && type.GetString() == "Visualizer";

    private static SampledMission ReadVisualizer(JsonElement item, Func<string, ObjectId> resolve, SpiceResources resources)
    {
        string tag = Text(item, "tag");
        ObjectId id = resolve(tag), owner = resolve(Text(item, "body"));
        var style = item.GetProperty("style");
        string type = Text(style, "type");
        if (type is "Plane" or "BodyDirection")
        {
            var overlay = ReadVectorShape(style, type == "Plane", resolve);
            MissionFrame orientation = Frame(style, "frame", resolve, tag, owner, resources);
            return Visualizer(overlay, orientation);
        }
        var kind = type == "BodyAxes" ? MissionAxesKind.BodyVisualizer : MissionAxesKind.FrameVisualizer;
        var axes = SourceAxes(style, kind, "size");
        if (kind == MissionAxesKind.FrameVisualizer)
            axes = axes with { ReferenceOwner = owner };
        MissionFrame frame = kind == MissionAxesKind.BodyVisualizer ? new MissionFrame.BodyFixed(owner) : Equatorial;
        return Visualizer(AxesShape(axes), frame);

        SampledMission Visualizer(MissionShape shape, MissionFrame orientation) =>
            new(id, tag, null, shape.RadiusKilometers, [new(Minimum, Maximum, owner, Constant(default), Equatorial, orientation)])
            {
                Geometry = [new(Minimum, Maximum, shape)]
            };
    }

    private static MissionShape ReadGlobe(JsonElement value, string root, string directory, string name, Action<string>? warning)
    {
        Vector3d radii;
        if (value.TryGetProperty("radii", out var ellipsoid))
            radii = Vector(ellipsoid);
        else
        {
            double radius = Distance(value, "radius");
            radii = new(radius, radius, radius);
        }
        if (!radii.IsFinite || radii.X <= 0 || radii.Y <= 0 || radii.Z <= 0)
            throw new InvalidDataException($"{name}: a mission globe requires positive finite radii.");

        string? texture = null;
        if (value.TryGetProperty("baseMap", out var map))
        {
            if (map.ValueKind == JsonValueKind.String && map.GetString() is { Length: > 0 } source)
            {
                try { texture = LocalAddonFiles.ResolveRelative(root, directory, source); }
                catch (FileNotFoundException) { warning?.Invoke($"{name}: globe map {source} is missing; the shape is available."); }
            }
            else if (map.ValueKind == JsonValueKind.Object)
                warning?.Invoke($"{name}: a tiled globe map requires a supported local surface pack.");
        }
        return new(null, Math.Max(radii.X, Math.Max(radii.Y, radii.Z)), Basis3d.Identity, default)
        {
            EllipsoidRadiiKilometers = radii,
            GlobeTexturePath = texture
        };
    }

    private static MissionShape ReadMesh(JsonElement value, string root, string directory, string name, Action<string>? warning)
    {
        double radius = Distance(value, "size");
        if (!(radius > 0))
            throw new InvalidDataException($"{name}: the mesh size must be finite and positive.");
        string source = Text(value, "source");
        string? path;
        try { path = LocalAddonFiles.ResolveRelative(root, directory, source); }
        catch (FileNotFoundException)
        {
            path = null;
            warning?.Invoke($"{name}: mesh {source} is missing; its trajectory is available.");
        }
        Basis3d rotation = value.TryGetProperty("meshRotation", out var q) ? Quaternion(q) : Basis3d.Identity;
        Vector3d offset = value.TryGetProperty("meshOffset", out var given) ? Vector(given) : default;
        return new(path, radius, rotation, offset);
    }

    private static MissionFrame Rotation(MissionFrame parent, JsonElement item, SpiceResources resources,
        string root, string directory, CancellationToken cancellation)
    {
        if (!item.TryGetProperty("rotationModel", out var model))
            return parent;
        string kind = Text(model, "type");
        if (kind == "Spice")
            return SpiceOrientation(model.TryGetProperty("frame", out _) ? Text(model, "frame") : Text(model, "name"), resources);
        if (kind == "Interpolated")
        {
            string path = LocalAddonFiles.ResolveRelative(root, directory, Text(model, "source"));
            if (!Path.GetExtension(path).Equals(".q", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Interpolated mission attitude requires a local Q file.");
            string? convention = model.TryGetProperty("compatibility", out var compatibility) ? compatibility.GetString() : null;
            if (convention is not (null or "celestia"))
                throw new NotSupportedException($"Unsupported sampled mission compatibility {convention}.");
            var attitude = SampledMissionAttitude.Read(path, convention is not null, cancellation);
            return new MissionFrame.Rotating(parent, attitude.Sample);
        }
        double ascending = Angle(model, "ascendingNode"), inclination = Angle(model, "inclination"), meridian = Angle(model, "meridianAngle");
        var mean = Multiply(RotateZ(ascending), RotateX(inclination));
        if (kind == "Fixed")
        {
            Basis3d basis = model.TryGetProperty("quaternion", out var q) ? Quaternion(q) : Multiply(mean, RotateZ(meridian));
            return new MissionFrame.Rotating(parent, _ => new FrameKinematics(basis, default(Vector3d)));
        }
        if (kind == "Uniform")
        {
            double period = Seconds(model, "period", JulianInstant.SecondsPerDay);
            if (period == 0)
                throw new InvalidDataException("A uniform mission rotation period must be nonzero.");
            double rate = Math.Tau / period;
            var reference = Date(model, "epoch", J2000);
            return new MissionFrame.Rotating(parent, epoch =>
            {
                double seconds = (double)epoch.DifferenceNanoseconds(reference) / JulianInstant.NanosecondsPerSecond;
                return new(Multiply(mean, RotateZ(meridian + Math.IEEERemainder(seconds, period) * rate)), mean.Z * rate);
            });
        }
        throw new NotSupportedException($"Unsupported mission rotation {kind}.");
    }

    internal static Basis3d Multiply(Basis3d parent, Basis3d child) =>
        new(parent.Transform(child.X), parent.Transform(child.Y), parent.Transform(child.Z));

    private static Basis3d RotateZ(double angle)
    {
        var (sin, cos) = Math.SinCos(angle);
        return new(new(cos, sin, 0), new(-sin, cos, 0), new(0, 0, 1));
    }

    private static Basis3d RotateX(double angle)
    {
        var (sin, cos) = Math.SinCos(angle);
        return new(new(1, 0, 0), new(0, cos, sin), new(0, -sin, cos));
    }

    private static Basis3d Quaternion(JsonElement value)
    {
        if (value.GetArrayLength() != 4)
            throw new InvalidDataException("A mission quaternion requires four finite components.");
        double[] q = value.EnumerateArray().Select(item => item.GetDouble()).ToArray();
        double length = Math.Sqrt(q.Sum(component => component * component));
        if (!(length > 0) || !double.IsFinite(length))
            throw new InvalidDataException("A mission quaternion must be finite and nonzero.");
        return QuaternionBasis.FromRaw(q[0] / length, q[1] / length, q[2] / length, q[3] / length);
    }

    private static Vector3d Vector(JsonElement value)
    {
        if (value.GetArrayLength() != 3)
            throw new InvalidDataException("A mission vector requires three finite components.");
        var vector = new Vector3d(value[0].GetDouble(), value[1].GetDouble(), value[2].GetDouble());
        return vector.IsFinite ? vector : throw new InvalidDataException("A mission vector requires finite components.");
    }
}
