# Observation event searches

The core searches increasing TDB windows for local solar eclipses, lunar eclipses, closest angular approaches and satellite passes above a supplied altitude. It reports clipped contacts separately from the refined peak and avoids interpolating over unavailable states. The local solar model uses an oblate Earth, mean equator precession and solar and lunar light travel time. The lunar shadow is geometric, with spherical radii and no atmospheric enlargement.

The external checks use the April 2024 solar and March 2025 lunar circumstances, the December 2020 Jupiter–Saturn conjunction, and four passes computed independently from the archived ISS elements. The search also checks a minimum in a truncated interval.

Reference sources

- [NASA GSFC April 2024 solar eclipse](https://eclipse.gsfc.nasa.gov/SEhistory/SEplot/SE2024Apr08T.pdf)
- [NASA GSFC March 2025 lunar eclipse](https://eclipse.gsfc.nasa.gov/LEplot/LEplot2001/LE2025Mar14T.pdf)
- [NASA JPL NAIF conjunction calculation](https://naif.jpl.nasa.gov/naif/wgc_easy_usage_example.pdf)
