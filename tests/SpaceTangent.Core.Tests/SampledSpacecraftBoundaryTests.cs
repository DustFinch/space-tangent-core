// SPDX-License-Identifier: GPL-2.0-or-later
using SpaceTangent.Contracts;
using SpaceTangent.Core.Addons;
using SpaceTangent.Core.Ephemerides;
using Xunit;

namespace SpaceTangent.Core.Tests;

public sealed class SampledSpacecraftBoundaryTests : IDisposable
{
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("st-sampled-boundaries-");
    private const string Phase = """
        { Beginning 2451545 Ending 2451546
          OrbitFrame { EclipticJ2000 { Center "Sol/Earth" } }
          SampledTrajectory { Source "witness.xyzv" Interpolation "cubic" DoublePrecision true }
          FixedRotation {}
          BodyFrame { TwoVector { Center "Sol/Earth/Probe"
            Primary { Axis "x" RelativePosition { Observer "Sol/Earth/Probe" Target "Sol/Earth" } }
            Secondary { Axis "z" RelativeVelocity { Observer "Sol/Earth/Probe" Target "Sol/Earth" } }
          } }
        }
        """;
    private const string Catalogue = "\"Probe\" \"Sol/Earth\" { Class \"spacecraft\" Radius 1 Mesh \"witness.obj\" Timeline [ PHASE ] }";
    private SscSampledSpacecraft Read(string? source = null, bool empty = false)
    {
        File.WriteAllText(Path.Combine(_root.FullName, "witness.obj"), "v 0 0 0\nv 1 0 0\nv 0 1 0\nf 1 2 3\n");
        File.WriteAllText(Path.Combine(_root.FullName, "witness.xyzv"), empty ? "" : "2451545 0 0 0 1 0 0\n2451547 172800 0 0 1 0 0\n");
        File.WriteAllText(Path.Combine(_root.FullName, "witness.xyz"), "2451545 0 0 0\n2451547 172800 0 0\n");
        File.WriteAllText(Path.Combine(_root.FullName, "witness.ssc"), source ?? Catalogue.Replace("PHASE", Phase));
        return SscSampledCatalog.Load("witness.ssc", _root.FullName, "Sol/Earth/Probe");
    }

    [Fact]
    public void AuthoredLinearTrajectoryRetainsItsCenterAndHalfOpenAvailability()
    {
        var track = Read();
        Assert.Equal(1, track.PhaseCount);
        Assert.False(track.TrySampleRelative(track.Start.AddNanoseconds(-1), out _, out _));
        Assert.False(track.TrySampleRelative(track.End, out _, out _));
        foreach (double seconds in new[] { 0d, 10, 3600, 43200 })
        {
            Assert.True(track.TrySampleRelative(track.Start.AddSeconds(seconds), out var center, out var state));
            Assert.Equal(SolarSystemSample.EarthId, center);
            Assert.InRange((state.PositionKilometers - new Vector3d(seconds, 0, 0)).Length, 0, 1e-4);
            Assert.InRange((state.VelocityKilometersPerSecond - new Vector3d(1, 0, 0)).Length, 0, 1e-9);
        }
        Assert.Throws<ArgumentException>(() => track.TrySampleRelative(new(2451545, 0, TimeScale.Tt), out _, out _));
    }

    [Theory]
    [InlineData("class")]
    [InlineData("interpolation")]
    [InlineData("precision")]
    [InlineData("extension")]
    [InlineData("rotation")]
    [InlineData("body-center")]
    [InlineData("unknown-reference")]
    public void UnsupportedSampledTrackConventionsAreExplicitFailures(string fault)
    {
        string source = Catalogue.Replace("PHASE", Phase);
        source = fault switch
        {
            "class" => source.Replace("\"spacecraft\"", "\"planet\""),
            "interpolation" => source.Replace("\"cubic\"", "\"linear\""),
            "precision" => source.Replace("DoublePrecision true", "DoublePrecision false"),
            "extension" => source.Replace("witness.xyzv", "witness.xyz"),
            "rotation" => source.Replace("FixedRotation {}", "FixedRotation { MeridianAngle 1 }"),
            "body-center" => source.Replace("TwoVector { Center \"Sol/Earth/Probe\"", "TwoVector { Center \"Sol/Earth\""),
            _ => source.Replace("Target \"Sol/Earth\"", "Target \"Sol/Absent\"")
        };
        Assert.Throws<NotSupportedException>(() => Read(source));
    }

    [Theory]
    [InlineData("radius")]
    [InlineData("empty-timeline")]
    [InlineData("many-phases")]
    [InlineData("not-object")]
    [InlineData("reversed")]
    [InlineData("late-beginning")]
    [InlineData("both-directions")]
    [InlineData("bad-date")]
    public void MalformedTracksCannotEscapeCatalogueValidation(string fault)
    {
        string source = Catalogue.Replace("PHASE", Phase);
        source = fault switch
        {
            "radius" => source.Replace("Radius 1", "Radius 0"),
            "empty-timeline" => Catalogue.Replace("PHASE", ""),
            "many-phases" => Catalogue.Replace("PHASE", string.Concat(Enumerable.Repeat(Phase, 129))),
            "not-object" => Catalogue.Replace("PHASE", "1"),
            "reversed" => source.Replace("Ending 2451546", "Ending 2451544"),
            "late-beginning" => Catalogue.Replace("PHASE", Phase + Phase),
            "both-directions" => source.Replace("Primary { Axis \"x\"", "Primary { Axis \"x\" RelativeVelocity {}"),
            _ => source.Replace("Beginning 2451545", "Beginning \"2000 99 99\"")
        };
        Assert.Throws<InvalidDataException>(() => Read(source));
    }

    [Fact]
    public void AnEmptySampleFileCannotPublishAnApparentlyValidTimeline()
    {
        Assert.Throws<InvalidDataException>(() => Read(empty: true));
    }

    public void Dispose() => _root.Delete(recursive: true);
}
