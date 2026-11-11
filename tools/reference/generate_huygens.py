"""Independent Huygens phase reference using the source samples and CSPICE."""
import argparse
import ctypes as ct
import math
from pathlib import Path

from generate_sampled_cassini import jd, sample


def rows(file):
    return [list(map(float, line.split())) for line in file.read_text().splitlines()
            if line.strip() and not line.lstrip().startswith("#")]


def normal(v):
    length = math.hypot(*v)
    return [x / length for x in v]


def cross(a, b):
    x, y, z = a
    i, j, k = b
    return [y*k-z*j, z*i-x*k, x*j-y*i]


def generate(library, de440, pck, trajectories):
    spice = ct.CDLL(str(library.resolve()))
    v3, v6 = ct.c_double * 3, ct.c_double * 6
    m3, m6 = v3 * 3, v6 * 6
    spice.furnsh_c.argtypes = [ct.c_char_p]
    spice.pxform_c.argtypes = [ct.c_char_p, ct.c_char_p, ct.c_double, m3]
    spice.sxform_c.argtypes = [ct.c_char_p, ct.c_char_p, ct.c_double, m6]
    spice.spkgeo_c.argtypes = [ct.c_int, ct.c_double, ct.c_char_p, ct.c_int, v6, ct.POINTER(ct.c_double)]
    spice.latrec_c.argtypes = [ct.c_double, ct.c_double, ct.c_double, v3]
    for path in (de440, pck):
        spice.furnsh_c(str(path.resolve()).encode("ascii"))
    ecliptic = m3()
    spice.pxform_c(b"ECLIPJ2000", b"J2000", 0, ecliptic)
    def equatorial(data):
        return [sum(a*b for a, b in zip(row, values)) for values in (data[:3], data[3:]) for row in ecliptic]

    cruise = rows(trajectories / "cassini-cruise.xyzv")
    year_one = rows(trajectories / "cassini-saturn_01y.xyzv")
    probe = rows(trajectories / "huygens.xyzv")
    def point_on_carrier(et):
        date = 2451545 + et / 86400
        first = date < 2453167.5
        rel = equatorial(sample(cruise if first else year_one, date))
        center, earth, lt = v6(), v6(), ct.c_double()
        spice.spkgeo_c(10 if first else 6, et, b"J2000", 0, center, ct.byref(lt))
        spice.spkgeo_c(399, et, b"J2000", 0, earth, ct.byref(lt))
        toward = [e-c-r for e, c, r in zip(earth, center, rel)]
        z_axis = normal(toward[:3])
        x_axis = normal(cross(toward[3:], z_axis))
        return [-.0014*x - .0002*z for x, z in zip(x_axis, z_axis)]

    yield "# SSC mount, XYZV, and CSPICE rotating site. TDB JD, phase (1/2/3), state relative to carrier or Titan"
    for date in (2451545.0, jd(2004, 12, 20)):
        et = (date - 2451545) * 86400
        offset = point_on_carrier(et)
        before, after = point_on_carrier(et - 1), point_on_carrier(et + 1)
        yield row(date, 1, *offset, *((b-a)/2 for a,b in zip(before, after)))
    for date in (jd(2004, 12, 27), jd(2005, 1, 13, 15)):
        yield row(date, 2, *equatorial(sample(probe, date)))
    local = v3()
    spice.latrec_c(2575.15 + .91926, math.radians(163.17754), math.radians(-10.29358), local)
    for date in (jd(2005, 1, 14, 12), jd(2005, 1, 15)):
        matrix = m6()
        spice.sxform_c(b"IAU_TITAN", b"J2000", (date - 2451545)*86400, matrix)
        yield row(date, 3, *(sum(a*b for a,b in zip(line, local)) for line in matrix))


def row(*values):
    return ",".join(format(value, ".17g") for value in values)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    for name in ("library", "de440", "pck", "trajectories", "output"):
        parser.add_argument(name, type=Path)
    args = parser.parse_args()
    result = generate(args.library, args.de440, args.pck, args.trajectories)
    args.output.write_text("\n".join(result) + "\n", encoding="utf-8", newline="\n")
