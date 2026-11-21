// SPDX-License-Identifier: GPL-2.0-or-later
using System.Globalization;
using SpaceTangent.Contracts;
using SpaceTangent.Core.Addons;
using Xunit;

namespace SpaceTangent.Core.Tests;

public sealed class MpcBoundaryContractTests : IDisposable
{
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("st-mpc-contract-");
    private const string Asteroid = "00433   10.39  0.15 K2669  62.51145  178.91814  304.26797   10.82855  0.2228780  0.55970463   1.4582437";

    private static string Put(string source, int start, int end, string value)
    {
        char[] text = source.PadRight(Math.Max(end, 194)).ToCharArray();
        if (value.Length > end - start) throw new ArgumentException("Field too long.");
        for (int index = start; index < end; index++) text[index] = ' ';
        value.CopyTo(0, text, start, value.Length);
        return new(text);
    }

    private MpcCatalogueResult Read(params string[] lines)
    {
        string path = Path.Combine(_root.FullName, "selection.mpc");
        File.WriteAllLines(path, lines);
        return MpcCatalogue.Load(path);
    }

    [Theory]
    [InlineData(0d)] [InlineData(1d)] [InlineData(1.2)]
    public void CometElementsRecoverAnalyticPerihelionPositionAndSpeed(double eccentricity)
    {
        string line = "0000C";
        foreach (var field in new (int Start, int End, string Value)[]
        {
            (14,18,"2000"),(19,21,"01"),(22,29,"01.0"),(30,39,"1.0"),
            (41,49,eccentricity.ToString(CultureInfo.InvariantCulture)),(51,59,"0"),(61,69,"0"),(71,79,"0"),
            (91,95,"10"),(102,158,"C/2000 Acceptance")
        }) line = Put(line, field.Start, field.End, field.Value);
        var result = Read(line);
        Assert.Empty(result.Warnings);
        var comet = Assert.Single(result.Orbits);
        Assert.True(comet.IsComet);
        Assert.Equal("C/2000 Acceptance", comet.Name);
        var state = comet.EclipticOrbit.Evaluate(comet.ElementEpoch);
        Assert.InRange((state.PositionKilometers - new Vector3d(149597870.7, 0, 0)).Length, 0, 1e-6);
        double speed = Math.Sqrt(132712440041.2794 * (1 + eccentricity) / 149597870.7);
        Assert.InRange((state.VelocityKilometersPerSecond - new Vector3d(0, speed, 0)).Length, 0, 1e-12);
    }

    [Theory]
    [InlineData(20, 25, "X2669")]
    [InlineData(20, 25, "K2609")]
    [InlineData(20, 25, "K26Z9")]
    [InlineData(70, 79, "1.1")]
    [InlineData(92, 103, "0")]
    [InlineData(8, 13, "-1000")]
    [InlineData(26, 35, "NaN")]
    public void InvalidPackedEpochsAndElementsAreReportedPerLine(int start, int end, string value)
    {
        var result = Read(Put(Asteroid, start, end, value));
        Assert.Empty(result.Orbits);
        Assert.StartsWith("Line 1:", Assert.Single(result.Warnings));
    }

    [Fact]
    public void InvalidLineMessagesAreBoundedWithoutLosingAValidRecord()
    {
        var lines = Enumerable.Repeat("broken", 25).Append(Asteroid).ToArray();
        var result = Read(lines);
        Assert.Single(result.Orbits);
        Assert.Equal(21, result.Warnings.Count);
        Assert.Contains("5 additional invalid", result.Warnings[^1]);
    }

    [Fact]
    public void OrbitLimitStopsAtTheDeclaredBoundary()
    {
        var lines = Enumerable.Range(1, MpcCatalogue.MaximumOrbits + 1)
            .Select(index => Put(Asteroid, 0, 7, index.ToString("D5", CultureInfo.InvariantCulture))).ToArray();
        var result = Read(lines);
        Assert.Equal(5000, result.Orbits.Count);
        Assert.Contains("5000 orbits", Assert.Single(result.Warnings));
    }

    [Fact]
    public void SscAndMpcSourcesShareAliasesAndReportDuplicatesRatherThanInstallingTwice()
    {
        File.WriteAllText(Path.Combine(_root.FullName, "body.ssc"),
            "\"Eros:433\" \"Sol\" { Radius 1 EllipticalOrbit { Period 1 SemiMajorAxis 1 } }");
        string first = Put(Asteroid, 166, 194, "(433) Eros");
        string second = Put(Put(Asteroid, 0, 7, "99942"), 166, 194, "(99942) Apophis");
        Read(first, second);
        var warnings = new List<string>();
        var bodies = SscBodyCatalogue.LoadWithMpc(_root.FullName, warnings.Add);
        Assert.Equal(2, bodies.Count);
        Assert.Contains("already has a catalogue entry", Assert.Single(warnings));
        var apophis = Assert.Single(bodies, body => body.Name == "Apophis");
        Assert.Equal("asteroid", apophis.Class);
        Assert.Contains("(99942) Apophis", apophis.Names);
        var epoch = new JulianInstant(2461200, 43200000000000, TimeScale.Tdb);
        Assert.True(apophis.Sample(epoch)!.Value.PositionKilometers.IsFinite);
        Assert.Empty(SscBodyCatalogue.LoadWithMpc(Path.Combine(_root.FullName, "absent"), _ => { }));
    }

    [Fact]
    public void OversizedAndCancelledMpcSelectionsCannotProducePartialCatalogues()
    {
        string path = Path.Combine(_root.FullName, "large.mpc");
        using (var stream = File.Create(path)) stream.SetLength(16 * 1024 * 1024 + 1);
        Assert.Throws<InvalidDataException>(() => MpcCatalogue.Load(path));
        File.WriteAllText(path, Asteroid);
        Assert.Throws<OperationCanceledException>(() => MpcCatalogue.Load(path, new CancellationToken(true)));
    }

    public void Dispose() => _root.Delete(true);
}
