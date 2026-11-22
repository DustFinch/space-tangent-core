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
        self.root = Path(self.scratch.name).resolve()
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
    def test_ci_summary_appends_real_counts_and_retains_a_failed_threshold_report(self):
        with tempfile.TemporaryDirectory() as scratch:
            root = Path(scratch)
            source, output, summary = root/'coverage.xml', root/'summary.json', root/'job.md'
            source.write_text('''<coverage><packages><package name="SpaceTangent.Core"><classes>
                <class filename="src/SpaceTangent.Core/A.cs"><lines>
                <line number="1" hits="1"/><line number="2" hits="0"/>
                </lines></class></classes></package></packages></coverage>''')
            summary.write_text('Existing job notes.\n')
            arguments = [str(source), '--require', 'SpaceTangent.Core', '--output', str(output),
                         '--markdown-output', str(summary)]
            self.assertEqual(1, coverage_report.main(arguments+['--minimum','SpaceTangent.Core=95']))
            self.assertEqual(50., json.loads(output.read_text())['modules']['SpaceTangent.Core']['percent'])
            text = summary.read_text()
            self.assertTrue(text.startswith('Existing job notes.\n'))
            self.assertIn('| SpaceTangent.Core | 1 / 2 | 50.000% |', text)
            self.assertIn('Skipped optional-data tests', text)
            self.assertNotIn(str(root), text)
            self.assertEqual(0, coverage_report.main(arguments+['--minimum','SpaceTangent.Core=50']))

    def test_invalid_report_cannot_publish_a_successful_ci_summary(self):
        with tempfile.TemporaryDirectory() as scratch:
            root = Path(scratch)
            source, summary = root/'empty.xml', root/'job.md'
            source.write_text('<coverage><packages /></coverage>')
            self.assertEqual(1, coverage_report.main([str(source), '--require','SpaceTangent.Core',
                '--output',str(root/'summary.json'),'--markdown-output',str(summary)]))
            self.assertFalse(summary.exists())

    def test_linked_unity_binding_keeps_its_own_denominator(self):
        with tempfile.TemporaryDirectory() as scratch:
            path = Path(scratch) / "binding.xml"
            path.write_text('''<coverage><packages><package name="SpaceTangent.UnityBinding"><classes>
                <class filename="bindings/unity/SpaceTangentEphemeris.cs"><lines>
                <line number="35" hits="1"/></lines></class></classes></package></packages></coverage>''', encoding="utf-8")
            result = coverage_report.summarize(coverage_report.collect([path]), ["SpaceTangent.UnityBinding"])
            self.assertEqual("SpaceTangent.UnityBinding/SpaceTangentEphemeris.cs", result["files"][0]["file"])
            self.assertEqual(1, result["modules"]["SpaceTangent.UnityBinding"]["executable"])

    def test_minimum_applies_to_each_named_module_instead_of_the_total(self):
        result = {"modules": {"SpaceTangent.Contracts": {"percent": 100.}, "SpaceTangent.Core": {"percent": 90.}}}
        coverage_report.enforce_minimum(result, ["SpaceTangent.Contracts=95", "SpaceTangent.Core=90"])
        with self.assertRaisesRegex(ValueError, "SpaceTangent.Core"):
            coverage_report.enforce_minimum(result, ["SpaceTangent.Core=95"])
        with self.assertRaises(ValueError):
            coverage_report.enforce_minimum(result, ["SpaceTangent.Native=1"])

    def test_invalid_or_duplicate_minimum_cannot_disable_the_gate(self):
        for value in ["x", "x=nan", "x=inf", "x=-1", "x=101", "x=not-a-number"]:
            with self.subTest(value=value), self.assertRaises(ValueError):
                coverage_report.enforce_minimum({"modules": {}}, [value])
        with self.assertRaises(ValueError):
            coverage_report.enforce_minimum({"modules": {"x": {"percent": 100}}}, ["x=95", "x=0"])

    def test_native_abi_filename_maps_to_its_real_source_project(self):
        with tempfile.TemporaryDirectory() as scratch:
            path = Path(scratch) / "native.xml"
            path.write_text('''<coverage><packages><package name="SpaceTangentNative"><classes>
                <class filename="src/SpaceTangent.Native/EphemerisExports.cs"><lines>
                <line number="35" hits="1"/><line number="90" hits="0"/>
                </lines></class></classes></package></packages></coverage>''', encoding="utf-8")
            result = coverage_report.summarize(coverage_report.collect([path]), ["SpaceTangent.Native"])
            self.assertEqual(50., result["modules"]["SpaceTangent.Native"]["percent"])
            self.assertEqual([90], result["files"][0]["uncovered_lines"])

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
