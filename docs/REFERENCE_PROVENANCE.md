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
