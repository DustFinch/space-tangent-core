// SPDX-License-Identifier: GPL-2.0-or-later
using System.Buffers.Binary;
using SpaceTangent.Contracts;
using SpaceTangent.Core.Observing;
using Xunit;

namespace SpaceTangent.Core.Tests;

public sealed class DistantCatalogueBoundaryTests
{
    private const int LeafOffset = 20 + 401 * 4, RecordsOffset = LeafOffset + 72;
    private static byte[] Fixture(int count = 2)
    {
        using var stream = new MemoryStream(); using var output = new BinaryWriter(stream);
        output.Write("CELWFAR\0"u8); output.Write((ushort)2); output.Write((ushort)0);
        output.Write((uint)count); output.Write(2u);
        for (int i = 0; i < 401; i++) output.Write(new byte[] { 255, 128, 64, 255 });
        for (uint leaf = 0; leaf < 2; leaf++)
        {
            for (int axis = 0; axis < 3; axis++) output.Write(-20f);
            for (int axis = 0; axis < 3; axis++) output.Write(20f);
            output.Write(-10f); output.Write(leaf); output.Write(1u);
        }
        for (int i = 0; i < count; i++)
        {
            output.Write(10f + i); output.Write(0f); output.Write(0f);
            output.Write((short)0); output.Write((ushort)60); output.Write(i == 0 ? 10u : 1_000_000_001u);
        }
        return stream.ToArray();
    }

    [Theory]
    [InlineData("magic")]
    [InlineData("version")]
    [InlineData("reserved")]
    [InlineData("count")]
    [InlineData("leaves")]
    [InlineData("bounds")]
    [InlineData("nan-bounds")]
    [InlineData("nan-magnitude")]
    [InlineData("first")]
    [InlineData("empty-leaf")]
    [InlineData("uncovered-record")]
    public void InvalidHeadersAndLeafTablesFailBeforeAnyRecordCanBeRead(string fault)
    {
        byte[] bytes = Fixture(fault == "uncovered-record" ? 3 : 2);
        switch (fault)
        {
            case "magic": bytes[0] = 0; break;
            case "version": bytes[8] = 3; break;
            case "reserved": bytes[10] = 1; break;
            case "count": BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(12), 3); break;
            case "leaves": BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(16), 3); break;
            case "bounds": BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(LeafOffset), 100); break;
            case "nan-bounds": BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(LeafOffset), float.NaN); break;
            case "nan-magnitude": BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(LeafOffset + 24), float.NaN); break;
            case "first": BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(LeafOffset + 28), 1); break;
            case "empty-leaf": BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(LeafOffset + 32), 0); break;
        }
        using var stream = new MemoryStream(bytes);
        Assert.Throws<InvalidDataException>(() => DistantStarIndex.Open(stream));
        Assert.True(stream.CanRead); // Ownership transfers only after a successful open.
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void InvalidRecordsAreRejectedWhenTheirLeafIsRequested(bool position)
    {
        byte[] bytes = Fixture();
        if (position) BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(RecordsOffset), float.NaN);
        else BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(RecordsOffset + 14), 401);
        using var index = DistantStarIndex.Open(new MemoryStream(bytes));
        Assert.Throws<InvalidDataException>(() => index.Query(default, 20, []));
    }

    [Fact]
    public void TinyCacheEvictsOldLeavesAndDisposalReleasesTheOwnedStream()
    {
        var stream = new MemoryStream(Fixture()); var index = DistantStarIndex.Open(stream, cacheBytes: 20);
        var result = new List<DistantStar>();
        Assert.Equal(2, index.Query(default, 20, result));
        Assert.Equal(2, index.LeavesRead); Assert.Equal(20, index.CachedBytes);
        Assert.Equal(2, index.Query(default, 20, result));
        Assert.Equal(4, index.LeavesRead); Assert.Equal(20, index.CachedBytes);
        Assert.Equal(4, result.Count);
        Assert.Equal(2, result.Select(star => star.Id).Distinct().Count());
        index.Dispose(); index.Dispose();
        Assert.Equal(0, index.CachedBytes); Assert.False(stream.CanRead);
        Assert.Throws<ObjectDisposedException>(() => index.Query(default, 20, []));
    }

    [Theory]
    [InlineData("hip/0")]
    [InlineData("hip/999")]
    [InlineData("unknown/10")]
    [InlineData("tyc/0-1-1")]
    [InlineData("tyc/1-100000-1")]
    [InlineData("tyc/1-1-4")]
    [InlineData("tyc/1-1")]
    public void MissingAndMalformedIdentifiersDoNotProduceAnotherStar(string value)
    {
        using var index = DistantStarIndex.Open(new MemoryStream(Fixture()));
        Assert.Null(index.Find(new(value))); Assert.Null(index.Find(default));
    }

    [Fact]
    public void NumberedAndTychoLookupsRetainNamesAndReuseThePublishedEntry()
    {
        var root = Directory.CreateTempSubdirectory("st-distant-names-");
        try
        {
            string path = Path.Combine(root.FullName, "stars.bin"), names = Path.Combine(root.FullName, "names.txt");
            File.WriteAllBytes(path, Fixture());
            File.WriteAllText(names, "invalid\nnot-a-number:Ignored\n10:Named Star:Alias\n1000000001:Tycho Witness\n0:Ignored\n");
            using var index = DistantStarIndex.Load(path, names);
            var named = Assert.IsType<StellarEntry>(index.Find(new("hip/10")));
            Assert.Equal("Named Star", named.Name); Assert.Contains("Alias", named.Aliases);
            Assert.Same(named, index.Find(new("hip/10")));
            Assert.Equal("Tycho Witness", index.Find(new("tyc/1-0-1"))!.Name);
            Assert.Equal(2, index.NamedStars.Count());
            Assert.Throws<ArgumentOutOfRangeException>(() => index.Query(new(double.NaN, 0, 0), 20, []));
            Assert.Throws<ArgumentOutOfRangeException>(() => index.Query(default, double.PositiveInfinity, []));
        }
        finally { root.Delete(recursive: true); }
    }

    [Fact]
    public void NegativeLookupCacheIsBoundedAndCanReloadAnEvictedIdentifier()
    {
        using var index = DistantStarIndex.Open(new MemoryStream(Fixture()));
        var original = new ObjectId("hip/999");
        Assert.Null(index.Find(original));
        for (int number = 1000; number < 5096; number++) Assert.Null(index.Find(new("hip/" + number)));
        int before = index.LeavesRead;
        Assert.Null(index.Find(original));
        Assert.Equal(before + 2, index.LeavesRead);
        Assert.Null(index.Find(original));
        Assert.Equal(before + 2, index.LeavesRead);
    }

    [Fact]
    public void ConcurrentLookupsPublishOneStableEntry()
    {
        using var index = DistantStarIndex.Open(new MemoryStream(Fixture()));
        var found = new StellarEntry?[256];
        Parallel.For(0, found.Length, i => found[i] = index.Find(new("hip/10")));
        Assert.NotNull(found[0]);
        Assert.All(found, entry => Assert.Same(found[0], entry));
    }

    [Fact]
    public void FailedFileOpenDoesNotLeakTheCatalogueHandle()
    {
        string path = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(path, new byte[20]);
            Assert.Throws<InvalidDataException>(() => DistantStarIndex.Load(path));
            using var exclusive = File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            Assert.Equal(20, exclusive.Length);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void OpenRequiresReadableSeekableInputAndAPositiveBudget()
    {
        Assert.Throws<ArgumentNullException>(() => DistantStarIndex.Open(null!));
        using var stream = new MemoryStream(Fixture());
        Assert.Throws<ArgumentException>(() => DistantStarIndex.Open(stream, 0));
        using var shortInput = new MemoryStream(new byte[19]);
        Assert.Throws<InvalidDataException>(() => DistantStarIndex.Open(shortInput));
        using var compressed = new System.IO.Compression.GZipStream(stream, System.IO.Compression.CompressionMode.Decompress, leaveOpen: true);
        Assert.Throws<ArgumentException>(() => DistantStarIndex.Open(compressed));
    }
}
