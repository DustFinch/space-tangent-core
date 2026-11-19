# TLE orbits (SGP4/SDP4)

`TwoLineElements` validates fixed-width lines, catalog numbers and checksums. The epoch is the exact decimal fraction of the nominal UTC day used in a TLE. UTC epochs from 1972 onward are supported. `Sgp4Orbit` converts the epoch to TDB once and evaluates elapsed TDB minutes to match the SPICE time convention.

`Sgp4Orbit` is a port of the Vallado SGP4 reference code with WGS 72 constants in the AFSPC operations mode, the mode that NAIF `evsgp4_c` implements. Orbits with periods of at least 225 minutes use the deep-space extension with lunar-solar periodics and the 12-hour and 24-hour resonance integrator. The integrator follows a fixed 720-minute grid from the element epoch. Immutable checkpoints every 64 steps are published lazily and shared safely between queries; fractional query results never enter this cache. Thus every state depends only on the requested time and never on query order. Each direction uses at most 44 checkpoints. `Propagate` reports the published SGP4 error codes through `Sgp4Status` and rejects non-finite or out-of-range input beyond ±2,000,000 minutes. The constructor rejects elements that fail at their own epoch. A decayed state (radius below the reference Earth) is returned with `Sgp4Status.Decayed`.

States are Earth-centred TEME in kilometres and kilometres per second. `TrySample` is limited to ±365 days from the TLE epoch. This window bounds the supported calculation; a TLE normally gives reliable predictions for a much shorter interval. A frame conversion is required before composing these vectors with inertial positions.

## Verification

`sgp4-verification-reference.csv` contains the 33 published SGP4-VER cases, including near-Earth drag, 12-hour and 24-hour resonances, equatorial and retrograde orbits, very low perigee and the expected error terminations. `tools/reference/generate_sgp4_verification.py` evaluates them with the reference C++ code (as compiled into python-sgp4) and independently with CSPICE `evsgp4`, and stops if the two differ by more than 1 mm. The port matches the fixture within 1 mm and 1e-9 km/s. The cases come with python-sgp4; the pinned tool versions are in `tools/reference/requirements.txt`.

`sgp4-reference.csv` records CSPICE N0067 `getelm_c` and `evsgp4_c` results for the original Jason-1 and SORCE science-satellite elements. The catalog used to produce it has SHA-256 `a07945cc9bd3e1ed38eed76814fe3f3cd39715a7d28efff3548d49a79aa83b51`. `tools/reference/generate_sgp4_reference.py` reproduces that fixture from the original catalog and NAIF leap-second kernel.

Algorithm source: David A. Vallado, Paul Crawford, Richard Hujsak and T.S. Kelso, *Revisiting Spacetrack Report #3*, AIAA 2006-6753. [Paper and reference software](https://celestrak.org/publications/AIAA/2006-6753/) · [Permission notice](https://celestrak.org/publications/AIAA/2006-6753/faq.php) · [NAIF evaluator](https://naif.jpl.nasa.gov/pub/naif/toolkit_docs/C/cspice/evsgp4_c.html).
