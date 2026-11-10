# Time coordinates

`JulianInstant` stores an integer day starting at noon and a nanosecond offset. It compares or subtracts only epochs in the same time scale. TT and TAI differ by exactly 32.184 seconds. UTC has its own Gregorian label with a possible second 60. Use TAI for elapsed SI time across a UTC leap second.

UTC conversion starts on 1972-01-01. [IERS Bulletin C 72](https://datacenter.iers.org/data/html/bulletinc-072.html) confirms the table through 2026-12-31. Subsequent UTC dates carry a prediction flag and use the latest published offset.

The [NAIF periodic TT/TDB approximation](https://naif.jpl.nasa.gov/pub/naif/toolkit_docs/C/req/time.html) is supported for TDB coordinates in [1900-01-01, 2100-01-01). The one-nanosecond grid describes representation precision. The periodic correction has the physical accuracy of its underlying model.

The tests check civil timestamps against ERFA two-part Julian date reference cases and the IERS leap-second announcements. The periodic correction samples retain the independent numerical references from the previous project.

Delta T estimates TT minus UT1. The Earth-rotation model blends published atomic offsets over 60 days around leap transitions and anchors the selected historical model at the coverage boundaries. It is an approximation. UTC conversion still applies the exact leap seconds. The selectable extrapolations are Espenak–Meeus, Morrison–Stephenson and a fixed offset.

[Espenak–Meeus polynomials](https://eclipse.gsfc.nasa.gov/SEcat5/deltatpoly.html) · [Morrison–Stephenson 2004](https://doi.org/10.1177/002182860403500305)
