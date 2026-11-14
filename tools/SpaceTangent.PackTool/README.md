# Catalogue pack tool

Build a portable SSC add-on folder, then check it with the same body loader used by the simulator.

```sh
dotnet run --project tools/SpaceTangent.PackTool -- import "/path/to/loose-addon" --pack "/path/to/new-pack"
dotnet run --project tools/SpaceTangent.PackTool -- check "/path/to/new-pack/pack.json" --strict
```

The tool embeds SSC files from the source root and its `data` directory in loader order. It preserves the folder layout of the companion files so meshes, scripts and metakernels can still find their own resources after relocation. Supply a destination outside the source that does not exist yet. The checker lists the bodies installed by the SSC and MPC readers. Strict mode fails when those readers produce warnings. Lua factories are not executed by the command-line tool.

Build a sky catalogue pack from a directory containing STC and DSC files.

```sh
dotnet run --project tools/SpaceTangent.PackTool -- import-sky "/path/to/catalogues" --pack "/path/to/new-sky-pack"
dotnet run --project tools/SpaceTangent.PackTool -- check "/path/to/new-sky-pack/sky-pack.json"
```

The versioned sky manifest lists the catalogue files in import order. The pack keeps the original catalogue bytes and companion paths. Point the simulator's `SPACETANGENT_STC_ROOT` and `SPACETANGENT_DSC_ROOT` at the pack directory to load both types. STC modifications are applied to the simulator's installed base catalogue. The standalone check runs without that base, so it reports diagnostics for modifications that require existing stars.

Check a mission pack built with import-missions or an archived versioned mission manifest:

```sh
dotnet run --project tools/SpaceTangent.PackTool -- check "/path/to/mission-pack/pack.json" --strict --body "Cassini"
```

Use --body for references to objects supplied by a separate installation. The check prints the missions, annotations and viewpoints handled by the mission reader. Packs with embedded SSC use the SSC reader.

Check a version 1 stellar distance correction pack against the archived catalogue directory containing `stars.bin`.

```sh
dotnet run --project tools/SpaceTangent.PackTool -- check "/path/to/corrections/pack.json" --strict --stellar-archive "/path/to/stellar-archive"
```

The check applies the named manifest and reports unresolved stellar targets. Strict mode fails on unresolved targets.



Exit codes are 0 for success, 1 for an input, output or strict check failure, and 2 for invalid arguments.
