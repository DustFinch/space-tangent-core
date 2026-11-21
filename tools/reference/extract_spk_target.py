"""Copy one body's original SPK segments with CSPICE, retaining their coverage.

This prepares a bounded test input without changing the application's file limits.
The original file checksum is required. No trajectory is fitted or resampled.
"""
import argparse
import hashlib
import json
from pathlib import Path
import spiceypy as spice


def digest(path):
    value = hashlib.sha256()
    with path.open("rb") as source:
        for block in iter(lambda: source.read(1024 * 1024), b""):
            value.update(block)
    return value.hexdigest()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("source", type=Path)
    parser.add_argument("target", type=int)
    parser.add_argument("output", type=Path)
    parser.add_argument("--sha256", required=True)
    args = parser.parse_args()
    if args.source.resolve() == args.output.resolve() or args.output.exists():
        raise ValueError("Choose a new output path separate from the original kernel.")
    if digest(args.source) != args.sha256.lower():
        raise ValueError("Original SPK SHA-256 mismatch.")
    source = spice.dafopr(str(args.source))
    segments = []
    try:
        spice.dafbfs(source)
        while spice.daffna():
            descriptor = spice.dafgs()
            times, ids = spice.dafus(descriptor, 2, 6)
            if ids[0] == args.target:
                segments.append((descriptor[:5].copy(), times.copy(), ids.copy(), spice.dafgn()))
        if not segments:
            raise ValueError("The requested body has no segments in this kernel.")
        args.output.parent.mkdir(parents=True, exist_ok=True)
        destination = spice.spkopn(str(args.output), "TARGET SEGMENT COPY", 0)
        try:
            for descriptor, times, _, name in segments:
                spice.spksub(source, descriptor, name, float(times[0]), float(times[1]), destination)
        except BaseException:
            spice.dafcls(destination)
            raise
        else:
            spice.spkcls(destination)
    finally:
        spice.dafcls(source)
    queries = [(int(ids[1]), frame, float(t)) for _, times, ids, _ in segments
               for frame in [spice.frmnam(int(ids[2]))]
               for t in [times[0], (times[0] + times[1]) / 2, times[1]]]
    spice.kclear()
    spice.furnsh(str(args.source))
    expected = [spice.spkgeo(args.target, second, frame, center)[0] for center, frame, second in queries]
    spice.kclear()
    spice.furnsh(str(args.output))
    for (center, frame, second), reference in zip(queries, expected):
        actual = spice.spkgeo(args.target, second, frame, center)[0]
        if not (actual == reference).all():
            raise ValueError("Copied segments differ from the original CSPICE state.")
    spice.kclear()
    args.output.with_suffix(".provenance.json").write_text(json.dumps({
        "sourceSha256": args.sha256.lower(), "outputSha256": digest(args.output), "target": args.target,
        "operation": "CSPICE spksub: original segments, no fit/resampling", "toolkit": spice.tkvrsn("TOOLKIT"),
        "spiceypy": spice.__version__, "segmentCount": len(segments), "identicalReferenceStates": len(queries),
        "coverageSeconds": [times.tolist() for _, times, _, _ in segments]}, indent=2) + "\n", encoding="utf-8")
    print(f"Copied {len(segments)} segments; {len(queries)} CSPICE states match exactly; {args.output.stat().st_size} bytes.")


if __name__ == "__main__":
    main()
