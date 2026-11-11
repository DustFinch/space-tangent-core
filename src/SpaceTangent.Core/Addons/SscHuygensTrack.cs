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
    public ObjectId Id { get; }
    public ObjectId Carrier { get; }
    public static ObjectId TitanId { get; } = new("naif/606");
    public string MeshPath { get; }
    public JulianInstant Start { get; }
    public JulianInstant Separation { get; }
    public JulianInstant Landing { get; }

    private SscHuygensTrack(ObjectId id, ObjectId carrier, string mesh, JulianInstant start, JulianInstant separation,
        JulianInstant landing, Vector3d mount, SampledStateTrajectory descent, Vector3d site)
    {
        Id = id;
        Carrier = carrier;
        MeshPath = mesh;
        Start = start;
        Separation = separation;
        Landing = landing;
        _mount = mount;
        _descent = descent;
        _site = site;
    }

    public static SscHuygensTrack Load(string catalogPath, string assetRoot, SscSampledSpacecraft carrier,
        SpicePolynomialBodyFrame titan)
    {
        if (titan.BodyId != 606)
            throw new ArgumentException("The landing frame must describe Titan.", nameof(titan));
        var (root, directory, names, parent, fields) = SscSampledCatalog.ReadDefinition(catalogPath, assetRoot,
            "Sol/Saturn/Titan/Huygens");
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
        string sourcePath = SscSampledCatalog.FindAsset(root, directory, source.Text("Source"),
            Path.Combine("models", "trajectories"), "data");
        if (!Path.GetExtension(sourcePath).Equals(".xyzv", StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException("The Huygens flight requires XYZV samples.");
        SampledStateTrajectory descent = SscSampledSpacecraft.ReadCurve(sourcePath);
        if (descent.Count == 0)
            throw new InvalidDataException("The Huygens flight has no trajectory samples.");

        string mesh = SscSampledCatalog.FindAsset(root, directory, fields.Text("Mesh"), "models");
        return new(new("ssc/" + (parent + "/" + names.Split(':')[0]).ToLowerInvariant()), carrier.Id, mesh,
            start, separation, landing, mount, descent, site);
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

    private static double Number(SscValue value) =>
        double.TryParse(value.AsText(), NumberStyles.Float, CultureInfo.InvariantCulture, out double result) && double.IsFinite(result)
            ? result : throw new InvalidDataException("Expected a finite SSC number.");
}
