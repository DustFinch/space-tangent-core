// SPDX-License-Identifier: GPL-2.0-or-later
using System.Text.Json;
using SpaceTangent.Contracts;
using SpaceTangent.Core.Addons;
using SpaceTangent.Core.Observing;
using Xunit;

namespace SpaceTangent.Core.Tests;

public sealed class StellarCorrectionPackTests
{
    [CorrectionArchiveFact]
    [Trait("Category", "ExternalData")]
    public void AppliesAllTenOriginalPleiadesCorrectionsAndPreservesApparentMagnitude()
    {
        string archive = Environment.GetEnvironmentVariable("SPACETANGENT_STELLAR_ARCHIVE")!;
        string pack = Environment.GetEnvironmentVariable("SPACETANGENT_STELLAR_CORRECTIONS")!;
        var original = StellarCatalogue.LoadArchive(archive);
        var result = original.WithDistanceCorrectionPacks(pack);
        Assert.Equal(10, result.AppliedRecords);
        Assert.Empty(result.Warnings);
        Assert.Equal(original.Stars.Count, result.Catalogue.Stars.Count);
        var epoch = JulianInstant.FromJulianDate(JulianInstant.J2000Day, TimeScale.Tdb);
        foreach (int hip in new[] { 17702, 17847, 17499, 17573, 17608, 17531, 17851, 17489, 17579, 17588 })
        {
            var id = new ObjectId($"hip/{hip}");
            var before = original.Find(id)!;
            var after = result.Catalogue.Find(id)!;
            var oldPosition = before.StateAt(epoch).PositionKilometers;
            var position = after.StateAt(epoch).PositionKilometers;
            Assert.Equal(444.2, position.Length / StellarEntry.KilometersPerLightYear, 9);
            Assert.InRange((position / position.Length - oldPosition / oldPosition.Length).Length, 0, 1e-12);
            double scale = position.Length / oldPosition.Length;
            Assert.Equal(before.RadiusKilometers * scale, after.RadiusKilometers, 5);
            Assert.Equal(before.AbsoluteMagnitude!.Value - 5 * Math.Log10(scale), after.AbsoluteMagnitude!.Value, 10);
            Assert.Equal(before.VisualMagnitude, after.VisualMagnitude, 9);
            Assert.Equal(before.Aliases, after.Aliases);
        }
        Assert.Same(original.Find(new("hip/70890")), result.Catalogue.Find(new("hip/70890")));
    }

    [Fact]
    public void LaterPackWinsAndReloadDependenciesDetectEdits()
    {
        var original = StellarCatalogue.Read(new StringReader(
            "id\tname\tdist_pc\tx_pc\ty_pc\tz_pc\tvx_pc_y\tvy_pc_y\tvz_pc_y\tv_mag\tb_v\tr_km\n" +
            "hip/12\tExample\t2\t2\t0\t0\t0.01\t0.02\t0\t4\t0.65\t500000\n"));
        var folder = Directory.CreateTempSubdirectory();
        try
        {
            string root = folder.FullName;
            string early = Directory.CreateDirectory(Path.Combine(root, "00-early")).FullName;
            string late = Directory.CreateDirectory(Path.Combine(root, "90-late")).FullName;
            Write(early, 10, "HIP 12");
            Write(late, 20, "Example", "HIP 77");
            var (result, inputs) = AddonDependencies.Capture(() => original.WithDistanceCorrectionPacks(root));
            Assert.True(inputs.IsCurrent());
            Assert.Equal(1, result.AppliedRecords);
            Assert.Equal(1, result.DiagnosticCount);
            Assert.Contains("HIP 77", Assert.Single(result.Warnings));
            var corrected = result.Catalogue.Find(new("hip/12"))!;
            Assert.Equal(20, corrected.PositionParsecs.Length * StellarEntry.KilometersPerParsec / StellarEntry.KilometersPerLightYear, 10);
            Assert.Equal(.01, corrected.MotionParsecsPerYear.X, 12);
            Assert.Equal(.02 * corrected.PositionParsecs.Length / 2, corrected.MotionParsecsPerYear.Y, 12);
            Write(late, -1, "Example");
            Assert.False(inputs.IsCurrent());
            Assert.Throws<InvalidDataException>(() => original.WithDistanceCorrectionPacks(root));
        }
        finally { folder.Delete(recursive: true); }

        static void Write(string path, double distance, params string[] names) =>
            File.WriteAllText(Path.Combine(path, "pack.json"), JsonSerializer.Serialize(new
            {
                formatVersion = 1,
                corrections = names.Select(id => new
                {
                    target = new { kind = "star", id },
                    @set = new { distanceLy = distance },
                    source = "Test catalogue"
                })
            }));
    }

    private sealed class CorrectionArchiveFactAttribute : FactAttribute
    {
        public CorrectionArchiveFactAttribute()
        {
            if (new[] { "SPACETANGENT_STELLAR_ARCHIVE", "SPACETANGENT_STELLAR_CORRECTIONS" }
                .Any(key => string.IsNullOrEmpty(Environment.GetEnvironmentVariable(key))))
                Skip = "Set the archived stellar catalogue and correction pack directories.";
        }
    }
}
