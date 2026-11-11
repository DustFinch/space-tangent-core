"""Generate Saturn center reference states from the original SAT480 kernel."""
import argparse
import ctypes as ct
from pathlib import Path


def generate(library, kernel):
    spice = ct.CDLL(str(library.resolve()))
    state_type = ct.c_double * 6
    spice.furnsh_c.argtypes = [ct.c_char_p]
    spice.spkgeo_c.argtypes = [ct.c_int, ct.c_double, ct.c_char_p, ct.c_int, state_type, ct.POINTER(ct.c_double)]
    spice.furnsh_c(str(kernel.resolve()).encode("ascii"))
    yield "# CSPICE N0067 geometric Saturn 699 relative to 6, J2000, original SAT480"
    first, last = -1577880000.0, 1577880000.0
    record_boundary = first + 13270 * 129600
    for epoch in (first, 141911269.46157101, 141917558.4203589, 141918830.41202343,
                  141922715.38656482, record_boundary - 1e-4, record_boundary, record_boundary + 1e-4,
                  465134400.0 - 1e-4, 465134400.0, 465134400.0 + 1e-4, last):
        state, light_time = state_type(), ct.c_double()
        spice.spkgeo_c(699, epoch, b"J2000", 6, state, ct.byref(light_time))
        yield ",".join(format(value, ".17g") for value in (epoch, *state))


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    for name in ("library", "kernel", "output"):
        parser.add_argument(name, type=Path)
    args = parser.parse_args()
    args.output.write_text("\n".join(generate(args.library, args.kernel)) + "\n", encoding="utf-8", newline="\n")
