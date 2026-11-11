# Local lunar relief data

The elevation reader accepts the existing little-endian version 1 terrain pack. The grid stores signed heights above its reference radius. Longitude increases eastward, column centers straddle the antimeridian, and rows run north to south. Queries interpolate between grid centers, wrapping longitude and clamping the row at each pole.

The original 4096 × 2048 lunar pack was prepared from NASA LRO LOLA LDEM_16. Its reference radius is 1737.4 km and each signed sample represents 0.5 m. The prepared pack SHA-256 is `05F006DB0116E97E022C3608C26C14FE4891F5EAD16C7922AF1D4A3053BE750C`. The source observations are public domain. Set `SPACETANGENT_MOON_DEM` to the existing `terrain-moon.bin` for the real-data regression. The data remain external to the code checkout.

[NASA PDS LOLA grid units and source archive](https://pds-geosciences.wustl.edu/missions/lro/lola_faq.htm)

The lunar frame uses IAU_MOON in `pck00011.tpc`. This is the text-PCK approximation for terrain orientation. The reference compares the three axes and their derivatives with CSPICE at seven epochs. Set `SPACETANGENT_SATURN_PCK` to the same text PCK used by the other body frame tests. Regenerate the fixture with `tools/reference/generate_moon_frame.py`.

[NAIF text PCK orientation models](https://naif.jpl.nasa.gov/pub/naif/toolkit_docs/C/req/pck.html)
