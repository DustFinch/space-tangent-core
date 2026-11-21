"""Reproduce the committed type-17 witnesses with the pinned native toolkit."""
import json
from pathlib import Path
import tempfile
import unittest
from generate_equinoctial_spk import generate


class EquinoctialReferenceTests(unittest.TestCase):
    def test_committed_kernel_and_states_are_reproducible_independently(self):
        reference = Path(__file__).resolve().parents[2] / "tests/SpaceTangent.Core.Tests/Fixtures/equinoctial-spk-reference.json"
        expected = json.loads(reference.read_text(encoding="utf-8"))
        with tempfile.TemporaryDirectory() as scratch:
            generate(Path(scratch))
            actual = json.loads((Path(scratch) / reference.name).read_text(encoding="utf-8"))
        self.assertEqual(expected["sha256"], actual["sha256"])
        self.assertEqual(expected["firstWord"], actual["firstWord"])
        self.assertEqual(24, len(actual["rows"]))
        for first, second in zip(expected["rows"], actual["rows"]):
            self.assertEqual((first["target"], first["seconds"]), (second["target"], second["seconds"]))
            for index, (a, b) in enumerate(zip(first["state"], second["state"])):
                self.assertLessEqual(abs(a - b), 1e-9 if index < 3 else 1e-12)

    def test_regeneration_cannot_replace_a_preexisting_kernel(self):
        with tempfile.TemporaryDirectory() as scratch:
            path = Path(scratch) / "equinoctial-spk-reference.bin"
            path.write_bytes(b"preserve this input")
            with self.assertRaises(ValueError):
                generate(Path(scratch))
            self.assertEqual(b"preserve this input", path.read_bytes())


if __name__ == "__main__":
    unittest.main()
