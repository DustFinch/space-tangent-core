# Supported contracts and evidence limits

This is an engine-independent .NET library with selected file adapters and a small versioned native ABI. Supported data types and successful numerical checks do not establish complete compatibility with every application that uses those formats. The simulator maintains the [interactive compatibility matrix](https://github.com/DustFinch/space-tangent/blob/main/docs/COMPATIBILITY.md) and the separately pending comparative UI acceptance.

## Managed library

| Area | Supported contract | Evidence and limits |
| --- | --- | --- |
| Time | Explicit time scales, leap-second-aware conversion, historical date labels and selected ΔT models. | [Time conventions](TIME.md), `JulianInstant`/calendar and time-model tests. A representable epoch does not extend an ephemeris or establish the accuracy of a historical ΔT estimate. |
| Solar ephemerides | The documented JPL DE440 reader, optional historical series and installed planetary center corrections. | [DE440](DE440.md), [accuracy migration](ACCURACY_MIGRATION.md), independent CSPICE state references. Coverage is determined by the installed files, not a universal UI date range. |
| Satellite orbits | SGP4/TLE propagation and deterministic state/velocity transforms. | [SGP4](SGP4.md), published verification cases and independent state witnesses. Computational bounds and useful prediction intervals are different; old TLEs do not become accurate when propagation succeeds. |
| Trajectories and frames | Conics, sampled trajectories, supported body frames, world-graph parent chains and transport velocities. | [World graph](WORLD_GRAPH.md), [conics](CONIC_ORBITS.md), [sampled data](SAMPLED_TRAJECTORIES.md). Unsupported frames and missing coverage must remain explicit. |
| Earth and body orientation | The documented Earth models and supported text PCK/mission orientations. | [Earth orientation](EARTH_ORIENTATION.md), independent ERFA/CSPICE checks, [accuracy changes](ACCURACY_MIGRATION.md). These checks do not validate arbitrary kernels or all historical dates. |
| Local SSC catalogues | Ordered supported definitions/overrides, source-relative resources and selected orbital/attitude models. | Adapter and packing tests, original selected spacecraft/body catalogues. An accepted selected catalogue does not imply every field/model in the format. |
| Mission JSON and packs | Supported local source formats, portable current-writer packs, declared resources and tested historical readers. | [Science catalogues](SCIENCE_CATALOG.md), sampled/packed mission tests. Unavailable exact historical installations remain skipped rather than being relabelled as current round trips. |
| SPICE | SPK types 1, 2, 3, 8, 9, 12, 13, 17 and 21; CK types 2 and 3; documented clocks and frame chains. | [Detailed scope](SSC_SPICE.md), original Cassini checks, `AdditionalSpkTests`, `SpiceCkOracleTests` and `PolynomialKernelContractTests`. This does not include every kernel type, DSK surface, frame or aberration service. |
| Sky catalogues | Supported spatial astrometry, native stellar/STC/DSC inputs and their identity/host relationships. | Catalogue and relocation tests. A regenerated astrometry fixture carries its own provenance; illustrative radius fields are not physical-size references. |
| Observing calculations | The implemented event searches, surface geometry and atmospheric/sky models. | [Event models](OBSERVATION_EVENTS.md) and independent event/ground-position references. Numerical tolerances and sampling models are specific to each calculation; no general observatory-grade accuracy claim is made. |

## Native and engine consumers

The Win64 C ABI currently exposes opening/closing a DE440 model and querying geometric six-component states for NAIF IDs 0, 10, 399 and 301: system barycenter, Sun, Earth and Moon. It does not export the whole managed world graph, catalogue, observing or rendering API. See [the ABI contract](NATIVE_ABI.md) for statuses, layout and ownership.

Managed IL contract coverage measures the native wrapper source and Unity binding separately. NativeAOT smoke tests exercise optimized exports but do not measure their machine-code line coverage. Reusing an unchanged AOT binary must be identified explicitly; it is not a new publish or editor run.

Standalone C, Unity editor and Unreal Python commandlet probes establish the tested state queries and calling contracts. Compiled Unreal C++ integration, packaged players, other native operating-system targets and complete engine feature adapters require their own validation. They are not implied by one editor successfully loading the library.

## Optional data and coverage

Use [pinned input profiles and source-line coverage](TEST_DATA_AND_COVERAGE.md). Missing historical archives are evidence gaps, not automatically runtime defects. Keep the original test, exact source/version and independent reference requirements. New authored fixtures and newly regenerated upstream data must remain distinguishable from a lost original installation.

The simulator's [historical-input inventory](https://github.com/DustFinch/space-tangent/blob/main/docs/OPTIONAL_TEST_INPUTS.md) links the retained Core and application cases and the repeatable DSS/HYG/photograph recipes. Those preparation tools are test-data utilities; the managed library has no runtime dependency on the simulator or its UI.

Report line coverage per module and configuration from fresh accepted XML, with raw logs and source hashes. Do not mix Debug and Release executable-line denominators, reuse stale line numbers after a source change, or treat managed coverage as evidence of shader/image correctness. Separate Native, CLI and engine-binding denominators must remain visible.
