# Near-Earth TLE orbits

`TwoLineElements` validates fixed-width lines, catalog numbers and checksums. The epoch is the exact decimal fraction of the nominal UTC day used in a TLE. UTC epochs from 1972 onward are supported. `Sgp4Orbit` converts the epoch to TDB once and evaluates elapsed TDB minutes to match the SPICE time convention.

The WGS 72 near-Earth equations return an Earth-centred TEME state in kilometres and kilometres per second. The current propagator supports periods shorter than 225 minutes and a window of ±365 days from the TLE epoch. This window bounds the supported calculation. A TLE normally gives reliable predictions for a much shorter interval. A frame conversion is required before composing these vectors with inertial positions.

The fixture records independent CSPICE N0067 `getelm_c` and `evsgp4_c` results for the original Jason-1 and SORCE science-satellite elements. The catalog used to produce it has SHA-256 `a07945cc9bd3e1ed38eed76814fe3f3cd39715a7d28efff3548d49a79aa83b51`. `tools/reference/generate_sgp4_reference.py` reproduces the fixture from the original catalog and NAIF leap-second kernel.

Algorithm source: David A. Vallado, Paul Crawford, Richard Hujsak and T.S. Kelso, *Revisiting Spacetrack Report #3*, AIAA 2006-6753. [Paper and reference software](https://celestrak.org/publications/AIAA/2006-6753/) · [Permission notice](https://celestrak.org/publications/AIAA/2006-6753/faq.php) · [NAIF evaluator](https://naif.jpl.nasa.gov/pub/naif/toolkit_docs/C/cspice/evsgp4_c.html).
