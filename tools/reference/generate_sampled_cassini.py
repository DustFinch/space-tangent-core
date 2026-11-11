"""Independent Hermite and CSPICE reference for the sampled Cassini SSC."""
import argparse
import bisect
import ctypes as ct
import datetime as dt
from pathlib import Path


def jd(year, month, day, hour=0, minute=0):
    return 2440587.5 + (dt.datetime(year, month, day, hour, minute) - dt.datetime(1970, 1, 1)).total_seconds() / 86400


def sample(rows, date):
    index = bisect.bisect_right(rows, date, key=lambda row: row[0])
    if index == 0:
        return rows[0][1:]
    if index == len(rows):
        return rows[-1][1:]
    left, right = rows[index - 1], rows[index]
    width = (right[0] - left[0]) * 86400
    u = (date - left[0]) / (right[0] - left[0])
    value, rate = [], []
    for p, q, v, w in zip(left[1:4], right[1:4], left[4:7], right[4:7]):
        value.append((2*u**3-3*u**2+1)*p + (u**3-2*u**2+u)*width*v +
                     (-2*u**3+3*u**2)*q + (u**3-u**2)*width*w)
        rate.append(((6*u*u-6*u)*p + (-6*u*u+6*u)*q)/width +
                    (3*u*u-4*u+1)*v + (3*u*u-2*u)*w)
    return [*value, *rate]


def generate(library, kernel, trajectories):
    spice = ct.CDLL(str(library.resolve()))
    vector = ct.c_double * 3
    matrix = vector * 3
    state = ct.c_double * 6
    spice.furnsh_c.argtypes = [ct.c_char_p]
    spice.pxform_c.argtypes = [ct.c_char_p, ct.c_char_p, ct.c_double, matrix]
    spice.spkgeo_c.argtypes = [ct.c_int, ct.c_double, ct.c_char_p, ct.c_int, state, ct.POINTER(ct.c_double)]
    spice.furnsh_c(str(kernel.resolve()).encode("ascii"))
    rotate = matrix()
    spice.pxform_c(b"ECLIPJ2000", b"J2000", 0, rotate)
    boundaries = [jd(1997, 10, 15, 9, 28), *(jd(year, 6, 11) for year in range(2004, 2018)),
                  jd(2017, 9, 15, 10, 34)]
    yield "# Hermite from source XYZV and CSPICE N0067 frame and center. TDB JD, center, J2000 relative and barycentric states"
    for phase, (start, end) in enumerate(zip(boundaries, boundaries[1:])):
        name = "cassini-cruise.xyzv" if phase == 0 else f"cassini-saturn_{phase:02d}y.xyzv"
        rows = [list(map(float, line.split())) for line in (trajectories / name).read_text().splitlines()
                if line.strip() and not line.lstrip().startswith("#")]
        center = 10 if phase == 0 else 6
        for date in (start, (start + end) / 2, end - 1/86400):
            local = sample(rows, date)
            relative = [sum(a*b for a, b in zip(row, axis)) for axis in (local[:3], local[3:]) for row in rotate]
            origin, light_time = state(), ct.c_double()
            spice.spkgeo_c(center, (date - 2451545) * 86400, b"J2000", 0, origin, ct.byref(light_time))
            absolute = [a+b for a, b in zip(relative, origin)]
            yield ",".join(format(value, ".17g") for value in [date, center, *relative, *absolute])


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    for name in ("library", "kernel", "trajectories", "output"):
        parser.add_argument(name, type=Path)
    args = parser.parse_args()
    lines = generate(args.library, args.kernel, args.trajectories)
    args.output.write_text("\n".join(lines) + "\n", encoding="utf-8", newline="\n")
