"""Regenerate native witnesses independently of the managed simulation."""
import json
from pathlib import Path
import tempfile
import unittest

import numpy as np
from generate_authored_worlds import conic, demo_rows, generate, moon_rows


class AuthoredWorldReferenceTests(unittest.TestCase):
    def test_native_propagator_has_the_known_circular_periapsis_state(self):
        actual = conic(1, 1000, 0, 0, 0, 0, 0, 0)
        np.testing.assert_allclose(actual, [1000, 0, 0, 0, 2 * np.pi * 1000 / 86400, 0], atol=1e-12)
        rows = demo_rows()
        self.assertEqual(45, len(rows))
        for row in rows:
            axes = np.array(row["axes"])
            np.testing.assert_allclose(axes @ axes.T, np.eye(3), atol=1e-14)
            self.assertAlmostEqual(1, np.linalg.det(axes), places=13)
        self.assertEqual(270, len(moon_rows()))

    def test_committed_witnesses_regenerate_without_loading_the_product(self):
        fixtures = Path(__file__).resolve().parents[2] / "tests/SpaceTangent.Core.Tests/Fixtures"
        with tempfile.TemporaryDirectory() as scratch:
            output = Path(scratch) / "new"
            generate(output)
            for name in ("demo-worlds-reference.json", "catalogue-moons-reference.json"):
                expected = json.loads((fixtures / name).read_text())
                actual = json.loads((output / name).read_text())
                # Last-bit native math can differ between supported operating systems.
                def compare(a, b):
                    if isinstance(a, dict):
                        self.assertEqual(a.keys(), b.keys())
                        for key in a: compare(a[key], b[key])
                    elif isinstance(a, list):
                        self.assertEqual(len(a), len(b))
                        for left, right in zip(a, b): compare(left, right)
                    elif isinstance(a, float):
                        self.assertLessEqual(abs(a - b), max(1e-11, 2e-14 * abs(a)))
                    else:
                        self.assertEqual(a, b)
                compare(expected, actual)
            with self.assertRaises(FileExistsError):
                generate(output)


if __name__ == "__main__":
    unittest.main()
