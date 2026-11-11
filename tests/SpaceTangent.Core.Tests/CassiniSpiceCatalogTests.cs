// SPDX-License-Identifier: GPL-2.0-or-later
using System.Globalization;
using System.Security.Cryptography;
using SpaceTangent.Contracts;
using SpaceTangent.Core.Addons;
using SpaceTangent.Core.Spice;
using Xunit;

namespace SpaceTangent.Core.Tests;

public sealed class CassiniSpiceCatalogTests
{
    [CassiniFact]
    public void LoadsOriginalSscAndMatchesCassiniStatesThroughCoverageEdges()
    {
        string directory = Environment.GetEnvironmentVariable("SPACETANGENT_CASSINI_SSC_ROOT")!;
        string path = Path.Combine(directory, "cassini.ssc");
        Assert.Equal("14DE7A143DB435FB862D8C7FD34CDF923595F13B717EFCC29603C4A5E06F5A83",
            Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))));
        SscSpiceObject cassini = Assert.Single(SscSpiceCatalog.Load(path, directory));
        Assert.Equal("Cassini-SSC", cassini.Name);
        Assert.Equal("Sol/Saturn", cassini.Parent);
        Assert.Equal(-82, cassini.Target);
        Assert.Equal(6, cassini.Center);
        Assert.Equal("CASSINI_SC_COORD", cassini.AttitudeFrame);
        Assert.Equal("J2000", cassini.AttitudeBaseFrame);
        Assert.Equal(".3ds", Path.GetExtension(cassini.MeshPath));

        var reference = Path.Combine(AppContext.BaseDirectory, "Fixtures", "cassini-reference.csv");
        foreach (string line in File.ReadLines(reference).Where(line => !line.StartsWith('#')))
        {
            double[] data = line.Split(',').Select(value => double.Parse(value, CultureInfo.InvariantCulture)).ToArray();
            Assert.True(cassini.TrySample(JulianInstant.FromTdbSeconds(data[0]), out var state));
            var expectedPosition = new Vector3d(data[1], data[2], data[3]);
            var expectedVelocity = new Vector3d(data[4], data[5], data[6]);
            Assert.InRange((state.PositionKilometers - expectedPosition).Length, 0, 1e-6);
            Assert.InRange((state.VelocityKilometersPerSecond - expectedVelocity).Length, 0, 1e-9);
        }
        Assert.False(cassini.TrySample(JulianInstant.FromTdbSeconds(140556964.18453318).AddNanoseconds(-1), out _));
        Assert.False(cassini.TrySample(JulianInstant.FromTdbSeconds(141955264.18408903).AddNanoseconds(1), out _));
    }

    [CassiniFact]
    public void ReadsCassiniCkAndLeavesPointingGapsUnfilled()
    {
        string root = Environment.GetEnvironmentVariable("SPACETANGENT_CASSINI_SSC_ROOT")!;
        SscSpiceObject cassini = Assert.Single(SscSpiceCatalog.Load(Path.Combine(root, "cassini.ssc"), root));
        string path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "cassini-ck-reference.csv");
        foreach (string line in File.ReadLines(path).Where(line => !line.StartsWith('#')))
        {
            double[] data = line.Split(',').Select(value => double.Parse(value, CultureInfo.InvariantCulture)).ToArray();
            bool found = cassini.TryAttitude(JulianInstant.FromTdbSeconds(data[0]), out var body);
            Assert.Equal(data[1] == 1, found);
            if (!found)
                continue;
            Assert.InRange((body.X - new Vector3d(data[2], data[3], data[4])).Length, 0, 3e-8);
            Assert.InRange((body.Y - new Vector3d(data[5], data[6], data[7])).Length, 0, 3e-8);
            Assert.InRange((body.Z - new Vector3d(data[8], data[9], data[10])).Length, 0, 3e-8);
        }
    }

    [Fact]
    public void RejectsInvalidBinaryKernelAndMalformedCatalog()
    {
        string path = Path.GetTempFileName();
        try
        {
            Assert.Throws<InvalidDataException>(() => new SpkKernel(path));
            File.WriteAllText(path, """
                "Spacecraft" "Sol/Earth" { # missing a value
                  Class }
                """);
            InvalidDataException exception = Assert.Throws<InvalidDataException>(() =>
                SscSpiceCatalog.Load(path, Path.GetDirectoryName(path)!));
            Assert.Contains("SSC line 2", exception.Message);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private sealed class CassiniFactAttribute : FactAttribute
    {
        public CassiniFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SPACETANGENT_CASSINI_SSC_ROOT")))
                Skip = "Set SPACETANGENT_CASSINI_SSC_ROOT to the Cassini SSC and kernel directory.";
        }
    }
}
