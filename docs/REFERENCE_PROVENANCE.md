# Reference provenance

The numerical fixtures are verification data, not production implementations. Their generators live in `tools/reference`; pinned Python dependencies are in `requirements.txt`. Keep upstream notices with any adapted implementation, including [the ERFA notice](../LICENSES/ERFA.txt).

## ASTORB discovery offsets

The frozen binary contains 5000 little-endian IEEE 754 binary32 offsets. Its adjacent JSON records the source catalogue, epoch-fixture, native-library and original-oracle hashes and the upstream revision.

The independent native loader and `addObject` oracle comes from [this pinned upstream source](https://github.com/claurel/cosmographia/blob/171462736a30c06594dfc45ad2daf85d024b20e2/src/main/KeplerianSwarm.cpp).

`generate_astorb_discovery_reference.py` first reproduces the complete archived native attribute buffer byte for byte. It then replaces only the input epochs with the independent ERFA midnight-TT values and asks the same native code to emit corrected attributes. The fixture stores its discovery-offset channel without calling the C# implementation or recomputing expected discovery dates inside the test.

To reproduce, provide the original source catalogue, native reference library with `loader_reference`/`objects_reference`, native JSON and attribute buffer:

```text
python tools/reference/generate_astorb_discovery_reference.py INPUT NATIVE_LIBRARY NATIVE_JSON NATIVE_ATTRIBUTES tests/SpaceTangent.Core.Tests/Fixtures/astorb-epoch-reference.csv tests/SpaceTangent.Core.Tests/Fixtures/astorb-discovery-reference.bin
```

Ordinary tests need only the frozen files. The full external-corpus test additionally checks every original attribute and all corrected discovery offsets. A hash/length test runs without external inputs. Updating an epoch or catalogue requires regenerating the oracle and reviewing its provenance, not copying production output into expected values.

## Authored systems and fixed-element moons

`demo-worlds-reference.json` contains 45 states and orientation matrices for the nine published demonstration bodies at five epochs. `catalogue-moons-reference.json` contains 270 position/velocity coordinates for five fixed-element moon orbits at nine epochs. The adjacent `authored-worlds-reference.json` records the generator and file hashes and native tool versions.

These are new independent replacements for unavailable historical witnesses. They do not recover or certify the missing old recordings. Parameters are transcribed from the authored definitions; propagation uses NAIF CSPICE `conics`, and orientations use its axis-angle matrices. No managed simulation assembly is invoked. Both existing acceptance tests retain their original assertion counts and tolerances and accept an explicit external override when supplied.

```text
python tools/reference/generate_authored_worlds.py NEW_OUTPUT_DIRECTORY
python -m unittest discover -s tools/reference -p test_authored_worlds.py
```

The output directory must not exist. The committed fixtures run in ordinary C# tests; the pinned native-tool CI job independently regenerates them. Reproduction allows last-bit differences in native math across operating systems while retaining the stricter position, velocity and orientation acceptance limits in C#.
