// SPDX-License-Identifier: GPL-2.0-or-later
using SpaceTangent.Contracts;
using SpaceTangent.Core.Addons;
using Xunit;

namespace SpaceTangent.Core.Tests;

public sealed class ScriptFactoryBoundaryTests : IDisposable
{
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("st-factory-boundaries-");
    private static readonly JulianInstant Epoch = new(2451545, 0, TimeScale.Tdb);
    private IReadOnlyList<SscBodyDefinition> Load(Factory factory, List<string>? warnings = null, string module = "factory")
    {
        File.WriteAllText(Path.Combine(_root.FullName, "factory.lua"), "-- Authored module resolution witness; execution is supplied by the test factory.\n");
        File.WriteAllText(Path.Combine(_root.FullName, "witness.ssc"), $$"""
            "Witness" "Sol/Earth" { Radius 1 FixedPosition [1 2 3]
              ScriptedOrbit { Module "{{module}}" Function "make" Enabled true Disabled false Count 2.5
                Options { Name "nested" } Items [1 "two" false] }
            }
            """);
        return SscBodyCatalogue.Load(_root.FullName, scripts: factory, warning: warnings is null ? null : warnings.Add);
    }

    [Fact]
    public void FactoriesReceiveTypedNestedArgumentsAndTheResolvedLocalModule()
    {
        var factory = new Factory(); Assert.Single(Load(factory));
        var request = Assert.IsType<SscScriptRequest>(factory.Request);
        Assert.Equal(Path.Combine(_root.FullName, "factory.lua"), request.ModulePath);
        Assert.Equal("make", request.Function);
        Assert.Equal(true, request.Arguments["Enabled"]); Assert.Equal(false, request.Arguments["Disabled"]);
        Assert.Equal(2.5, request.Arguments["Count"]);
        Assert.Equal("nested", Assert.IsAssignableFrom<IReadOnlyDictionary<string, object>>(request.Arguments["Options"])["Name"]);
        Assert.Equal(new object[] { 1d, "two", false }, Assert.IsAssignableFrom<IReadOnlyList<object>>(request.Arguments["Items"]));
    }

    [Theory]
    [InlineData("io")]
    [InlineData("invalid")]
    [InlineData("argument")]
    [InlineData("reject")]
    public void RejectedOrFailingFactoriesRetainTheDeclaredFixedPositionFallback(string kind)
    {
        var factory = new Factory { Failure = kind }; var warnings = new List<string>();
        var body = Assert.Single(Load(factory, warnings));
        Assert.Equal(new Vector3d(1, 2, 3), body.Sample(Epoch)!.Value.PositionKilometers);
        Assert.Contains("initialization failed", Assert.Single(warnings));
    }

    [Fact]
    public void InvalidModuleMetadataReportsFallbackWithoutCallingTheFactory()
    {
        var factory = new Factory(); var warnings = new List<string>();
        Assert.NotNull(Assert.Single(Load(factory, warnings, module: "")).Sample(Epoch));
        Assert.Null(factory.Request); Assert.Contains("module or function", Assert.Single(warnings));
    }

    [Theory]
    [InlineData("past")]
    [InlineData("none")]
    [InlineData("nonfinite")]
    public void ScriptStateDerivativeUsesAValidNeighborOrReportsNoState(string available)
    {
        var factory = new Factory { Orbit = new(epoch =>
        {
            double seconds = (double)epoch.DifferenceNanoseconds(Epoch) / JulianInstant.NanosecondsPerSecond;
            if (available == "nonfinite") return new(double.NaN, 0, 0);
            return seconds == 0 ? new(1, 2, 3) : available == "past" && seconds < 0 ? new(seconds + 1, 2, 3) : null;
        }) };
        var body = Assert.Single(Load(factory)); var state = body.Sample(Epoch);
        if (available == "past")
        {
            Assert.Equal(new Vector3d(1, 2, 3), state!.Value.PositionKilometers);
            Assert.Equal(new Vector3d(1, 0, 0), state.Value.VelocityKilometersPerSecond);
        }
        else Assert.Null(state);
    }

    private sealed class Factory : ISscScriptModels
    {
        internal SscScriptRequest? Request;
        internal string? Failure;
        internal SscScriptOrbit Orbit = new(_ => new(1, 2, 3));
        public SscScriptOrbit? CreateOrbit(SscScriptRequest request)
        {
            Request = request;
            return Failure switch { "io" => throw new IOException("witness"), "invalid" => throw new InvalidDataException("witness"),
                "argument" => throw new ArgumentException("witness"), "reject" => null, _ => Orbit };
        }
        public SscScriptRotation? CreateRotation(SscScriptRequest request) => null;
    }

    public void Dispose() => _root.Delete(recursive: true);
}
