// SPDX-License-Identifier: GPL-2.0-or-later
using SpaceTangent.Contracts;
using SpaceTangent.Core.Observing;
using Xunit;

namespace SpaceTangent.Core.Tests;

public sealed class StcContractTests : IDisposable
{
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("st-stc-contract-");
    private const string Header = "id\tname\tdist_pc\tx_pc\ty_pc\tz_pc\tvx_pc_y\tvy_pc_y\tvz_pc_y\tv_mag\tb_v\tr_km\n";
    private static StellarCatalogue Seed() => StellarCatalogue.Read(new StringReader(Header +
        "hip/1\tAnchor\t10\t10\t0\t0\t0.01\t0.02\t0.03\t5\t0.7\t100000\n"));
    private StellarImport Read(string text)
    {
        File.WriteAllText(Path.Combine(_root.FullName, "input.stc"), text);
        return Seed().WithStc(_root.FullName);
    }
    private static StellarEntry Star(StellarImport result, string name) =>
        Assert.IsType<StellarEntry>(result.Catalogue.FindByName(name));

    [Fact]
    public void MissingSourceKeepsTheOriginalCatalogueInstance()
    {
        var seed = Seed();
        var result = seed.WithStc(Path.Combine(_root.FullName, "missing"));
        Assert.Same(seed, result.Catalogue);
        Assert.Equal((0, 0), (result.AppliedRecords, result.DiagnosticCount));
    }

    [Fact]
    public void EmptySourceDoesNotRemoveExistingStars()
    {
        var seed = Seed();
        Assert.Same(seed, seed.WithStc(_root.FullName).Catalogue);
    }

    [Fact]
    public void CancellationPreventsApplyingTheFirstRecord()
    {
        File.WriteAllText(Path.Combine(_root.FullName, "input.stc"), "Star 1 { Radius 1 }");
        Assert.Throws<OperationCanceledException>(() => Seed().WithStc(_root.FullName, new CancellationToken(true)));
        Assert.Equal(100000, Seed().Find(new("hip/1"))!.RadiusKilometers);
    }

    [Fact]
    public void SourceFileLimitRejectsAnOversizedInstallation()
    {
        for (int i = 0; i < 65; i++) File.WriteAllText(Path.Combine(_root.FullName, $"{i:D2}.stc"), "# empty");
        Assert.Throws<InvalidDataException>(() => Seed().WithStc(_root.FullName));
    }

    [Fact]
    public void APositionedStarUsesParsecUnitsAndRightAscensionWrapping()
    {
        var result = Read("Star \"Wrapped\" { RA 450 Dec 0 Distance<pc> 10 AbsMag 4 }");
        var star = Star(result, "Wrapped");
        Assert.Equal(10, star.DistanceParsecs, 10);
        Assert.InRange((star.PositionParsecs - new Vector3d(0, 10, 0)).Length, 0, 1e-12);
        Assert.Equal(4, star.VisualMagnitude, 12);
    }

    [Fact]
    public void ApparentMagnitudeAtTenParsecsIsTheAbsoluteMagnitude()
    {
        var star = Star(Read("Star \"Photometry\" { RA 0 Dec 0 Distance<pc> 10 AppMag 6.5 }"), "Photometry");
        Assert.Equal(6.5, star.AbsoluteMagnitude);
        Assert.Equal(6.5, star.VisualMagnitude);
    }

    [Fact]
    public void AbsoluteMagnitudeUsesTheDistanceModulus()
    {
        var star = Star(Read("Star \"Distant\" { RA 0 Dec 0 Distance<pc> 100 AbsMag 2 }"), "Distant");
        Assert.Equal(7, star.VisualMagnitude, 12);
    }

    [Fact]
    public void SolarRadiusUnitsAreConvertedToKilometers()
    {
        var star = Star(Read("Modify 1 { Radius<rS> 2 }"), "Anchor");
        Assert.Equal(1_391_400, star.RadiusKilometers);
    }

    [Fact]
    public void JovianRadiusUnitsAreConvertedToKilometers()
    {
        var star = Star(Read("Modify 1 { Radius<rJ> 0.5 }"), "Anchor");
        Assert.Equal(35_746, star.RadiusKilometers);
    }

    [Fact]
    public void CentimeterRadiusUnitsRetainSmallObjects()
    {
        Assert.Equal(2, Star(Read("Modify 1 { Radius<cm> 200000 }"), "Anchor").RadiusKilometers);
    }

    [Fact]
    public void ModifyingAnExistingStarKeepsItsMotionAndPosition()
    {
        var before = Seed().Find(new("hip/1"))!;
        var after = Star(Read("Modify 1 { Radius 12345 }"), "Anchor");
        Assert.Equal(before.MotionParsecsPerYear, after.MotionParsecsPerYear);
        Assert.Equal(before.PositionParsecs, after.PositionParsecs);
        Assert.Equal(before.VisualMagnitude, after.VisualMagnitude, 12);
        Assert.Equal(12345, after.RadiusKilometers);
    }

    [Fact]
    public void AliasesResolveCaseInsensitivelyToTheSameIdentity()
    {
        var result = Read("Modify 1 \"Renamed:New Alias\" { Radius 123 }");
        Assert.Equal(new ObjectId("hip/1"), Star(result, "new alias").Id);
        Assert.Same(Star(result, "Renamed"), Star(result, "Anchor"));
    }

    [Fact]
    public void AUnitQualifiedOverrideReplacesAnEarlierUnqualifiedRadius()
    {
        var star = Star(Read("Modify 1 { Radius 10 } Modify 1 { Radius<rS> 1 }"), "Anchor");
        Assert.Equal(695700, star.RadiusKilometers);
    }

    [Theory]
    [InlineData("Radius -1")]
    [InlineData("Radius 100000000001")]
    [InlineData("Temperature 0")]
    [InlineData("Temperature -10")]
    [InlineData("Dec 91 RA 0 Distance 10")]
    [InlineData("Radius NaN")]
    public void InvalidPhysicalFieldsKeepThePriorStarAndEmitADiagnostic(string fields)
    {
        var before = Seed().Find(new("hip/1"))!;
        var result = Read("Modify 1 { " + fields + " }");
        Assert.Equal(before, result.Catalogue.Find(before.Id));
        Assert.Equal(0, result.AppliedRecords);
        Assert.Equal(1, result.DiagnosticCount);
        Assert.Single(result.Warnings);
    }

    [Fact]
    public void AnUnpositionedNewStarIsOmittedWithoutLosingExistingStars()
    {
        var result = Read("Star \"Missing coordinates\" { AppMag 5 }");
        Assert.Single(result.Catalogue.Stars);
        Assert.Null(result.Catalogue.FindByName("Missing coordinates"));
        Assert.Single(result.Warnings);
    }

    [Fact]
    public void ANewStarWithoutPhotometryIsNotPublished()
    {
        var result = Read("Star \"No magnitude\" { RA 0 Dec 0 Distance 10 }");
        Assert.Equal(0, result.AppliedRecords);
        Assert.Null(result.Catalogue.FindByName("No magnitude"));
    }

    [Fact]
    public void BarycentersDoNotRequirePhotometryOrEmitALuminousDisk()
    {
        var star = Star(Read("Barycenter \"System\" { RA 0 Dec 0 Distance<pc> 5 }"), "System");
        Assert.True(star.IsBarycenter);
        Assert.Equal(0, star.RadiusKilometers);
        Assert.Equal(5, star.DistanceParsecs, 12);
    }

    [Fact]
    public void ForwardDeclaredBarycenterResolvesACircularCompanion()
    {
        var result = Read("""
            Star "Companion" { OrbitBarycenter "System" AbsMag 5
                EllipticalOrbit { SemiMajorAxis<km> 1000 Period<d> 1 } }
            Barycenter "System" { RA 0 Dec 0 Distance<pc> 10 }
            """);
        var star = Star(result, "Companion");
        var parent = Star(result, "System");
        Assert.Equal(parent.Id, star.BarycenterId);
        var epoch = new JulianInstant(2451545, 0, TimeScale.Tdb);
        var initial = star.StateAt(epoch) - parent.StateAt(epoch);
        Assert.InRange(initial.PositionKilometers.Length, 999.99, 1000.01);
        var half = star.StateAt(epoch.AddSeconds(43200)) - parent.StateAt(epoch.AddSeconds(43200));
        Assert.InRange((initial.PositionKilometers + half.PositionKilometers).Length, 0, .01);
        Assert.InRange(initial.VelocityKilometersPerSecond.Length, .07272, .07273);
        Assert.InRange(parent.SystemRadiusKilometers, 1000, 2_000_000);
    }

    [Fact]
    public void MissingParentUsesRecordedCoordinatesWithAVisibleDiagnostic()
    {
        var result = Read("Star \"Orphan\" { OrbitBarycenter \"Absent\" RA 0 Dec 0 Distance<pc> 3 AbsMag 5 }");
        Assert.Null(Star(result, "Orphan").BarycenterId);
        Assert.Equal(3, Star(result, "Orphan").DistanceParsecs, 12);
        Assert.Single(result.Warnings);
    }

    [Fact]
    public void SolarReferenceRecordsDoNotReplaceTheEphemerisOrigin()
    {
        var result = Read("Star 0 \"Sun\" { Radius 100 } Barycenter \"Solar System Barycenter\" { RA 0 Dec 0 Distance 10 }");
        Assert.Equal(0, result.AppliedRecords);
        Assert.Single(result.Catalogue.Stars);
    }

    [Fact]
    public void UnsupportedRecordKindDoesNotPublishAStar()
    {
        var result = Read("Nebula \"Wrong kind\" { RA 0 Dec 0 Distance 10 AbsMag 4 }");
        Assert.Equal(0, result.AppliedRecords);
        Assert.Single(result.Warnings);
    }

    public void Dispose() => _root.Delete(recursive: true);
}
