# World evaluation

The world graph builds one order for state and orientation dependencies. A local trajectory can depend on a centre and on the orientation of a frame owner. Its transformed velocity includes the frame's angular motion. A two-vector orientation reads positions or velocities from the same snapshot. Unknown dependencies and cycles fail during construction.

Each evaluation uses a common TDB epoch and one DE440 sample. Missing source data propagates to dependent nodes. A rotation without a published angular velocity can orient a model, while a trajectory using that rotating frame needs a rate to produce a complete state.

The real-data regression composes Cassini's SPK trajectory relative to Saturn's system barycenter with the DE440 barycentric state. It verifies the composed state and CK orientation against CSPICE and queries the same graph concurrently.
