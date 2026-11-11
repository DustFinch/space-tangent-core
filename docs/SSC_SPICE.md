# SSC spacecraft trajectories

The SSC reader loads spacecraft definitions with `SpiceOrbit`, local kernel paths, a mesh and a radius. It resolves the name or numeric ID of the SPK target and centre. The DAF reader accepts IEEE little- and big-endian kernels. The SPK evaluator supports geometric J2000 states with type 1 modified difference arrays. Each direct trajectory reports its own coverage. Later matching kernels and segments have priority.

The Cassini regression uses the original `cassini.ssc` fixture and the NASA Cassini kernel `040615AP_SK_04167_04186.bsp`. Set `SPACETANGENT_CASSINI_SSC_ROOT` to the directory with the SSC, mesh and companion kernels. The expected positions and velocities come from CSPICE N0067 at five epochs, including both coverage endpoints. They can be regenerated with `tools/reference/generate_cassini_reference.py`.

The reader evaluates `SpiceRotation` for J2000 CK type 3 frames. It reads the frame ID and clock link from FK, converts TDB to encoded clock ticks using SCLK type 1, and interpolates only inside CK pointing intervals. The second regression compares five attitudes and one pointing gap with CSPICE using the original Cassini kernels. It can be reproduced with `tools/reference/generate_cassini_attitude.py`.

Metakernel expansion and script definitions require additional adapters. The application currently displays the science satellites from the JSON adapter.

[NAIF SPK format and interpolation types](https://naif.jpl.nasa.gov/pub/naif/toolkit_docs/C/req/spk.html)
