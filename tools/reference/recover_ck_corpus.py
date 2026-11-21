"""Rebuild adapter catalogues and independent angular rates for a retained CK oracle.

Inputs are the original CK reference directory and its original companion kernels.
No SpaceTangent assemblies are loaded. Existing numerical references are copied only
after their input hashes have been checked; additional rates come from CSPICE sxform.
"""
import argparse
import hashlib
import json
from pathlib import Path
import shutil
import spiceypy as spice
import numpy as np


def write(path, value):
    path.write_text(json.dumps(value, indent=2, allow_nan=False) + "\n", encoding="utf-8")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("reference", type=Path)
    parser.add_argument("kernels", type=Path)
    parser.add_argument("output", type=Path)
    args = parser.parse_args()
    reference = json.loads((args.reference / "ck-reference.json").read_text(encoding="utf-8"))
    args.output.mkdir(parents=True, exist_ok=True)
    if args.output.resolve() in (args.reference.resolve(), args.kernels.resolve()):
        raise ValueError("Recovery output must be separate from retained inputs.")
    for entry in reference["generatedAssets"] + reference["originalAssets"]:
        name = entry["file"]
        if Path(name).name != name:
            raise ValueError("Expected flat retained kernel names.")
        source = next((root / name for root in (args.reference, args.kernels) if (root / name).is_file()), None)
        if source is None or source.stat().st_size != entry["bytes"] or hashlib.sha256(source.read_bytes()).hexdigest() != entry["sha256"]:
            raise ValueError(f"Retained input missing or changed: {name}")
        shutil.copyfile(source, args.output / name)
    shutil.copyfile(args.reference / "ck-reference.json", args.output / "ck-reference.json")
    common = ["oracle.tf", "oracle.tsc", "pck00011.tpc", "naif0012.tls"]
    groups = {}
    for row in reference["samples"]:
        groups.setdefault(row["group"], []).append(row)
    groups["static-frames"] = reference["frameSamples"]
    rates = []
    for name, rows in groups.items():
        files = list(dict.fromkeys(common + [file for row in rows for file in row.get("files", [])]))
        items = [{"name": frame, "class": "spacecraft", "center": "Sun",
                  "trajectory": {"type": "FixedPoint", "position": [0, 0, 0]},
                  "bodyFrame": {"type": "Spice", "name": frame}}
                 for frame in dict.fromkeys(row["frame"] for row in rows)]
        write(args.output / f"catalogue-{name}.json", {"version": "1.0", "spiceKernels": files, "items": items})
        if name == "static-frames":
            continue
        spice.kclear()
        spice.furnsh([str(args.output / file) for file in files])
        for row in rows:
            rate = {"group": name, "seconds": row["seconds"], "found": False}
            try:
                _, velocity = spice.xf2rav(spice.sxform("J2000", row["frame"], row["seconds"]))
                rate.update(found=True, angularVelocity=velocity.tolist())
            except spice.utils.exceptions.SpiceyError:
                # Pointing-only CK records intentionally have no angular-rate transform.
                pass
            rates.append(rate)
    spice.kclear()
    found = sum(row["found"] and sample["found"] for row, sample in zip(rates, reference["samples"]))
    if len(rates) != 440 or found != 139:
        raise ValueError(f"Unexpected retained CK corpus: {len(rates)} rows, {found} available rates.")
    write(args.output / "ck-rates-oracle.json", rates)
    spice.furnsh([str(args.output / file) for file in common + ["native-ck2.bc"]])
    kernel = args.output / "ck-frame-state.bsp"
    if kernel.exists():
        kernel.unlink()
    handle = spice.spkopn(str(kernel), "CK FRAME STATE WITNESS", 0)
    try:
        spice.spkw08(handle, 900001, 10, "NIGHT_CK2", 0., 100., "CK FRAME STATE", 1, 2,
                     np.array([[100., 200., 300., .1, .2, .3], [110., 220., 330., .1, .2, .3]]), 0., 100.)
    finally:
        spice.spkcls(handle)
    spice.furnsh(str(kernel))
    states = [{"seconds": second, "state": spice.spkgeo(900001, second, "J2000", 10)[0].tolist()}
              for second in [0., .1, 1., 10., 20., 29.9, 30.]]
    write(args.output / "ck-frame-state.json", states)
    write(args.output / "catalogue-ck-frame-state.json", {"version": "1.0",
          "spiceKernels": common + ["native-ck2.bc", "ck-frame-state.bsp"], "items": [{
              "name": "CK Frame State", "class": "spacecraft", "center": "Sun",
              "trajectory": {"type": "Spice", "target": "900001", "center": "10"}}]})
    spice.kclear()
    write(args.output / "recovery-provenance.json", {
        "toolkit": spice.tkvrsn("TOOLKIT"), "spiceypy": spice.__version__,
        "nativeLibrarySha256": hashlib.sha256(Path(spice.utils.libspicehelper.libspice._name).read_bytes()).hexdigest(),
        "referenceSha256": hashlib.sha256((args.reference / "ck-reference.json").read_bytes()).hexdigest(),
        "operation": "CSPICE sxform(J2000, frame, et), xf2rav; original CK input hash verification",
        "movingFrameWitness": {"target": 900001, "center": 10, "sourceFrame": "NIGHT_CK2",
            "spkSha256": hashlib.sha256(kernel.read_bytes()).hexdigest(), "referenceStates": len(states)},
        "rows": len(rates), "ratesWithPointing": found})
    print(f"Recovered {len(groups)} catalogues and {found} independent angular-rate samples.")


if __name__ == "__main__":
    main()
