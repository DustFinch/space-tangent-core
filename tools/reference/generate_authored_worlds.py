"""Independent CSPICE states for the published authored systems and five catalogue moons.

These are new references, not recovered historical output. Only the authored
orbital parameters are shared; propagation and rotation matrices use CSPICE.
The managed simulation assemblies are never loaded by this generator.
"""
import argparse
import hashlib
import json
import math
from pathlib import Path

import numpy as np
import spiceypy as spice

DAY = 86400.0
AU = 149597870.7
LIGHT_YEAR = 9460730472580.8
RAD = math.pi / 180
OBLIQUITY = spice.axisar(np.array([1., 0., 0.]), 23.4392911 * RAD)
Z = np.array([0., 0., 1.])
X = np.array([1., 0., 0.])

# Period (days), semimajor axis (km), e, inclination, node, periapsis, mean anomaly.
BINARY = (27.71064176455162, .22 * AU, .12, 35, 70, 40, 0)
SYSTEMS = [
    ("Tessera-A", "center", BINARY, -.85 / 1.85, 24, 35, 70),
    ("Tessera-B", "center", BINARY, 1 - .85 / 1.85, 18, 35, 70),
    ("Nova", "center", (370.8043247867439, 1.24 * AU, .02, 35.4, 70, 110, 200), 1, 26.4 / 24, 55, 85),
    ("Selena", "Nova", (18.46537308901845, 280000, .03, 36.9, 70, 30, 0), 1, 18.46537308901845, 36.9, 70),
    ("Lumen", None, None, 0, 27, 12, 200),
    ("Pyra", "Lumen", (68.93206334153751, .32 * AU, .03, 13.1, 200, 20, 40), 1, 41 / 24, 20, 230),
    ("Aridus", "Lumen", (352.59952171790155, .95 * AU, .05, 11.3, 200, 140, 250), 1, 30.5 / 24, 36, 180),
    ("Glacies", "Lumen", (1242.5970898555138, 2.2 * AU, .08, 14.3, 200, 300, 120), 1, 19 / 24, 23, 260),
    ("Zephyr", "Lumen", (4778.4489503599025, 5.4 * AU, .04, 12.5, 200, 75, 300), 1, 11.2 / 24, 30, 210),
]
MOONS = [
    ("phobos", 2460000., .31891023, 9378.938, .01468, 2.097806, 38.701507, 66.811489, 173.124729),
    ("deimos", 2460000., 1.2624407, 23459.739, .000283, 2.217941, 286.210554, 125.429446, 331.164065),
    ("phoebe", 2453167.5, 548.167596, 12917060.944, .152921, 173.212514, 263.558611, 2.526169, 31.167678),
    ("proteus", 2447763.5, 1.12231477, 117647, .00053, 28.908433, 48.083589, 81.195458, 34.162384),
    ("nereid", 2447763.5, 359.396374, 5506217.008, .753882, 5.055101, 319.892917, 296.451886, 36.05586),
]


def conic(period, axis, eccentricity, inclination, node, periapsis, anomaly, seconds):
    motion = math.tau / (period * DAY)
    mu = motion * motion * axis ** 3
    return spice.conics(np.array([axis * (1 - eccentricity), eccentricity, inclination * RAD,
        node * RAD, periapsis * RAD, anomaly * RAD, 0, mu]), seconds)


def fixed(x, y, z):
    return np.concatenate((OBLIQUITY @ (np.array([x, -z, y]) * LIGHT_YEAR), np.zeros(3)))


def demo_rows():
    rows = []
    for jd in (2451545., 2451545.125, 2455755.5, 2460410.263, 2461340.7):
        seconds = (jd - 2451545) * DAY
        states = {"center": fixed(82.39295868539722, 52.43188279979823, -113.85208836527616)}
        for name, parent, elements, scale, spin, tilt, node in SYSTEMS:
            if elements is None:
                state = fixed(-38, 21, 49)
            else:
                raw = conic(*elements, seconds)
                state = np.concatenate((OBLIQUITY @ raw[:3], OBLIQUITY @ raw[3:])) * scale + states[parent]
            states[name] = state
            longitude = math.pi + math.remainder(seconds, spin * DAY) * math.tau / (spin * DAY)
            axes = OBLIQUITY @ spice.axisar(Z, node * RAD) @ spice.axisar(X, tilt * RAD) @ spice.axisar(Z, longitude)
            # Subtract a shared origin before recording the relative state so that
            # output retains the actual binary64 accuracy at interstellar scales.
            reference = "Tessera-A" if name in ("Tessera-A", "Tessera-B", "Nova", "Selena") else "Lumen"
            relative = state - states[reference]
            prefix = "Tessera" if reference == "Tessera-A" else "Lumen"
            rows.append(dict(name=prefix + "/" + name, jd=jd, position=state[:3].tolist(),
                relative=relative[:3].tolist(), speed=relative[3:].tolist(), axes=axes.T.tolist()))
    return rows


def moon_rows():
    rows = []
    for name, epoch, *elements in MOONS:
        for offset in (-1000, -100, -1, -.125, 0, .125, 1, 100, 1000):
            jd = epoch + offset
            state = conic(*elements, offset * DAY)
            # The retained acceptance contract describes native Y-up columns.
            native = (state[0], state[2], -state[1], state[3], state[5], -state[4])
            for quantity, start in (("position-km", 0), ("velocity-km-s", 3)):
                for coordinate in range(3):
                    rows.append(dict(profile="source-binary64-direct-a", body=name,
                        instantTdb=dict(WholeDays=math.floor(jd), NanosecondsOfDay=round((jd % 1) * DAY * 1e9)),
                        quantity=quantity, coordinateOneBased=coordinate + 1, expectedNearestBinary64=float(native[start + coordinate])))
    return rows


def generate(output):
    output.mkdir(parents=True, exist_ok=False)
    products = {"demo-worlds-reference.json": demo_rows(), "catalogue-moons-reference.json": {"cases": moon_rows()}}
    for name, payload in products.items():
        records = payload if isinstance(payload, list) else payload["cases"]
        content = ",\n".join(json.dumps(row, separators=(",", ":"), allow_nan=False) for row in records)
        text = "[\n" + content + "\n]\n" if isinstance(payload, list) else '{"cases":[\n' + content + "\n]}\n"
        (output / name).write_text(text, encoding="utf-8", newline="\n")
    metadata = dict(method="New independent NAIF conics and axis-angle witnesses; not recovered historical output. "
        "Parameters transcribed from the published authored definitions; no managed product code invoked.",
        toolkit=spice.tkvrsn("TOOLKIT"), spiceypy=spice.__version__, numpy=np.__version__,
        generatorNormalizedSha256=hashlib.sha256(Path(__file__).read_text(encoding="utf-8").encode("utf-8")).hexdigest(),
        files={name: hashlib.sha256((output / name).read_bytes()).hexdigest() for name in products})
    (output / "authored-worlds-reference.json").write_text(json.dumps(metadata, indent=2) + "\n", encoding="utf-8", newline="\n")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("output", type=Path)
    generate(parser.parse_args().output)
