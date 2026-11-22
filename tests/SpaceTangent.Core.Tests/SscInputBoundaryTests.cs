// SPDX-License-Identifier: GPL-2.0-or-later
using SpaceTangent.Contracts;
using SpaceTangent.Core.Addons;
using Xunit;

namespace SpaceTangent.Core.Tests;

public sealed class SscInputBoundaryTests : IDisposable
{
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("st-ssc-boundaries-");
    private IReadOnlyList<SscBodyDefinition> Read(string fields, string parent = "Sol/Earth", Action<string>? warning = null)
    {
        File.WriteAllText(Path.Combine(_root.FullName, "body.ssc"), $"\"Probe\" \"{parent}\" {{ {fields} }}");
        return SscBodyCatalogue.Load(_root.FullName, warning: warning);
    }

    [Theory]
    [InlineData("Radius 0")]
    [InlineData("Radius -1")]
    [InlineData("Radius 2000001")]
    [InlineData("SemiAxes [1 0 1]")]
    [InlineData("SemiAxes [1 -1 1]")]
    [InlineData("SemiAxes [1 2 2000001]")]
    [InlineData("SemiAxes [1 2]")]
    [InlineData("Color [1.1 0 0]")]
    [InlineData("Color [0 -0.1 0]")]
    [InlineData("BodyFrame { EquatorJ2000 {} EclipticJ2000 {} }")]
    [InlineData("FixedRotation 3")]
    [InlineData("Beginning 2460000 Ending 2450000")]
    [InlineData("Rings { Inner 0 Outer 100 }")]
    [InlineData("Rings { Inner 100 Outer 100 }")]
    [InlineData("Rings { Inner 100 Outer 10000001 }")]
    public void InvalidPhysicalFieldsFailWithACatalogueDiagnostic(string fields)
    {
        var error = Assert.Throws<InvalidDataException>(() => Read("FixedPosition [1 2 3] " + fields));
        Assert.Equal(AddonStage.Catalog, AddonDiagnostics.Get(error)!.Stage);
    }

    [Theory]
    [InlineData(.5)]
    [InlineData(1.5)]
    public void ExtrasolarBindingsKeepStarAndMoonUnitsAndBoundedAvailability(double eccentricity)
    {
        double axis = eccentricity > 1 ? -2 : 2;
        string text = FormattableString.Invariant($"Beginning 2451545 Ending 2451548 EllipticalOrbit {{ Epoch 2451545 Period 2 SemiMajorAxis {axis} Eccentricity {eccentricity} MeanAnomaly 0 }}");
        var definition = Assert.Single(Read(text, "Host"));
        var bind = Assert.IsType<Func<bool, SscExtrasolarConic>>(definition.BindExtrasolarOrbit);
        foreach (bool star in new[] { false, true })
        {
            var orbit = bind(star);
            Assert.Equal(121, orbit.Points.Count);
            Assert.Equal(eccentricity < 1, orbit.Points[0] == orbit.Points[^1]);
            double periapsis = star ? 149597870.7 : 1;
            var first = Assert.IsType<CartesianState>(orbit.Sample(new(2451545, 0, TimeScale.Tdb)));
            Assert.InRange(Math.Abs(first.PositionKilometers.Length - periapsis), 0, periapsis * 1e-12);
            Assert.True(first.VelocityKilometersPerSecond.IsFinite);
            Assert.NotNull(orbit.Sample(new(2451548, 0, TimeScale.Tdb)));
            Assert.Null(orbit.Sample(new(2451544, 0, TimeScale.Tdb)));
            Assert.Null(orbit.Sample(new(2451549, 0, TimeScale.Tdb)));
        }
    }

    [Fact]
    public void MissingOptionalMapsKeepTheBodyAndReportEachFallback()
    {
        var warnings = new List<string>();
        var body = Assert.Single(Read("FixedPosition [1 2 3] Texture \"missing.*\" Rings { Inner 1 Outer 2 Texture \"ring.*\" } GeomAlbedo -1", warning: warnings.Add));
        Assert.Null(body.SurfaceMap);
        Assert.Null(body.Rings);
        Assert.Null(body.GeometricAlbedo);
        Assert.Contains(warnings, message => message.Contains("Texture missing.*", StringComparison.Ordinal));
        Assert.Contains(warnings, message => message.Contains("Ring texture ring.*", StringComparison.Ordinal));
        Assert.NotNull(body.Sample(new(2451545, 0, TimeScale.Tdb)));
    }

    [Fact]
    public void CatalogueObjectAndFileBudgetsAreEnforced()
    {
        string file = Path.Combine(_root.FullName, "body.ssc");
        File.WriteAllText(file, string.Join('\n', Enumerable.Range(0, 513).Select(i => $"\"Probe{i}\" \"Sol\" {{ FixedPosition [1 0 0] }}")));
        Assert.Throws<InvalidDataException>(() => SscBodyCatalogue.Load(_root.FullName));
        File.WriteAllText(file, "");
        for (int i = 0; i < 64; i++) File.WriteAllText(Path.Combine(_root.FullName, $"extra{i}.ssc"), "");
        Assert.Throws<InvalidDataException>(() => SscBodyCatalogue.Load(_root.FullName));
        Assert.Throws<ArgumentNullException>(() => SscBodyCatalogue.LoadWithMpc(_root.FullName, null!));
    }

    public void Dispose() => _root.Delete(recursive: true);
}
