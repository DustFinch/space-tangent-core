// SPDX-License-Identifier: GPL-2.0-or-later
using System.Runtime.InteropServices;
using SpaceTangent.Unity;
using Xunit;

namespace SpaceTangent.Native.Tests;

public sealed class UnityBindingContractTests
{
    private static readonly Lazy<bool> Resolver = new(() =>
    {
        string path = Environment.GetEnvironmentVariable("ST_NATIVE_DLL")!;
        NativeLibrary.SetDllImportResolver(typeof(SpaceTangentEphemeris).Assembly,
            (name, _, _) => name == "SpaceTangentNative" ? NativeLibrary.Load(path) : 0);
        return true;
    });

    [NativeLibraryFact]
    public void BindingKeepsTheAbiLayoutAndReportsOpenFailures()
    {
        Assert.True(Resolver.Value);
        Assert.Equal(0x10000u, SpaceTangentEphemeris.AbiVersion);
        Assert.Equal(48, Marshal.SizeOf<EphemerisState>());
        Assert.Equal(24, Marshal.OffsetOf<EphemerisState>(nameof(EphemerisState.VxKilometersPerSecond)).ToInt32());
        var relative = Assert.Throws<InvalidOperationException>(() => new SpaceTangentEphemeris("relative.bin"));
        Assert.Contains("InvalidArgument", relative.Message);
        var missing = Assert.Throws<InvalidOperationException>(() =>
            new SpaceTangentEphemeris(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".bin")));
        Assert.Contains("FileUnavailable", missing.Message);
    }

    [NativeModelFact]
    public void BindingReturnsAllSixNativeComponentsAndRejectsQueriesAfterDispose()
    {
        Assert.True(Resolver.Value);
        var model = new SpaceTangentEphemeris(Environment.GetEnvironmentVariable("SPACETANGENT_DE440")!);
        try
        {
            Assert.Equal(EphemerisStatus.Ok, model.GetState(2451545, 0, SolarObject.Moon, SolarObject.Earth, out var state));
            Near(-291608.38463343546, state.XKilometers, 5e-6);
            Near(-266716.83339423337, state.YKilometers, 5e-6);
            Near(-76102.48709990202, state.ZKilometers, 5e-6);
            Near(.64353138771903273, state.VxKilometersPerSecond, 5e-10);
            Near(-.66608768409163044, state.VyKilometersPerSecond, 5e-10);
            Near(-.30132570498227307, state.VzKilometersPerSecond, 5e-10);
            Assert.Equal(EphemerisStatus.InvalidArgument,
                model.GetState(2451545, -1, SolarObject.Moon, SolarObject.Earth, out var invalid));
            Assert.Equal(default, invalid);
            Assert.Equal(EphemerisStatus.OutsideCoverage,
                model.GetState(1, 0, SolarObject.Moon, SolarObject.Earth, out _));
        }
        finally { model.Dispose(); }
        model.Dispose();
        Assert.Throws<ObjectDisposedException>(() => model.GetState(2451545, 0, SolarObject.Moon, SolarObject.Earth, out _));
    }

    [NativeModelFact]
    public void IndependentSafeHandlesRemainUsableWhenAnotherModelCloses()
    {
        Assert.True(Resolver.Value);
        string path = Environment.GetEnvironmentVariable("SPACETANGENT_DE440")!;
        using var survivor = new SpaceTangentEphemeris(path);
        using (var first = new SpaceTangentEphemeris(path))
            Assert.Equal(EphemerisStatus.Ok, first.GetState(2451545, 0, SolarObject.Earth, SolarObject.Sun, out _));
        Assert.Equal(EphemerisStatus.Ok, survivor.GetState(2451545, 0, SolarObject.Earth, SolarObject.Sun, out var state));
        Assert.True(double.IsFinite(state.XKilometers) && Math.Abs(state.XKilometers) > 1e6);
    }

    private static void Near(double expected, double actual, double tolerance) =>
        Assert.InRange(Math.Abs(actual - expected), 0, tolerance);

    private class NativeLibraryFactAttribute : FactAttribute
    {
        public NativeLibraryFactAttribute()
        {
            if (!File.Exists(Environment.GetEnvironmentVariable("ST_NATIVE_DLL")))
                Skip = "Set ST_NATIVE_DLL to the published NativeAOT library for P/Invoke acceptance.";
        }
    }

    private sealed class NativeModelFactAttribute : NativeLibraryFactAttribute
    {
        public NativeModelFactAttribute()
        {
            if (!File.Exists(Environment.GetEnvironmentVariable("SPACETANGENT_DE440")))
                Skip = "Set the pinned DE440 model for P/Invoke acceptance.";
        }
    }
}
