// SPDX-License-Identifier: GPL-2.0-or-later
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using SpaceTangent.Contracts;
using SpaceTangent.Core.Orbits;
using SpaceTangent.Core.Spice;
using Xunit;

namespace SpaceTangent.Core.Tests;

public sealed class PolynomialKernelContractTests : IDisposable
{
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("st-polynomial-");
    private static string Fixture(string extension) => Path.Combine(AppContext.BaseDirectory, "Fixtures", "equinoctial-spk-reference." + extension);

    [Fact]
    public void EquinoctialStatesMatchNativeSpiceAcrossPrecessionAndCoverageEndpoints()
    {
        using var reference = JsonDocument.Parse(File.ReadAllBytes(Fixture("json")));
        Assert.Equal(reference.RootElement.GetProperty("sha256").GetString(),
            Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Fixture("bin")))).ToLowerInvariant());
        var kernel = new SpkKernel(Fixture("bin"));
        int count = 0;
        foreach (var row in reference.RootElement.GetProperty("rows").EnumerateArray())
        {
            int target = row.GetProperty("target").GetInt32();
            var epoch = JulianInstant.FromTdbSeconds(row.GetProperty("seconds").GetDouble());
            Assert.True(kernel.TryState(target, 399, epoch, out var state));
            var expected = row.GetProperty("state");
            var p = new Vector3d(expected[0].GetDouble(), expected[1].GetDouble(), expected[2].GetDouble());
            var v = new Vector3d(expected[3].GetDouble(), expected[4].GetDouble(), expected[5].GetDouble());
            Assert.InRange((state.PositionKilometers - p).Length, 0, 2e-7);
            Assert.InRange((state.VelocityKilometersPerSecond - v).Length, 0, 2e-9);
            count++;
        }
        Assert.Equal(24, count);
        Assert.False(kernel.TryState(9000001, 399, JulianInstant.FromTdbSeconds(20001), out _));
        Assert.False(kernel.TryState(9000001, 10, JulianInstant.FromTdbSeconds(0), out _));
    }

    [Theory]
    [InlineData(1, -1d)] [InlineData(1, 0d)] [InlineData(2, 1d)]
    [InlineData(3, double.NaN)] [InlineData(10, double.PositiveInfinity)]
    public void EquinoctialDescriptorsRejectInvalidElements(int word, double value)
    {
        using var reference = JsonDocument.Parse(File.ReadAllBytes(Fixture("json")));
        byte[] bytes = File.ReadAllBytes(Fixture("bin"));
        int first = reference.RootElement.GetProperty("firstWord").GetInt32();
        BinaryPrimitives.WriteDoubleLittleEndian(bytes.AsSpan((first - 1 + word) * 8), value);
        string path = Path.Combine(_root.FullName, "invalid.bsp");
        File.WriteAllBytes(path, bytes);
        Assert.Throws<InvalidDataException>(() => new SpkKernel(path));
    }

    // Two records with explicitly known T0,T1,T2 polynomials. The second starts at
    // the first's endpoint but has a different slope, checking interval selection.
    private byte[] Cheb()
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write("CHEBPOLY"u8); writer.Write(2); writer.Write(2); writer.Write(0d); writer.Write(1350d);
        foreach (double value in new[] { 10d, 2, 3, -5, 4, -2, 8, -1, .5, 15, 1, 0, -3, 2, 0, 7.5, 3, 0 }) writer.Write(value);
        return stream.ToArray();
    }

    private ChebyshevTrajectory Load(byte[] bytes)
    {
        string path = Path.Combine(_root.FullName, "orbit.cheb");
        File.WriteAllBytes(path, bytes);
        return ChebyshevTrajectory.Load(path);
    }

    [Theory]
    [InlineData(-10, 11, -11, 9.5, -.2 / 13.5, .24 / 13.5, -.06 / 13.5)]
    [InlineData(0, 11, -11, 9.5, -.2 / 13.5, .24 / 13.5, -.06 / 13.5)]
    [InlineData(675, 7, -3, 7.5, .04 / 13.5, .08 / 13.5, -.02 / 13.5)]
    [InlineData(1350, 14, -5, 4.5, .02 / 13.5, .04 / 13.5, .06 / 13.5)]
    [InlineData(2700, 16, -1, 10.5, .02 / 13.5, .04 / 13.5, .06 / 13.5)]
    [InlineData(3000, 16, -1, 10.5, .02 / 13.5, .04 / 13.5, .06 / 13.5)]
    public void ChebyshevPolynomialsHaveAnalyticPositionsDerivativesAndEndpointHold(double seconds,
        double x, double y, double z, double vx, double vy, double vz)
    {
        var actual = Load(Cheb()).Sample(JulianInstant.FromTdbSeconds(seconds));
        // A record lasts exactly 1/64 day, so its boundary survives JD projection.
        Assert.InRange((actual.PositionKilometers - new Vector3d(x, y, z)).Length, 0, 1e-5);
        Assert.InRange((actual.VelocityKilometersPerSecond - new Vector3d(vx, vy, vz)).Length, 0, 1e-7);
    }

    [Theory]
    [InlineData(8, 0)] [InlineData(12, -1)] [InlineData(12, 129)]
    public void ChebyshevRejectsUnsupportedRecordCountsAndDegrees(int offset, int value)
    {
        byte[] bytes = Cheb(); BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(offset), value);
        Assert.Throws<InvalidDataException>(() => Load(bytes));
    }

    [Theory]
    [InlineData(16, double.NaN)] [InlineData(24, 0d)] [InlineData(24, double.PositiveInfinity)]
    [InlineData(32, double.NaN)] [InlineData(40, double.NegativeInfinity)]
    public void ChebyshevRejectsInvalidDurationAndNonfiniteCoefficients(int offset, double value)
    {
        byte[] bytes = Cheb(); BinaryPrimitives.WriteDoubleLittleEndian(bytes.AsSpan(offset), value);
        Assert.Throws<InvalidDataException>(() => Load(bytes));
    }

    [Fact]
    public void ChebyshevRejectsTruncationSignatureAndWrongTimeScale()
    {
        byte[] bytes = Cheb();
        Assert.Throws<InvalidDataException>(() => Load(bytes[..12]));
        Assert.Throws<InvalidDataException>(() => Load(bytes[..^1]));
        bytes[0] ^= 1;
        Assert.Throws<InvalidDataException>(() => Load(bytes));
        Assert.Throws<ArgumentException>(() => Load(Cheb()).Sample(new(2451545, 0, TimeScale.Tt)));
    }

    public void Dispose() => _root.Delete(true);
}
