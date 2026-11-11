"""Generate geometric Saturn camera intercepts with CSPICE N0067."""
import argparse
import ctypes as ct
import math
from pathlib import Path


def generate(library, root, lsk, centers, pck):
    spice = ct.CDLL(str(library.resolve()))
    vector = ct.c_double * 3
    state = ct.c_double * 6
    matrix = vector * 3
    corners = vector * 8
    spice.furnsh_c.argtypes = [ct.c_char_p]
    spice.spkgeo_c.argtypes = [ct.c_int, ct.c_double, ct.c_char_p, ct.c_int, state, ct.POINTER(ct.c_double)]
    spice.pxform_c.argtypes = [ct.c_char_p, ct.c_char_p, ct.c_double, matrix]
    spice.getfov_c.argtypes = [ct.c_int, ct.c_int, ct.c_int, ct.c_int, ct.c_char_p, ct.c_char_p,
                               vector, ct.POINTER(ct.c_int), corners]
    spice.bodvrd_c.argtypes = [ct.c_char_p, ct.c_char_p, ct.c_int, ct.POINTER(ct.c_int), vector]
    spice.surfpt_c.argtypes = [vector, vector, ct.c_double, ct.c_double, ct.c_double, vector, ct.POINTER(ct.c_int)]
    for file in (lsk, centers, pck, *(root / name for name in (
        "cas_v43.tf", "cas00172.tsc", "04182_04183pa_soi_itl.bc", "cas_iss_v10.ti", "040615AP_SK_04167_04186.bsp"
    ))):
        spice.furnsh_c(str(file.resolve()).encode("ascii"))

    radii = vector()
    dimension = ct.c_int()
    spice.bodvrd_c(b"SATURN", b"RADII", 3, ct.byref(dimension), radii)
    yield "# CSPICE getfov, spkgeo, pxform and surfpt. ET, instrument, ray index, observer and ray in J2000, found, IAU_SATURN intercept"
    for epoch in (141911269.46157101, 141916279.42874023, 141917558.4203589, 141918830.41202343, 141922715.38656482):
        observer, center, light_time = state(), state(), ct.c_double()
        spice.spkgeo_c(-82, epoch, b"J2000", 6, observer, ct.byref(light_time))
        spice.spkgeo_c(699, epoch, b"J2000", 6, center, ct.byref(light_time))
        fixed = matrix()
        spice.pxform_c(b"J2000", b"IAU_SATURN", epoch, fixed)
        origin = transform(fixed, (observer[i] - center[i] for i in range(3)), vector)
        for instrument in (-82360, -82361):
            shape, frame = ct.create_string_buffer(64), ct.create_string_buffer(64)
            boresight, bounds, count = vector(), corners(), ct.c_int()
            spice.getfov_c(instrument, 8, 64, 64, shape, frame, boresight, ct.byref(count), bounds)
            if shape.value != b"RECTANGLE" or count.value != 4:
                raise ValueError("Expected the original rectangular Cassini camera.")
            inertial = matrix()
            spice.pxform_c(frame.value, b"J2000", epoch, inertial)
            for index, local_ray in enumerate((boresight, *bounds[:4])):
                ray = transform(inertial, local_ray, vector)
                point, found = vector(), ct.c_int()
                spice.surfpt_c(origin, transform(fixed, ray, vector), *radii, point, ct.byref(found))
                if found.value and math.dist(point, origin) > 200000:
                    found.value = 0
                    point = vector()
                row = [epoch, instrument, index, *observer[:3], *ray, found.value, *point]
                yield ",".join(format(value, ".17g") for value in row)


def transform(matrix, vector, vector_type):
    components = tuple(vector)
    return vector_type(*(sum(a * b for a, b in zip(row, components)) for row in matrix))


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    for name in ("library", "root", "lsk", "centers", "pck", "output"):
        parser.add_argument(name, type=Path)
    args = parser.parse_args()
    lines = generate(args.library, args.root, args.lsk, args.centers, args.pck)
    args.output.write_text("\n".join(lines) + "\n", encoding="utf-8", newline="\n")
