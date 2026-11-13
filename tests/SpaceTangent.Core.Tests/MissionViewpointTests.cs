// SPDX-License-Identifier: GPL-2.0-or-later
using SpaceTangent.Contracts;
using SpaceTangent.Core.Addons;
using SpaceTangent.Core.Ephemerides;
using Xunit;

namespace SpaceTangent.Core.Tests;

public sealed class MissionViewpointTests
{
    [ViewpointCorpusFact]
    [Trait("Category", "ExternalData")]
    public void ArchivedEarthViewpointsAndPackedDefinitionsAgree()
    {
        string root = Environment.GetEnvironmentVariable("SPACETANGENT_VIEWPOINT_CORPUS")!;
        var warnings = new List<string>();
        var source = SampledMissionCatalog.LoadContent(["views.json"], Path.Combine(root, "p4-viewpoint-source"), Resolve, warnings.Add);
        var packed = SampledMissionCatalog.LoadContent(["pack.json"], Path.Combine(root, "p4-viewpoint-packed"), Resolve);
        Assert.Contains("Vulcan", Assert.Single(warnings));
        Assert.Equal(2, source.Viewpoints.Count);
        Assert.Equal(source.Viewpoints.ToArray(), packed.Viewpoints.ToArray());
        Assert.Equal(new MissionViewpoint("Earth from sunward", SolarSystemSample.EarthId, SolarSystemSample.SunId, 100_000, 0, 0, false),
            source.Viewpoints[0]);
        Assert.Equal(90, source.Viewpoints[1].ElevationDegrees);

        static ObjectId? Resolve(string name) => name switch
        {
            "Earth" => SolarSystemSample.EarthId,
            "Sun" or "Sol" => SolarSystemSample.SunId,
            _ => null
        };
    }

    private sealed class ViewpointCorpusFactAttribute : FactAttribute
    {
        public ViewpointCorpusFactAttribute()
        {
            if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SPACETANGENT_VIEWPOINT_CORPUS")))
                Skip = "Set the archived and packed mission viewpoint fixtures.";
        }
    }
}
