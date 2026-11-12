// SPDX-License-Identifier: GPL-2.0-or-later
using SpaceTangent.Contracts;
using SpaceTangent.Core.Addons;
using Xunit;

namespace SpaceTangent.Core.Tests;

public sealed class OrderedSpiceCatalogueTests
{
    [OrderedSpiceFact]
    [Trait("Category", "ExternalData")]
    public void LaterKernelOnlyCatalogueCompletesTheOriginalCassiniInstrumentFrames()
    {
        string global = Environment.GetEnvironmentVariable("SPACETANGENT_SPICE_GLOBAL")!;
        string mission = Environment.GetEnvironmentVariable("SPACETANGENT_CASSINI_JSON_ROOT")!;
        string archive = Environment.GetEnvironmentVariable("SPACETANGENT_SPK_ARCHIVE")!;
        string oracle = Environment.GetEnvironmentVariable("SPACETANGENT_SPK_ORACLE")!;
        var folder = Directory.CreateTempSubdirectory();
        try
        {
            Copy(global, "cassini-catalog.json");
            Copy(global, "late.dat");
            Copy(archive, "naif0012.tls");
            foreach (string name in new[] { "040615AP_SK_04167_04186.bsp", "04182_04183pa_soi_itl.bc", "cas_iss_v10.ti", "cassini.3ds" })
                Copy(mission, name);
            File.WriteAllText(Path.Combine(folder.FullName, "late-catalogue.json"), """{"version":"1.0","spiceKernels":["late.dat"]}""");
            Assert.Throws<InvalidDataException>(() => SpiceJsonCatalog.Load("cassini-catalog.json", folder.FullName));
            var combined = SpiceJsonCatalog.Load(["cassini-catalog.json", "late-catalogue.json"], folder.FullName);
            var reference = SpiceJsonCatalog.Load("cassini.json", mission);
            Assert.Equal(2, combined.SensorCount);
            var track = Assert.Single(combined.Spacecraft);
            var original = Assert.Single(reference.Spacecraft);
            Assert.Equal("META_CASSINI", track.AttitudeFrame);
            double cos = .9063077870366499, sin = .42261826174069944;
            var toOriginal = new Basis3d(new(cos, -sin, 0), new(sin, cos, 0), new(0, 0, 1));
            foreach (double second in new[] { 141917558.4203589, 141918830.41202343 })
            {
                var epoch = JulianInstant.FromTdbSeconds(second);
                Assert.True(track.TrySample(epoch, out var state));
                Assert.True(original.TrySample(epoch, out var expected));
                Assert.Equal(expected, state);
                Assert.True(track.TryAttitude(epoch, out var attitude));
                Assert.True(original.TryAttitude(epoch, out var expectedAttitude));
                Assert.InRange((expectedAttitude.Transform(toOriginal.X) - attitude.X).Length, 0, 3e-8);
                Assert.InRange((expectedAttitude.Transform(toOriginal.Y) - attitude.Y).Length, 0, 3e-8);
                Assert.InRange((expectedAttitude.Transform(toOriginal.Z) - attitude.Z).Length, 0, 3e-8);
            }
            foreach (var sensor in combined.Sensors)
            {
                var expected = Assert.Single(reference.Sensors, item => item.Name == sensor.Name);
                Assert.InRange((expected.BoresightInBody - toOriginal.Transform(sensor.BoresightInBody)).Length, 0, 1e-14);
                Assert.Equal(expected.CornersInBody.Count, sensor.CornersInBody.Count);
                for (int index = 0; index < expected.CornersInBody.Count; index++)
                    Assert.InRange((expected.CornersInBody[index] - toOriginal.Transform(sensor.CornersInBody[index])).Length, 0, 1e-14);
            }

            Copy(oracle, "fixed-frames.tf");
            File.WriteAllText(Path.Combine(folder.FullName, "fixed-catalogue.json"), """
                {"version":"1.0","spiceKernels":["040615AP_SK_04167_04186.bsp","fixed-frames.tf"],"items":[
                  {"name":"Matrix Attitude","class":"spacecraft","center":"Saturn","trajectory":{"type":"Spice","target":"-82","center":"6"},
                   "bodyFrame":{"type":"Spice","name":"MATRIX_TEST"},"geometry":{"type":"Mesh","source":"cassini.3ds","size":0.006}},
                  {"name":"Quaternion Attitude","class":"spacecraft","center":"Saturn","trajectory":{"type":"Spice","target":"-82","center":"6"},
                   "bodyFrame":{"type":"Spice","name":"QUATERNION_TEST"},"geometry":{"type":"Mesh","source":"cassini.3ds","size":0.006}}
                ]}
                """);
            var fixedFrames = SpiceJsonCatalog.Load("fixed-catalogue.json", folder.FullName);
            Assert.Equal(2, fixedFrames.Spacecraft.Count);
            foreach (var fixedTrack in fixedFrames.Spacecraft)
            {
                Assert.True(fixedTrack.TryAttitude(JulianInstant.FromTdbSeconds(141917558.4203589), out var attitude));
                Assert.InRange((new Vector3d(0, 1, 0) - attitude.X).Length, 0, 1e-14);
                Assert.InRange((new Vector3d(-1, 0, 0) - attitude.Y).Length, 0, 1e-14);
            }
        }
        finally { folder.Delete(recursive: true); }

        void Copy(string source, string name) => File.Copy(Path.Combine(source, name), Path.Combine(folder.FullName, name));
    }

    private sealed class OrderedSpiceFactAttribute : FactAttribute
    {
        public OrderedSpiceFactAttribute()
        {
            if (new[] { "SPACETANGENT_SPICE_GLOBAL", "SPACETANGENT_CASSINI_JSON_ROOT", "SPACETANGENT_SPK_ARCHIVE", "SPACETANGENT_SPK_ORACLE" }
                .Any(key => string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(key))))
                Skip = "Set the archived SPICE pool and original Cassini kernel roots.";
        }
    }
}
