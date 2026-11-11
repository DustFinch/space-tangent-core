# SSC spacecraft trajectories

The SSC reader loads spacecraft definitions with `SpiceOrbit`, local kernel paths, a mesh and a radius. It resolves the name or numeric ID of the SPK target and centre. The DAF reader accepts IEEE little- and big-endian kernels. The SPK evaluator supports geometric J2000 states with type 1 modified difference arrays and type 2 Chebyshev position coefficients. Type 2 velocity is the analytic derivative in kilometers per second. Each direct trajectory reports its own coverage. Later matching kernels and segments have priority.

The Cassini regression uses the original `cassini.ssc` fixture and the NASA Cassini kernel `040615AP_SK_04167_04186.bsp`. Set `SPACETANGENT_CASSINI_SSC_ROOT` to the directory with the SSC, mesh and companion kernels. The expected positions and velocities come from CSPICE N0067 at five epochs, including both coverage endpoints. They can be regenerated with `tools/reference/generate_cassini_reference.py`.

The reader evaluates `SpiceRotation` for J2000 CK type 3 frames. It reads the frame ID and clock link from FK, converts TDB to encoded clock ticks using SCLK type 1, and interpolates only inside CK pointing intervals. The second regression compares five attitudes and one pointing gap with CSPICE using the original Cassini kernels. It can be reproduced with `tools/reference/generate_cassini_attitude.py`.

The version 1.0 SPICE JSON adapter shares the same trajectory and attitude readers. It expands ordered local metakernels, including relative path symbols, within the supplied add-on root. The original Cassini SOI JSON regression is enabled by setting `SPACETANGENT_CASSINI_JSON_ROOT` to its directory. It loads the spacecraft and both cameras. The sensor reader resolves IK instrument aliases, rectangular fields of view and Euler FK rotations directly into the spacecraft CK frame. It preserves the target, range, color and opacities. The regression compares both boresights and all eight corners at two epochs against CSPICE N0067. Run tools/reference/generate_cassini_fov.py with the CSPICE library, the original add-on directory and a leap-seconds kernel to regenerate the reference.

The SAT480 regression uses Saturn's physical center (699) relative to the system barycenter (6). Set `SPACETANGENT_SAT480` to the original kernel, SHA-256 `12D2CFD8DA371883BD4EDBD67C31FAFA98CA7779A09DA6819E29D53CDC6DAF32`. The reference covers Cassini mission epochs, both coverage ends, and record and segment transitions. Regenerate it with `tools/reference/generate_saturn_center.py`.

[NAIF SPK format and interpolation types](https://naif.jpl.nasa.gov/pub/naif/toolkit_docs/C/req/spk.html) · [SAT480 data and provenance](https://naif.jpl.nasa.gov/pub/naif/generic_kernels/spk/satellites/sat480.cmt)

[NAIF field of view definitions](https://naif.jpl.nasa.gov/pub/naif/toolkit_docs/C/cspice/getfov_c.html) · [NAIF reference frames](https://naif.jpl.nasa.gov/pub/naif/toolkit_docs/C/req/frames.html)
