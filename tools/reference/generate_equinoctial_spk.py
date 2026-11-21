"""Build a small type-17 SPK and independent CSPICE geometric state witnesses."""
import argparse
import hashlib
import json
from pathlib import Path
import spiceypy as spice


def generate(destination):
    destination.mkdir(parents=True, exist_ok=True)
    kernel = destination / "equinoctial-spk-reference.bin"
    if kernel.exists():
        raise ValueError("Refusing to replace an existing reference kernel.")
    handle = spice.spkopn(str(kernel), "Equinoctial ABI acceptance", 0)
    elements = [
        [10000., .1, .2, .4, .12, -.08, 1e-7, 1e-3, -2e-7],
        [25000., .8, .35, -2., -.2, .1, -2e-7, 2e-4, 3e-7],
        [7000., 0., 0., 6.2, 0., 0., 0., 1.1e-3, 0.],
    ]
    try:
        for index, values in enumerate(elements):
            spice.spkw17(handle, 9000001 + index, 399, "J2000", -20000., 20000.,
                         f"Independent equinoctial {index}", 0., values, .7, 1.1)
    finally:
        spice.spkcls(handle)
    rows = []
    spice.furnsh(str(kernel))
    try:
        for index in range(len(elements)):
            for seconds in [-20000., -12345.125, -100., 0., 1., 1234.5, 15000., 20000.]:
                state, _ = spice.spkezr(str(9000001 + index), seconds, "J2000", "NONE", "399")
                rows.append({"target": 9000001 + index, "seconds": seconds, "state": state.tolist()})
    finally:
        spice.unload(str(kernel))
    handle = spice.dafopr(str(kernel))
    try:
        spice.dafbfs(handle)
        assert spice.daffna()
        descriptor = spice.spkuds(spice.dafgs(5))
        first_word = int(descriptor[-2])
    finally:
        spice.dafcls(handle)
    report = {"generator": "CSPICE spkw17 + spkezr J2000/NONE; no product code",
              "spiceypy": spice.__version__, "toolkit": spice.tkvrsn("TOOLKIT"),
              "sha256": hashlib.sha256(kernel.read_bytes()).hexdigest(),
              "firstWord": first_word, "rows": rows}
    (destination / "equinoctial-spk-reference.json").write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("destination", type=Path)
    generate(parser.parse_args().destination)
