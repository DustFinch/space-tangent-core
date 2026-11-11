// SPDX-License-Identifier: GPL-2.0-or-later
using System.Globalization;
using SpaceTangent.Contracts;
using SpaceTangent.Core.Orbits;

namespace SpaceTangent.Core.Addons;

public readonly record struct SscOrbitContext(FrameId Frame, double KilometersPerDistanceUnit, double SecondsPerPeriodUnit);
public enum SscConicAxes { ParentDefault, EclipticJ2000, EquatorJ2000, MeanEquator, BodyFixed }
public readonly record struct SscConicReference(SscConicAxes Axes, string Center, string? PoleObject, JulianInstant? Freeze);
public sealed record SscConicOrbit(string Name, string Parent, JulianInstant ElementEpoch, ConicOrbit Orbit)
{
    public SscConicReference Reference { get; init; } = new(SscConicAxes.ParentDefault, Parent, null, null);
}

/// <summary>Reads the static EllipticalOrbit blocks of an SSC catalogue.</summary>
public static class SscConicOrbits
{
    public static IReadOnlyList<SscConicOrbit> Read(string path, Func<string, SscOrbitContext> resolveParent)
        => Read([path], resolveParent);

    public static IReadOnlyList<SscConicOrbit> Read(IEnumerable<string> catalogPaths, Func<string, SscOrbitContext> resolveParent)
    {
        ArgumentNullException.ThrowIfNull(catalogPaths);
        ArgumentNullException.ThrowIfNull(resolveParent);
        var result = new List<SscConicOrbit>();
        foreach (var entry in SscDefinitions.Load(catalogPaths, root: null))
        {
            if (entry.Properties.Fields.ContainsKey("Timeline") ||
                !entry.Properties.Fields.TryGetValue("EllipticalOrbit", out var block))
                continue;
            result.Add(ReadOrbit(entry, block, resolveParent(entry.Parent)));
        }
        return result.AsReadOnly();
    }

    /// <summary>Reads one named orbit and resolves its SSC frame declaration, including separate origin and pole objects.</summary>
    public static SscConicOrbit ReadBody(IEnumerable<string> catalogPaths, string objectPath,
        Func<SscConicReference, SscOrbitContext> resolveFrame)
    {
        ArgumentNullException.ThrowIfNull(catalogPaths);
        ArgumentNullException.ThrowIfNull(resolveFrame);
        SscDefinition? entry = SscDefinitions.Load(catalogPaths, root: null).FirstOrDefault(body =>
            body.Names.Split(':').Any(alias => (body.Parent + "/" + alias).Equals(objectPath, StringComparison.OrdinalIgnoreCase)));
        if (entry is null || entry.Properties.Fields.ContainsKey("Timeline"))
            throw new InvalidDataException($"Expected a static SSC orbit for {objectPath}.");

        var reference = ReadReference(entry);
        return ReadOrbit(entry, entry.Properties.Get("EllipticalOrbit"), resolveFrame(reference)) with { Reference = reference };
    }

    private static SscConicReference ReadReference(SscDefinition entry)
    {
        if (!entry.Properties.Fields.TryGetValue("OrbitFrame", out var field))
            return new(SscConicAxes.ParentDefault, entry.Parent, null, null);
        if (field is not SscValue.Map { Fields.Count: 1 } frame)
            throw new InvalidDataException("SSC OrbitFrame requires one frame definition.");
        var (name, definition) = frame.Fields.Single();
        var axes = name switch
        {
            "EclipticJ2000" => SscConicAxes.EclipticJ2000,
            "EquatorJ2000" => SscConicAxes.EquatorJ2000,
            "MeanEquator" => SscConicAxes.MeanEquator,
            "BodyFixed" => SscConicAxes.BodyFixed,
            _ => throw new NotSupportedException($"Unsupported conic orbit frame {name}.")
        };
        if (definition is not SscValue.Map parameters)
            throw new InvalidDataException($"Expected an SSC frame definition for {name}.");
        string center = parameters.Fields.TryGetValue("Center", out var origin) ? origin.AsText() : entry.Parent;
        if (axes != SscConicAxes.MeanEquator)
            return new(axes, center, null, null);
        string pole = parameters.Fields.TryGetValue("Object", out var body) ? body.AsText() : center;
        JulianInstant? freeze = parameters.Fields.TryGetValue("Freeze", out var date)
            ? SscSampledCatalog.ReadDate(date.AsText()) : null;
        return new(axes, center, pole, freeze);
    }

    private static SscConicOrbit ReadOrbit(SscDefinition entry, SscValue block, SscOrbitContext context)
    {
        if (block is not SscValue.Map orbit)
            throw new InvalidDataException("Expected an SSC EllipticalOrbit object.");
        double a = Number(orbit, "SemiMajorAxis") * context.KilometersPerDistanceUnit;
        double period = Number(orbit, "Period") * context.SecondsPerPeriodUnit;
        double e = Number(orbit, "Eccentricity", 0);
        if (!(double.IsFinite(a) && a > 0 && double.IsFinite(period) && period > 0 && e >= 0 && e < 1))
            throw new InvalidDataException("SSC elliptical elements or unit scales are invalid.");
        double motion = Math.Tau / period;
        double mu = motion * motion * a * a * a;
        double radians = Math.PI / 180;
        JulianInstant epoch = JulianInstant.FromJulianDate(Number(orbit, "Epoch", JulianInstant.J2000Day), TimeScale.Tdb);
        double mean = Math.IEEERemainder(Number(orbit, "MeanAnomaly", 0) * radians, Math.Tau);
        JulianInstant periapsis = epoch.AddSeconds(-mean / motion);
        double argument = orbit.Fields.ContainsKey("ArgOfPericenter")
            ? Number(orbit, "ArgOfPericenter")
            : Number(orbit, "ArgOfPeriapsis", 0);
        var elements = new ConicElements(a * (1 - e), e, Number(orbit, "Inclination", 0) * radians,
            Number(orbit, "AscendingNode", 0) * radians, argument * radians, periapsis, mu, context.Frame);
        return new(entry.Names, entry.Parent, epoch, new ConicOrbit(elements));
    }

    private static double Number(SscValue.Map map, string name, double? fallback = null)
    {
        if (!map.Fields.TryGetValue(name, out var value))
            return fallback ?? throw new InvalidDataException($"Missing SSC orbital element {name}.");
        if (!double.TryParse(value.AsText(), NumberStyles.Float, CultureInfo.InvariantCulture, out double number) ||
            !double.IsFinite(number))
            throw new InvalidDataException($"Invalid SSC orbital element {name}.");
        return number;
    }
}
