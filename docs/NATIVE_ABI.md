# Win64 C ABI

Build the shared library on Windows with the .NET SDK and Visual C++ build tools.

```powershell
dotnet restore src/SpaceTangent.Native/SpaceTangent.Native.csproj --locked-mode
dotnet publish src/SpaceTangent.Native/SpaceTangent.Native.csproj --no-restore -c Release
```

Use `include/space_tangent.h` with the published `SpaceTangentNative.dll`. The ABI uses C calling conventions, fixed-width integers and six contiguous doubles per state. Pass the DE440 file as an absolute UTF-8 path without a NUL terminator. The caller owns each nonzero handle and closes it once. Queries can run concurrently. Closing a handle leaves an in-flight query valid and rejects later queries. Retain the DLL for the lifetime of the host process.

The internal state is a plain sequential wire struct, matching the C header and Unity binding; it has no record equality or generated property API. Directory paths are unavailable kernel files and clear the output handle just like other file-open failures. The ABI version and 48-byte layout remain unchanged.

Use the exact TDB day and nanosecond offset. Position and velocity are geometric ICRF3 coordinates in kilometres and kilometres per second. NAIF IDs 0, 10, 301 and 399 identify the solar-system barycenter, Sun, Moon and Earth. Status values come from the header.

The C regression in `tests/native/smoke.c` builds with `cl /std:c17 /Iinclude tests\native\smoke.c` in a Win64 Visual C++ command prompt. Pass it the absolute DLL and DE440 paths.

`dotnet test tests/SpaceTangent.Native.Tests` invokes the same attributed C entry points through unmanaged function pointers in the managed build. It checks status codes, UTF-8 paths, output clearing, handles and the pinned CSPICE Moon state when `SPACETANGENT_DE440` is set. This permits IL line instrumentation of `SpaceTangentNative`; it does not measure optimized NativeAOT machine-code coverage or replace the C/editor smoke checks. The report tool maps this ABI assembly name to the `SpaceTangent.Native` source project explicitly.

For Unity, put `SpaceTangentNative.dll` under `Assets/Plugins/x86_64` and copy `bindings/unity/SpaceTangentEphemeris.cs` into `Assets`. The editor probe belongs in `Assets/Editor`. Set `SPACETANGENT_DE440` and call `SpaceTangent.Unity.SpaceTangentNativeSmoke.Run` using the editor's `-executeMethod` option.

The optional `bindings/unity/SpaceTangent.UnityBinding.csproj` builds that same wrapper as a .NET Standard 2.1 library without UnityEngine or editor sources. It makes the C# binding measurable independently; Unity can continue importing the single source file. Set `ST_NATIVE_DLL` to an absolute published NativeAOT DLL and `SPACETANGENT_DE440` to the pinned model when running `SpaceTangent.Native.Tests` to check real P/Invoke state values, failure statuses and SafeHandle disposal. These checks supplement the in-editor smoke, rather than claiming coverage of the editor itself. The NativeAOT runtime/compiler are pinned to the patched 8.0.33 toolchain.

For Unreal, copy `bindings/unreal` to a writable project directory. Set `SPACETANGENT_NATIVE_DLL` and `SPACETANGENT_DE440` to absolute paths. The example uses the editor's Python Script Plugin to load the C ABI and compare all six state values with CSPICE from inside the editor process.

```powershell
& 'C:/Program Files/Epic Games/UE_5.8/Engine/Binaries/Win64/UnrealEditor-Cmd.exe' 'C:/work/SpaceTangentAbi/SpaceTangentAbi.uproject' -run=pythonscript '-script=C:/work/SpaceTangentAbi/smoke.py' -unattended -nullrhi -nop4
```

The Unity and Unreal probes use the same ABI version and query the Moon relative to Earth at JD 2451545.0 TDB. They cover editor embedding. A compiled Unreal C++ module and packaged engine builds require their own validation.

[NativeAOT exports](https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/interop) · [Unity native plug-ins](https://docs.unity3d.com/6000.0/Documentation/Manual/plug-ins-native.html) · [Unreal Python commandlet](https://dev.epicgames.com/documentation/en-us/unreal-engine/scripting-the-unreal-editor-using-python)
