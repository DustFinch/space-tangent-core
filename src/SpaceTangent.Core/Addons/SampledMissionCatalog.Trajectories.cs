// SPDX-License-Identifier: GPL-2.0-or-later
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using SpaceTangent.Contracts;
using SpaceTangent.Core.Coordinates;
using SpaceTangent.Core.Ephemerides;
using SpaceTangent.Core.Orbits;
using SpaceTangent.Core.Spice;

namespace SpaceTangent.Core.Addons;

public static partial class SampledMissionCatalog
{
    private static readonly FrameId MissionSource = new("mission-source");
    private static readonly JulianInstant J2000 = new(JulianInstant.J2000Day, 0, TimeScale.Tdb);

    private static Func<JulianInstant, CartesianState?> ReadTrajectory(JsonElement value, string root, string directory,
        JulianInstant start, JulianInstant end, MissionFrame frame, SpiceResources resources, string center, int depth = 0)
    {
        if (depth >= 16)
            throw new InvalidDataException("A mission trajectory exceeds 16 nested levels.");
        string type = Text(value, "type");
        switch (type)
        {
            case "FixedPoint":
                double[] components = value.GetProperty("position").EnumerateArray().Select(component => component.GetDouble()).ToArray();
                if (components.Length != 3 || components.Any(component => !double.IsFinite(component)))
                    throw new InvalidDataException("A fixed mission point requires three finite components.");
                return Constant(new(components[0], components[1], components[2]));
            case "FixedSpherical":
                var (sinLat, cosLat) = Math.SinCos(Angle(value, "latitude"));
                var (sinLon, cosLon) = Math.SinCos(Angle(value, "longitude"));
                return Constant(new Vector3d(cosLat * cosLon, cosLat * sinLon, sinLat) * Distance(value, "radius"));
            case "Keplerian":
                return Keplerian(value);
            case "InterpolatedStates":
                string path = LocalAddonFiles.ResolveRelative(root, directory, Text(value, "source"));
                var curve = Path.GetExtension(path).ToLowerInvariant() switch
                {
                    ".xyzv" => SampledStateTrajectory.ReadXyzv(path, MissionSource),
                    ".xyz" => SampledStateTrajectory.ReadXyz(path, MissionSource),
                    _ => throw new InvalidDataException("A sampled mission trajectory requires a local XYZ or XYZV file.")
                };
                return epoch => curve.TrySample(epoch, out var state) ? state : null;
            case "ChebyshevPoly":
                var polynomial = ChebyshevTrajectory.Load(LocalAddonFiles.ResolveRelative(root, directory, Text(value, "source")));
                return epoch => polynomial.Sample(epoch);
            case "Composite":
                return Composite();
            case "LinearCombination":
                return Combination();
            case "Spice":
                return SpiceTrajectory(value, resources, center);
            case "Builtin":
                return BuiltinTrajectory(Text(value, "name"), frame);
            default:
                throw new NotSupportedException($"Unsupported mission trajectory {type}.");
        }

        Func<JulianInstant, CartesianState?> Composite()
        {
            var children = value.TryGetProperty("segments", out var list) ? list : value.GetProperty("arcs");
            if (children.GetArrayLength() is < 1 or > 128)
                throw new InvalidDataException("A composite trajectory requires one to 128 segments.");
            var segments = new List<(JulianInstant Start, JulianInstant End, Func<JulianInstant, CartesianState?> Sample)>();
            JulianInstant previous = Date(value, "startTime", start);
            for (int index = 0; index < children.GetArrayLength(); index++)
            {
                var segment = children[index];
                var begins = Date(segment, "startTime", previous);
                var fallback = index + 1 < children.GetArrayLength() ? Date(children[index + 1], "startTime", end) : end;
                var until = Date(segment, "endTime", fallback);
                if (until.CompareTo(end) > 0)
                    until = end;
                if (begins.CompareTo(previous) < 0 || until.CompareTo(begins) <= 0)
                    throw new InvalidDataException("Composite trajectory segments must be ordered and non-overlapping.");
                var nested = segment.TryGetProperty("trajectory", out var child) ? child : segment;
                segments.Add((begins, until, ReadTrajectory(nested, root, directory, begins, until, frame, resources, center, depth + 1)));
                previous = until;
            }
            return epoch =>
            {
                foreach (var segment in segments)
                    if (epoch.CompareTo(segment.Start) >= 0 && epoch.CompareTo(segment.End) < 0)
                        return segment.Sample(epoch);
                return null;
            };
        }

        Func<JulianInstant, CartesianState?> Combination()
        {
            var children = value.GetProperty("trajectories");
            var weights = value.GetProperty("weights");
            int count = children.GetArrayLength();
            if (count is < 2 or > 16 || count != weights.GetArrayLength())
                throw new InvalidDataException("A trajectory combination requires matching trajectories and weights.");
            var terms = children.EnumerateArray().Select((child, index) =>
                (Sample: ReadTrajectory(child, root, directory, start, end, frame, resources, center, depth + 1), Weight: weights[index].GetDouble())).ToArray();
            return WeightedTrajectory(terms);
        }
    }

    private static Func<JulianInstant, CartesianState?> BuiltinTrajectory(string name, MissionFrame frame)
    {
        if (frame != Equatorial || !TrySatelliteTheory(name, out var theory))
            throw new NotSupportedException($"Unsupported mission built-in trajectory {name}.");
        return epoch => AnalyticSatelliteOrbit.Sample(theory, epoch);
    }

    private static bool TrySatelliteTheory(string name, out SatelliteTheory theory) =>
        Enum.TryParse(name, ignoreCase: false, out theory) && Enum.IsDefined(theory) && theory.ToString() == name;

    private static Func<JulianInstant, CartesianState?> WeightedTrajectory(
        (Func<JulianInstant, CartesianState?> Sample, double Weight)[] terms)
    {
        if (terms.Any(term => !double.IsFinite(term.Weight)))
            throw new InvalidDataException("Trajectory combination weights must be finite.");
        return epoch =>
        {
            CartesianState state = default;
            foreach (var (sample, weight) in terms)
            {
                if (sample(epoch) is not { } component)
                    return null;
                state += new CartesianState(component.PositionKilometers * weight, component.VelocityKilometersPerSecond * weight);
            }
            return state;
        };
    }

    private static Func<JulianInstant, CartesianState?> Keplerian(JsonElement value)
    {
        double majorAxis = Distance(value, "semiMajorAxis");
        double eccentricity = value.TryGetProperty("eccentricity", out var given) ? given.GetDouble() : 0;
        double period = Seconds(value, "period", JulianInstant.SecondsPerDay);
        return Keplerian(majorAxis, eccentricity, period, Date(value, "epoch", J2000), Angle(value, "meanAnomaly"),
            Angle(value, "inclination"), Angle(value, "ascendingNode"), Angle(value, "argumentOfPeriapsis"));
    }

    private static Func<JulianInstant, CartesianState?> Keplerian(double majorAxis, double eccentricity, double period,
        JulianInstant reference, double anomaly, double inclination, double node, double periapsis)
    {
        if (majorAxis <= 0 || !double.IsFinite(eccentricity) || eccentricity is < 0 or >= 1 || period <= 0)
            throw new InvalidDataException("Invalid elliptical mission orbit.");
        double meanMotion = Math.Tau / period;
        double initial = anomaly / meanMotion;
        var orbit = new ConicOrbit(new(majorAxis * (1 - eccentricity), eccentricity, inclination, node, periapsis, reference,
            majorAxis * majorAxis * majorAxis * meanMotion * meanMotion, MissionSource));
        return epoch => orbit.EvaluateElapsedSeconds(initial +
            (double)epoch.DifferenceNanoseconds(reference) / JulianInstant.NanosecondsPerSecond);
    }

    private static Func<JulianInstant, CartesianState?> Constant(Vector3d point)
    {
        var state = new CartesianState(point, default);
        return _ => state;
    }

    private static double Angle(JsonElement value, string field) =>
        value.TryGetProperty(field, out var angle) ? angle.GetDouble() * (Math.PI / 180) : 0;

    private static double Distance(JsonElement value, string field) => Quantity(value.GetProperty(field), "km", field, units =>
        units switch { "km" => 1, "m" => .001, "cm" => 1e-5, "mm" => 1e-6, "au" => AstronomicalUnits.KilometersPerAstronomicalUnit, _ => 0 });

    private static double Seconds(JsonElement value, string field, double fallback) =>
        value.TryGetProperty(field, out var quantity) ? Quantity(quantity, "d", field, units => units switch
        {
            "ms" => .001, "s" => 1, "m" => 60, "h" => 3600, "d" => JulianInstant.SecondsPerDay,
            "y" or "a" => 365.25 * JulianInstant.SecondsPerDay, _ => 0
        }) : fallback;

    private static double Quantity(JsonElement value, string defaultUnit, string field, Func<string, double> factor)
    {
        if (value.ValueKind == JsonValueKind.Number)
        {
            double scaled = value.GetDouble() * factor(defaultUnit);
            return double.IsFinite(scaled) ? scaled : throw new InvalidDataException($"Invalid mission quantity {field}.");
        }
        var match = QuantityPattern().Match(value.GetString() ?? "");
        if (!match.Success || !double.TryParse(match.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double number))
            throw new InvalidDataException($"Invalid mission quantity {field}.");
        string unit = match.Groups[2].Success ? match.Groups[2].Value : defaultUnit;
        double multiplier = factor(unit);
        if (!(multiplier > 0) || !double.IsFinite(number * multiplier))
            throw new InvalidDataException($"Invalid units or magnitude for mission quantity {field}.");
        return number * multiplier;
    }

    [GeneratedRegex(@"^\s*([+-]?(?:\d+(?:\.\d*)?|\.\d+)(?:[eE][+-]?\d+)?)\s*([A-Za-z]+)?\s*$", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex QuantityPattern();
}
