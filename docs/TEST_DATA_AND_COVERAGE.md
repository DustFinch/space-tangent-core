# Test data and source-line coverage

The ordinary unit suite works without optional archives and reports its missing-data cases as skips. A full local data profile records the selected files so that a changed, incomplete or missing corpus fails before tests start. Keep profiles and assets outside the repositories: profiles contain local paths and capturing a file does not grant redistribution rights or authenticate its provenance.

The [supported-contract matrix](COMPATIBILITY.md) separates format support from accepted scientific evidence. Reproducible HYG astrometry and original upstream photograph/survey capture recipes, plus the retained historical-case inventory, are maintained in the simulator's [test-data guide](https://github.com/DustFinch/space-tangent/blob/main/docs/TEST_COVERAGE.md). Supplementary STC recovery needs a separate source-only baseline without `stars.dat` and a separately pinned original binary table. Do not remove files from an original installation to create that baseline.

Keep `SPACETANGENT_SKY_CULTURES` on the original 128-image installation. The separate `SPACETANGENT_RECOVERED_SKY_CULTURES` test checks the same 33 definitions with all 299 artwork files available in the pinned upstream subset, including all three anchor directions per image. Six referenced images remain absent upstream. This new optional-input test supplements the original contract; it is not another restored historical skip.

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

Supply both `--start-jd` and `--end-jd` to restrict coverage to a finite increasing TDB Julian-date interval. Only overlapping target segments are copied, with endpoints clipped by CSPICE; trajectory data are not fitted again. Both requested dates and the actual selected segment coverage are recorded. Missing bodies, disjoint windows and invalid dates fail instead of producing an empty successful corpus. The reference-tool tests use a separately authored linear native SPK to verify target selection, coverage, state preservation and refusal paths.

Geometry and sensor references can use `SPACETANGENT_MISSION_ORACLES` independently of the Saturn series input `SPACETANGENT_CHEBYSHEV_ROOT`. The latter remains a compatibility fallback. Separate dependency guards report unavailable fixed-frame and mesh-scale archives without suppressing unrelated retained cases.

## Collect and combine coverage

Restore the pinned Coverlet CLI using `dotnet tool restore`. Run instrumentation against a copy of built binaries, with `--single-hit` for line coverage and no rebuild between instrumentation and execution. Include all intended production modules and exclude test sources and generated `obj` sources. Keep the collector's raw XML and the corresponding test log/TRX.

```powershell
python scripts/testing/coverage_report.py core.cobertura.xml application.cobertura.xml godot.cobertura.xml --require SpaceTangent.Core --require SpaceTangent.Contracts --require SpaceTangent.Application --require SpaceTangent.Godot --output C:/TestResults/coverage.json
```

The report merges a source line with logical OR across runs and classes. It excludes generated sources, third-party assemblies and test code, while retaining unexecuted lines in production sources. Required modules must have a nonempty denominator and recorded hits; an empty XML cannot produce a successful 100% report. Only listed modules are measured. Report Native, CLI and engine bindings separately until they are included by explicit runs. Shader execution is outside managed C# coverage and needs GPU checks.

Use the same build configuration when comparing executable-line denominators. Debug and Release can emit different sequence points even for identical source files; a higher ratio with fewer emitted lines is not additional testing. Keep their counts and accepted reports separate.

`coverage_report.py --markdown-output PATH` appends the measured per-module table to a job summary without removing earlier notes. JSON and Markdown retain the actual result when a coverage minimum fails, while the command still returns a failure status. The public-data and Native jobs publish both raw artifacts and readable GitHub summaries. Native and Unity binding each require 95% in their own Release job; the full scientific/GPU target remains a separate local-data matrix.

The pack CLI suite covers strict/non-strict diagnostics, bounded manifests, current portable round trips and optional real planetary, atmosphere, science-satellite and stellar-correction installations. Build the tool in Debug, set `ST_PACK_TOOL_DLL` to its DLL and run `python -m unittest discover -s scripts/testing -p 'test_pack_cli.py'` through a verified data profile. Data-free CI skips the four real-input contracts. `ST_CLI_COVERLET` and a fresh `ST_CLI_COVERAGE_DIRECTORY` collect each invocation separately while `cli_test_support.py` preserves the tool's actual exit status and stderr. An expected CLI rejection is accepted only when the outer test checks that status and diagnostic.

The CLI's planetary-model adapter deliberately does not provide the host's Earth rotation model for `iau-earth`; a catalogue requiring that callback receives an explicit unsupported-feature diagnostic. Mixed science packs can contain frame-helper bodies that are not modeled Earth satellites: ordinary satellite checks retain the valid spacecraft and report the helpers, while strict checks fail on those notices. These diagnostic contracts must remain visible rather than being suppressed to obtain a green check.

The Godot runner and its explicit tracker flush are documented in the simulator's `docs/TEST_COVERAGE.md`. Headless and rendered input-smoke reports can be combined; hitting renderer C# lines does not establish image correctness.

Check the verification tools themselves with `python -m unittest discover -s scripts/testing -p 'test_*.py'`.

`tools/reference/generate_equinoctial_spk.py NEW_DIRECTORY` uses the pinned CSPICE writer and geometric state reader to produce the small committed type-17 fixture: three orbits, 24 epochs, nonzero apsidal/nodal rates and both coverage endpoints. Its JSON records toolkit version, kernel SHA and states. The C# suite compares the managed reader with those independent native values; reference CI regenerates the same witnesses. Synthetic polynomial fixtures also check analytic CHEBPOLY values and derivatives, endpoint holding and invalid inputs. These new cases do not stand in for a missing historical multi-type SPK archive.
