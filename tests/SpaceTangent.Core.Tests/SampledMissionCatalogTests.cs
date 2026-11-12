// SPDX-License-Identifier: GPL-2.0-or-later
using SpaceTangent.Contracts;
using SpaceTangent.Core.Addons;
using SpaceTangent.Core.World;
using Xunit;

namespace SpaceTangent.Core.Tests;

public sealed class SampledMissionCatalogTests
{
    private static readonly ObjectId Sun = new("sol/sun"), Mars = new("sol/mars"), Vesta = new("sol/vesta");
    private static readonly Vector3d Spin = new(0, 0, .00007);

    [OriginalMissionsFact]
    [Trait("Category", "ExternalData")]
    public void OriginalMslAndDawnSelectTheCorrectArcAndMatchLegacyCruiseStates()
    {
        var messages = new List<string>();
        var msl = Load("msl", "SPACETANGENT_MSL_ROOT", messages);
        var dawn = Load("dawn", "SPACETANGENT_DAWN_ROOT", messages);
        Assert.Contains(messages, message => message.StartsWith("MSL: mesh", StringComparison.Ordinal));
        Assert.DoesNotContain(messages, message => message.Contains("ParticleSystem geometry is not supported", StringComparison.Ordinal));
        Assert.Equal(2, msl.Arcs.Count);
        Assert.Equal(3, dawn.Arcs.Count);
        Assert.Equal(2455892.161261574, msl.Start.ToJulianDate(), 8);

        var builder = BuildWorld();
        builder.AddSampledMission(msl);
        builder.AddSampledMission(dawn);
        var world = builder.Build();
        // Parent-relative samples from the archived simulator evaluated against the original JSON and XYZV.
        Check(msl, 2456000, Sun, new(-180080903.39383754, -5514431.514625985, -64626766.02592923),
            new(-13.980317398351223, -.0699758562384318, 22.078525290714317));
        Check(msl, 2456145.5, Sun, new(-129719929.41414447, -816187.2468269079, 189677537.633033),
            new(17.490024754962977, .6736831815774351, 11.851181037153477));
        Check(dawn, 2454800, Sun, new(-63117600.67895683, -3250314.855347193, 227067081.7664086),
            new(22.54116685743942, -.6125268654938123, 3.112852047316813));
        Check(dawn, 2455800, Vesta, new(-1097.703438086951, 1521.1275799839518, -2337.452666559715),
            new(-.03607413225540235, -.06251343629090389, -.02357088891549175));
        Check(dawn, 2456300, Sun, new(37504497.0686887, -14105384.96930257, -379776500.1584654),
            new(-17.886654878263727, 2.4229362319556964, -1.2906081842850534));

        var before = world.Evaluate(msl.Arcs[1].Start.AddNanoseconds(-1));
        Assert.True(before.TryStateCenter(msl.Id, out var center));
        Assert.Equal(Sun, center);
        var landed = world.Evaluate(msl.Arcs[1].Start);
        Assert.True(landed.TryStateCenter(msl.Id, out center));
        Assert.Equal(Mars, center);

        Assert.True(landed.TryMissionReferenceFrame(msl.Id, orbit: true, before.Epoch, out var oldOrigin, out var oldAxes));
        Assert.True(landed.TryMissionReferenceFrame(msl.Id, orbit: true, landed.Epoch, out var newOrigin, out var newAxes));
        Assert.Equal(default, oldOrigin.PositionKilometers);
        Assert.Equal(new Vector3d(1.7e8, 1e8, 1e7), newOrigin.PositionKilometers);
        Assert.InRange((oldAxes.ToWorld.Z - Ecliptic(new(0, 0, 1))).Length, 0, 1e-9);
        Assert.Equal(Basis3d.Identity, newAxes.ToWorld);
        Assert.Equal(Spin, newAxes.AngularVelocityRadiansPerSecond);
        Assert.False(landed.TryMissionReferenceFrame(msl.Id, orbit: true, msl.Start.AddNanoseconds(-1), out _, out _));
        Assert.True(landed.TryState(msl.Id, out var state));
        Assert.True(landed.TryState(Mars, out var mars));
        var local = new Vector3d(-2492.7174249882, 2283.7760531933, -246.57248398216);
        Assert.InRange(((state - mars).PositionKilometers - local).Length, 0, 1e-8);
        Assert.InRange(((state - mars).VelocityKilometersPerSecond - Vector3d.Cross(Spin, Ecliptic(local))).Length, 0, 1e-12);
        Assert.True(landed.TryOrientation(msl.Id, out var frame));
        Assert.True(landed.TryState(Sun, out var sun));
        Assert.True(landed.TryMissionReferenceFrame(msl.Id, orbit: false, landed.Epoch, out var bodyOrigin, out var bodyAxes));
        Assert.Equal(state, bodyOrigin);
        Assert.Equal(frame.ToWorld, bodyAxes.ToWorld);
        var direction = sun.PositionKilometers - state.PositionKilometers;
        Assert.InRange(Vector3d.Dot(frame.ToWorld.Z, direction / direction.Length), -1.0000000001, -.9999999999);
        Assert.False(world.Evaluate(msl.Start.AddNanoseconds(-1)).TryState(msl.Id, out _));
        Assert.False(world.Evaluate(msl.End).TryOrientation(msl.Id, out _));
        Assert.False(world.Evaluate(dawn.End).TryState(dawn.Id, out _));

        void Check(SampledMission mission, double day, ObjectId parent, Vector3d expectedPosition, Vector3d expectedVelocity)
        {
            var snapshot = world.Evaluate(JulianInstant.FromJulianDate(day, TimeScale.Tdb));
            Assert.True(snapshot.TryStateCenter(mission.Id, out var actual));
            Assert.Equal(parent, actual);
            Assert.True(snapshot.TryState(mission.Id, out var value));
            Assert.True(snapshot.TryState(parent, out var origin));
            Assert.True(snapshot.TryOrientation(mission.Id, out _));
            var relative = value - origin;
            Assert.InRange((relative.PositionKilometers - Ecliptic(Native(expectedPosition))).Length, 0, .0005);
            Assert.InRange((relative.VelocityKilometersPerSecond - Ecliptic(Native(expectedVelocity))).Length, 0, 1e-7);
        }
    }

    [Fact]
    public void XyzDerivesEndpointAndInteriorVelocitiesAndRejectsDuplicateEpochs()
    {
        string file = Path.GetTempFileName();
        try
        {
            File.WriteAllText(file, "2451545 0 0 0\n2451546 86400 0 0\n2451547 345600 0 0");
            var curve = SpaceTangent.Core.Orbits.SampledStateTrajectory.ReadXyz(file, new("test"));
            for (int day = 0; day <= 2; day++)
            {
                Assert.True(curve.TrySample(new(2451545 + day, 0, TimeScale.Tdb), out var state));
                Assert.Equal(new Vector3d(day + 1, 0, 0), state.VelocityKilometersPerSecond);
            }
            File.WriteAllText(file, "2451545 0 0 0\n2451545 1 0 0");
            Assert.Throws<InvalidDataException>(() => SpaceTangent.Core.Orbits.SampledStateTrajectory.ReadXyz(file, new("test")));
        }
        finally { File.Delete(file); }
    }

    private static SampledMission Load(string filename, string variable, List<string> messages)
    {
        string root = Environment.GetEnvironmentVariable(variable)!;
        var missions = SampledMissionCatalog.Load(filename + ".json", root, name => name switch
        {
            "Sun" => Sun,
            "Mars" => Mars,
            "Vesta" => Vesta,
            _ => null
        }, messages.Add);
        if (filename == "dawn")
        {
            var exhaust = Assert.Single(missions, mission => mission.Name == "_Dawn Engine 1");
            var emitter = Assert.Single(Assert.Single(exhaust.Geometry!).Shape.ParticleEmitters!);
            Assert.Equal(2000, emitter.SpawnRate);
        }
        return Assert.Single(missions, mission => mission.Name.Equals(filename, StringComparison.OrdinalIgnoreCase));
    }

    private static WorldGraphBuilder BuildWorld()
    {
        var world = new WorldGraphBuilder();
        world.AddTrajectory(Sun, _ => new(default, default));
        world.AddTrajectory(Mars, _ => new(new(1.7e8, 1e8, 1e7), new(-12, 17, .5)));
        world.AddOrientation(Mars, _ => new(Basis3d.Identity, Spin));
        world.AddTrajectory(Vesta, epoch => epoch.Day is >= 2455743 and <= 2456166
            ? new(new(-2.8e8, 1.5e8, 1.7e7), new(-10, -12, 1))
            : null);
        return world;
    }

    private static Vector3d Native(Vector3d vector) => new(vector.X, -vector.Z, vector.Y);
    private static Vector3d Ecliptic(Vector3d vector)
    {
        double radians = 23.43929111111111 * Math.PI / 180;
        (double sin, double cos) = Math.SinCos(radians);
        return new(vector.X, vector.Y * cos - vector.Z * sin, vector.Y * sin + vector.Z * cos);
    }

    private sealed class OriginalMissionsFactAttribute : FactAttribute
    {
        public OriginalMissionsFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SPACETANGENT_MSL_ROOT")) ||
                string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SPACETANGENT_DAWN_ROOT")))
                Skip = "Set the MSL and Dawn roots to the original mission JSON and trajectory files.";
        }
    }
}
