# Observation event searches

`BodySurfaceFrame.OnEllipsoid` places a geodetic observer on a rotating oblate body and returns barycentric position/velocity and world-coordinate east/north/up axes. The supplied fixed-body rotation and angular velocity define the frame; this generic path supplies no Earth atmosphere or refraction. Earth clients retain their WGS 84 and Earth-orientation model. Horizontal coordinates use east-positive azimuth from north, with no azimuth at zenith/nadir.

`ObserverDaySearch` accepts a captured observer sampler and a bounded TDB interval. It returns quarter-hour chart samples, the first rising and setting contacts and the highest covered altitude, with explicit coverage flags. Crossings use five-minute brackets refined to one second; null/non-finite samples break brackets instead of becoming zero-height observations. The interval may span a 23/25-hour civil day; `ObserverSiteClock.At` maps local dates through zone transitions, leap seconds and modeled historical UT1. Horizon/refraction policy belongs to the caller.

`SolarSystemEventSearch.Find` restores the geocentric almanac: lunar phases, outer-planet oppositions, inner-planet elongations, lunar/planet/star approaches and seasonal solar-longitude crossings. Windows are clipped to the installed ephemeris and limited to 367 TDB days, allowing a complete civil leap year including a leap second. Extrema use additional bracketing samples beyond the requested edges when coverage permits, but results stay inside the requested window. Directions include first-order light time and annual aberration from Earth's barycentric velocity; the seasonal longitude adds the adopted approximate precession/nutation correction without applying aberration twice. Fixed bright-star directions are J2000 catalogue directions with the same annual aberration. Roots are refined numerically to one second; this does not promise one-second or one-minute physical accuracy. Retained phase/season checks and independent USNO minute labels bound representative dates.

`ObservationEventSearch.FindShadowTransits` finds the umbral cone crossing a spherical receiving body. Its contacts are global events, not visibility from an observing site. Unavailable states and window boundaries produce explicitly clipped contacts. Supply positions from the same epoch and reference frame; include actual satellite ephemerides when searching a planetary system.

The shadow score uses distance to the solid cone, including its apex, rather than the cone radius at the receiver's centre. It therefore retains hybrid eclipses whose umbra reaches the near surface but ends before the centre. Contact and peak searches share this geometry. Controls include hybrid eclipses in 2005 and 2023, the total eclipse in April 2024, the annular eclipse in October 2023, and analytic short-apex and grazing intersections.

Geodetic datum heights may be negative down to −12 km. Generic ellipsoid placement also rejects heights at or beyond the body's centre. A negative datum height supplies observer coordinates, not a terrain elevation map; clients own the rendering/terrain policy and the validity of atmospheric assumptions.

The core searches increasing TDB windows for local solar eclipses, lunar eclipses, closest angular approaches and satellite passes above a supplied altitude. It reports clipped contacts separately from the refined peak and avoids interpolating over unavailable states. Observers stand on the WGS 84 ellipsoid oriented by [Earth orientation](EARTH_ORIENTATION.md). The local solar model includes solar and lunar light travel time. The lunar shadow follows Danjon's rule (the Earth's radius, reduced to 0.998340 for oblateness, enlarged by 1/85 for the atmosphere) and the light time between the Sun, the Earth and the Moon. Contacts of the 2025 March 14 eclipse agree with NASA/GSFC within 10 s.

The external checks use the April 2024 solar and March 2025 lunar circumstances, the December 2020 Jupiter–Saturn conjunction, and four passes computed independently from the archived ISS elements. The search also checks a minimum in a truncated interval.

Reference sources

- [NASA GSFC April 2024 solar eclipse](https://eclipse.gsfc.nasa.gov/SEhistory/SEplot/SE2024Apr08T.pdf)
- [NASA GSFC March 2025 lunar eclipse](https://eclipse.gsfc.nasa.gov/LEplot/LEplot2001/LE2025Mar14T.pdf)
- [NASA JPL NAIF conjunction calculation](https://naif.jpl.nasa.gov/naif/wgc_easy_usage_example.pdf)
