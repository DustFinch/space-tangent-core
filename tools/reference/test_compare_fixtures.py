# SPDX-License-Identifier: GPL-2.0-or-later
import unittest

from compare_fixtures import compare


class FixtureComparisonTests(unittest.TestCase):
    def test_platform_rounding(self):
        earth = "# matrix radians\n" + ",".join(["123", "64"] + ["0.5"] * 28)
        compare(earth, earth.replace("0.5", "0.5000000000000001"), "earth")
        sgp4 = "# km and km/s\nTLE1|TLE2\n0,0,1000,2000,3000,1,2,3\n"
        compare(sgp4, sgp4.replace("1000", "1000.00000005"), "sgp4")

    def test_real_changes_are_rejected(self):
        original = "# km and km/s\nTLE1|TLE2\n0,0,1000,2000,3000,1,2,3\n60,6,1,2,3,4,5,6\n"
        for changed in (
            original.replace("km/s", "m/s"),
            original.replace("TLE1", "TLE3"),
            original.replace("0,0,", "0,1,"),
            original.replace("60,6", "61,6"),
            original.replace("1000", "1000.0001"),
            original.replace("1000", "nan"),
            original.replace("1000", "inf"),
            original.replace("1000", "oops"),
            original.replace("1000,", ""),
            original.replace("1000", ""),
            "\n".join(original.splitlines()[:-1]),
            "\n".join(original.splitlines()[:2] + original.splitlines()[2:][::-1]),
        ):
            with self.subTest(changed=changed), self.assertRaises(ValueError):
                compare(original, changed, "sgp4")

    def test_failed_propagation_keeps_status_and_blank_states(self):
        row = "# states\nTLE1|TLE2\n60,3,,,,,,\n"
        compare(row, row, "sgp4")
        with self.assertRaises(ValueError):
            compare(row, row.replace("60,3", "60,4"), "sgp4")


if __name__ == "__main__":
    unittest.main()
