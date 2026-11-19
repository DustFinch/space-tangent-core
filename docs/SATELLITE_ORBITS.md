# Satellite element catalogues

`TwoLineElements.Parse` validates paired catalogue numbers, field ranges and checksums. `LocalTleCatalogue.Load` reads local two-line or named three-line catalogues, retaining the newest valid elements per number and aliases from all contributing groups. Propagated states use the TDB instant supplied by the caller. Raw states are Earth-centered TEME in kilometers and kilometers per second. `SampleInWorld` rotates TEME to the Earth-fixed frame through the 1982 Greenwich mean sidereal time, as in Vallado's `teme2ecef`, and then to the world axes with [Earth orientation](EARTH_ORIENTATION.md), using the same ΔT model as the Earth and the observer.

Both the near-Earth and the deep-space branches follow the Vallado reference implementation; see [TLE orbits](SGP4.md) for the algorithm, its error codes and the verification data. Every state depends only on the requested time.
