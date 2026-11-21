"""Acceptance of the built pack CLI; optional per-invocation IL coverage."""
import json
import os
from pathlib import Path
import tempfile
import unittest
from cli_test_support import run_cli


@unittest.skipUnless(os.environ.get("ST_PACK_TOOL_DLL"), "Set ST_PACK_TOOL_DLL to the built pack tool.")
class PackCliTests(unittest.TestCase):
    def setUp(self):
        self.scratch = tempfile.TemporaryDirectory(prefix="st-pack-cli-")
        self.addCleanup(self.scratch.cleanup)
        self.root = Path(self.scratch.name).resolve()
        self.source = self.root / "sources with spaces"
        self.source.mkdir()
        self.tool = Path(os.environ["ST_PACK_TOOL_DLL"]).resolve(strict=True)
        self.sequence = 0

    def cli(self, *arguments, status=0, contains="", stderr=""):
        self.sequence += 1
        result = run_cli(self.tool, arguments, self._testMethodName, self.sequence, "SpaceTangent.PackTool")
        self.assertEqual(status, result.returncode, result.stdout + result.stderr)
        self.assertIn(contains, result.stdout)
        self.assertIn(stderr, result.stderr)
        return result

    def mission(self, center="Earth"):
        path = self.source / "mission.json"
        path.write_text(json.dumps({"version": "1.0", "items": [{"name": "Beacon", "center": center,
            "trajectory": {"type": "FixedPoint", "position": [0, 5, 0]}}]}), encoding="utf-8")
        return path

    def test_usage_and_missing_inputs_have_distinct_exit_codes(self):
        self.cli(status=2, stderr="Usage:")
        self.cli("unknown", status=2, stderr="Usage:")
        self.cli("check", self.root / "missing.json", status=1, stderr="Pack manifest not found")
        wrong = self.root / "wrong.json"
        wrong.write_text("{}", encoding="utf-8")
        self.cli("check", wrong, status=1, stderr="Supply a pack.json or sky-pack.json")

    def test_ssc_pack_relocates_and_strict_check_reports_the_body(self):
        (self.source / "body.ssc").write_text('"Pebble" "Sol" { Radius 1 EllipticalOrbit { Period 1 SemiMajorAxis 1 } }', encoding="utf-8")
        output = self.root / "packed"
        self.cli("import", self.source, "--pack", output, contains="1 SSC bodies")
        moved = self.root / "relocated pack"
        output.rename(moved)
        self.cli("check", moved / "pack.json", "--strict", contains="Sol/Pebble")
        self.cli("check", moved / "pack.json", "--body", "X", status=1, stderr="SSC packs do not take body references")
        original = (moved / "pack.json").read_bytes()
        self.cli("import", self.source, "--pack", moved, status=1)
        self.assertEqual(original, (moved / "pack.json").read_bytes())

    def test_sky_pack_retains_a_named_star_after_relocation(self):
        (self.source / "star.stc").write_text('Star 123456 "Acceptance Star" { RA 90 Dec 20 Distance 10 SpectralType "G2V" AppMag 4 }', encoding="utf-8")
        output = self.root / "sky"
        self.cli("import-sky", self.source, "--pack", output, contains="1 STC")
        moved = self.root / "moved sky"
        output.rename(moved)
        self.cli("check", moved / "sky-pack.json", "--strict", contains="1 effective STC records")
        self.cli("check", moved / "sky-pack.json", "--body", "Earth", status=1,
                 stderr="Sky catalogue packs do not take body references")

    def test_mission_source_and_relocated_pack_keep_a_fixed_beacon(self):
        manifest = self.mission()
        self.cli("check-missions", self.source, manifest.name, "--strict", contains="Beacon")
        output = self.root / "mission pack"
        self.cli("import-missions", self.source, "--pack", output, contains="1 missions")
        moved = self.root / "moved missions"
        output.rename(moved)
        self.cli("check", moved / "pack.json", "--strict", contains="1 missions")

    def test_custom_mission_body_reference_is_explicit_and_validated(self):
        manifest = self.mission("InstalledPartner")
        self.cli("check-missions", self.source, manifest.name, "--strict", "--body", "InstalledPartner", contains="1 missions")
        self.cli("check-missions", self.source, manifest.name, "--body", status=1,
                 stderr="Supply installed mission references")
        self.cli("check-missions", self.source, "--strict", status=1,
                 stderr="Supply at least one mission manifest")
        self.cli("import-missions", self.source, "--pack", self.root / "bad", "--unknown", "X", status=1)
        self.assertFalse((self.root / "bad").exists())

    def test_optional_pack_modes_reject_missing_required_payloads(self):
        pack = self.root / "pack.json"
        pack.write_text('{"corrections":[]}', encoding="utf-8")
        self.cli("check", pack, status=1, stderr="Supply the base stellar catalogue directory")
        pack.write_text('{"formatVersion":14,"bodies":[]}', encoding="utf-8")
        self.cli("check", pack, status=1)
        pack.write_text('{}', encoding="utf-8")
        self.cli("check", pack, "--atmospheres", status=1)
        self.cli("check-ssc", "missing", "missing", "missing", status=1,
                 stderr="Supply at least one SSC pack directory")
        self.cli("import", self.source, "--pack", self.root / "models", "--planetary-models",
                 "missing", "missing", "missing", status=1)


if __name__ == "__main__":
    unittest.main()
