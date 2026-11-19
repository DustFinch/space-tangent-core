# Observation event searches

The core searches increasing TDB windows for local solar eclipses, lunar eclipses, closest angular approaches and satellite passes above a supplied altitude. It reports clipped contacts separately from the refined peak and avoids interpolating over unavailable states. Observers stand on the WGS 84 ellipsoid oriented by [Earth orientation](EARTH_ORIENTATION.md). The local solar model includes solar and lunar light travel time. The lunar shadow follows Danjon's rule (the Earth's radius, reduced to 0.998340 for oblateness, enlarged by 1/85 for the atmosphere) and the light time between the Sun, the Earth and the Moon. Contacts of the 2025 March 14 eclipse agree with NASA/GSFC within 10 s.

The external checks use the April 2024 solar and March 2025 lunar circumstances, the December 2020 Jupiter–Saturn conjunction, and four passes computed independently from the archived ISS elements. The search also checks a minimum in a truncated interval.

Reference sources

- [NASA GSFC April 2024 solar eclipse](https://eclipse.gsfc.nasa.gov/SEhistory/SEplot/SE2024Apr08T.pdf)
- [NASA GSFC March 2025 lunar eclipse](https://eclipse.gsfc.nasa.gov/LEplot/LEplot2001/LE2025Mar14T.pdf)
- [NASA JPL NAIF conjunction calculation](https://naif.jpl.nasa.gov/naif/wgc_easy_usage_example.pdf)
