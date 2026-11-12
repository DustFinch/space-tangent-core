// SPDX-License-Identifier: GPL-2.0-or-later
using SpaceTangent.Contracts;
using SpaceTangent.Core.Observing;
using SpaceTangent.Core.Orbits;

namespace SpaceTangent.Core.World;

public enum DemoWorldSurface { Star, Temperate, Barren, Volcanic, Desert, Ice, GasGiant }

public sealed record DemoAtmosphere(double HeightKilometers, Vector3d RayleighPerKilometer, double RayleighHeightKilometers,
    double MiePerKilometer, double MieHeightKilometers, double Asymmetry, Vector3d AbsorptionPerKilometer,
    Vector3d? MieAlbedo = null);

public sealed record DemoWorldBody(ObjectId Id, string Name, string NativeName, ObjectId? Parent, DemoWorldSurface Surface,
    double RadiusKilometers, double PolarRadiusKilometers, Vector3d Color, double LuminositySolar = 0,
    int TemperatureKelvin = 0, DemoAtmosphere? Atmosphere = null, double CloudBaseKilometers = 0,
    double CloudTopKilometers = 0, double RingInnerKilometers = 0, double RingOuterKilometers = 0);

public readonly record struct DemoWorldLight(ObjectId Star, Vector3d Direction, Vector3d Color,
    double AngularRadius, double VisibleFraction, double RelativeFlux);

/// <summary>The authored binary Tessera and single-star Lumen demonstration systems.</summary>
public static class DemoWorlds
{
    private const double Au = 149_597_870.7, SolarRadius = 695_700;
    private const double Degrees = Math.PI / 180, BinaryFraction = .85 / 1.85;
    private static readonly JulianInstant J2000 = new(2451545, 0, TimeScale.Tdb);
    private static readonly ObjectId TesseraCenter = new("demo/tessera-center");
    public static ObjectId TesseraA { get; } = new("demo/tessera-a");
    public static ObjectId TesseraB { get; } = new("demo/tessera-b");
    public static ObjectId Nova { get; } = new("demo/nova");
    public static ObjectId Selena { get; } = new("demo/selena");
    public static ObjectId Lumen { get; } = new("demo/lumen");
    public static ObjectId Pyra { get; } = new("demo/pyra");
    public static ObjectId Aridus { get; } = new("demo/aridus");
    public static ObjectId Glacies { get; } = new("demo/glacies");
    public static ObjectId Zephyr { get; } = new("demo/zephyr");

    private sealed record Definition(DemoWorldBody Body, ObjectId? Center, ConicOrbit? Orbit, double OrbitScale,
        double SpinPeriodDays, double Tilt, double Node);

    private static readonly ConicOrbit Binary = Orbit(27.71064176455162, .22 * Au, .12, 35, 70, 40, 0);
    private static readonly Definition[] Definitions =
    [
        new(new(TesseraA, "Tessera-A", "Тессера A", null, DemoWorldSurface.Star, 1.02 * SolarRadius,
            1.02 * SolarRadius, new(1, .93, .87), 1.10, 5850), TesseraCenter, Binary, -BinaryFraction, 24, 35, 70),
        new(new(TesseraB, "Tessera-B", "Тессера B", TesseraA, DemoWorldSurface.Star, .84 * SolarRadius,
            .84 * SolarRadius, new(1, .8, .6), .45, 5150), TesseraCenter, Binary, 1 - BinaryFraction, 18, 35, 70),
        new(new(Nova, "Nova", "Нова", TesseraA, DemoWorldSurface.Temperate, 5900, 5880, new(.62, .72, .9),
            Atmosphere: new(60, new(.0058, .0135, .0331), 8, .025, 1.5, .76, default),
            CloudBaseKilometers: 7, CloudTopKilometers: 12), TesseraCenter,
            Orbit(370.8043247867439, 1.24 * Au, .02, 35.4, 70, 110, 200), 1, 26.4 / 24, 55, 85),
        new(new(Selena, "Selena", "Селена", Nova, DemoWorldSurface.Barren, 1200, 1200, new(.72, .69, .66)), Nova,
            Orbit(18.46537308901845, 280_000, .03, 36.9, 70, 30, 0), 1, 18.46537308901845, 36.9, 70),
        new(new(Lumen, "Lumen", "Люмен", null, DemoWorldSurface.Star, .92 * SolarRadius,
            .92 * SolarRadius, new(1, .95, .86), .75, 5600), null, null, 0, 27, 12, 200),
        new(new(Pyra, "Pyra", "Пира", Lumen, DemoWorldSurface.Volcanic, 4300, 4290, new(.45, .38, .25),
            Atmosphere: new(100, new(.0012, .0028, .0068), 12, .02, 12, .7, new(0, .002, .006), new(.97, .85, .55))), Lumen,
            Orbit(68.93206334153751, .32 * Au, .03, 13.1, 200, 20, 40), 1, 41d / 24, 20, 230),
        new(new(Aridus, "Aridus", "Аридус", Lumen, DemoWorldSurface.Desert, 5200, 5185, new(.85, .62, .42),
            Atmosphere: new(100, new(.00174, .00407, .00993), 11, .03, 11, .7, default, new(.97, .9, .7))), Lumen,
            Orbit(352.59952171790155, .95 * Au, .05, 11.3, 200, 140, 250), 1, 30.5 / 24, 36, 180),
        new(new(Glacies, "Glacies", "Гляциес", Lumen, DemoWorldSurface.Ice, 3700, 3695, new(.85, .9, .95),
            Atmosphere: new(80, new(.00035, .00081, .002), 9, .002, 6, .7, default),
            CloudBaseKilometers: 4, CloudTopKilometers: 6), Lumen,
            Orbit(1242.5970898555138, 2.2 * Au, .08, 14.3, 200, 300, 120), 1, 19d / 24, 23, 260),
        new(new(Zephyr, "Zephyr", "Зефир", Lumen, DemoWorldSurface.GasGiant, 64_000, 60_500, new(.85, .75, .6),
            Atmosphere: new(170, new(.00107, .00254, .00639), 17.5, .001, 17.5, .6, new(.00024, .00003, 0)),
            RingInnerKilometers: 85_000, RingOuterKilometers: 150_000), Lumen,
            Orbit(4778.4489503599025, 5.4 * Au, .04, 12.5, 200, 75, 300), 1, 11.2 / 24, 30, 210)
    ];
    private static readonly Dictionary<ObjectId, Definition> ById = Definitions.ToDictionary(item => item.Body.Id);
    private static readonly ObjectId[] BinaryStars = [TesseraA, TesseraB];
    private static readonly ObjectId[] LumenStar = [Lumen];

    public static IReadOnlyList<DemoWorldBody> Bodies { get; } = Array.AsReadOnly(Definitions.Select(item => item.Body).ToArray());
    public static DemoWorldBody? Find(ObjectId id) => ById.GetValueOrDefault(id)?.Body;

    public static void AddTo(WorldGraphBuilder graph)
    {
        ArgumentNullException.ThrowIfNull(graph);
        graph.AddTrajectory(TesseraCenter, _ => Fixed(new(82.39295868539722, 52.43188279979823, -113.85208836527616)));
        foreach (Definition item in Definitions)
        {
            var id = item.Body.Id;
            graph.AddTrajectory(id, at =>
            {
                if (item.Orbit is not { } orbit)
                    return Fixed(new(-38, 21, 49));
                var sample = orbit.Evaluate(at);
                return new CartesianState(ToEquatorial(sample.PositionKilometers * item.OrbitScale),
                    ToEquatorial(sample.VelocityKilometersPerSecond * item.OrbitScale));
            }, item.Center);
            graph.AddOrientation(id, at => Spin(item, at));
        }
    }

    public static IReadOnlyList<DemoWorldLight> Lights(WorldSnapshot world, ObjectId body, Vector3d surfaceOffset = default)
    {
        if (Find(body) is not { LuminositySolar: 0 } || !world.TryState(body, out var receiver))
            return [];
        Vector3d origin = receiver.PositionKilometers + surfaceOffset;
        ObjectId[] stars = body == Nova || body == Selena ? BinaryStars : LumenStar;
        var light = new List<DemoWorldLight>(stars.Length);
        double total = 0;
        foreach (ObjectId star in stars)
        {
            if (!world.TryState(star, out var source))
                continue;
            var lamp = Find(star)!;
            Vector3d ray = source.PositionKilometers - origin;
            double distance = ray.Length;
            if (!(distance > lamp.RadiusKilometers))
                continue;
            double visible = 1;
            foreach (ObjectId other in stars)
                if (other != star && world.TryState(other, out var blocker))
                    visible *= SolarOccultation.VisibleFraction(ray, lamp.RadiusKilometers,
                        blocker.PositionKilometers - origin, Find(other)!.RadiusKilometers);
            if (body == Nova && world.TryState(Selena, out var moon))
                visible *= SolarOccultation.VisibleFraction(ray, lamp.RadiusKilometers,
                    moon.PositionKilometers - origin, Find(Selena)!.RadiusKilometers);
            double flux = lamp.LuminositySolar / Math.Pow(distance / Au, 2);
            total += flux;
            light.Add(new(star, ray / distance, lamp.Color, Math.Asin(lamp.RadiusKilometers / distance), visible, flux));
        }
        return total > 0 ? light.Select(item => item with { RelativeFlux = item.RelativeFlux * item.VisibleFraction / total }).ToArray() : [];
    }

    private static ConicOrbit Orbit(double periodDays, double radius, double eccentricity, double inclination,
        double node, double periapsis, double anomaly)
    {
        double period = periodDays * JulianInstant.SecondsPerDay;
        return new(new(radius * (1 - eccentricity), eccentricity, inclination * Degrees, node * Degrees,
            periapsis * Degrees, J2000.AddSeconds(-period * anomaly / 360), Math.Pow(Math.Tau / period, 2) * Math.Pow(radius, 3), new("eclipj2000")));
    }

    private static CartesianState Fixed(Vector3d legacyLightYears) => new(
        ToEquatorial(new Vector3d(legacyLightYears.X, -legacyLightYears.Z, legacyLightYears.Y) * StellarEntry.KilometersPerLightYear), default);

    private static FrameKinematics Spin(Definition item, JulianInstant epoch)
    {
        double period = item.SpinPeriodDays * JulianInstant.SecondsPerDay;
        double elapsed = (double)epoch.DifferenceNanoseconds(J2000) / JulianInstant.NanosecondsPerSecond;
        double longitude = Math.PI + Math.IEEERemainder(elapsed, period) * Math.Tau / period;
        var (st, ct) = Math.SinCos(item.Tilt * Degrees);
        var (sn, cn) = Math.SinCos(item.Node * Degrees);
        var (sl, cl) = Math.SinCos(longitude);
        var axes = new Basis3d(ToEquatorial(new(cn * cl - sn * ct * sl, sn * cl + cn * ct * sl, st * sl)),
            ToEquatorial(new(-cn * sl - sn * ct * cl, -sn * sl + cn * ct * cl, st * cl)),
            ToEquatorial(new(sn * st, -cn * st, ct)));
        return new(axes, axes.Z * (Math.Tau / period));
    }

    private static Vector3d ToEquatorial(Vector3d ecliptic)
    {
        var (sin, cos) = Math.SinCos(23.4392911 * Degrees);
        return new(ecliptic.X, ecliptic.Y * cos - ecliptic.Z * sin, ecliptic.Y * sin + ecliptic.Z * cos);
    }
}
