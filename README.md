# SpaceTangent Core

SpaceTangent Core is an engine-independent C# library for evaluating astronomical positions, velocities, orbits, and reference frames. It powers the [SpaceTangent desktop simulator](https://github.com/DustFinch/space-tangent) and targets .NET 8.

## Quick start

Install Git and the .NET SDK specified in [`global.json`](global.json). The commands below use PowerShell and build the library from source.

```powershell
git clone https://github.com/DustFinch/space-tangent-core.git
Set-Location space-tangent-core
curl.exe --fail --location --output de440.440 https://ssd.jpl.nasa.gov/ftp/eph/planets/Linux/de440/linux_p1550p2650.440
dotnet new console --output examples/MoonState --no-restore
dotnet add examples/MoonState/MoonState.csproj reference src/SpaceTangent.Core/SpaceTangent.Core.csproj
```

Replace `examples/MoonState/Program.cs` with the following code.

```csharp
using SpaceTangent.Contracts;
using SpaceTangent.Core.Ephemerides;

var epoch = new JulianInstant(JulianInstant.J2000Day, 0, TimeScale.Tdb);
var ephemeris = De440Ephemeris.Load(args[0]);
var moon = ephemeris.Sample(epoch).RelativeState(SolarSystemSample.MoonId, SolarSystemSample.EarthId);

var p = moon.PositionKilometers;
var v = moon.VelocityKilometersPerSecond;
Console.WriteLine(FormattableString.Invariant($"Position (km): {p.X:F3}, {p.Y:F3}, {p.Z:F3}"));
Console.WriteLine(FormattableString.Invariant($"Velocity (km/s): {v.X:F6}, {v.Y:F6}, {v.Z:F6}"));
```

```powershell
dotnet run --project examples/MoonState/MoonState.csproj -- de440.440
```

The example evaluates the geometric state of the Moon relative to Earth at JD 2451545.0 TDB (J2000), with ICRF3 axes. See the [DE440 data notes](docs/DE440.md) for the input checksum and coverage.

## Capabilities

| Area | Current implementation |
| --- | --- |
| Ephemerides | Geometric states for the Sun, Earth, Moon and planetary systems from JPL DE440. An optional analytic model extends coverage to years 1–4000. [Data and coverage](docs/DE440.md) |
| Time and frames | UTC, TAI, TT, and TDB conversions, ΔT models, and a world graph with body-fixed, two-vector, and mean-equator frames. [Time](docs/TIME.md) · [World graph](docs/WORLD_GRAPH.md) |
| Trajectories | Near-Earth SGP4 and deep-space SDP4 from TLEs, analytic satellite orbits, elliptic through hyperbolic conics, and interpolation of sampled states. [Satellite orbits](docs/SATELLITE_ORBITS.md) · [Conics](docs/CONIC_ORBITS.md) · [Sampled states](docs/SAMPLED_TRAJECTORIES.md) |
| Mission data | Supported SSC and SPICE mission JSON, ordered catalogue overrides, local metakernels, selected SPK segments, CK types 2 and 3, and camera geometry. PackTool validates and builds portable add-ons. [Mission formats and PackTool](docs/SSC_SPICE.md) |
| Observation events | Solar and lunar eclipses, angular approaches, and passes of Earth satellites above a supplied altitude. [Event searches](docs/OBSERVATION_EVENTS.md) |

## Engine integration

Reference `SpaceTangent.Core` and `SpaceTangent.Contracts` from a .NET project, or use the versioned [Win64 C ABI](docs/NATIVE_ABI.md). The native library has been exercised by a standalone C program, a Unity editor probe, and an Unreal editor probe using Python. Packaged Unity and Unreal builds and a compiled Unreal C++ integration are outside the verified scope.

## Build and validate

Install the .NET 8 runtime for the test suite, then run the following commands from the repository root.

```powershell
dotnet restore SpaceTangent.Core.sln --locked-mode
dotnet build SpaceTangent.Core.sln --no-restore
dotnet test SpaceTangent.Core.sln --no-build --no-restore
```

Tests that use external data report a skip when their inputs are unavailable. To include the DE440 cases, set `$env:SPACETANGENT_DE440 = (Resolve-Path de440.440).Path` before running tests. The format notes above identify the other reference datasets. Report reproducible problems through [GitHub Issues](https://github.com/DustFinch/space-tangent-core/issues).

## License and data

The source code is licensed under [GPL-2.0-or-later](COPYING). Ephemerides, kernels, and other external datasets retain their own distribution terms. The [DE440 notes](docs/DE440.md) include the source citation and checksum.
