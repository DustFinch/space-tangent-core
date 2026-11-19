"""Generate the Earth orientation fixture with ERFA (pyerfa).

For each TT epoch and an explicit TT - UT1, the script composes the celestial-to-terrestrial matrix exactly as
ERFA does for the long-term precession: eraLtpb (Vondrak et al. 2011 with frame bias), eraNut00b with eraObl06 and
eraNumat, eraBpn2xy, eraS06, eraC2ixys, eraEra00 and eraC2tcio without polar motion. It also records the mean
equator of date (eraLtpb), the Earth rotation angle and the TEME-to-celestial matrix through eraGmst82.

As a model check it prints the largest angle between this composition and the full IAU 2006/2000A matrix
(eraC2t06a) over 1900-2100; the fixture itself stores only ERFA outputs.

Usage: python -I generate_earth_orientation.py output.csv
"""

import argparse
from pathlib import Path

import erfa
import numpy as np

J2000 = 2451545.0
# TT epochs as (day offset from J2000, TT - UT1 seconds). The offsets cover years 1 to 4000 and
# a dense modern span; TT - UT1 values are representative but arbitrary, since they are inputs.
EPOCHS = [
    (-730119.5, 10583.6), (-547500.25, 6000.0), (-345953.7, 1800.0), (-182621.0, 120.0),
    (-36524.5, -2.7), (-18262.0, 29.4), (-1.0, 63.8), (0.0, 63.83), (0.5, 63.83), (3653.25, 64.7),
    (9132.125, 69.2), (9600.75, 69.3), (36525.0, 120.0), (365250.0, 4500.0), (730120.5, 12000.0),
]


def matrices(days, ttmut1):
    tt1, tt2 = J2000, days
    ut1 = days - ttmut1 / 86400.0
    epj = 2000.0 + days / 365.25
    rpb = erfa.ltpb(epj)
    dpsi, deps = erfa.nut00b(tt1, tt2)
    epsa = erfa.obl06(tt1, tt2)
    rnpb = erfa.numat(epsa, dpsi, deps) @ rpb
    x, y = erfa.bpn2xy(rnpb)
    s = erfa.s06(tt1, tt2, x, y)
    rc2i = erfa.c2ixys(x, y, s)
    era = erfa.era00(J2000, ut1)
    rc2t = erfa.c2tcio(rc2i, era, np.eye(3))
    gmst = erfa.gmst82(J2000, ut1)
    teme = rc2t.T @ erfa.rz(gmst, np.eye(3))
    return rpb, rc2t, era, teme


def angle(a, b):
    r = a @ b.T
    return np.degrees(np.arccos(np.clip((np.trace(r) - 1) / 2, -1, 1))) * 3600


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("output", type=Path)
    args = parser.parse_args()
    rows = [
        f"# pyerfa {erfa.__version__}: ltpb, nut00b, obl06, s06, c2ixys, era00, c2tcio, gmst82",
        "# tt_days_from_J2000,tt_minus_ut1_s,era_rad,ltpb[9],c2t[9],teme_to_celestial[9]",
    ]
    for days, ttmut1 in EPOCHS:
        rpb, rc2t, era, teme = matrices(days, ttmut1)
        values = [days, ttmut1, era, *rpb.ravel(), *rc2t.ravel(), *teme.ravel()]
        rows.append(",".join(format(v, ".17g") for v in values))
    args.output.write_text("\n".join(rows) + "\n", encoding="utf-8", newline="\n")

    worst = 0.0
    for days in np.linspace(-36525, 36525, 401):
        _, rc2t, _, _ = matrices(days, 64.0)
        ut1 = days - 64.0 / 86400.0
        full = erfa.c2t06a(J2000, days, J2000, ut1, 0.0, 0.0)
        worst = max(worst, angle(rc2t, full))
    print(f"largest difference from IAU 2006/2000A over 1900-2100: {worst * 1000:.2f} mas")


if __name__ == "__main__":
    main()
