"""Generate IAU Moon axes and their time derivatives with CSPICE."""
import argparse
import ctypes
from pathlib import Path


def generate(library, kernel):
    spice = ctypes.CDLL(str(library.resolve()))
    matrix = (ctypes.c_double * 6) * 6
    spice.furnsh_c.argtypes = [ctypes.c_char_p]
    spice.sxform_c.argtypes = [ctypes.c_char_p, ctypes.c_char_p, ctypes.c_double, matrix]
    spice.furnsh_c(str(kernel.resolve()).encode("ascii"))
    yield "# CSPICE N0067: TDB seconds, IAU_MOON-to-J2000 columns, time derivatives"
    for seconds in (-4733553600, -86400, 0, 86400, 774173435.5, 788875269.125, 4733553600):
        result = matrix()
        spice.sxform_c(b"IAU_MOON", b"J2000", seconds, result)
        values = [result[row][axis] for row in range(6) for axis in range(3)]
        yield ",".join(format(value, ".17g") for value in [seconds, *values])


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    for name in ("library", "kernel", "output"):
        parser.add_argument(name, type=Path)
    arguments = parser.parse_args()
    lines = generate(arguments.library, arguments.kernel)
    arguments.output.write_text("\n".join(lines) + "\n", encoding="utf-8", newline="\n")
