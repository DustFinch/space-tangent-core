"""Independent midnight TT/TDB epochs from ASTORB dates using ERFA, not the legacy calendar formula.

Usage: python -I generate_astorb_epoch_reference.py astorb_file output.csv
"""
import argparse
from pathlib import Path
import erfa
p = argparse.ArgumentParser(description=__doc__)
p.add_argument('source', type=Path)
p.add_argument('output', type=Path)
args = p.parse_args()
dates = sorted({line[106:114] for line in args.source.read_text().splitlines() if line.strip()})
rows = ['# ASTORB midnight TT; ERFA cal2jd/dtdb at geocentre; date,TDB seconds since J2000']
for date in dates:
    a,b = erfa.cal2jd(int(date[:4]),int(date[4:6]),int(date[6:]))
    seconds = (a-2451545+b)*86400 + erfa.dtdb(a,b,0,0,0,0)
    rows.append(f'{date},{seconds:.12f}')
args.output.write_text('\n'.join(rows)+'\n')
