// SPDX-License-Identifier: GPL-2.0-or-later
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using Xunit;

namespace SpaceTangent.Native.Tests;

// Invoke the actual exported C entry points with their unmanaged calling convention.
// Discover by export attribute, never by private implementation method or field name.
// The managed IL run supports line instrumentation; NativeAOT is verified separately.
public sealed unsafe class AbiContractTests
{
    [StructLayout(LayoutKind.Sequential)]
    private struct State { public double X, Y, Z, Vx, Vy, Vz; }

    private static nint Export(string name)
    {
        var assembly = Assembly.Load("SpaceTangentNative");
        var method = Assert.Single(assembly.GetTypes().SelectMany(type => type.GetMethods(BindingFlags.Public | BindingFlags.Static)),
            method => method.GetCustomAttribute<UnmanagedCallersOnlyAttribute>()?.EntryPoint == name);
        return method.MethodHandle.GetFunctionPointer();
    }

    private static readonly delegate* unmanaged[Cdecl]<uint> Version = (delegate* unmanaged[Cdecl]<uint>)Export("st_abi_version");
    private static readonly delegate* unmanaged[Cdecl]<byte*, int, nuint*, int> Open = (delegate* unmanaged[Cdecl]<byte*, int, nuint*, int>)Export("st_de440_open");
    private static readonly delegate* unmanaged[Cdecl]<nuint, int> Close = (delegate* unmanaged[Cdecl]<nuint, int>)Export("st_de440_close");
    private static readonly delegate* unmanaged[Cdecl]<nuint, long, long, int, int, State*, int> Sample = (delegate* unmanaged[Cdecl]<nuint, long, long, int, int, State*, int>)Export("st_de440_state");

    [Fact]
    public void VersionAndWireStateLayoutMatchTheDocumentedAbi()
    {
        Assert.Equal(0x10000u, Version());
        Assert.Equal(48, sizeof(State));
        Assert.Equal(24, Marshal.OffsetOf<State>(nameof(State.Vx)).ToInt32());
    }

    [Theory]
    [InlineData(0)] [InlineData(-1)] [InlineData(4097)]
    public void InvalidPathLengthsClearOutput(int length)
    {
        nuint handle = 99;
        byte path = 65;
        Assert.Equal(1, Open(&path, length, &handle));
        Assert.Equal((nuint)0, handle);
    }

    [Fact]
    public void NullPointersAndInvalidHandlesReturnStatusesWithoutDereference()
    {
        nuint handle = 99;
        Assert.Equal(1, Open(null, 1, &handle));
        Assert.Equal((nuint)0, handle);
        Assert.Equal(1, Open(null, 1, null));
        Assert.Equal(2, Close(0));
        Assert.Equal(2, Close(nuint.MaxValue));
        Assert.Equal(1, Sample(0, 2451545, 0, 301, 399, null));
        State state = new() { X = 99, Y = 99, Z = 99, Vx = 99, Vy = 99, Vz = 99 };
        Assert.Equal(2, Sample(0, 2451545, 0, 301, 399, &state));
        AssertZero(state);
    }

    [Theory]
    [InlineData("relative.bin")]
    [InlineData("/tmp/embedded\0suffix.bin")]
    public void InvalidPathContentsAreRejectedBeforeOpening(string path)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(path);
        nuint handle = 99;
        fixed(byte* pointer = bytes) Assert.Equal(1, Open(pointer, bytes.Length, &handle));
        Assert.Equal((nuint)0, handle);
    }

    [Fact]
    public void MalformedUtf8MissingFileAndInvalidKernelHaveDistinctStatuses()
    {
        byte[] invalid = [0xc3, 0x28];
        nuint handle = 99;
        fixed(byte* pointer = invalid) Assert.Equal(1, Open(pointer, invalid.Length, &handle));
        Assert.Equal((nuint)0, handle);
        string path = Path.Combine(Path.GetTempPath(), "st-native-" + Guid.NewGuid().ToString("N") + ".bin");
        Assert.Equal(3, OpenFile(path, out handle));
        Assert.Equal((nuint)0, handle);
        try
        {
            File.WriteAllText(path, "not a DE440 model");
            Assert.Equal(4, OpenFile(path, out handle));
            Assert.Equal((nuint)0, handle);
        }
        finally { File.Delete(path); }
    }

    [ModelFact]
    public void MoonStateMatchesCspiceAndClosedHandlesCannotBeReused()
    {
        Assert.Equal(0, OpenFile(Environment.GetEnvironmentVariable("SPACETANGENT_DE440")!, out nuint handle));
        Assert.NotEqual((nuint)0, handle);
        try
        {
            State state;
            Assert.Equal(0, Sample(handle, 2451545, 0, 301, 399, &state));
            Near(-291608.38463343546, state.X, 5e-6);
            Near(-266716.83339423337, state.Y, 5e-6);
            Near(-76102.48709990202, state.Z, 5e-6);
            Near(.64353138771903273, state.Vx, 5e-10);
            Near(-.66608768409163044, state.Vy, 5e-10);
            Near(-.30132570498227307, state.Vz, 5e-10);
            foreach (int target in new[] { 0, 10, 399, 301 })
            {
                State same;
                Assert.Equal(0, Sample(handle, 2451545, 0, target, target, &same));
                AssertZero(same);
            }
        }
        finally { Assert.Equal(0, Close(handle)); }
        Assert.Equal(2, Close(handle));
        State closed;
        Assert.Equal(2, Sample(handle, 2451545, 0, 301, 399, &closed));
        AssertZero(closed);
    }

    [Fact]
    public void ADirectoryIsUnavailableAsAKernelAndClearsTheOutputHandle()
    {
        var directory = Directory.CreateTempSubdirectory("st-native-directory-");
        try
        {
            Assert.Equal(3, OpenFile(directory.FullName, out nuint handle));
            Assert.Equal((nuint)0, handle);
        }
        finally { directory.Delete(); }
    }

    [ModelFact]
    public void ModelRejectsUnsupportedObjectsAndEpochsAndClearsTheCallerState()
    {
        Assert.Equal(0, OpenFile(Environment.GetEnvironmentVariable("SPACETANGENT_DE440")!, out nuint handle));
        try
        {
            foreach (var row in new (long Day, long Nanos, int Target, int Center, int Status)[]
            {
                (2451545, 0, 499, 399, 1), (2451545, 0, 301, 499, 1),
                (-1, 0, 301, 399, 1), (2451545, -1, 301, 399, 1),
                (2451545, 86400000000000, 301, 399, 1), (long.MaxValue, 0, 301, 399, 1),
                (1, 0, 301, 399, 5), (4000000, 0, 301, 399, 5)
            })
            {
                State state = new() { X = 77, Vz = 88 };
                Assert.Equal(row.Status, Sample(handle, row.Day, row.Nanos, row.Target, row.Center, &state));
                AssertZero(state);
            }
        }
        finally { Assert.Equal(0, Close(handle)); }
    }

    private static int OpenFile(string path, out nuint handle)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(path);
        // A failed open must overwrite caller-owned memory, not merely leave a zero intact.
        nuint value = 0xDEADBEEF;
        int status;
        fixed(byte* pointer = bytes) status = Open(pointer, bytes.Length, &value);
        handle = value;
        return status;
    }

    private static void AssertZero(State state) => Assert.Equal(new double[6],
        new[] { state.X, state.Y, state.Z, state.Vx, state.Vy, state.Vz });
    private static void Near(double expected, double actual, double tolerance) =>
        Assert.InRange(Math.Abs(actual - expected), 0, tolerance);

    private sealed class ModelFactAttribute : FactAttribute
    {
        public ModelFactAttribute()
        {
            if (!File.Exists(Environment.GetEnvironmentVariable("SPACETANGENT_DE440")))
                Skip = "Set the pinned DE440 model for native ABI acceptance.";
        }
    }
}
