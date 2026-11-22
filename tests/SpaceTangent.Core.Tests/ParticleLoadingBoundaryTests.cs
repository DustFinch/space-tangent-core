// SPDX-License-Identifier: GPL-2.0-or-later
using System.Text.Json.Nodes;
using SpaceTangent.Contracts;
using SpaceTangent.Core.Addons;
using Xunit;

namespace SpaceTangent.Core.Tests;

public sealed class ParticleLoadingBoundaryTests : IDisposable
{
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("st-particle-loading-");
    private static JsonObject Emitter() => JsonNode.Parse("""
        {"generator":{"type":"Point","position":[1,2,3]},"spawnRate":2,"lifetime":10,
         "startTime":2451545,"endTime":2451546,"startSize":1,"endSize":2}
        """)!.AsObject();
    private MissionParticleEmitter Load(JsonObject emitter, Action<string>? warning = null)
    {
        var item = new JsonObject { ["name"] = "Plume", ["center"] = "Earth",
            ["trajectory"] = new JsonObject { ["type"] = "FixedPoint", ["position"] = new JsonArray(0, 0, 0) },
            ["geometry"] = new JsonObject { ["type"] = "ParticleSystem", ["emitters"] = new JsonArray(emitter) } };
        var document = new JsonObject { ["version"] = "1.0", ["items"] = new JsonArray(item) };
        File.WriteAllText(Path.Combine(_root.FullName, "source.json"), document.ToJsonString());
        var mission = Assert.Single(SampledMissionCatalog.Load("source.json", _root.FullName,
            name => name == "Earth" ? new ObjectId("earth") : null, warning));
        return Assert.Single(Assert.Single(mission.Geometry!).Shape.ParticleEmitters!);
    }

    [Theory]
    [InlineData("Strip", 6)] [InlineData("ArcStrip", 4)]
    public void StripInputsRetainIndependentPositionsAndVelocities(string kind, int stride)
    {
        var emitter = Emitter();
        emitter["generator"] = kind == "Strip" ? JsonNode.Parse("""
            {"type":"Strip","states":[1,2,3,4,5,6,7,8,9,10,11,12]}
            """) : JsonNode.Parse("""
            {"type":"ArcStrip","arcs":[0,0,2,3,90,0,4,5]}
            """);
        var actual = Load(emitter);
        if (stride == 6)
        {
            Assert.Equal(new Vector3d(1, 2, 3), actual.Strip[0].Position);
            Assert.Equal(new Vector3d(10, 11, 12), actual.Strip[1].Velocity);
        }
        else
        {
            Assert.Equal(new Vector3d(2, 0, 0), actual.Arcs[0].Position);
            Assert.InRange((actual.Arcs[1].Position - new Vector3d(0, 0, 4)).Length, 0, 1e-6);
            Assert.Equal(5, actual.Arcs[1].SpeedKilometersPerSecond);
        }
    }

    [Theory]
    [InlineData("odd-colors")] [InlineData("many-colors")]
    [InlineData("unknown-color")] [InlineData("bad-opacity")]
    [InlineData("bad-rgb")] [InlineData("too-short-strip")]
    [InlineData("incomplete-strip")] [InlineData("too-long-strip")]
    [InlineData("bad-latitude")] [InlineData("zero-radius")]
    [InlineData("huge-number")] [InlineData("negative-variation")]
    [InlineData("negative-radius")] [InlineData("reversed-time")]
    [InlineData("excess-population")] [InlineData("huge-trace")]
    [InlineData("huge-lifetime")] [InlineData("huge-size")]
    [InlineData("zero-rate")] [InlineData("zero-lifetime")]
    [InlineData("bad-texture-extension")]
    public void InvalidParticleDefinitionsAreRejectedBeforePublication(string fault)
    {
        var emitter = Emitter();
        switch (fault)
        {
            case "odd-colors": emitter["colors"] = new JsonArray("red"); break;
            case "many-colors": emitter["colors"] = JsonNode.Parse("[\"red\",1,\"red\",1,\"red\",1,\"red\",1,\"red\",1,\"red\",1]"); break;
            case "unknown-color": emitter["colors"] = new JsonArray("unlisted-witness", 1); break;
            case "bad-opacity": emitter["colors"] = new JsonArray("red", 2); break;
            case "bad-rgb": emitter["colors"] = new JsonArray(new JsonArray(2, 0, 0), 1); break;
            case "too-short-strip": case "incomplete-strip": case "too-long-strip":
                int count = fault == "too-short-strip" ? 6 : fault == "incomplete-strip" ? 13 : 2049 * 6;
                var numbers = new JsonArray(); for (int i = 0; i < count; i++) numbers.Add(i);
                emitter["generator"] = new JsonObject { ["type"] = "Strip", ["states"] = numbers }; break;
            case "bad-latitude": emitter["generator"] = JsonNode.Parse("""{"type":"ArcStrip","arcs":[91,0,2,1,0,0,2,1]}"""); break;
            case "zero-radius": emitter["generator"] = JsonNode.Parse("""{"type":"ArcStrip","arcs":[0,0,0,1,0,0,2,1]}"""); break;
            case "huge-number": emitter["force"] = new JsonArray(1e16, 0, 0); break;
            case "negative-variation": emitter["velocityVariation"] = -1; break;
            case "negative-radius": emitter["generator"]!["radius"] = -1; break;
            case "reversed-time": emitter["endTime"] = 2451544; break;
            case "excess-population": emitter["spawnRate"] = 1000000; break;
            case "huge-trace": emitter["trace"] = 1e16; break;
            case "huge-lifetime": emitter["lifetime"] = 1e16; break;
            case "huge-size": emitter["startSize"] = 1e16; break;
            case "zero-rate": emitter["spawnRate"] = 0; break;
            case "zero-lifetime": emitter["lifetime"] = 0; break;
            case "bad-texture-extension": emitter["texture"] = "wrong.dds"; break;
        }
        Assert.Throws<InvalidDataException>(() => Load(emitter));
    }

    [Fact]
    public void NumericGeneratorTagsAreNotAcceptedAsEnumNames()
    {
        var emitter = Emitter(); emitter["generator"]!["type"] = "0";
        Assert.Throws<NotSupportedException>(() => Load(emitter));
    }

    [Fact]
    public void MissingTextureReportsFallbackWithoutDiscardingTheEmitter()
    {
        var emitter = Emitter(); emitter["texture"] = "absent.png";
        var warnings = new List<string>();
        Assert.Null(Load(emitter, warnings.Add).TexturePath);
        Assert.Contains("absent.png", Assert.Single(warnings));
    }

    [Fact]
    public void FiveColorKeysPreserveTheArchivedSingleActiveKeyConvention()
    {
        var emitter = Emitter(); emitter["colors"] = JsonNode.Parse("[\"#804020\",0.5,\"red\",1,\"green\",1,\"blue\",1,\"white\",1]");
        var color = Assert.Single(Load(emitter).Colors);
        Assert.Equal(new Vector3d(128 / 255d, 64 / 255d, 32 / 255d), color.Color);
        Assert.Equal(.5, color.Opacity);
    }

    public void Dispose() => _root.Delete(recursive: true);
}
