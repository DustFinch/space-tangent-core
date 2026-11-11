// SPDX-License-Identifier: GPL-2.0-or-later
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using SpaceTangent.Contracts;
using SpaceTangent.Core.Ephemerides;

namespace SpaceTangent.Native;

internal enum Status
{
    Ok, InvalidArgument, InvalidHandle, FileUnavailable, InvalidData, OutsideCoverage, InternalError
}

[StructLayout(LayoutKind.Sequential)]
internal readonly record struct NativeState(double X, double Y, double Z, double Vx, double Vy, double Vz);

internal static unsafe class EphemerisExports
{
    private static readonly ConcurrentDictionary<nuint, De440Ephemeris> Handles = new();
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static long _nextHandle;

    [UnmanagedCallersOnly(EntryPoint = "st_abi_version", CallConvs = [typeof(CallConvCdecl)])]
    public static uint AbiVersion() => 0x0001_0000;

    [UnmanagedCallersOnly(EntryPoint = "st_de440_open", CallConvs = [typeof(CallConvCdecl)])]
    public static Status Open(byte* path, int pathLength, nuint* outputHandle)
    {
        if (outputHandle == null)
            return Status.InvalidArgument;
        *outputHandle = 0;
        if (path == null || pathLength is < 1 or > 4096)
            return Status.InvalidArgument;

        try
        {
            string file = StrictUtf8.GetString(new ReadOnlySpan<byte>(path, pathLength));
            if (file.Contains('\0') || !Path.IsPathFullyQualified(file))
                return Status.InvalidArgument;
            De440Ephemeris ephemeris = De440Ephemeris.Load(file);
            nuint handle;
            do
            {
                handle = unchecked((nuint)Interlocked.Increment(ref _nextHandle));
            }
            while (handle == 0 || !Handles.TryAdd(handle, ephemeris));

            *outputHandle = handle;
            return Status.Ok;
        }
        catch (ArgumentException) { return Status.InvalidArgument; }
        catch (InvalidDataException) { return Status.InvalidData; }
        catch (IOException) { return Status.FileUnavailable; }
        catch (UnauthorizedAccessException) { return Status.FileUnavailable; }
        catch (Exception) { return Status.InternalError; }
    }

    [UnmanagedCallersOnly(EntryPoint = "st_de440_close", CallConvs = [typeof(CallConvCdecl)])]
    public static Status Close(nuint handle) =>
        Handles.TryRemove(handle, out _) ? Status.Ok : Status.InvalidHandle;

    [UnmanagedCallersOnly(EntryPoint = "st_de440_state", CallConvs = [typeof(CallConvCdecl)])]
    public static Status State(nuint handle, long julianDay, long nanosecondsOfDay, int target, int center, NativeState* output)
    {
        if (output == null)
            return Status.InvalidArgument;
        *output = default;
        if (!Handles.TryGetValue(handle, out var model))
            return Status.InvalidHandle;
        if (!TryMapObject(target, out var targetId) || !TryMapObject(center, out var centerId))
            return Status.InvalidArgument;
        if (julianDay is < 0 or > JulianInstant.MaximumDay || nanosecondsOfDay is < 0 or >= JulianInstant.NanosecondsPerDay)
            return Status.InvalidArgument;

        try
        {
            var tdb = new JulianInstant(julianDay, nanosecondsOfDay, TimeScale.Tdb);
            CartesianState state = model.Sample(tdb).RelativeState(targetId, centerId);
            var p = state.PositionKilometers;
            var v = state.VelocityKilometersPerSecond;
            *output = new(p.X, p.Y, p.Z, v.X, v.Y, v.Z);
            return Status.Ok;
        }
        catch (ArgumentOutOfRangeException) { return Status.OutsideCoverage; }
        catch (Exception) { return Status.InternalError; }
    }

    private static bool TryMapObject(int naifId, out ObjectId objectId)
    {
        objectId = naifId switch
        {
            0 => SolarSystemSample.BarycenterId,
            10 => SolarSystemSample.SunId,
            399 => SolarSystemSample.EarthId,
            301 => SolarSystemSample.MoonId,
            _ => default
        };
        return objectId != default;
    }
}
