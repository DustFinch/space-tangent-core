// SPDX-License-Identifier: GPL-2.0-or-later
using System.Security.Cryptography;
using SpaceTangent.Contracts;
using SpaceTangent.Core.Addons;
using Xunit;

namespace SpaceTangent.Core.Tests;

public sealed class ScienceSatelliteCatalogTests
{
    [ScienceCatalogFact]
    public void LoadsTheOriginalScienceCatalogAndEvaluatesBothAttitudes()
    {
        string root = Environment.GetEnvironmentVariable("SPACETANGENT_SCIENCE_ROOT")!;
        string catalog = Path.Combine(root, "catalog", "sciencesat.json");
        Assert.Equal("A07945CC9BD3E1ED38EED76814FE3F3CD39715A7D28EFFF3548D49A79AA83B51",
            Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(catalog))));
        IReadOnlyList<ScienceSatellite> satellites = ScienceSatelliteCatalog.Load(catalog, root);

        Assert.Collection(satellites, jason =>
        {
            Assert.Equal(new ObjectId("norad/26997"), jason.Id);
            Assert.Equal("Jason-1", jason.Name);
            Assert.Equal(Path.Combine(root, "data", "models", "jason.obj"), jason.MeshPath);
            Assert.True(jason.TrySample(jason.Epoch, out var state));
            Assert.Equal(Basis3d.Identity, state.MeshToBody);
            var expected = new Vector3d(4224.4245982988114, -6459.9662457795475, -0.47199931797623285);
            Assert.InRange((state.EarthRelativeState.PositionKilometers - expected).Length, 0, 1e-5);
            CheckAxes(state, state.BodyToTrajectory.X * -1, state.BodyToTrajectory.Y * -1);
        }, sorce =>
        {
            Assert.Equal(new ObjectId("norad/27651"), sorce.Id);
            Assert.Equal("SORCE", sorce.Name);
            Assert.Equal(Path.Combine(root, "catalog", "sorce.obj"), sorce.MeshPath);
            Assert.True(sorce.TrySample(sorce.Epoch.AddSeconds(3600), out var state));
            Assert.NotEqual(Basis3d.Identity, state.MeshToBody);
            CheckAxes(state, state.BodyToTrajectory.Z, state.BodyToTrajectory.Y * -1);
        });
    }

    [Theory]
    [InlineData("../satellite.obj", "outside")]
    [InlineData("missing.obj", "missing")]
    public void EnforcesAddOnRootAndReportsMissingAssets(string mesh, string message)
    {
        DirectoryInfo folder = Directory.CreateTempSubdirectory("SpaceTangent-catalog-");
        try
        {
            string path = Path.Combine(folder.FullName, "catalog.json");
            File.WriteAllText(path, $$$"""
                {"version":"1.0","items":[{
                    "name":"Probe","center":"Earth","trajectoryFrame":"EquatorJ2000",
                    "trajectory":{"type":"TLE",
                       "line1":"1 26997U 01055A   11192.81984987 -.00000038  00000-0  10000-3 0  3699",
                       "line2":"2 26997  66.0463 303.1838 0007562 261.9351  98.0768 12.80928955448691"},
                    "bodyFrame":{"type":"TwoVector","primaryAxis":"-x","secondaryAxis":"-y",
                        "primary":{"type":"RelativePosition","observer":"Probe","target":"Earth"},
                        "secondary":{"type":"RelativeVelocity","observer":"Probe","target":"Earth"}},
                    "geometry":{"type":"Mesh","source":"{{{mesh}}}","size":0.01}
                }]}
                """);
            Assert.Contains(message, Assert.Throws<InvalidDataException>(() =>
                ScienceSatelliteCatalog.Load(path, folder.FullName)).Message);
        }
        finally
        {
            File.Delete(Path.Combine(folder.FullName, "catalog.json"));
            folder.Delete();
        }
    }

    private static void CheckAxes(ScienceSatelliteSample sample, Vector3d primary, Vector3d secondary)
    {
        Vector3d position = sample.EarthRelativeState.PositionKilometers;
        Vector3d velocity = sample.EarthRelativeState.VelocityKilometersPerSecond;
        Vector3d nadir = position / -position.Length;
        Vector3d guide = velocity * -1 - nadir * Dot(velocity * -1, nadir);
        Assert.InRange((primary - nadir).Length, 0, 1e-12);
        Assert.InRange((secondary - guide / guide.Length).Length, 0, 1e-12);
        Assert.InRange(Math.Abs(Dot(primary, secondary)), 0, 1e-12);
    }

    private static double Dot(Vector3d first, Vector3d second) =>
        first.X * second.X + first.Y * second.Y + first.Z * second.Z;

    private sealed class ScienceCatalogFactAttribute : FactAttribute
    {
        public ScienceCatalogFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SPACETANGENT_SCIENCE_ROOT")))
                Skip = "Set SPACETANGENT_SCIENCE_ROOT to the original science satellite add-on root.";
        }
    }
}
