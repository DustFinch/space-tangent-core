// SPDX-License-Identifier: GPL-2.0-or-later
using System.Security.Cryptography;
using System.Text.Json;
using SpaceTangent.Contracts;
using SpaceTangent.Core.World;
using Xunit;

namespace SpaceTangent.Core.Tests;

public sealed class ProceduralSurfaceTests
{
    [Fact]
    [Trait("Category", "ExternalData")]
    public void ArchivedSurfaceMapsAndReliefMatchAcrossSixWorlds()
    {
        string? oracle = Environment.GetEnvironmentVariable("SPACETANGENT_PROCEDURAL_SURFACE_ORACLE");
        if (string.IsNullOrWhiteSpace(oracle))
            return;
        using JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(oracle));
        Assert.Equal(6, document.RootElement.GetArrayLength());
        foreach (JsonElement world in document.RootElement.EnumerateArray())
        {
            var surface = new ProceduralSurface(world.GetProperty("seed").GetInt32(), world.GetProperty("radius").GetDouble(),
                Enum.Parse<ProceduralWorldKind>(world.GetProperty("kind").GetString()!));
            var maps = surface.BakeSurface(64, 32, maximumThreads: 1);
            VerifyMap(world, "color", maps.Color);
            VerifyMap(world, "normal", maps.Normal);
            VerifyMap(world, "cloud", surface.BakeClouds(64, 32, maximumThreads: 1));
            VerifyMap(world, "emission", surface.BakeEmission(64, 32, maximumThreads: 1));
            VerifyMap(world, "rings", surface.BakeRings(128));
            Assert.Equal(16, world.GetProperty("samples").GetArrayLength());
            foreach (JsonElement sample in world.GetProperty("samples").EnumerateArray())
            {
                JsonElement position = sample.GetProperty("dir");
                var direction = new Vector3d(position[0].GetDouble(), position[1].GetDouble(), position[2].GetDouble());
                double relief = surface.Elevation(direction, .05);
                Assert.InRange(Math.Abs(relief - sample.GetProperty("relief").GetDouble()), 0, 1e-7);
                ProceduralAlbedo albedo = surface.Surface(direction, relief, .95, .05);
                JsonElement expected = sample.GetProperty("albedo");
                Assert.InRange(Math.Abs(albedo.Red - expected[0].GetSingle()), 0, 1e-6);
                Assert.InRange(Math.Abs(albedo.Green - expected[1].GetSingle()), 0, 1e-6);
                Assert.InRange(Math.Abs(albedo.Blue - expected[2].GetSingle()), 0, 1e-6);
                Assert.InRange(Math.Abs(albedo.Water - expected[3].GetSingle()), 0, 1e-6);
                Assert.InRange(Math.Abs(surface.Clouds(direction, .05) - sample.GetProperty("clouds").GetDouble()), 0, 1e-7);
            }
        }
    }

    private static void VerifyMap(JsonElement world, string name, byte[] bytes) =>
        Assert.Equal(world.GetProperty(name).GetString(), Convert.ToHexString(SHA256.HashData(bytes)));
}
