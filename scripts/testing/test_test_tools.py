"""Regressions for corpus integrity and coverage-report completeness."""
import json
from pathlib import Path
import tempfile
import os
import unittest
import data_profile
import coverage_report


class DataProfileTests(unittest.TestCase):
    def setUp(self):
        self.scratch = tempfile.TemporaryDirectory()
        self.addCleanup(self.scratch.cleanup)
        self.root = Path(self.scratch.name)
        self.input = self.root / "source"
        self.input.mkdir()
        (self.input / "data.bin").write_bytes(b"original")

    def test_unchanged_directory_and_file_bindings_are_verified(self):
        profile = data_profile.capture([f"SPACETANGENT_SOURCE={self.input}", f"SPACETANGENT_FILE={self.input / 'data.bin'}"])
        values = data_profile.verify(profile, ["SPACETANGENT_SOURCE"])
        self.assertEqual(str(self.input), values["SPACETANGENT_SOURCE"])

    def test_changed_bytes_are_detected_even_with_the_same_size(self):
        profile = data_profile.capture([f"SPACETANGENT_SOURCE={self.input}"])
        (self.input / "data.bin").write_bytes(b"modified")
        with self.assertRaisesRegex(ValueError, "changed"):
            data_profile.verify(profile)

    def test_added_file_invalidates_a_directory_profile(self):
        profile = data_profile.capture([f"SPACETANGENT_SOURCE={self.input}"])
        (self.input / "extra.bin").write_bytes(b"extra")
        with self.assertRaises(ValueError):
            data_profile.verify(profile)

    def test_removed_file_invalidates_a_directory_profile(self):
        profile = data_profile.capture([f"SPACETANGENT_SOURCE={self.input}"])
        (self.input / "data.bin").unlink()
        with self.assertRaises(ValueError):
            data_profile.verify(profile)

    def test_a_required_missing_binding_is_an_error(self):
        profile = data_profile.capture([f"SPACETANGENT_SOURCE={self.input}"])
        with self.assertRaisesRegex(ValueError, "Required bindings missing"):
            data_profile.verify(profile, ["SPACETANGENT_OTHER"])

    def test_unrelated_environment_variables_cannot_be_bound(self):
        with self.assertRaises(ValueError):
            data_profile.capture([f"PATH={self.input}"])

    def test_duplicate_binding_is_rejected(self):
        with self.assertRaises(ValueError):
            data_profile.capture([f"SPACETANGENT_SOURCE={self.input}"] * 2)

    def test_empty_corpus_is_not_an_accepted_fixture(self):
        (self.input / "data.bin").unlink()
        with self.assertRaisesRegex(ValueError, "empty"):
            data_profile.capture([f"SPACETANGENT_SOURCE={self.input}"])

    def test_profile_roundtrip_preserves_hashes_and_absolute_paths(self):
        profile = data_profile.capture([f"SPACETANGENT_SOURCE={self.input}"])
        path = self.root / "profile.json"
        path.write_text(json.dumps(profile), encoding="utf-8")
        self.assertEqual(profile, data_profile.load(path))

    def test_a_profile_without_pinned_hashes_is_rejected(self):
        path = self.root / "profile.json"
        path.write_text(json.dumps({"version": 1, "bindings": {"SPACETANGENT_SOURCE": {
            "path": str(self.input), "kind": "directory", "files": {}}}}), encoding="utf-8")
        with self.assertRaisesRegex(ValueError, "pinned file hashes"):
            data_profile.load(path)

    def test_a_path_list_retains_order_and_verifies_each_input(self):
        second = self.root / "second.bin"
        second.write_bytes(b"another")
        joined = str(self.input) + os.pathsep + str(second)
        profile = data_profile.capture([], ["SPACETANGENT_PACKS=" + joined])
        self.assertEqual(joined, data_profile.verify(profile)["SPACETANGENT_PACKS"])
        second.write_bytes(b"changed")
        with self.assertRaises(ValueError):
            data_profile.verify(profile)

    def test_empty_path_list_component_is_rejected(self):
        with self.assertRaisesRegex(ValueError, "Empty input"):
            data_profile.capture([], ["SPACETANGENT_PACKS=" + str(self.input) + os.pathsep])


class CoverageReportTests(unittest.TestCase):
    def test_required_module_cannot_be_hidden_by_a_high_total(self):
        modules = {"SpaceTangent.Core": {"SpaceTangent.Core/A.cs": {1: True}}}
        with self.assertRaisesRegex(ValueError, "SpaceTangent.Godot"):
            coverage_report.summarize(modules, ["SpaceTangent.Godot"])

    def test_an_empty_report_is_not_one_hundred_percent(self):
        with self.assertRaises(ValueError):
            coverage_report.summarize({})

    def test_required_module_with_no_hits_is_rejected(self):
        with self.assertRaises(ValueError):
            coverage_report.summarize({"SpaceTangent.Core": {"A.cs": {1: False}}}, ["SpaceTangent.Core"])

    def test_distinct_lines_form_the_denominator(self):
        result = coverage_report.summarize({"SpaceTangent.Core": {"A.cs": {1: True, 2: False}}})
        self.assertEqual({"covered": 1, "executable": 2, "percent": 50.}, result["total"])
        self.assertEqual([2], result["files"][0]["uncovered_lines"])

    def test_reports_merge_hits_without_double_counting_classes_or_source_roots(self):
        with tempfile.TemporaryDirectory() as scratch:
            paths = []
            for index, hits in enumerate([0, 1]):
                path = Path(scratch) / f"{index}.xml"
                path.write_text(f'''<coverage><sources><source>checkout</source></sources><packages>
                    <package name="SpaceTangent.Core"><classes>
                    <class filename="src/SpaceTangent.Core/A.cs"><lines><line number="7" hits="{hits}"/></lines></class>
                    <class filename="src/SpaceTangent.Core/A.cs"><lines><line number="7" hits="0"/></lines></class>
                    <class filename="src/SpaceTangent.Core/obj/Generated.g.cs"><lines><line number="1" hits="0"/></lines></class>
                    </classes></package></packages></coverage>''', encoding="utf-8")
                paths.append(path)
            result = coverage_report.summarize(coverage_report.collect(paths), ["SpaceTangent.Core"])
            self.assertEqual({"covered": 1, "executable": 1, "percent": 100.}, result["total"])


if __name__ == "__main__":
    unittest.main()
