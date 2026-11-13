// SPDX-License-Identifier: GPL-2.0-or-later
using System.Text.Json;
using SpaceTangent.Contracts;
using SpaceTangent.Core.Addons;
using Xunit;

namespace SpaceTangent.Core.Tests;

public sealed class SscSourceOrderTests
{
    [Fact]
    public void PackedBaseAndLooseEditsShareAliasesButKeepResourceOwnership()
    {
        var scratch = Directory.CreateTempSubdirectory("space-ssc-sources-");
        try
        {
            string first = Directory.CreateDirectory(Path.Combine(scratch.FullName, "base")).FullName;
            string second = Directory.CreateDirectory(Path.Combine(scratch.FullName, "modify")).FullName;
            string last = Directory.CreateDirectory(Path.Combine(scratch.FullName, "replace")).FullName;
            string model = Path.Combine(Directory.CreateDirectory(Path.Combine(first, "models")).FullName, "base.obj");
            File.WriteAllText(model, "# path witness");
            File.WriteAllText(Path.Combine(first, "pack.json"), JsonSerializer.Serialize(new
            {
                formatVersion = 11,
                sscCatalogs = new[]
                {
                    new
                    {
                        name = "base.ssc",
                        text = """
                            "Probe:Alias" "Sol/Earth" { Radius 1 Mesh "probe.obj" FixedPosition [ 7000 0 0 ] }
                            "Child" "Sol/Earth/Alias" { Radius 1 FixedPosition [ 1 2 3 ] }
                            """,
                        resources = new[] { new { kind = "model", source = "probe.obj", file = "base.obj" } }
                    }
                }
            }));
            File.WriteAllText(Path.Combine(second, "change.ssc"), """
                Modify "Alias" "Sol/Earth" { Radius 2 }
                """);
            File.WriteAllText(Path.Combine(last, "replace.ssc"), """
                Replace "Alias" "Sol/Earth" { Radius 3 FixedPosition [ 7100 0 0 ] }
                """);
            var changed = SscBodyCatalogue.LoadSources([first, second], _ => { });
            var probe = Assert.Single(changed, body => body.Name == "Probe");
            Assert.Equal(model, probe.Mesh);
            Assert.Equal(first, probe.MeshRoot);
            Assert.Equal(2, probe.RadiusKilometers);
            Assert.Equal("Sol/Earth/Probe", Assert.Single(changed, body => body.Name == "Child").Parent);

            var replaced = Assert.Single(SscBodyCatalogue.LoadSources([first, second, last], _ => { }), body => body.Name == "Probe");
            Assert.Equal("Probe:Alias", replaced.Names);
            Assert.Null(replaced.Mesh);
            Assert.Equal(3, replaced.RadiusKilometers);
            Assert.Equal(7100, replaced.Sample(new JulianInstant(2451545, 0, TimeScale.Tdb))!.Value.PositionKilometers.X);
        }
        finally { scratch.Delete(recursive: true); }
    }
}
