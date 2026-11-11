# Two-body conic trajectories

`ConicOrbit` describes an orbit through periapsis distance, eccentricity, orientation, periapsis epoch and gravitational parameter. One universal-variable solver covers circular, elliptic, parabolic and hyperbolic trajectories. It evaluates TDB duration before converting to a floating-point offset. Output states use kilometres and kilometres per second in the explicitly supplied reference frame.

`SscConicOrbits.Read` extracts static `EllipticalOrbit` blocks. The caller supplies the parent's reference frame and the format's distance and period unit scales. This entry point reads orbital curves. Body lifetime, visual properties and timeline blocks belong to their object adapters.

The committed fixture contains 40 states from [NAIF conics_c](https://naif.jpl.nasa.gov/pub/naif/toolkit_docs/C/cspice/conics_c.html), including near-parabolic cases on both sides of eccentricity 1 and positive and negative time offsets. The original ISS catalogue regression is enabled with `SPACETANGENT_ISS_SSC`. Its model file is external.
