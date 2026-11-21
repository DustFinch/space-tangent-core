"""Check target/time selection against an independently authored native SPK."""
import hashlib
import json
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest

try:
    import spiceypy as spice
except ImportError:
    spice = None


@unittest.skipUnless(spice, 'Install the pinned reference requirements.')
class ExtractTargetTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix='spk selection ')
        self.addCleanup(self.temp.cleanup)
        self.addCleanup(spice.kclear)
        self.root = Path(self.temp.name)
        self.source = self.root / 'original.bsp'
        self.output = self.root / 'selected.bsp'
        handle = spice.spkopn(str(self.source), 'INDEPENDENT LINEAR WITNESS', 0)
        try:
            for body in [900001, 900002]:
                states = [[10+t, 2*t, 0, 1, 2, 0] for t in [-10., -5., 0., 5., 10.]]
                spice.spkw08(handle, body, 0, 'J2000', -10., 10., 'LINEAR STATES', 3, 5, states, -10., 5.)
        finally:
            spice.spkcls(handle)
        self.sha = hashlib.sha256(self.source.read_bytes()).hexdigest()
        self.script = Path(__file__).with_name('extract_spk_target.py')

    def run_tool(self, *arguments, checksum=None):
        return subprocess.run([sys.executable, '-I', str(self.script), str(self.source), '900001',
            str(self.output), '--sha256', checksum or self.sha, *arguments],
            capture_output=True, text=True, timeout=30)

    def test_full_selection_preserves_only_the_requested_body(self):
        result = self.run_tool()
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertEqual([900001], list(spice.spkobj(str(self.output))))
        self.assertEqual([-10., 10.], list(spice.spkcov(str(self.output), 900001)))
        spice.furnsh(str(self.output))
        for t in [-10., 0., 10.]:
            state = spice.spkgeo(900001, t, 'J2000', 0)[0]
            for actual, expected in zip(state, [10+t, 2*t, 0, 1, 2, 0]):
                self.assertAlmostEqual(expected, actual, places=12)

    def test_trimmed_selection_retains_exact_native_states_and_metadata(self):
        start, end = 2451544.99995, 2451545.00005
        result = self.run_tool('--start-jd', str(start), '--end-jd', str(end))
        self.assertEqual(0, result.returncode, result.stderr)
        coverage = list(spice.spkcov(str(self.output), 900001))
        self.assertEqual([(start-2451545)*86400, (end-2451545)*86400], coverage)
        metadata = json.loads(self.output.with_suffix('.provenance.json').read_text())
        self.assertEqual([start, end], metadata['requestedTdbJulianDates'])
        self.assertEqual(3, metadata['identicalReferenceStates'])
        self.assertEqual(self.sha, metadata['sourceSha256'])

    def test_checksum_mismatch_never_creates_an_output(self):
        self.assertNotEqual(0, self.run_tool(checksum='0'*64).returncode)
        self.assertFalse(self.output.exists())

    def test_invalid_or_incomplete_windows_never_create_an_output(self):
        for arguments in [('--start-jd','2451545'), ('--start-jd','nan','--end-jd','2451546'),
                          ('--start-jd','2451546','--end-jd','2451545')]:
            with self.subTest(arguments=arguments):
                self.assertNotEqual(0, self.run_tool(*arguments).returncode)
                self.assertFalse(self.output.exists())

    def test_window_outside_original_coverage_does_not_create_an_output(self):
        result = self.run_tool('--start-jd','2451546','--end-jd','2451547')
        self.assertNotEqual(0, result.returncode)
        self.assertFalse(self.output.exists())


if __name__ == '__main__':
    unittest.main()
