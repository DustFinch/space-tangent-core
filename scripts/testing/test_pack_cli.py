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

    def test_strict_sky_check_preserves_a_missing_base_star_diagnostic(self):
        (self.source / "changes.stc").write_text('Modify 987654 "Missing primary" { Radius 1 }', encoding="utf-8")
        output = self.root / "sky with missing base"
        self.cli("import-sky", self.source, "--pack", output)
        self.cli("check", output / "sky-pack.json", contains="1 STC diagnostics", stderr="Warning:")
        self.cli("check", output / "sky-pack.json", "--strict", status=1,
                 contains="1 STC diagnostics", stderr="Warning:")

    def test_unsupported_mission_geometry_remains_a_visible_warning(self):
        manifest = self.mission()
        data = json.loads(manifest.read_text())
        data["items"].append(dict(data["items"][0], name="Unsupported companion",
                                  geometry={"type": "UnsupportedShapeForContract"}))
        manifest.write_text(json.dumps(data))
        self.cli("check-missions", self.source, manifest.name, contains="1 missions", stderr="Warning:")
        self.cli("check-missions", self.source, manifest.name, "--strict", status=1,
                 contains="1 missions", stderr="Warning:")

    def test_oversized_pack_is_rejected_before_json_parsing(self):
        manifest = self.root / "pack.json"
        with manifest.open("wb") as output:
            output.truncate(16 * 1024 * 1024 + 1)
        self.cli("check", manifest, status=1, stderr="exceeds 16 MiB")

    @unittest.skipUnless(all(os.environ.get(key) for key in
        ("SPACETANGENT_DE440", "SPACETANGENT_VSOP87", "SPACETANGENT_SATURN_PCK")),
        "Supply pinned planetary models for the CLI integration contract.")
    def test_real_planetary_models_import_and_check_custom_orbits_and_frames(self):
        models = [os.environ[key] for key in
                  ("SPACETANGENT_DE440", "SPACETANGENT_VSOP87", "SPACETANGENT_SATURN_PCK")]
        (self.source / "body.ssc").write_text(
            '"First frame" "Sol" { Radius 1 CustomOrbit "vsop87-mars" CustomRotation "iau-mars" }\n'
            '"Second frame" "Sol" { Radius 1 CustomOrbit "vsop87-mars" CustomRotation "iau-mars" }\n', encoding="utf-8")
        output = self.root / "planetary pack"
        self.cli("import", self.source, "--pack", output, "--planetary-models", *models, contains="2 SSC bodies")
        self.cli("check-ssc", *models, "--strict", output, contains="2 SSC/MPC bodies, 0 warnings")
        self.cli("check-ssc", *models, self.source, contains="Sol/First frame")
        empty = self.root / "empty source"
        empty.mkdir()
        self.cli("check-ssc", *models, empty, status=1, stderr="contains no readable bodies")
        (self.source / "earth.ssc").write_text(
            '"Earth frame" "Sol" { Radius 1 CustomOrbit "vsop87-earth" CustomRotation "iau-earth" }', encoding="utf-8")
        # The CLI deliberately does not replace the host's Earth model with a
        # generic text-PCK rotation. Make that unsupported contract explicit.
        self.cli("check-ssc", *models, self.source, status=1, stderr="Catalog/UnsupportedFeature")

    @unittest.skipUnless(os.environ.get("SPACETANGENT_PACKED_SCIENCE"), "Supply the pinned original science pack.")
    def test_original_science_pack_lists_satellites_and_reports_other_bodies(self):
        manifest = Path(os.environ["SPACETANGENT_PACKED_SCIENCE"]) / "pack.json"
        result = self.cli("check", manifest, "--satellites", contains="Jason-1", stderr="not a modeled Earth satellite")
        self.assertIn("SORCE", result.stdout)
        self.assertIn("2 modeled Earth satellites, 3 warnings", result.stdout)
        self.cli("check", manifest, "--strict", "--satellites", status=1,
                 contains="2 modeled Earth satellites, 3 warnings", stderr="ScienceFrameFollower")

    @unittest.skipUnless(os.environ.get("SPACETANGENT_ARCHIVED_ATMOSPHERE_PACKS"), "Supply the pinned atmosphere packs.")
    def test_original_atmosphere_packs_check_real_tables(self):
        roots = os.environ["SPACETANGENT_ARCHIVED_ATMOSPHERE_PACKS"].split(os.pathsep)
        for root in roots:
            with self.subTest(root=root):
                result = self.cli("check", Path(root) / "pack.json", "--atmospheres", contains="atmosphere tables verified")
                self.assertIn("transmission", result.stdout)
                self.assertIn("scattering", result.stdout)

    @unittest.skipUnless(all(os.environ.get(key) for key in
        ("SPACETANGENT_STELLAR_ARCHIVE", "SPACETANGENT_STELLAR_CORRECTIONS")),
        "Supply the pinned original stellar archive and correction pack.")
    def test_original_correction_pack_check_uses_its_required_base_catalogue(self):
        root = Path(os.environ["SPACETANGENT_STELLAR_CORRECTIONS"])
        self.cli("check", root / "pack.json", "--strict", "--stellar-archive",
                 os.environ["SPACETANGENT_STELLAR_ARCHIVE"], contains="stellar distance corrections, 0 warnings")


if __name__ == "__main__":
    unittest.main()
