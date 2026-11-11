"""Generate Titan and Saturn states and IAU frame derivatives with CSPICE."""
import argparse
import ctypes as ct
import datetime as dt
from pathlib import Path


def seconds(year, month, day, hour=0, minute=0):
    return (dt.datetime(year, month, day, hour, minute) - dt.datetime(2000, 1, 1, 12)).total_seconds()


def generate(library, titan, saturn, pck):
    spice = ct.CDLL(str(library.resolve()))
    state_type = ct.c_double * 6
    matrix_type = state_type * 6
    spice.furnsh_c.argtypes = [ct.c_char_p]
    spice.spkgeo_c.argtypes = [ct.c_int, ct.c_double, ct.c_char_p, ct.c_int, state_type, ct.POINTER(ct.c_double)]
    spice.sxform_c.argtypes = [ct.c_char_p, ct.c_char_p, ct.c_double, matrix_type]
    for file in (titan, saturn, pck):
        spice.furnsh_c(str(file.resolve()).encode("ascii"))
    yield "# CSPICE N0067: body, ET, state relative to 6, IAU-to-J2000 columns and column derivatives"
    separation = seconds(2004, 12, 25, 2, 2)
    touchdown = seconds(2005, 1, 14, 11, 22)
    for body, frame, last in ((606, b"IAU_TITAN", 410875200.0), (699, b"IAU_SATURN", 284904000.0)):
        for epoch in (126273600.0, separation, touchdown, touchdown + 86400, last):
            state, light_time = state_type(), ct.c_double()
            spice.spkgeo_c(body, epoch, b"J2000", 6, state, ct.byref(light_time))
            frame_state = matrix_type()
            spice.sxform_c(frame, b"J2000", epoch, frame_state)
            rotation = [frame_state[row][column] for column in range(3) for row in range(3)]
            rate = [frame_state[row][column] for column in range(3) for row in range(3, 6)]
            yield ",".join(format(value, ".17g") for value in [body, epoch, *state, *rotation, *rate])


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    for name in ("library", "titan", "saturn", "pck", "output"):
        parser.add_argument(name, type=Path)
    args = parser.parse_args()
    lines = generate(args.library, args.titan, args.saturn, args.pck)
    args.output.write_text("\n".join(lines) + "\n", encoding="utf-8", newline="\n")
