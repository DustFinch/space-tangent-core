# Science satellite catalogue

The version 1.0 JSON adapter loads Earth-centred TLE trajectories with the source `EquatorJ2000` frame, a nadir/relative-velocity `TwoVector` body frame and a local OBJ resource. It retains raw fixed WXYZ mesh transforms separately from the rigid body basis. The trajectory frame follows the source catalogue convention and has a compatibility frame identifier.

Give the adapter a local add-on root containing the original files. The regression expects `catalog/sciencesat.json` and `catalog/sorce.obj`, plus `data/models/jason.obj` as resolved by the original catalogue path. Keep their companion resources next to the meshes. Set `SPACETANGENT_SCIENCE_ROOT` to this directory before running the tests. The core validates asset paths and computes states and orientations. The application owns decoding and rendering the meshes.

The catalogue and models are external files. The regression checks the unchanged catalogue SHA-256 and evaluates Jason-1 and SORCE. The independent TEME position and velocity fixture and its sources are documented in [the SGP4 notes](SGP4.md).
