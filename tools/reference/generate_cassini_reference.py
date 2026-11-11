"""Evaluate the original Cassini SPK directly with NAIF CSPICE."""

import argparse
import ctypes as ct
import hashlib
from pathlib import Path


def generate(library, kernel):
    spice = ct.CDLL(str(library.resolve()))
    spice.furnsh_c.argtypes = [ct.c_char_p]
    spice.spkgeo_c.argtypes = [ct.c_int, ct.c_double, ct.c_char_p, ct.c_int,
                              ct.POINTER(ct.c_double), ct.POINTER(ct.c_double)]
    spice.furnsh_c(str(kernel.resolve()).encode("ascii"))
    yield f"# CSPICE N0067 Cassini -82 relative to Saturn barycentre 6, J2000"
    yield f"# SPK SHA256 {hashlib.sha256(kernel.read_bytes()).hexdigest()}"
    for et in (140556964.18453318, 140557097.65166655, 141000000.0, 141500000.0, 141955264.18408903):
        values = (ct.c_double * 6)()
        light_time = ct.c_double()
        spice.spkgeo_c(-82, et, b"J2000", 6, values, ct.byref(light_time))
        yield ",".join(format(value, ".17g") for value in (et, *values))


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("library", type=Path)
    parser.add_argument("kernel", type=Path)
    parser.add_argument("output", type=Path)
    args = parser.parse_args()
    args.output.write_text("\n".join(generate(args.library, args.kernel)) + "\n", encoding="utf-8", newline="\n")
