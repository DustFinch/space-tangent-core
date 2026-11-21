# Test data and source-line coverage

The ordinary unit suite works without optional archives and reports its missing-data cases as skips. A full local data profile records the selected files so that a changed, incomplete or missing corpus fails before tests start. Keep profiles and assets outside the repositories: profiles contain local paths and capturing a file does not grant redistribution rights or authenticate its provenance.

## Pin an installed corpus

Check the source version, published checksum and distribution terms first. Record the files actually used by a test variable. A directory binding pins all its files, including their relative names. Keep mutable outputs outside bound input directories.

```powershell
python scripts/testing/data_profile.py capture --output C:/TestData/profile.json --bind SPACETANGENT_DE440=C:/TestData/de440.440 --bind SPACETANGENT_SSC_WORLD_ROOT=C:/TestData/world
python scripts/testing/data_profile.py verify C:/TestData/profile.json --require SPACETANGENT_DE440
python scripts/testing/data_profile.py run --profile C:/TestData/profile.json --require SPACETANGENT_DE440 -- dotnet test tests/SpaceTangent.Core.Tests -c Release --no-restore
```

`run` removes inherited `SPACETANGENT_` variables before installing the verified bindings. It passes command arguments directly, without a shell. Maintain separate profiles when a variable denotes different inputs in two consumers. A file with the same name, size and timestamp is insufficient: SHA-256 is checked. Do not recapture a failing profile merely to accept unexpected input changes.

For ordered archive installations, use `--bind-list "SPACETANGENT_PACKED_MISSION_VERSIONS=C:/TestData/v7;C:/TestData/v8"` on Windows (colon-separated paths on Linux). Each input is independently pinned and the original order is preserved. Empty path-list entries are rejected.

The retained CK corpus can be prepared with `tools/reference/recover_ck_corpus.py REFERENCE_DIRECTORY ORIGINAL_KERNEL_DIRECTORY OUTPUT_DIRECTORY` using the pinned reference environment. It checks every input named by the retained manifest before copying and rebuilds adapter catalogues. Additional rates and the moving-frame SPK witness are generated through CSPICE, without loading product assemblies. The resulting provenance distinguishes preserved reference rows from the newly generated witness. Missing historical pack versions remain missing; a fresh pack is a different round-trip test.

`tools/reference/extract_spk_target.py SOURCE TARGET OUTPUT --sha256 ORIGINAL_SHA256` copies the original segments for one target with CSPICE `spksub`. It does not fit or resample a trajectory. Use it to prepare a bounded scientific input when a complete upstream kernel exceeds the application's file-size limit. Its provenance records original/output hashes, coverage and exact native state comparisons at segment boundaries and midpoints. Keep the original source URL and distribution terms with the data profile.

Geometry and sensor references can use `SPACETANGENT_MISSION_ORACLES` independently of the Saturn series input `SPACETANGENT_CHEBYSHEV_ROOT`. The latter remains a compatibility fallback. Separate dependency guards report unavailable fixed-frame and mesh-scale archives without suppressing unrelated retained cases.

## Collect and combine coverage

Restore the pinned Coverlet CLI using `dotnet tool restore`. Run instrumentation against a copy of built binaries, with `--single-hit` for line coverage and no rebuild between instrumentation and execution. Include all intended production modules and exclude test sources and generated `obj` sources. Keep the collector's raw XML and the corresponding test log/TRX.

```powershell
python scripts/testing/coverage_report.py core.cobertura.xml application.cobertura.xml godot.cobertura.xml --require SpaceTangent.Core --require SpaceTangent.Contracts --require SpaceTangent.Application --require SpaceTangent.Godot --output C:/TestResults/coverage.json
```

The report merges a source line with logical OR across runs and classes. It excludes generated sources, third-party assemblies and test code, while retaining unexecuted lines in production sources. Required modules must have a nonempty denominator and recorded hits; an empty XML cannot produce a successful 100% report. Only listed modules are measured. Report Native, CLI and engine bindings separately until they are included by explicit runs. Shader execution is outside managed C# coverage and needs GPU checks.

The Godot runner and its explicit tracker flush are documented in the simulator's `docs/TEST_COVERAGE.md`. Headless and rendered input-smoke reports can be combined; hitting renderer C# lines does not establish image correctness.

Check the verification tools themselves with `python -m unittest discover -s scripts/testing -p 'test_*.py'`.
