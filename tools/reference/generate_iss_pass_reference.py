"""Independent one-second geometric pass scan: python-sgp4 TEME -> ERFA GMST82 -> geodetic ENU.

Usage: python -I generate_iss_pass_reference.py tle_directory output.csv
The archived groups must contain NORAD 25544 for 2026-10-27. No SpaceTangent code is called.
"""
import argparse
import math
from pathlib import Path
import erfa
import numpy as np
from sgp4.api import Satrec, WGS72

p = argparse.ArgumentParser(description=__doc__)
p.add_argument('tle_directory', type=Path)
p.add_argument('output', type=Path)
args = p.parse_args()
candidates = []
for path in args.tle_directory.glob('*.txt'):
    lines = path.read_text().splitlines()
    for i, line in enumerate(lines[:-1]):
        if line.startswith('1 25544') and lines[i+1].startswith('2 25544'):
            sat = Satrec.twoline2rv(line, lines[i+1], WGS72)
            candidates.append((sat.jdsatepoch + sat.jdsatepochF, sat, line, lines[i+1]))
_, sat, first, second = max(candidates, key=lambda item: item[0])
seconds = np.arange(86401, dtype=float)
base, fraction = erfa.cal2jd(2026, 10, 27)
jd = np.full(len(seconds), base + fraction)
fractions = seconds / 86400
error, r, _ = sat.sgp4_array(jd, fractions)
assert not np.any(error)
# TT-UTC = TT-UT1 = 69.184 s for the model/epoch used by this regression, hence UT1 = UTC.
gmst = erfa.gmst82(jd, fractions)
c, s = np.cos(gmst), np.sin(gmst)
fixed = np.column_stack((c*r[:,0]+s*r[:,1], -s*r[:,0]+c*r[:,1], r[:,2]))
lat, lon = np.radians([55.7558, 37.6173])
f = 1/298.257223563
n = 6378.1366 / math.sqrt(1-f*(2-f)*math.sin(lat)**2)
eye = np.array([(n+.15)*math.cos(lat)*math.cos(lon), (n+.15)*math.cos(lat)*math.sin(lon),
                (n*(1-f*(2-f))+.15)*math.sin(lat)])
up = np.array([math.cos(lat)*math.cos(lon), math.cos(lat)*math.sin(lon), math.sin(lat)])
direction = fixed-eye
altitude = np.degrees(np.arcsin(direction@up / np.linalg.norm(direction, axis=1)))
indices = np.where(altitude >= 10)[0]
groups = np.split(indices, np.where(np.diff(indices) > 1)[0]+1)
rows = ['# 2026-10-27 UTC; seconds since midnight; threshold 10 degrees; 1-second sampling',
        '# ' + first, '# ' + second, '# first,peak,last,geometric_altitude_degrees']
for group in groups:
    peak = group[np.argmax(altitude[group])]
    rows.append(f'{group[0]},{peak},{group[-1]},{altitude[peak]:.12f}')
args.output.write_text('\n'.join(rows)+'\n')
