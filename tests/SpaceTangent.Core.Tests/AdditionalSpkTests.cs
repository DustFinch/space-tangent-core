// SPDX-License-Identifier: GPL-2.0-or-later
using System.Text.Json;
using SpaceTangent.Contracts;
using SpaceTangent.Core.Spice;
using Xunit;

namespace SpaceTangent.Core.Tests;

public sealed class AdditionalSpkTests
{
    [SpkReferenceFact]
    [Trait("Category", "ExternalData")]
    public void SampledAndAnalyticSegmentsAgreeWithIndependentSpice()
    {
        string folder = Environment.GetEnvironmentVariable("SPACETANGENT_SPK_ORACLE")!;
        string archive = Environment.GetEnvironmentVariable("SPACETANGENT_SPK_ARCHIVE")!;
        using var json = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(folder, "spk-reference.json")));
        var types = new HashSet<int>();
        int compared = 0;
        foreach (var item in json.RootElement.EnumerateObject())
        {
            string path = Path.Combine(item.Name == "sliding-windows.bsp" ? folder : archive, item.Name);
            var kernel = new SpkKernel(path);
            foreach (var row in item.Value.EnumerateArray())
            {
                double seconds = row.GetProperty("et").GetDouble();
                int kind = row.GetProperty("kind").GetInt32();
                Assert.True(kernel.TryState(row.GetProperty("target").GetInt32(), row.GetProperty("center").GetInt32(),
                    JulianInstant.FromTdbSeconds(seconds), out var actual));
                var expected = row.GetProperty("state");
                var position = new Vector3d(expected[0].GetDouble(), expected[1].GetDouble(), expected[2].GetDouble());
                var velocity = new Vector3d(expected[3].GetDouble(), expected[4].GetDouble(), expected[5].GetDouble());
                double displacement = (actual.PositionKilometers - position).Length;
                double speed = (actual.VelocityKilometersPerSecond - velocity).Length;
                Assert.True(displacement < (kind == 17 ? 2e-5 : 2e-7) && speed < (kind == 17 ? 2e-8 : 2e-9),
                    $"Type {kind} at {seconds:R}: position {displacement:R} km, velocity {speed:R} km/s.");
                types.Add(kind);
                compared++;
            }
        }
        Assert.Equal(140, compared);
        Assert.Equal(new[] { 1, 8, 9, 12, 13, 17, 21 }, types.Order());
    }

    private sealed class SpkReferenceFactAttribute : FactAttribute
    {
        public SpkReferenceFactAttribute()
        {
            if (new[] { "SPACETANGENT_SPK_ORACLE", "SPACETANGENT_SPK_ARCHIVE" }
                .Any(key => string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(key))))
                Skip = "Set the native SPK oracle and the original comet and moon kernel archive.";
        }
    }
}
