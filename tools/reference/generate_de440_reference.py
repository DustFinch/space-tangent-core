"""Generate the fixture with NAIF CSPICE and the official DE440s SPK."""

import argparse
import ctypes as ct
import hashlib
from pathlib import Path


KERNEL_SHA256 = "c1c7feeab882263fc493a9d5a5b2ddd71b54826cdf65d8d17a76126b260a49f2"
EPOCHS = (2414992.5, 2415024.5, 2451545.0, 2457754.5, 2460409.25, 2488080.5)
BODIES = ((10, 0), (399, 10), (301, 399))


def generate(library, kernel):
    if hashlib.sha256(kernel.read_bytes()).hexdigest() != KERNEL_SHA256:
        raise SystemExit("Expected the pinned official de440s.bsp kernel.")

    spice = ct.CDLL(str(library.resolve()))
    spice.furnsh_c.argtypes = [ct.c_char_p]
    spice.spkgeo_c.argtypes = [ct.c_int, ct.c_double, ct.c_char_p, ct.c_int,
                              ct.POINTER(ct.c_double), ct.POINTER(ct.c_double)]
    spice.tkvrsn_c.argtypes = [ct.c_char_p]
    spice.tkvrsn_c.restype = ct.c_char_p
    version = spice.tkvrsn_c(b"TOOLKIT").decode("ascii")
    spice.furnsh_c(str(kernel.resolve()).encode("ascii"))

    yield f"# {version}; DE440s SHA256 {KERNEL_SHA256}; J2000 geometric states"
    yield "# JD_TDB,target,center,X_km,Y_km,Z_km,VX_km_s,VY_km_s,VZ_km_s"
    for jd in EPOCHS:
        for target, center in BODIES:
            state = (ct.c_double * 6)()
            light_time = ct.c_double()
            spice.spkgeo_c(target, (jd - 2451545) * 86400, b"J2000", center, state, ct.byref(light_time))
            yield ",".join((str(jd), str(target), str(center), *(format(value, ".17g") for value in state)))


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("library", type=Path, help="Path to a native CSPICE shared library")
    parser.add_argument("kernel", type=Path, help="Path to the official de440s.bsp")
    parser.add_argument("output", type=Path, help="CSV output path")
    args = parser.parse_args()
    args.output.write_text("\n".join(generate(args.library, args.kernel)) + "\n", encoding="utf-8", newline="\n")
