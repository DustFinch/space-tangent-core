# SPDX-License-Identifier: GPL-2.0-or-later
"""Freeze discovery offsets using the independent native loader and addObject oracle.

First reproduce the entire archived native ASTORB attribute buffer byte for byte.
Then replace only record epochs with the independent ERFA midnight-TT fixture and
ask the same native addObject code to produce the corrected discovery offsets.
This generator never imports or executes the production C# implementation.
"""
import argparse
import ctypes
import hashlib
import json
from pathlib import Path
import re


def digest(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("source", type=Path)
    parser.add_argument("native_library", type=Path)
    parser.add_argument("native_reference", type=Path)
    parser.add_argument("native_attributes", type=Path)
    parser.add_argument("epochs", type=Path)
    parser.add_argument("output", type=Path)
    args = parser.parse_args()
    reference = json.loads(args.native_reference.read_text(encoding="utf-8"))
    case = next(item for item in reference["catalogues"] if item["format"] == "astorb")
    if digest(args.source) != case["sha256"] or digest(args.native_attributes) != reference["objectsSha256"]:
        raise ValueError("The original corpus does not match its pinned native provenance")
    epochs = dict(line.split(",") for line in args.epochs.read_text().splitlines() if not line.startswith("#"))
    native = ctypes.CDLL(str(args.native_library.resolve()))
    doubles, floats = ctypes.POINTER(ctypes.c_double), ctypes.POINTER(ctypes.c_float)
    native.loader_reference.argtypes = [ctypes.c_int, doubles, ctypes.c_int, ctypes.c_int, ctypes.c_int, ctypes.c_double, doubles]
    native.objects_reference.argtypes = [doubles, ctypes.c_uint, floats, doubles, floats]
    records, dates = [], []
    for line in args.source.read_text(encoding="ascii").splitlines():
        if not line.strip():
            continue
        date = line[106:114]
        values = [float(line[a:b]) for a, b in [(169,181), (158,168), (148,157), (137,147), (126,136), (115,125)]]
        designation = line[7:26].strip()
        discovery = -365.25 * 100 * 86400
        if re.fullmatch(r"\d{4} [A-Z][A-Z]\d*", designation):
            discovery = ((int(designation[:4]) - 2000) * 365.25 + (ord(designation[5]) - ord("A")) * (365.25 / 24)) * 86400
        result = (ctypes.c_double * 9)()
        native.loader_reference(0, (ctypes.c_double * 6)(*values), int(date[:4]), int(date[4:6]), int(date[6:]), discovery, result)
        records.extend(result)
        dates.append(date)
    count = len(dates)

    def attributes():
        output, epoch, bound = (ctypes.c_float * (count * 9))(), ctypes.c_double(), ctypes.c_float()
        native.objects_reference((ctypes.c_double * len(records))(*records), count, output, ctypes.byref(epoch), ctypes.byref(bound))
        return bytes(output)

    original = args.native_attributes.read_bytes()[case["offsetBytes"]:case["offsetBytes"] + case["attributesBytes"]]
    if attributes() != original:
        raise ValueError("The native oracle did not reproduce the archived buffer byte for byte")
    for index, date in enumerate(dates):
        records[index * 9] = float(epochs[date])
    corrected = attributes()
    frozen = b"".join(corrected[index * 36 + 32:index * 36 + 36] for index in range(count))
    args.output.write_bytes(frozen)
    metadata = {"count": count, "floatFormat": "IEEE754 binary32 little-endian", "sha256": digest(args.output),
                "sourceSha256": digest(args.source), "nativeLibrarySha256": digest(args.native_library),
                "nativeReferenceSha256": digest(args.native_reference), "nativeAttributesSha256": digest(args.native_attributes),
                "epochFixtureSha256": digest(args.epochs), "upstreamCommit": reference["commit"],
                "verification": "Original native attribute buffer reproduced byte for byte before the epoch correction."}
    args.output.with_suffix(".json").write_text(json.dumps(metadata, indent=2) + "\n", encoding="utf-8")
    print(f"Frozen {count} independent native discovery offsets; SHA256 {metadata['sha256']}")


if __name__ == "__main__":
    main()
