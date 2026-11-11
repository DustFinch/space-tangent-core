# Two-body conic trajectories

`ConicOrbit` describes an orbit through periapsis distance, eccentricity, orientation, periapsis epoch and gravitational parameter. One universal-variable solver covers circular, elliptic, parabolic and hyperbolic trajectories. It evaluates TDB duration before converting to a floating-point offset. Output states use kilometres and kilometres per second in the explicitly supplied reference frame.

`SscConicOrbits.Read` extracts static `EllipticalOrbit` blocks. The caller supplies the parent's reference frame and the format's distance and period unit scales. This entry point reads orbital curves. Body lifetime, visual properties and timeline blocks belong to their object adapters.

The committed fixture contains 40 states from [NAIF conics_c](https://naif.jpl.nasa.gov/pub/naif/toolkit_docs/C/cspice/conics_c.html), including near-parabolic cases on both sides of eccentricity 1 and positive and negative time offsets. The original ISS catalogue regression is enabled with `SPACETANGENT_ISS_SSC`. Its model file is external.

`ReadBody` selects a single conic by its full SSC path or alias and exposes the orbit frame to the caller. A `MeanEquator` declaration can use different objects for the origin and the pole. Without `Object` the origin supplies the pole. Without `Freeze` the equator follows the pole of date. `WorldGraphBuilder.AddMeanEquator` uses the text PCK pole terms and excludes prime-meridian spin. A frozen frame has zero angular velocity. A moving frame contributes its angular velocity to transformed trajectories. Use `AddTrajectoryInFrame` and `TryFrame` to reference the frame by `FrameId`. Body orientations remain addressable by `ObjectId`.

Set `SPACETANGENT_PLANET_SSC` to the original `data/solarsys.ssc` at upstream commit `60316cf1c7df289b44b3c99711913bdbf1daea2e` and `SPACETANGENT_SATURN_PCK` to `pck00011.tpc`. The Phobos and Deimos conics use their default Mars equator. `tools/reference/generate_mean_equator.py` produces independent relative J2000 states from CSPICE `conics_c` and the CSPICE state transformation with Mars's prime-meridian terms set to zero.
