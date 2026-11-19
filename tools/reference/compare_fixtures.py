# SPDX-License-Identifier: GPL-2.0-or-later
"""Compare regenerated references, allowing only documented floating-point rounding.

Earth orientation: 5e-15 absolute for radians/dimensionless matrix elements.
SGP4: 1e-7 km position (0.1 mm), 1e-10 km/s velocity. These are tighter than
the independent CSPICE cross-check and allow the observed platform libm differences.
Epochs, inputs, status codes, TLEs, provenance/units, shape and order must agree exactly.
"""

import argparse
import math
from pathlib import Path


def compare(reference, generated, kind):
    expected = reference.splitlines()
    actual = generated.splitlines()
    if len(expected) != len(actual) or not expected:
        raise ValueError("Fixture row count changed or fixture is empty")
    columns = 30 if kind == "earth" else 8
    for row, (left, right) in enumerate(zip(expected, actual), 1):
        if left.startswith("#") or "|" in left:
            if left != right:
                raise ValueError(f"Metadata or case changed at row {row}")
            continue
        a, b = left.split(","), right.split(",")
        if len(a) != columns or len(b) != columns:
            raise ValueError(f"Invalid column count at row {row}")
        for col, (x, y) in enumerate(zip(a, b)):
            if not x or not y:
                if kind != "sgp4" or col < 2 or x != y or a[1] in ("0", "6"):
                    raise ValueError(f"Unexpected empty value at row {row}, column {col}")
                continue
            try:
                xv, yv = float(x), float(y)
            except ValueError as error:
                raise ValueError(f"Invalid number at row {row}, column {col}") from error
            tolerance = 0 if col < 2 else (5e-15 if kind == "earth" else 1e-7 if col < 5 else 1e-10)
            if not math.isfinite(xv) or not math.isfinite(yv) or abs(xv - yv) > tolerance:
                raise ValueError(f"Reference differs at row {row}, column {col}: {x} versus {y}")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("kind", choices=("earth", "sgp4"))
    parser.add_argument("reference", type=Path)
    parser.add_argument("generated", type=Path)
    args = parser.parse_args()
    compare(args.reference.read_text(encoding="utf-8"), args.generated.read_text(encoding="utf-8"), args.kind)
    print(f"{args.kind}: fixture values and metadata agree within the documented rounding tolerance")
