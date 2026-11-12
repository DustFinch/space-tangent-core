// SPDX-License-Identifier: GPL-2.0-or-later
using SpaceTangent.Contracts;
using SpaceTangent.Core.Addons;
using Xunit;

namespace SpaceTangent.Core.Tests;

public sealed class SscScriptModelCatalogueTests
{
    [ScriptFactoryCorpusFact]
    [Trait("Category", "ExternalData")]
    public void ArchivedSscKeepsScriptOrderFallbacksAndTypedParameters()
    {
        string root = Environment.GetEnvironmentVariable("SPACETANGENT_SCRIPTED_SSC")!;
        var warnings = new List<string>();
        var probe = new RecordingFactories();
        var loaded = SscBodyCatalogue.Load(root, scripts: probe, warning: warnings.Add);
        Assert.Equal(["rejectOrbit", "rejectOrbit", "rejectSpin", "make", "turn"], probe.Calls);
        Assert.Equal(["Fallback Sample", "Fallback Spin", "Cassini SOI"], loaded.Select(body => body.Name));
        Assert.Equal(3, warnings.Count);
        var sample = JulianInstant.FromJulianDate(2453187.55, TimeScale.Tdb);
        Assert.NotNull(loaded[0].Sample(sample));
        Assert.NotNull(loaded[1].SampleRotation!(sample));
        Assert.Equal(Basis3d.Identity, loaded[2].SampleRotation!(sample)!.Value.ToWorld);
        var satellite = loaded[2].Sample(sample)!.Value;
        Assert.InRange(satellite.PositionKilometers.Length, 100_002, 100_003);
        Assert.Equal(0, satellite.VelocityKilometersPerSecond.Length);
        Assert.EndsWith("cassini.3ds", loaded[2].Mesh, StringComparison.OrdinalIgnoreCase);

        warnings.Clear();
        var disabled = SscBodyCatalogue.Load(root, warning: warnings.Add);
        Assert.Equal(["Fallback Sample", "Fallback Spin"], disabled.Select(body => body.Name));
        Assert.Contains(warnings, warning => warning.Contains("alternate SampledTrajectory resource is unavailable", StringComparison.Ordinal));
    }

    [PackedFactoryCorpusFact]
    [Trait("Category", "ExternalData")]
    public void OriginalPackUsesItsEmbeddedSscAndDeclaredLocalResources()
    {
        string root = Environment.GetEnvironmentVariable("SPACETANGENT_SCRIPTED_PACK")!;
        var warnings = new List<string>();
        var probe = new RecordingFactories();
        var (loaded, inputs) = AddonDependencies.Capture(() => SscBodyCatalogue.Load(root, scripts: probe, warning: warnings.Add));
        Assert.Equal(["rejectOrbit", "rejectOrbit", "rejectSpin", "make", "turn"], probe.Calls);
        Assert.Equal(["Fallback Sample", "Fallback Spin", "Cassini SOI"], loaded.Select(body => body.Name));
        Assert.Equal(3, warnings.Count);
        Assert.EndsWith("cassini.glb", loaded[2].Mesh, StringComparison.OrdinalIgnoreCase);
        Assert.True(inputs.IsCurrent());
        var instant = JulianInstant.FromJulianDate(2453187.55, TimeScale.Tdb);
        Assert.NotNull(loaded[0].Sample(instant));
        Assert.NotNull(loaded[1].SampleRotation!(instant));
    }

    private sealed class RecordingFactories : ISscScriptModels
    {
        public List<string> Calls { get; } = [];

        public SscScriptOrbit? CreateOrbit(SscScriptRequest request)
        {
            Calls.Add(request.Function);
            Assert.Equal("factory", request.Arguments["Module"]);
            Assert.EndsWith("factory.lua", request.ModulePath, StringComparison.Ordinal);
            if (request.Function != "make")
                return null;
            Assert.Equal(true, request.Arguments["Enabled"]);
            Assert.Equal(1.25, request.Arguments["Number"]);
            return new(_ => new Vector3d(100_000, -500, 500));
        }

        public SscScriptRotation? CreateRotation(SscScriptRequest request)
        {
            Calls.Add(request.Function);
            return request.Function == "turn" ? new(_ => new SscScriptQuaternion(1, 0, 0, 0)) : null;
        }
    }

    private sealed class ScriptFactoryCorpusFactAttribute : FactAttribute
    {
        public ScriptFactoryCorpusFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SPACETANGENT_SCRIPTED_SSC")))
                Skip = "Set the path to the archived scripted body catalogue.";
        }
    }

    private sealed class PackedFactoryCorpusFactAttribute : FactAttribute
    {
        public PackedFactoryCorpusFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SPACETANGENT_SCRIPTED_PACK")))
                Skip = "Set the path to the original packed scripted catalogue.";
        }
    }
}
