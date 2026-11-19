# Accuracy update

The orbit and Earth-frame models have changed. Rebuild consumers together with this core version.

- Replace `Sgp4Orbit.CreateEvaluation()` and `WorldEvaluation` with independent calls to the orbit or graph.
  `WorldGraph.Evaluate(epoch)` is deterministic; callers no longer need a history per view or thread.
- Use `WorldGraph.Evaluate(epoch, deltaTModel)` when evaluating an observer's world. The model travels through
  Earth orientation, local TLE transport and nested frame evaluations. The overload without a model preserves
  the Earth model specified by the builder. Snapshots do not change when another observer uses a different model.
- `Sgp4Orbit.Propagate` reports `Sgp4Status`. Its computational limit is ±2,000,000 minutes, which includes the
  longest published verification example. Values outside the limit throw `ArgumentOutOfRangeException`.
  `TrySample` retains its ±365-day limit. Neither interval guarantees useful predictions for old TLEs.
- Observing sites use geodetic latitude and an ellipsoid, rather than a spherical geocentric latitude.
  Renderers must use `SurfaceLighting.EarthToWorld` for terrestrial texture coordinates and poles.
  `EarthRotationRadians` remains a compatibility projection, not a replacement for that basis.
- SSC calendar values are TDB labels, like numeric Julian dates. UTC-labelled application inputs still require
  an explicit conversion. ASTORB calendar epochs are midnight TT, correcting a former half-day offset.
- The precession fixture covers years 1–4000. This does not establish the accuracy of all ephemerides or ΔT
  over that interval, or extend the validity interval of an installed planetary kernel.

The Earth-frame implementation includes ERFA-derived code. Distributions must retain
[its upstream copyright and full notice](../LICENSES/ERFA.txt).
