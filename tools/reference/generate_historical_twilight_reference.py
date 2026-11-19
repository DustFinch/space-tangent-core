"""ERFA rise/set integration fixture for a specified constant ICRF Sun, geodetic Moscow, fixed TT-UT1.

This checks the historical civil-time/TT/orientation/event pipeline without depending on a second ephemeris.
Usage: python -I generate_historical_twilight_reference.py output.csv
"""
import sys, math
from pathlib import Path
import erfa
import numpy as np

lat,lon=np.radians([55.7558,37.6173])
f=1/298.257223563
n=6378.1366/math.sqrt(1-f*(2-f)*math.sin(lat)**2)
eye=np.array([(n+.15)*math.cos(lat)*math.cos(lon),(n+.15)*math.cos(lat)*math.sin(lon),(n*(1-f*(2-f))+.15)*math.sin(lat)])
up=np.array([math.cos(lat)*math.cos(lon),math.cos(lat)*math.sin(lon),math.sin(lat)])
sun=np.array([1e8,4e7,2e7])
rows=['# constant ICRF Sun (1e8,4e7,2e7) km; Moscow; TT-UT1=69.184 s; local mean date May 1',
      '# year,rise_TT_seconds_J2000,set_TT_seconds_J2000,civil_rise_TT_seconds_J2000,civil_set_TT_seconds_J2000']
for year in [1054,3000]:
    a,b=erfa.cal2jd(year,5,1)
    start=(a-2451545+b)*86400-37.6173*240+69.184
    def altitude(t):
        days=t/86400
        precession=erfa.ltpb(2000+days/365.25)
        dpsi,deps=erfa.nut00b(2451545,days)
        npb=erfa.numat(erfa.obl06(2451545,days),dpsi,deps)@precession
        x,y=erfa.bpn2xy(npb)
        c2i=erfa.c2ixys(x,y,erfa.s06(2451545,days,x,y))
        c2t=erfa.c2tcio(c2i,erfa.era00(2451545,days-69.184/86400),np.eye(3))
        direction=c2t@sun-eye
        return math.degrees(math.asin(direction@up/np.linalg.norm(direction)))
    crossings=[]
    for threshold in [-.833,-6]:
        times=[]
        for offset in range(0,86400,300):
            low,high=start+offset,start+offset+300
            below=altitude(low)<threshold
            if below == (altitude(high)<threshold): continue
            while high-low>.001:
                mid=(low+high)/2
                if (altitude(mid)<threshold)==below: low=mid
                else: high=mid
            times.append((below,(low+high)/2))
        crossings.extend([next(t for rising,t in times if rising),next(t for rising,t in times if not rising)])
    rows.append(str(year)+','+','.join(f'{t:.6f}' for t in crossings))
Path(sys.argv[1]).write_text('\n'.join(rows)+'\n')
