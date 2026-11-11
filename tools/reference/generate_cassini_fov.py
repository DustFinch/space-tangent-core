"""Generate the original Cassini camera rays with CSPICE N0067."""
import argparse
import ctypes as ct
import math
from pathlib import Path


def generate(library, root, lsk):
    spice = ct.CDLL(str(library.resolve()))
    vector = ct.c_double * 3
    matrix = vector * 3
    boundaries = vector * 8
    spice.furnsh_c.argtypes = [ct.c_char_p]
    spice.getfov_c.argtypes = [ct.c_int, ct.c_int, ct.c_int, ct.c_int, ct.c_char_p,
                               ct.c_char_p, vector, ct.POINTER(ct.c_int), boundaries]
    spice.pxform_c.argtypes = [ct.c_char_p, ct.c_char_p, ct.c_double, matrix]
    for kernel in [lsk, *(root / name for name in
                        ("cas_v43.tf", "cas00172.tsc", "04182_04183pa_soi_itl.bc", "cas_iss_v10.ti"))]:
        spice.furnsh_c(str(kernel.resolve()).encode("ascii"))

    yield "# CSPICE N0067 getfov and pxform. ID, TDB seconds, J2000 unit boresight, four J2000 unit corners"
    for instrument in (-82360, -82361):
        shape, frame = ct.create_string_buffer(64), ct.create_string_buffer(64)
        boresight, bounds, count = vector(), boundaries(), ct.c_int()
        spice.getfov_c(instrument, 8, 64, 64, shape, frame, boresight, ct.byref(count), bounds)
        if shape.value != b"RECTANGLE" or count.value != 4:
            raise ValueError("Expected the original rectangular Cassini camera.")
        for epoch in (141917558.4203589, 141918830.41202343):
            rotation = matrix()
            spice.pxform_c(frame.value, b"J2000", epoch, rotation)
            values = []
            for ray in (boresight, *bounds[:4]):
                transformed = [sum(a * b for a, b in zip(row, ray)) for row in rotation]
                length = math.hypot(*transformed)
                values.extend(component / length for component in transformed)
            yield ",".join([str(instrument), repr(epoch), *(format(v, ".17g") for v in values)])


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    for name in ("library", "root", "lsk", "output"):
        parser.add_argument(name, type=Path)
    args = parser.parse_args()
    result = "\n".join(generate(args.library, args.root, args.lsk)) + "\n"
    args.output.write_text(result, encoding="utf-8", newline="\n")
