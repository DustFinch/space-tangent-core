// SPDX-License-Identifier: GPL-2.0-or-later
using System.Buffers.Binary;
using System.Text;
using SpaceTangent.Contracts;
using SpaceTangent.Core.Addons;
using SpaceTangent.Core.Spice;
using Xunit;

namespace SpaceTangent.Core.Tests;

public sealed class AccuracyImportTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SyntheticChebyshevKernelsSupportBothByteOrdersAndQuotedParentheses(bool bigEndian)
    {
        var root = Directory.CreateTempSubdirectory("st-kernel-");
        try
        {
            string kernel = Path.Combine(root.FullName, "kernel (revision 1).bsp");
            File.WriteAllBytes(kernel, Kernel(bigEndian));
            var epoch = JulianInstant.FromTdbSeconds(5);
            Assert.True(new SpkKernel(kernel).TryState(3031, 0, epoch, out var state));
            Assert.Equal(new Vector3d(1, 2, 3), state.PositionKilometers);
            Assert.Equal(default, state.VelocityKilometersPerSecond);
            File.WriteAllText(Path.Combine(root.FullName, "quoted.tm"), """
                KPL/MK
                \begindata
                KERNELS_TO_LOAD = ( 'kernel (revision 1).bsp' )
                """);
            File.WriteAllText(Path.Combine(root.FullName, "probe.ssc"), """
                "Probe" "Sol" {
                    OrbitFrame { EquatorJ2000 {} }
                    SpiceOrbit { Kernel "quoted.tm" Target "3031" Origin "0" BoundingRadius 100 }
                    FixedPosition [9 0 0]
                }
                """);
            var warnings = new List<string>();
            var probe = Assert.Single(SscBodyCatalogue.Load(root.FullName, warning: warnings.Add));
            Assert.Empty(warnings);
            Assert.InRange(Math.Abs(probe.Sample(epoch)!.Value.PositionKilometers.Length - state.PositionKilometers.Length), 0, 1e-12);

            // A broken optional kernel must take the authored fallback, not abort the catalogue.
            File.WriteAllBytes(kernel, new byte[17]);
            warnings.Clear();
            var fallback = Assert.Single(SscBodyCatalogue.Load(root.FullName, warning: warnings.Add));
            Assert.NotEmpty(warnings);
            Assert.NotNull(fallback.Sample(epoch));
        }
        finally { root.Delete(true); }
    }

    [Fact]
    public void SwarmStorageIsSharedWithinOneLoadAndRefreshedOnTheNextLoad()
    {
        var root = Directory.CreateTempSubdirectory("st-swarm-cache-");
        try
        {
            string source = Path.Combine(root.FullName, "orbits.dat");
            // Original 36-byte big-endian solar swarm record: six floats, a JD double, discovery JD float.
            var record = new byte[36];
            float[] fields = [2.5f, .1f, 3, 4, 5, 6];
            for (int i = 0; i < fields.Length; i++) BinaryPrimitives.WriteSingleBigEndian(record.AsSpan(i * 4), fields[i]);
            BinaryPrimitives.WriteDoubleBigEndian(record.AsSpan(24), 2451545);
            BinaryPrimitives.WriteSingleBigEndian(record.AsSpan(32), 2451545);
            File.WriteAllBytes(source, record);
            File.WriteAllText(Path.Combine(root.FullName, "catalog.json"), """
                {"version":"1.0","items":[
                    {"name":"First","center":"Sun","geometry":{"type":"KeplerianSwarm","format":"binary","source":"orbits.dat"}},
                    {"name":"Second","center":"Sun","geometry":{"type":"KeplerianSwarm","format":"binary","source":"orbits.dat"}}
                ]}
                """);
            MissionSwarm[] Load() => SampledMissionCatalog.Load("catalog.json", root.FullName, _ => new ObjectId("sun"))
                .Select(mission => Assert.Single(mission.Geometry!).Shape.Swarm!.Elements).ToArray();
            var first = Load();
            Assert.Equal(2, first.Length);
            Assert.Same(first[0], first[1]);
            var next = Load();
            Assert.NotSame(first[0], next[0]);
            Assert.Same(next[0], next[1]);
            Assert.True(first[0].Attributes.SequenceEqual(next[0].Attributes));
        }
        finally { root.Delete(true); }
    }

    private static byte[] Kernel(bool bigEndian)
    {
        byte[] bytes = new byte[4096];
        Encoding.ASCII.GetBytes("DAF/SPK ").CopyTo(bytes, 0);
        Encoding.ASCII.GetBytes(bigEndian ? "BIG-IEEE" : "LTL-IEEE").CopyTo(bytes, 88);
        Integer(8, 2); Integer(12, 6); Integer(76, 2); Integer(80, 2); Integer(84, 394);
        Number(1024, 0); Number(1032, 0); Number(1040, 1);
        Number(1048, 0); Number(1056, 10);
        Integer(1064, 3031); Integer(1068, 0); Integer(1072, 1); Integer(1076, 2);
        Integer(1080, 385); Integer(1084, 393);
        double[] data = [5, 5, 1, 2, 3, 0, 10, 5, 1];
        for (int i = 0; i < data.Length; i++) Number(3072 + i * 8, data[i]);
        return bytes;

        void Integer(int offset, int value)
        {
            if (bigEndian) BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(offset), value);
            else BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(offset), value);
        }
        void Number(int offset, double value)
        {
            if (bigEndian) BinaryPrimitives.WriteDoubleBigEndian(bytes.AsSpan(offset), value);
            else BinaryPrimitives.WriteDoubleLittleEndian(bytes.AsSpan(offset), value);
        }
    }
}
