"""Generate universal-conic state references with NAIF CSPICE."""

import argparse
import ctypes as ct
from pathlib import Path


def generate(library):
    spice = ct.CDLL(str(library.resolve()))
    spice.conics_c.argtypes = [ct.POINTER(ct.c_double), ct.c_double, ct.POINTER(ct.c_double)]
    yield "# CSPICE N0067 conics_c; q_km,e,i_rad,node_rad,periapsis_rad,gm_km3_s2,elapsed_s,x,y,z,vx,vy,vz"
    for eccentricity in (0, .01, .75, .999999999, 1, 1.000000001, 1.5, 3):
        for seconds in (0, -3600, 3600, 86400, 10000000):
            parameters = (7000, eccentricity, .7, 1.2, -.4, 398600.4418)
            elements = (ct.c_double * 8)(*parameters[:5], 0, 0, parameters[5])
            result = (ct.c_double * 6)()
            spice.conics_c(elements, seconds, result)
            yield ",".join(format(value, ".17g") for value in (*parameters, seconds, *result))


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("library", type=Path)
    parser.add_argument("output", type=Path)
    args = parser.parse_args()
    args.output.write_text("\n".join(generate(args.library)) + "\n", encoding="utf-8", newline="\n")
