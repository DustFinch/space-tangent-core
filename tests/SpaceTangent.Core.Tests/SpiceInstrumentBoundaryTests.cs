// SPDX-License-Identifier: GPL-2.0-or-later
using System.Buffers.Binary;
using System.Text;
using SpaceTangent.Contracts;
using SpaceTangent.Core.Addons;
using Xunit;

namespace SpaceTangent.Core.Tests;

public sealed class SpiceInstrumentBoundaryTests : IDisposable
{
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("st-instrument-boundaries-");
    private IReadOnlyList<SampledMission> Load(string definitions, string bodyFrame = "J2000")
    {
        // An authored empty CK container satisfies the frame-pool dependency for
        // symbolic alias validation. No spacecraft pointing is asserted here.
        var empty = new byte[1024]; Encoding.ASCII.GetBytes("DAF/CK  ").CopyTo(empty, 0);
        BinaryPrimitives.WriteInt32LittleEndian(empty.AsSpan(8), 2);
        BinaryPrimitives.WriteInt32LittleEndian(empty.AsSpan(12), 6);
        BinaryPrimitives.WriteInt32LittleEndian(empty.AsSpan(84), 129);
        Encoding.ASCII.GetBytes("LTL-IEEE").CopyTo(empty, 88);
        File.WriteAllBytes(Path.Combine(_root.FullName, "empty.bc"), empty);
        File.WriteAllText(Path.Combine(_root.FullName, "instrument.tf"), "KPL/FK\n\\begindata\n" + definitions + "\n\\begintext\n");
        File.WriteAllText(Path.Combine(_root.FullName, "mission.json"), """
            {"version":"1.0","spiceKernels":["instrument.tf","empty.bc"],"items":[
             {"name":"Craft","center":"Earth","trajectory":{"type":"FixedPoint","position":[0,0,10]},"bodyFrame":{"type":"Spice","name":"J2000"}},
             {"name":"Instrument","center":"Craft","geometry":{"type":"Spice","instrName":"-1001","target":"Earth","range":1000}}]}
            """.Replace("\"name\":\"J2000\"", "\"name\":\"" + bodyFrame + "\""));
        return SampledMissionCatalog.Load("mission.json", _root.FullName, name => name == "Earth" ? new ObjectId("earth") : null);
    }
    private const string Metadata = """
        FRAME_INST = 1001
        FRAME_1001_CLASS = 4
        FRAME_1001_CLASS_ID = 1001
        TKFRAME_1001_RELATIVE = 'J2000'
        INS-1001_FOV_FRAME = 'INST'
        INS-1001_FOV_SHAPE = 'CIRCLE'
        INS-1001_BORESIGHT = (0 0 1)
        INS-1001_FOV_BOUNDARY_CORNERS = (.1 0 1)
        """;

    [Theory]
    [InlineData("MATRIX", "TKFRAME_1001_MATRIX = (1 0 0 0 1 0 0 0 1)")]
    [InlineData("QUATERNION", "TKFRAME_1001_Q = (2 0 0 0)")]
    [InlineData("ANGLES", "TKFRAME_1001_ANGLES = (0 0 0)\nTKFRAME_1001_AXES = (1 2 3)\nTKFRAME_1001_UNITS = 'DEGREES'")]
    public void FixedInstrumentDefinitionsPreserveBoresightAndUnitBoundaryRays(string kind, string parameters)
    {
        var tracks = Load(Metadata + $"\nTKFRAME_1001_SPEC = '{kind}'\n" + parameters);
        var sensor = Assert.Single(tracks.Single(item => item.Name == "Instrument").Geometry!).Shape.Sensor!;
        Assert.Equal(new Vector3d(0, 0, 1), sensor.Boresight);
        Assert.Equal(32, sensor.Boundary.Count);
        double expected = 1 / Math.Sqrt(1.01);
        Assert.All(sensor.Boundary, ray =>
        {
            Assert.InRange(Math.Abs(ray.Length - 1), 0, 1e-12);
            Assert.InRange(Math.Abs(ray.Z - expected), 0, 1e-12);
        });
    }

    [Theory]
    [InlineData("cycle")]
    [InlineData("matrix")]
    [InlineData("matrix-count")]
    [InlineData("text-value")]
    public void InvalidFixedFrameDefinitionsCannotPublishAnInstrument(string fault)
    {
        string source = Metadata + "\nTKFRAME_1001_SPEC = 'MATRIX'\nTKFRAME_1001_MATRIX = (1 0 0 0 1 0 0 0 1)\n";
        source = fault switch
        {
            "cycle" => source.Replace("RELATIVE = 'J2000'", "RELATIVE = 'INST'"),
            "matrix" => source.Replace("(1 0 0 0 1 0 0 0 1)", "(2 0 0 0 1 0 0 0 1)"),
            "matrix-count" => source.Replace("(1 0 0 0 1 0 0 0 1)", "(1 0 0)"),
            _ => source.Replace("SPEC = 'MATRIX'", "SPEC = ('MATRIX' 'ANGLES')")
        };
        Assert.Throws<InvalidDataException>(() => Load(source));
    }

    [Theory]
    [InlineData("unknown-spec")]
    [InlineData("unknown-unit")]
    public void UnsupportedFixedFrameConventionsAreReportedWithoutGuessing(string fault)
    {
        string source = Metadata + (fault == "unknown-spec" ? "\nTKFRAME_1001_SPEC = 'UNKNOWN'\n" :
            "\nTKFRAME_1001_SPEC = 'ANGLES'\nTKFRAME_1001_ANGLES=(0 0 0)\nTKFRAME_1001_AXES=(1 2 3)\nTKFRAME_1001_UNITS='UNKNOWN'\n");
        Assert.Throws<NotSupportedException>(() => Load(source));
    }

    [Theory]
    [InlineData(true)] [InlineData(false)]
    public void DistinctCkFrameNamesMustReferToTheSameInstrument(bool sameInstrument)
    {
        string source = Metadata.Replace("RELATIVE = 'J2000'", "RELATIVE = 'ROOT'") + "\n" +
            "TKFRAME_1001_SPEC='MATRIX'\nTKFRAME_1001_MATRIX=(1 0 0 0 1 0 0 0 1)\n" +
            "FRAME_ROOT=2001\nFRAME_2001_CLASS=3\nFRAME_2001_CLASS_ID=-1000\n" +
            "FRAME_BODY=2000\nFRAME_2000_CLASS=3\nFRAME_2000_CLASS_ID=" + (sameInstrument ? "-1000" : "-2000") + "\n" +
            "CK_-1000_SCLK=-1\nCK_-2000_SCLK=-1\nSCLK_DATA_TYPE_1=1\nSCLK01_MODULI_1=(4294967296 1)\n" +
            "SCLK_PARTITION_START_1=(0)\nSCLK_PARTITION_END_1=(1000000000000)\nSCLK01_COEFFICIENTS_1=(0 0 1)\n";
        if (sameInstrument)
            Assert.Equal(32, Assert.Single(Load(source, "BODY").Single(track => track.Name == "Instrument").Geometry!).Shape.Sensor!.Boundary.Count);
        else
            Assert.Throws<NotSupportedException>(() => Load(source, "BODY"));
    }

    [Fact]
    public void NamedFixedFrameKeysResolveWhenNumericDefinitionsAreAbsent()
    {
        string source = Metadata.Replace("TKFRAME_1001_RELATIVE = 'J2000'", "") +
            "\nFRAME_1001_NAME='INST'\nTKFRAME_INST_SPEC='MATRIX'\nTKFRAME_INST_MATRIX=(1 0 0 0 1 0 0 0 1)\n";
        Assert.Equal(new Vector3d(0, 0, 1), Assert.Single(Load(source).Single(track => track.Name == "Instrument").Geometry!).Shape.Sensor!.Boresight);
    }

    public void Dispose() => _root.Delete(recursive: true);
}
