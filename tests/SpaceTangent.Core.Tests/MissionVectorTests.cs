// SPDX-License-Identifier: GPL-2.0-or-later
using SpaceTangent.Contracts;
using SpaceTangent.Core.Addons;
using SpaceTangent.Core.World;
using Xunit;

namespace SpaceTangent.Core.Tests;

public sealed class MissionVectorTests
{
    [VectorCatalogueFact]
    [Trait("Category", "ExternalData")]
    public void OriginalPlanesAndArchivedDirectionContractMatchPackedGeometry()
    {
        string corpus = Environment.GetEnvironmentVariable("SPACETANGENT_MISSION_VECTORS")!;
        var earth = new ObjectId("earth");
        var moon = new ObjectId("moon");
        var source = SampledMissionCatalog.Load(["plane.json", "direction.json"], Path.Combine(corpus, "p4-vector-source"), Resolve);
        var packed = SampledMissionCatalog.Load("pack.json", Path.Combine(corpus, "p4-vector-packed"), Resolve);
        Assert.Equal(3, source.Count);
        Assert.Equal(3, packed.Count);
        foreach (var mission in source)
        {
            var other = Assert.Single(packed, item => item.Name == mission.Name);
            Assert.Equal(Style(mission), Style(other));
            Assert.Equal(earth, Assert.Single(other.Arcs).Center);
            var sourceFrame = Assert.IsType<MissionFrame.Inertial>(Assert.Single(mission.Arcs).BodyFrame);
            var packedFrame = Assert.IsType<MissionFrame.Rotating>(Assert.Single(other.Arcs).BodyFrame);
            Assert.Equal(sourceFrame, packedFrame.Parent);
        }
        var ecliptic = Assert.Single(source, mission => mission.Name == "ecliptic plane");
        var equator = Assert.Single(source, mission => mission.Name == "equatorial plane");
        var frame = Assert.IsType<MissionFrame.Inertial>(Assert.Single(ecliptic.Arcs).BodyFrame);
        Assert.InRange(Math.Acos(frame.ToWorld.Z.Z) * 180 / Math.PI, 23.43, 23.45);
        Assert.Equal(Basis3d.Identity, Assert.IsType<MissionFrame.Inertial>(Assert.Single(equator.Arcs).BodyFrame).ToWorld);
        Assert.Equal(20, Style(ecliptic).Subdivisions);
        Assert.Equal(20_000, Style(ecliptic).SizeKilometers);
        Assert.Equal(new Vector3d(.6, 1, 1), Style(ecliptic).Color);
        Assert.Equal(.2, Style(ecliptic).Opacity);
        Assert.Equal(moon, Style(Assert.Single(source, item => item.Name == "toMoon")).Target);

        ObjectId? Resolve(string name) => name switch
        {
            "Earth" or "Sol/Earth" => earth,
            "Moon" or "Sol/Earth/Moon" => moon,
            _ => null
        };

        static MissionVectorStyle Style(SampledMission mission) =>
            Assert.IsType<MissionVectorStyle>(Assert.Single(mission.Geometry!).Shape.VectorOverlay);
    }

    private sealed class VectorCatalogueFactAttribute : FactAttribute
    {
        public VectorCatalogueFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SPACETANGENT_MISSION_VECTORS")))
                Skip = "Set the root containing the archived source and packed visualizer cases.";
        }
    }
}
