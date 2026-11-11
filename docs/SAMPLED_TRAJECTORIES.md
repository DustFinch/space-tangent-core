# Sampled state trajectories

Position and velocity use the same cubic Hermite segment and analytic derivative. Authored curves use exact, strictly increasing TDB epochs and reject queries beyond coverage by default.

The XYZV adapter accepts Julian date, three kilometre coordinates and three kilometre-per-second velocities. It follows the file format's source double-Julian clock and endpoint state policy. At a duplicate epoch it selects the first record. After the final epoch it returns the last record, including its stored velocity. Empty compatibility files evaluate to a zero state.

Set `SPACETANGENT_2008TC3_XYZV` and `SPACETANGENT_MSL_XYZV` to the original input files for the real-data checks. The asteroid regression compares 19 states against the previously preserved independent native implementation. The Mars Science Laboratory check reads the 286-sample cruise file and checks both stored endpoints. The Mars-fixed landing arc and model are separate from the cruise trajectory.
