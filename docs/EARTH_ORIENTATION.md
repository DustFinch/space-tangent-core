# Earth orientation

`EarthOrientation` gives the rotation from Earth-fixed axes (X towards Greenwich, Z towards the pole) to the world ICRF axes, together with Earth's angular velocity. It composes:

- the long-term precession of Vondrák, Capitaine and Wallace (2011, A&A 534, A22), including the ICRS frame bias. It is valid for ±200 000 years and agrees with IAU 2006 near J2000;
- IAU 2000B nutation (77 luni-solar terms and the fixed planetary offset) with the IAU 2006 mean obliquity;
- the Earth rotation angle from UT1 = TT − ΔT, with the selected ΔT model, through the CIO-based transformation. The CIO locator *s* uses the polynomial part of IAU 2006.

Polar motion is neglected (below 0.5″). Over 1900–2100 the composition agrees with the full IAU 2006/2000A matrix (ERFA `eraC2t06a`) within 7 mas. Far from J2000 the result is limited by ΔT, whose uncertainty reaches minutes of time (a minute of time is 0.25° of rotation) in antiquity and in the far future.

`EarthSurfaceLighting.Sample`, the world graph Earth node (`WorldGraphBuilder.AddMeanEarthRotation`), the TEME transport of TLE satellites and the event searches all use this rotation. Observers stand on the WGS 84 ellipsoid and their up axis is the ellipsoid normal. `SurfaceLighting.EarthToWorld` carries the full rotation, and `EarthRotationRadians` is the right ascension of the Greenwich meridian in world axes. `MeanEquatorOfDateToWorld` provides the mean equator and equinox of date for catalogue frames.

The formulas are ported from ERFA (BSD-3-Clause, derived from IAU SOFA). `tools/reference/generate_earth_orientation.py` regenerates `earth-orientation-reference.csv` with pyerfa for epochs from year 1 to year 4000. The tests require the precession matrix to agree to 1e-12 and the terrestrial and TEME matrices to 5 mas plus 1 mas per century squared, the size of the periodic terms of *s* that are not ported.

Distributions retain the complete ERFA notice in [LICENSES/ERFA.txt](../LICENSES/ERFA.txt). See [migration notes](ACCURACY_MIGRATION.md) for the graph evaluation model and API changes.
