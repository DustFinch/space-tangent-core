"""Generate the SGP4 verification fixture from Vallado's published SGP4-VER test cases.

The 33 cases ship with python-sgp4 (``sgp4/SGP4-VER.TLE``). Each case is evaluated with
Vallado's C++ code as compiled into python-sgp4 (AFSPC operations mode, WGS-72) and,
independently, with NAIF CSPICE ``evsgp4`` through spiceypy. CSPICE implements the AFSPC mode,
which follows the operational code that produces published TLEs. The fixture stores the python-sgp4 states; the script fails if the
two implementations disagree by more than the documented tolerance on any successful state.

Usage: python -I generate_sgp4_verification.py naif0012.tls output.csv
"""

import argparse
import importlib.resources
import math
from pathlib import Path

import spiceypy
from sgp4.api import Satrec, WGS72

# CSPICE ships a Fortran translation of the same algorithm; the two agree to rounding.
POSITION_TOLERANCE_KM = 1e-6
VELOCITY_TOLERANCE_KM_S = 1e-9


def checksum(line):
    total = sum(int(c) if c.isdigit() else 1 if c == "-" else 0 for c in line[:68])
    return line[:68] + str(total % 10)


def cases():
    text = importlib.resources.files("sgp4").joinpath("SGP4-VER.TLE").read_text(encoding="ascii")
    first = None
    for raw in text.splitlines():
        if raw.startswith("1 "):
            first = raw[:69].ljust(69)
        elif raw.startswith("2 "):
            second = raw[:69].ljust(69)
            start, stop, step = (float(value) for value in raw[69:].split())
            yield checksum(first), checksum(second), start, stop, step


def times(start, stop, step):
    values = [0.0]
    count = int(math.floor((stop - start) / step + 1e-9))
    values += [start + index * step for index in range(count + 1)]
    if abs(values[-1] - stop) > 1e-9:
        values.append(stop)
    seen, result = set(), []
    for value in values:
        key = round(value, 9)
        if key not in seen:
            seen.add(key)
            result.append(value)
    return result


def cspice_state(first, second, minutes):
    epoch, elements = spiceypy.getelm(1957, [first, second])
    radius = 6378.135
    xke = 60 / math.sqrt(radius**3 / 398600.8)
    geophysics = [0.001082616, -0.00000253881, -0.00000165597, xke, 120.0, 78.0, radius, 1.0]
    return spiceypy.evsgp4(epoch + minutes * 60, geophysics, elements)


def generate(leapseconds):
    spiceypy.furnsh(str(leapseconds))
    yield (f"# Vallado SGP4-VER cases; Vallado C++ AFSPC mode WGS-72; cross-checked with "
           f"{spiceypy.tkvrsn('TOOLKIT')} evsgp4 to {POSITION_TOLERANCE_KM} km and {VELOCITY_TOLERANCE_KM_S} km/s")
    yield "# line1|line2 rows introduce a case; state rows: minutes,error,X_km,Y_km,Z_km,VX_km_s,VY_km_s,VZ_km_s"
    worst = (0.0, 0.0)
    for first, second, start, stop, step in cases():
        parsed = Satrec.twoline2rv(first, second, WGS72)
        satellite = Satrec()
        satellite.sgp4init(WGS72, "a", parsed.satnum, parsed.jdsatepoch + parsed.jdsatepochF - 2433281.5,
                           parsed.bstar, parsed.ndot, parsed.nddot, parsed.ecco, parsed.argpo, parsed.inclo,
                           parsed.mo, parsed.no_kozai, parsed.nodeo)
        yield f"{first}|{second}"
        for minutes in times(start, stop, step):
            error, position, velocity = satellite.sgp4_tsince(minutes)
            if error not in (0, 6):
                yield f"{minutes!r},{error},,,,,,"
                break
            yield ",".join([repr(minutes), str(error), *(format(v, ".17g") for v in (*position, *velocity))])
            try:
                reference = cspice_state(first, second, minutes)
            except spiceypy.utils.exceptions.SpiceyError:
                continue
            dp = math.dist(position, reference[:3])
            dv = math.dist(velocity, reference[3:])
            worst = (max(worst[0], dp), max(worst[1], dv))
            if error == 0 and (dp > POSITION_TOLERANCE_KM or dv > VELOCITY_TOLERANCE_KM_S):
                raise SystemExit(f"CSPICE disagrees for {first[2:7]} at {minutes} min: {dp} km, {dv} km/s")
            if error == 6:
                break
    print(f"largest CSPICE difference: {worst[0]:.3g} km, {worst[1]:.3g} km/s")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("leapseconds", type=Path, help="NAIF leap-second kernel, e.g. naif0012.tls")
    parser.add_argument("output", type=Path)
    args = parser.parse_args()
    lines = list(generate(args.leapseconds))
    args.output.write_text("\n".join(lines) + "\n", encoding="utf-8", newline="\n")
