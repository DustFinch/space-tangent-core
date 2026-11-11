"""Evaluate the original science satellite TLEs with NAIF CSPICE."""

import argparse
import ctypes as ct
import json
from pathlib import Path


def states(library, leapseconds, catalog):
    spice = ct.CDLL(str(library.resolve()))
    spice.furnsh_c.argtypes = [ct.c_char_p]
    spice.getelm_c.argtypes = [ct.c_int, ct.c_int, ct.c_void_p,
                              ct.POINTER(ct.c_double), ct.POINTER(ct.c_double)]
    spice.evsgp4_c.argtypes = [ct.c_double, ct.POINTER(ct.c_double),
                               ct.POINTER(ct.c_double), ct.POINTER(ct.c_double)]
    spice.furnsh_c(str(leapseconds.resolve()).encode("ascii"))
    radius = 6378.135
    xke = 60 / (radius**3 / 398600.8)**0.5
    geophysics = (ct.c_double * 8)(.001082616, -.00000253881, -.00000165597, xke, 120, 78, radius, 1)
    items = json.loads(catalog.read_text(encoding="utf-8-sig"))["items"]

    yield "# CSPICE N0067 getelm/evsgp4 with WGS 72 and the published NAIF leap-second kernel"
    yield "# satellite,offset_TDB_s,X_km,Y_km,Z_km,VX_km_s,VY_km_s,VZ_km_s"
    for item in items:
        if item["name"] not in ("Jason-1", "SORCE"):
            continue
        trajectory = item["trajectory"]
        lines = ct.create_string_buffer((trajectory["line1"] + "\0" + trajectory["line2"] + "\0").encode("ascii"))
        epoch = ct.c_double()
        elements = (ct.c_double * 10)()
        spice.getelm_c(1957, 70, lines, ct.byref(epoch), elements)
        for offset in (-86400, -3600, 0, 3600, 86400, 1209600):
            result = (ct.c_double * 6)()
            spice.evsgp4_c(epoch.value + offset, geophysics, elements, result)
            yield ",".join((item["name"], str(offset), *(format(value, ".17g") for value in result)))


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("library", type=Path)
    parser.add_argument("leapseconds", type=Path)
    parser.add_argument("catalog", type=Path)
    parser.add_argument("output", type=Path)
    args = parser.parse_args()
    args.output.write_text("\n".join(states(args.library, args.leapseconds, args.catalog)) + "\n",
                           encoding="utf-8", newline="\n")
