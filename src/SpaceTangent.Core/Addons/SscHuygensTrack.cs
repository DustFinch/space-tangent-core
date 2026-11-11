// SPDX-License-Identifier: GPL-2.0-or-later
using System.Globalization;
using SpaceTangent.Contracts;
using SpaceTangent.Core.Orbits;
using SpaceTangent.Core.Spice;

namespace SpaceTangent.Core.Addons;

public sealed class SscHuygensTrack
{
    private readonly SampledStateTrajectory _descent;
    private readonly Vector3d _mount, _site;
    private readonly Basis3d _attachedRotation, _flightEquator, _landedRotation;
    private readonly JulianInstant _spinEpoch;
    private readonly double _spinPeriodSeconds;
    public ObjectId Id { get; }
    public ObjectId Carrier { get; }
    public static ObjectId TitanId { get; } = new("naif/606");
    public string MeshPath { get; }
    public Basis3d MeshToBody { get; }
    public JulianInstant Start { get; }
    public JulianInstant Separation { get; }
    public JulianInstant Landing { get; }

    private SscHuygensTrack(ObjectId id, ObjectId carrier, string mesh, Basis3d meshToBody, JulianInstant start, JulianInstant separation,
        JulianInstant landing, Vector3d mount, SampledStateTrajectory descent, Vector3d site,
        Basis3d attachedRotation, Basis3d flightEquator, Basis3d landedRotation, JulianInstant spinEpoch, double spinPeriodSeconds)
    {
        Id = id;
        Carrier = carrier;
        MeshPath = mesh;
        MeshToBody = meshToBody;
        Start = start;
        Separation = separation;
        Landing = landing;
        _mount = mount;
        _descent = descent;
        _site = site;
        _attachedRotation = attachedRotation;
        _flightEquator = flightEquator;
        _landedRotation = landedRotation;
        _spinEpoch = spinEpoch;
        _spinPeriodSeconds = spinPeriodSeconds;
    }

    public static SscHuygensTrack Load(string catalogPath, string assetRoot, SscSampledSpacecraft carrier,
        SpicePolynomialBodyFrame titan)
        => Load([catalogPath], assetRoot, carrier, titan);

    public static SscHuygensTrack Load(IEnumerable<string> catalogPaths, string assetRoot, SscSampledSpacecraft carrier,
        SpicePolynomialBodyFrame titan, CancellationToken cancellation = default)
    {
        if (titan.BodyId != 606)
            throw new ArgumentException("The landing frame must describe Titan.", nameof(titan));
        var (root, definition) = SscSampledCatalog.ReadDefinition(catalogPaths, assetRoot,
            "Sol/Saturn/Titan/Huygens", cancellation);
        var (names, parent, fields, _) = definition;
        if (fields.Text("Class") != "spacecraft" ||
            fields.Get("Timeline") is not SscValue.List { Items: [SscValue.Map attached, SscValue.Map flight, SscValue.Map landed] })
            throw new InvalidDataException("Huygens requires an attached, a flight and a landed phase.");

        JulianInstant start = SscSampledCatalog.ReadDate(attached.Text("Beginning"));
        JulianInstant separation = SscSampledCatalog.ReadDate(attached.Text("Ending"));
        JulianInstant landing = SscSampledCatalog.ReadDate(flight.Text("Ending"));
        if (start.CompareTo(separation) >= 0 || separation.CompareTo(landing) >= 0 ||
            flight.Fields.ContainsKey("Beginning") || landed.Fields.ContainsKey("Beginning") || landed.Fields.ContainsKey("Ending"))
            throw new InvalidDataException("Invalid Huygens timeline limits.");

        string attachedTo = attached.Object("OrbitFrame").Object("BodyFixed").Text("Center");
        string flightCenter = flight.Object("OrbitFrame").Object("EclipticJ2000").Text("Center");
        string landingCenter = landed.Object("OrbitFrame").Object("BodyFixed").Text("Center");
        if (!("ssc/" + attachedTo).Equals(carrier.Id.Value, StringComparison.OrdinalIgnoreCase) ||
            !flightCenter.Equals(parent, StringComparison.OrdinalIgnoreCase) || !landingCenter.Equals(parent, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The Huygens phase centers do not match Cassini and Titan.");
        var mount = Vector(attached.Get("FixedPosition"));
        var coordinates = Vector(landed.Object("FixedPosition").Get("Planetographic"));
        if (Math.Abs(coordinates.Y) > 90 || coordinates.Z <= -titan.RadiiKilometers.X)
            throw new InvalidDataException("The Huygens landing coordinates are invalid.");
        // SSC FixedPosition treats this tuple as a planetocentric direction on a reference sphere.
        const double Degrees = Math.PI / 180;
        double longitude = coordinates.X * Degrees, latitude = coordinates.Y * Degrees;
        double distance = titan.RadiiKilometers.X + coordinates.Z;
        var site = new Vector3d(Math.Cos(latitude) * Math.Cos(longitude), Math.Cos(latitude) * Math.Sin(longitude),
            Math.Sin(latitude)) * distance;

        SscValue.Map source = flight.Object("SampledTrajectory");
        if (source.Text("Interpolation") != "cubic" || source.Text("DoublePrecision") != "true")
            throw new NotSupportedException("The Huygens flight requires cubic double-precision samples.");
        string sourcePath = SscSampledCatalog.FindAsset(root, definition.DirectoryFor("Timeline"), source.Text("Source"),
            Path.Combine("models", "trajectories"), "data");
        if (!Path.GetExtension(sourcePath).Equals(".xyzv", StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException("The Huygens flight requires XYZV samples.");
        SampledStateTrajectory descent = SscSampledSpacecraft.ReadCurve(sourcePath);
        if (descent.Count == 0)
            throw new InvalidDataException("The Huygens flight has no trajectory samples.");

        string probePath = parent + "/" + names.Split(':')[0];
        if (!attached.Object("BodyFrame").Object("BodyFixed").Text("Center").Equals(attachedTo, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The attached Huygens attitude requires the carrier body frame.");
        CheckTwoVector(flight, probePath, "Sol/Saturn");
        CheckTwoVector(landed, probePath, parent);
        SscValue.Map spin = flight.Object("UniformRotation");
        double spinPeriod = Number(spin.Get("Period")) * 3600;
        if (spinPeriod <= 0 || !double.IsFinite(spinPeriod))
            throw new InvalidDataException("The Huygens spin period must be finite and positive.");
        JulianInstant spinEpoch = spin.Fields.TryGetValue("Epoch", out var sourceEpoch)
            ? SscSampledCatalog.ReadDate(sourceEpoch.AsText()) : JulianInstant.FromTdbSeconds(0);

        string mesh = SscSampledCatalog.FindAsset(root, definition.DirectoryFor("Mesh"), fields.Text("Mesh"), "models");
        return new(new("ssc/" + probePath.ToLowerInvariant()), carrier.Id, mesh, ReadMeshOrientation(fields),
            start, separation, landing, mount, descent, site, Rotation(attached.Object("FixedRotation"), Math.PI),
            Rotation(spin, Math.PI), Rotation(landed.Object("FixedRotation"), Math.PI), spinEpoch, spinPeriod);
    }

    internal Basis3d BodyToFrame(JulianInstant epoch)
    {
        if (epoch.CompareTo(Separation) < 0)
            return _attachedRotation;
        if (epoch.CompareTo(Landing) >= 0)
            return _landedRotation;

        double elapsed = (double)epoch.DifferenceNanoseconds(_spinEpoch) / JulianInstant.NanosecondsPerSecond;
        double angle = (elapsed % _spinPeriodSeconds) / _spinPeriodSeconds * Math.Tau;
        double sin = Math.Sin(angle), cos = Math.Cos(angle);
        return new(_flightEquator.X * cos + _flightEquator.Y * sin,
            _flightEquator.Y * cos - _flightEquator.X * sin, _flightEquator.Z);
    }

    internal bool TryLocalState(JulianInstant epoch, out ObjectId center, out ObjectId? rotatingFrame, out CartesianState state)
    {
        center = default;
        rotatingFrame = null;
        state = default;
        if (epoch.CompareTo(Start) < 0)
            return false;
        if (epoch.CompareTo(Separation) < 0)
        {
            center = Carrier;
            rotatingFrame = Carrier;
            state = new(_mount, default);
            return true;
        }

        center = TitanId;
        if (epoch.CompareTo(Landing) >= 0)
        {
            rotatingFrame = TitanId;
            state = new(_site, default);
            return true;
        }
        if (!_descent.TrySample(epoch, out var sample))
            return false;
        state = new(SscSampledSpacecraft.ToEquatorial(sample.PositionKilometers),
            SscSampledSpacecraft.ToEquatorial(sample.VelocityKilometersPerSecond));
        return true;
    }

    private static Vector3d Vector(SscValue value)
    {
        if (value is not SscValue.List { Items: [var x, var y, var z] })
            throw new InvalidDataException("Expected an SSC three-component vector.");
        return new(Number(x), Number(y), Number(z));
    }

    private static void CheckTwoVector(SscValue.Map phase, string probe, string target)
    {
        var frame = phase.Object("BodyFrame").Object("TwoVector");
        CheckDirection(frame.Object("Primary"), "z", "RelativePosition");
        CheckDirection(frame.Object("Secondary"), "y", "RelativeVelocity");
        if (!frame.Text("Center").Equals(probe, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The Huygens two-vector frame must be centered on the probe.");

        void CheckDirection(SscValue.Map entry, string axis, string quantity)
        {
            var direction = entry.Object(quantity);
            if (!entry.Text("Axis").Equals(axis, StringComparison.OrdinalIgnoreCase) ||
                !direction.Text("Observer").Equals(probe, StringComparison.OrdinalIgnoreCase) ||
                !direction.Text("Target").Equals(target, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Unsupported direction in the Huygens two-vector frame.");
        }
    }

    private static Basis3d Rotation(SscValue.Map rotation, double phase)
    {
        // Invert the SSC frame-to-body rotation and express it in astronomical Z-up axes.
        return SpiceEuler.Compose([-Angle("AscendingNode"), -Angle("Inclination"), -phase - Angle("MeridianAngle")], [3, 1, 3]);

        double Angle(string key) => rotation.Fields.TryGetValue(key, out var value) ? Number(value) * (Math.PI / 180) : 0;
    }

    private static Basis3d ReadMeshOrientation(SscValue.Map fields)
    {
        var geometry = Basis3d.Identity;
        if (fields.Fields.TryGetValue("Orientation", out var orientation))
        {
            if (orientation is not SscValue.List { Items: [var angle, var x, var y, var z] })
                throw new InvalidDataException("Expected an SSC mesh rotation angle and axis.");
            var axis = new Vector3d(Number(x), Number(y), Number(z));
            double length = axis.Length;
            if (length == 0 || !double.IsFinite(length))
                throw new InvalidDataException("Invalid SSC mesh rotation axis.");

            // The SSC quaternion maps body coordinates into geometry coordinates.
            axis /= length;
            double radians = -Number(angle) * (Math.PI / 180);
            double c = Math.Cos(radians), s = Math.Sin(radians);
            geometry = new(Rotate(geometry.X), Rotate(geometry.Y), Rotate(geometry.Z));

            Vector3d Rotate(Vector3d vector) => vector * c + Vector3d.Cross(axis, vector) * s +
                axis * (Vector3d.Dot(axis, vector) * (1 - c));
        }
        return new(ToAstronomical(geometry.X), ToAstronomical(geometry.Y), ToAstronomical(geometry.Z));

        static Vector3d ToAstronomical(Vector3d vector) => new(vector.X, -vector.Z, vector.Y);
    }

    private static double Number(SscValue value) =>
        double.TryParse(value.AsText(), NumberStyles.Float, CultureInfo.InvariantCulture, out double result) && double.IsFinite(result)
            ? result : throw new InvalidDataException("Expected a finite SSC number.");
}
