// SPDX-License-Identifier: GPL-2.0-or-later
using System.Globalization;
using System.Text;
using SpaceTangent.Contracts;
using SpaceTangent.Core.Orbits;

namespace SpaceTangent.Core.Addons;

public readonly record struct SscOrbitContext(FrameId Frame, double KilometersPerDistanceUnit, double SecondsPerPeriodUnit);
public sealed record SscConicOrbit(string Name, string Parent, JulianInstant ElementEpoch, ConicOrbit Orbit);

/// <summary>Reads the static EllipticalOrbit blocks of an SSC catalogue.</summary>
public static class SscConicOrbits
{
    public static IReadOnlyList<SscConicOrbit> Read(string path, Func<string, SscOrbitContext> resolveParent)
    {
        ArgumentNullException.ThrowIfNull(resolveParent);
        using FileStream file = File.OpenRead(path);
        if (file.Length > 1024 * 1024)
            throw new InvalidDataException("SSC orbit catalogue exceeds 1 MiB.");
        using var reader = new StreamReader(file, new UTF8Encoding(false, true));
        var result = new List<SscConicOrbit>();
        foreach (var (name, parent, body) in new SscReader(reader.ReadToEnd()).ReadDefinitions())
        {
            if (!body.Fields.TryGetValue("EllipticalOrbit", out var block))
                continue;
            if (block is not SscValue.Map orbit)
                throw new InvalidDataException("Expected an SSC EllipticalOrbit object.");

            SscOrbitContext context = resolveParent(parent);
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
            result.Add(new(name, parent, epoch, new ConicOrbit(elements)));
        }
        return result.AsReadOnly();
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
