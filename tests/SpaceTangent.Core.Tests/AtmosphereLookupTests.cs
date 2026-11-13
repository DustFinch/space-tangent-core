// SPDX-License-Identifier: GPL-2.0-or-later
using System.Buffers.Binary;
using System.Security.Cryptography;
using SpaceTangent.Core.Addons;
using Xunit;

namespace SpaceTangent.Core.Tests;

public sealed class AtmosphereLookupTests
{
    [Fact]
    public void ValidatesMetadataBeforeReadingEitherTable()
    {
        byte[] source = Fixture();
        Assert.Throws<InvalidDataException>(() => AtmosphereLookup.Read(source.AsSpan(0, 75)));
        Assert.Throws<InvalidDataException>(() => AtmosphereLookup.Read(source.AsSpan(0, source.Length - 1)));
        BinaryPrimitives.WriteUInt32LittleEndian(source.AsSpan(56), uint.MaxValue);
        Assert.Throws<InvalidDataException>(() => AtmosphereLookup.Read(source));
        BinaryPrimitives.WriteUInt32LittleEndian(source.AsSpan(56), 1);
        BinaryPrimitives.WriteSingleLittleEndian(source.AsSpan(12), float.PositiveInfinity);
        Assert.Throws<InvalidDataException>(() => AtmosphereLookup.Read(source));
    }

    [Fact]
    public void PreservesRawValuesAndAppliesTheNativeClampWhenPreparingTheUpload()
    {
        byte[] source = Fixture();
        var lookup = AtmosphereLookup.Read(source);
        source[76] = 0;
        Assert.True(float.IsNaN(lookup.Transmission[0]));
        Assert.Equal(-1f, lookup.Transmission[1]);
        Assert.Equal(512f, lookup.Transmission[2]);
        Assert.Equal(64f, lookup.TransparentHeightKilometers);
        byte[] upload = lookup.TransmissionHalfBytes();
        Assert.Equal(BitConverter.HalfToUInt16Bits((Half)256), BinaryPrimitives.ReadUInt16LittleEndian(upload));
        Assert.Equal(BitConverter.HalfToUInt16Bits((Half).00001), BinaryPrimitives.ReadUInt16LittleEndian(upload.AsSpan(2)));
        Assert.Equal(BitConverter.HalfToUInt16Bits((Half)256), BinaryPrimitives.ReadUInt16LittleEndian(upload.AsSpan(4)));
    }

    [AtmosphereFact]
    [Trait("Category", "ExternalData")]
    public void RealTablesMatchIndependentNativeLoaderAndBinary16Reference()
    {
        string root = Environment.GetEnvironmentVariable("SPACETANGENT_ATMOSPHERE_ROOT")!;
        var rows = new[]
        {
            ("earth", "85aa5d9e88fc6a2b4634b4a45f73ebf9568d06b42d225cbc85d325c4a9b3d6bf",
                "7138dc4778e51f6be3d63183ceb4c2d84ec651cdba0356a3bdf0ca92fdcf3f34", "2552139efce7c4b662225742828b6f19669925f176dfab4538c9e1ea7b16071d"),
            ("mars", "d440d6b2d42bf81f205b4c7fbf6fd3d604c3ac8079f4e9140763166aa182152d",
                "ca30686c4213cf9fc7de2eee9b29a507a9f47b59850eb23f7684eac90dfba64d", "3ff5c9e663803647afa6c8e40715be58afb894abe72ddb96094c6c08a06f65e4"),
            ("titan", "4bc0b1a578093f3e08f60bfda6c2d929b5dce15efcc38a9914957963371c8920",
                "8dab4aa86a2e4530db92bc27339f43b911dfe74718c3ab33c309d36d6c5c5859", "03478ae2fb82840af81f82526ed75525c0bd1c2bfea2de7347e76f56b3e2cd4b")
        };
        foreach (var (name, source, transmission, scattering) in rows)
        {
            string path = Path.Combine(root, $"builtin-original-{name}.atmscat");
            using (var input = File.OpenRead(path))
                Assert.Equal(source, Convert.ToHexString(SHA256.HashData(input)).ToLowerInvariant());
            AtmosphereLookup lookup = AtmosphereLookup.Load(path);
            Assert.Equal((1024, 128, 32, 256, 32), (lookup.TransmissionWidth, lookup.TransmissionHeight,
                lookup.ScatteringWidth, lookup.ScatteringHeight, lookup.ScatteringDepth));
            Assert.Equal(transmission, Hash(lookup.TransmissionHalfBytes()));
            Assert.Equal(scattering, Hash(lookup.ScatteringHalfBytes()));
        }
        static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }

    [ArchivedAtmosphereFact]
    [Trait("Category", "ExternalData")]
    public void ArchivedWorldPacksResolveTheOriginalTables()
    {
        string[] roots = Environment.GetEnvironmentVariable("SPACETANGENT_ARCHIVED_ATMOSPHERE_PACKS")!.Split(Path.PathSeparator);
        string reference = Environment.GetEnvironmentVariable("SPACETANGENT_ATMOSPHERE_ROOT")!;
        Assert.Equal(2, roots.Length);
        var required = new[] { "Sol/Earth", "Sol/Saturn/Titan" };
        Assert.Equal(required, AtmosphereLookup.LoadPackPaths(roots[0]).Keys.Order(StringComparer.Ordinal));
        Assert.Equal(new[] { "Sol/Earth", "Sol/Mars", "Sol/Saturn/Titan" },
            AtmosphereLookup.LoadPackPaths(roots[1]).Keys.Order(StringComparer.Ordinal));
        foreach (string root in roots)
        {
            foreach (var (body, path) in AtmosphereLookup.LoadPackPaths(root))
            {
                string name = body[(body.LastIndexOf('/') + 1)..].ToLowerInvariant();
                var actual = AtmosphereLookup.Load(path);
                var original = AtmosphereLookup.Load(Path.Combine(reference, $"builtin-original-{name}.atmscat"));
                Assert.Equal(original.RadiusKilometers, actual.RadiusKilometers);
                Assert.Equal(original.TransmissionHalfBytes(), actual.TransmissionHalfBytes());
                Assert.Equal(original.ScatteringHalfBytes(), actual.ScatteringHalfBytes());
            }
        }
    }

    private sealed class ArchivedAtmosphereFactAttribute : FactAttribute
    {
        public ArchivedAtmosphereFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SPACETANGENT_ARCHIVED_ATMOSPHERE_PACKS")) ||
                string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SPACETANGENT_ATMOSPHERE_ROOT")))
                Skip = "Set the original v13 and v14 world pack folders and the loose atmosphere root.";
        }
    }

    private static byte[] Fixture()
    {
        var bytes = new byte[104];
        "atmscatr"u8.CopyTo(bytes);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(8), 1);
        BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(12), 8);
        BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(28), 1.2f);
        BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(52), 6378);
        for (int index = 56; index < 76; index += 4)
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(index), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(76), 0x7fc12345);
        BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(80), -1);
        BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(84), 512);
        return bytes;
    }

    private sealed class AtmosphereFactAttribute : FactAttribute
    {
        public AtmosphereFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SPACETANGENT_ATMOSPHERE_ROOT")))
                Skip = "Set SPACETANGENT_ATMOSPHERE_ROOT to the original Earth, Mars and Titan atmosphere tables.";
        }
    }
}
