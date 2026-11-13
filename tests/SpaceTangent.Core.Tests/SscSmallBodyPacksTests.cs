// SPDX-License-Identifier: GPL-2.0-or-later
using SpaceTangent.Contracts;
using SpaceTangent.Core.Addons;
using SpaceTangent.Core.Time;
using SpaceTangent.Core.World;
using Xunit;

namespace SpaceTangent.Core.Tests;

public sealed class SscSmallBodyPacksTests
{
    [OriginalCataloguesFact]
    [Trait("Category", "ExternalData")]
    public void OriginalSmallBodyCataloguesKeepStatesAttitudesAndAssetsWhenPacked()
    {
        string corpus = Environment.GetEnvironmentVariable("SPACETANGENT_SMALL_BODY_SSC")!;
        var scratch = Directory.CreateTempSubdirectory("space-tangent-small-bodies-");
        try
        {
            Verify("p4-original-asteroids", 60);
            Verify("p4-original-comets", 23);
            Verify("p4-interstellar-ssc", 3);
            Verify("p4-precessing-ssc", 2);
        }
        finally
        {
            scratch.Delete(recursive: true);
        }

        void Verify(string name, int count)
        {
            string source = Path.Combine(corpus, name);
            string pack = Path.Combine(scratch.FullName, name);
            var loose = SscBodyCatalogue.Load(source, warning: RejectWarning);
            Assert.Equal(count, loose.Count);
            Assert.Equal(count, SscBodyCatalogue.BuildPack(source, pack, RejectWarning));
            var packed = SscBodyCatalogue.Load(pack, warning: RejectWarning);
            Assert.Equal(loose.Select(body => (body.Names, body.Parent)), packed.Select(body => (body.Names, body.Parent)));

            for (int index = 0; index < loose.Count; index++)
            {
                var expected = loose[index];
                var actual = packed[index];
                Assert.Equal(expected.EllipsoidRadiiKilometers, actual.EllipsoidRadiiKilometers);
                Assert.Equal(expected.GeometricAlbedo, actual.GeometricAlbedo);
                if (actual.Name == "4 Vesta")
                    Assert.Equal(.38, actual.GeometricAlbedo);
                CompareAsset(expected.Mesh, actual.Mesh);
                CompareAsset(expected.SurfaceMap, actual.SurfaceMap);
                var times = new[] { Epoch(2451545), Epoch(2459849.469136174), Epoch(2462200.1) };
                if (expected.BindTimeline is null)
                {
                    Assert.Null(actual.BindTimeline);
                    Assert.Equal(expected.SampleRotation is null, actual.SampleRotation is null);
                    foreach (var time in times)
                    {
                        Assert.Equal(expected.Sample(time), actual.Sample(time));
                        Assert.Equal(expected.SampleRotation?.Invoke(time), actual.SampleRotation?.Invoke(time));
                    }
                    continue;
                }

                ObjectId id = new("body");
                ObjectId Center(string path) => new(path);
                var sourceTrack = expected.BindTimeline(id, Center);
                var packedTrack = actual.BindTimeline!(id, Center);
                Assert.Equal(sourceTrack.Arcs.Count, packedTrack.Arcs.Count);
                for (int phase = 0; phase < sourceTrack.Arcs.Count; phase++)
                {
                    var first = sourceTrack.Arcs[phase];
                    var second = packedTrack.Arcs[phase];
                    Assert.Equal((first.Start, first.End, first.Center, first.TrajectoryFrame),
                        (second.Start, second.End, second.Center, second.TrajectoryFrame));
                    foreach (var time in times.Append(first.Start).Append(first.End.AddNanoseconds(-1))
                        .Where(time => time.Day is >= 2400000 and <= 2500000 && time.CompareTo(first.Start) >= 0 && time.CompareTo(first.End) < 0))
                    {
                        Assert.Equal(first.LocalState(time), second.LocalState(time));
                        Assert.Equal(first.BodyReferenceFrame, second.BodyReferenceFrame);
                        if (first.BodyFrame is MissionFrame.Rotating sourceRotation)
                            Assert.Equal(sourceRotation.Local(time), Assert.IsType<MissionFrame.Rotating>(second.BodyFrame).Local(time));
                        else
                            Assert.Equal(first.BodyFrame, second.BodyFrame);
                    }
                }

                if (actual.Name == "Dimorphos")
                {
                    Assert.Equal(2, packedTrack.Arcs.Count);
                    var before = packedTrack.Arcs[0];
                    var after = packedTrack.Arcs[1];
                    Assert.Equal(before.End, after.Start);
                    Assert.Equal(Epoch(2459849.469136174), after.Start);
                    CheckDimorphos(before, 11.921493, 1.189);
                    CheckDimorphos(after, 11.3674, 1.15729 * 1.0274);
                }
            }
        }

        static void CheckDimorphos(MissionArc arc, double hours, double radius)
        {
            var boundary = Epoch(2459849.469136174);
            var position = Assert.IsType<CartesianState>(arc.LocalState(boundary)).PositionKilometers;
            Assert.InRange(Math.Abs(position.Length - radius), 0, 1e-9);
            var spin = Assert.IsType<MissionFrame.Rotating>(arc.BodyFrame).Local(boundary);
            double rate = Assert.IsType<Vector3d>(spin.AngularVelocityRadiansPerSecond).Length;
            Assert.InRange(Math.Abs(rate - Math.Tau / (hours * 3600)), 0, 1e-15);
        }

        static JulianInstant Epoch(double day) => JulianInstant.FromJulianDate(day, TimeScale.Tdb);
        static void RejectWarning(string warning) => throw new InvalidDataException(warning);

        static void CompareAsset(string? loose, string? packed)
        {
            Assert.Equal(loose is null, packed is null);
            if (loose is not null)
                Assert.Equal(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(loose)),
                    System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(packed!)));
        }
    }

    private sealed class OriginalCataloguesFactAttribute : FactAttribute
    {
        public OriginalCataloguesFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SPACETANGENT_SMALL_BODY_SSC")))
                Skip = "Set the directory containing the original small-body SSC corpora.";
        }
    }
}
