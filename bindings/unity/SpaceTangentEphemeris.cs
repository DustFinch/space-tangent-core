// SPDX-License-Identifier: GPL-2.0-or-later
using System;
using System.Runtime.InteropServices;
using System.Text;

namespace SpaceTangent.Unity
{
    public enum EphemerisStatus
    {
        Ok, InvalidArgument, InvalidHandle, FileUnavailable, InvalidData, OutsideCoverage, InternalError
    }

    public enum SolarObject { Barycenter = 0, Sun = 10, Moon = 301, Earth = 399 }

    [StructLayout(LayoutKind.Sequential)]
    public struct EphemerisState
    {
        public double XKilometers, YKilometers, ZKilometers;
        public double VxKilometersPerSecond, VyKilometersPerSecond, VzKilometersPerSecond;
    }

    public sealed class SpaceTangentEphemeris : IDisposable
    {
        private const string Library = "SpaceTangentNative";
        private readonly ModelHandle handle;

        public SpaceTangentEphemeris(string absolutePath)
        {
            byte[] path = new UTF8Encoding(false, true).GetBytes(absolutePath);
            EphemerisStatus status = Open(path, path.Length, out IntPtr value);
            if (status != EphemerisStatus.Ok)
                throw new InvalidOperationException("Could not open DE440: " + status);
            handle = new ModelHandle(value);
        }

        public static uint AbiVersion => Version();

        public EphemerisStatus GetState(long julianDay, long nanosecondsOfDay, SolarObject target, SolarObject center,
            out EphemerisState state) => ReadState(handle, julianDay, nanosecondsOfDay, target, center, out state);

        public void Dispose() => handle.Dispose();

        private sealed class ModelHandle : SafeHandle
        {
            public ModelHandle(IntPtr value) : base(IntPtr.Zero, true) => SetHandle(value);
            public override bool IsInvalid => handle == IntPtr.Zero;
            protected override bool ReleaseHandle() => CloseNative(handle) == EphemerisStatus.Ok;
        }

        [DllImport(Library, EntryPoint = "st_abi_version", CallingConvention = CallingConvention.Cdecl)]
        private static extern uint Version();

        [DllImport(Library, EntryPoint = "st_de440_open", CallingConvention = CallingConvention.Cdecl)]
        private static extern EphemerisStatus Open(byte[] path, int bytes, out IntPtr model);

        [DllImport(Library, EntryPoint = "st_de440_close", CallingConvention = CallingConvention.Cdecl)]
        private static extern EphemerisStatus CloseNative(IntPtr model);

        [DllImport(Library, EntryPoint = "st_de440_state", CallingConvention = CallingConvention.Cdecl)]
        private static extern EphemerisStatus ReadState(ModelHandle model, long julianDay, long nanosecondsOfDay,
            SolarObject target, SolarObject center, out EphemerisState state);
    }
}
