"""Generate Mars satellite conic states in the moving IAU equator using CSPICE."""
import argparse
import ctypes as ct
import math
from pathlib import Path
import re


def rows(library, pck, catalog):
    spice = ct.CDLL(str(library.resolve()))
    state = ct.c_double * 6
    elements = ct.c_double * 8
    matrix = state * 6
    spice.furnsh_c.argtypes = [ct.c_char_p]
    spice.pdpool_c.argtypes = [ct.c_char_p, ct.c_int, ct.POINTER(ct.c_double)]
    spice.sxform_c.argtypes = [ct.c_char_p, ct.c_char_p, ct.c_double, matrix]
    spice.conics_c.argtypes = [elements, ct.c_double, state]
    spice.furnsh_c(str(pck.resolve()).encode("ascii"))

    # CSPICE computes a complete state transform with the pole terms and zero spin.
    zero = (ct.c_double * 3)()
    spice.pdpool_c(b"BODY499_PM", len(zero), zero)
    nutation = re.search(r"BODY499_NUT_PREC_PM\s*=\s*\(([^)]+)\)", pck.read_text(encoding="ascii"))
    zero = (ct.c_double * len(nutation[1].split()))()
    spice.pdpool_c(b"BODY499_NUT_PREC_PM", len(zero), zero)
    source = catalog.read_text(encoding="utf-8-sig")
    yield "# CSPICE N0067: moon, ET, six relative J2000 components, mean-equator axes and derivatives"
    for name in ("Phobos", "Deimos"):
        match = re.search(rf'"{name}:[^"]+" "Sol/Mars".*?EllipticalOrbit\s*\{{([^}}]+)\}}', source, re.S)
        values = dict(re.findall(r"(?m)^\s*(\w+)\s+([-+\d.]+)", match[1]))
        a, eccentricity, period, jd = (float(values[key]) for key in ("SemiMajorAxis", "Eccentricity", "Period", "Epoch"))
        epoch = (jd - 2451545) * 86400
        angles = [math.radians(float(values[key])) for key in ("Inclination", "AscendingNode", "ArgOfPericenter", "MeanAnomaly")]
        mu = (math.tau / (period * 86400)) ** 2 * a ** 3
        orbit = elements(a * (1 - eccentricity), eccentricity, *angles, epoch, mu)
        for seconds in (-86400, 0, 12345, 259200):
            et = epoch + seconds
            local, equator = state(), matrix()
            spice.conics_c(orbit, et, local)
            spice.sxform_c(b"IAU_MARS", b"J2000", et, equator)
            transformed = [sum(coefficient * value for coefficient, value in zip(line, local)) for line in equator]
            columns = [equator[row][axis] for axis in range(3) for row in range(6)]
            yield ",".join([name, *(format(value, ".17g") for value in [et, *transformed, *columns])])


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    for name in ("library", "pck", "catalog", "output"):
        parser.add_argument(name, type=Path)
    args = parser.parse_args()
    args.output.write_text("\n".join(rows(args.library, args.pck, args.catalog)) + "\n", encoding="utf-8", newline="\n")
