# Observation event searches

`ObserverDaySearch` accepts a captured observer sampler and a bounded TDB interval. It returns quarter-hour chart samples, the first rising and setting contacts and the highest covered altitude, with explicit coverage flags. Crossings use five-minute brackets refined to one second; null/non-finite samples break brackets instead of becoming zero-height observations. The interval may span a 23/25-hour civil day; `ObserverSiteClock.At` maps local dates through zone transitions, leap seconds and modeled historical UT1. Horizon/refraction policy belongs to the caller.

`SolarSystemEventSearch.Find` restores the geocentric almanac: lunar phases, outer-planet oppositions, inner-planet elongations, lunar/planet/star approaches and seasonal solar-longitude crossings. Windows are clipped to the installed ephemeris and limited to 366 days. It uses first-order light time and the adopted approximate seasonal precession/nutation correction; fixed bright-star directions are J2000 catalogue directions. The retained almanac checks allow two minutes for phases and three for seasons, rather than claiming an exact observational solver.

`ObservationEventSearch.FindShadowTransits` finds the umbral cone crossing a spherical receiving body. Its contacts are global events, not visibility from an observing site. Unavailable states and window boundaries produce explicitly clipped contacts. Supply positions from the same epoch and reference frame; include actual satellite ephemerides when searching a planetary system.

The core searches increasing TDB windows for local solar eclipses, lunar eclipses, closest angular approaches and satellite passes above a supplied altitude. It reports clipped contacts separately from the refined peak and avoids interpolating over unavailable states. Observers stand on the WGS 84 ellipsoid oriented by [Earth orientation](EARTH_ORIENTATION.md). The local solar model includes solar and lunar light travel time. The lunar shadow follows Danjon's rule (the Earth's radius, reduced to 0.998340 for oblateness, enlarged by 1/85 for the atmosphere) and the light time between the Sun, the Earth and the Moon. Contacts of the 2025 March 14 eclipse agree with NASA/GSFC within 10 s.

The external checks use the April 2024 solar and March 2025 lunar circumstances, the December 2020 Jupiter–Saturn conjunction, and four passes computed independently from the archived ISS elements. The search also checks a minimum in a truncated interval.

Reference sources

- [NASA GSFC April 2024 solar eclipse](https://eclipse.gsfc.nasa.gov/SEhistory/SEplot/SE2024Apr08T.pdf)
- [NASA GSFC March 2025 lunar eclipse](https://eclipse.gsfc.nasa.gov/LEplot/LEplot2001/LE2025Mar14T.pdf)
- [NASA JPL NAIF conjunction calculation](https://naif.jpl.nasa.gov/naif/wgc_easy_usage_example.pdf)
