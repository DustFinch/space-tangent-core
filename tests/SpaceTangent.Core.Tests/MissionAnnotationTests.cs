// SPDX-License-Identifier: GPL-2.0-or-later
using SpaceTangent.Contracts;
using SpaceTangent.Core.Addons;
using Xunit;

namespace SpaceTangent.Core.Tests;

public sealed class MissionAnnotationTests
{
    [AnnotationCorpusFact]
    [Trait("Category", "ExternalData")]
    public void ArchivedRadarCaptionsKeepBothTimeWindowsAcrossFormats()
    {
        string root = Environment.GetEnvironmentVariable("SPACETANGENT_ANNOTATION_CORPUS")!;
        var source = SampledMissionCatalog.LoadContent(["notes.json"], Path.Combine(root, "p4-annotation-source"), _ => null);
        var packed = SampledMissionCatalog.LoadContent(["pack.json"], Path.Combine(root, "p4-annotation-packed"), _ => null);
        Assert.Empty(source.Missions);
        Assert.Empty(packed.Missions);
        var at = JulianInstant.FromJulianDate(2451545, TimeScale.Tdb);
        foreach (var content in new[] { source, packed })
        {
            var track = Assert.Single(content.Annotations);
            Assert.Equal(("RADAR notes", .05, .7, "left", "small"), (track.Name, track.X, track.Y, track.Alignment, track.Size));
            Assert.Equal(2, track.Lines.Count);
            Assert.Equal("RADAR observation progress:", track.Lines[0].Text);
            Assert.Equal(new Vector3d(.5, 0, 0), track.Lines[1].Color);
            Assert.DoesNotContain(track.Lines, line => line.VisibleAt(at.AddSeconds(-1)));
            Assert.All(track.Lines, line => Assert.True(line.VisibleAt(at)));
            Assert.Equal(1, Assert.Single(track.Lines, line => line.VisibleAt(at.AddSeconds(900))).Line);
            Assert.DoesNotContain(track.Lines, line => line.VisibleAt(at.AddSeconds(3601)));
        }
    }

    private sealed class AnnotationCorpusFactAttribute : FactAttribute
    {
        public AnnotationCorpusFactAttribute()
        {
            if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SPACETANGENT_ANNOTATION_CORPUS")))
                Skip = "Set the archived annotation source and packed fixture.";
        }
    }
}
