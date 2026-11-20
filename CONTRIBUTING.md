# Contributing

Use the .NET SDK selected by `global.json` and install the .NET 8 runtime for tests. The core and contracts build without Godot, Unity or Unreal.

```sh
dotnet restore SpaceTangent.Core.sln --locked-mode
dotnet build SpaceTangent.Core.sln -c Release --no-restore
dotnet test tests/SpaceTangent.Core.Tests -c Release --no-build
```

Windows and Linux CI run the portable checks. The separate data workflow downloads pinned public inputs and regenerates selected independent references. Optional archive tests state the environment variables they need and are skipped when those inputs are unset. Report skips separately from successful tests. New numerical regressions should identify units, time scales, frames, tolerances and the origin of expected values; do not compute an expected reference by calling the production implementation.

Preserve existing source notices when adapting code or coefficients. The [reference inventory](docs/REFERENCE_PROVENANCE.md) identifies committed verification sources and generators. Keep optional datasets and private artifacts outside source commits.

Use focused, working commits with a clear subject, a short explanation and relevant validation. Include applicable contributor and co-author trailers. Keep engine-specific UI, rendering and resource types out of public numerical contracts.

Cloning does not copy local Git hook configuration. The repository audit hooks are an optional local setup for contributors and require a nonempty policy file outside the checkout. Maintainers should supply that setup. To enable it, use `git config core.hooksPath .githooks` and set `SPACE_TANGENT_STOPWORDS` to the absolute external policy path. Do not publish the private policy, silently bypass an enabled audit, or make public build/test commands depend on that file.
