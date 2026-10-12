# Physical atmosphere phase 1 evidence

## Local Metal proof

Task 12 ran the atmosphere GPU facts on the real Apple M2 Max Metal device on
2026-10-13. The tested HEAD was `ac34cf93394ffef3e74f8852fa4de68de2cca1fe` with the
Task 12 source changes delivered alongside this document. This was a working-tree
measurement, not a measurement of the unchanged parent commit.

The final Release run selected all atmosphere GPU facts, including the Task 6
transmittance endpoint, plus CPU mapping and Vulkan binding/descriptor checks.
It executed 101 tests. 97 passed and four failed only because their new grids are
absent. All 22 selected GPU facts executed with zero skips. The four absent grids
are `scene3d_atmosphere_noon`, `scene3d_atmosphere_sunset`,
`scene3d_atmosphere_moonlit` and `scene3d_atmosphere_fog`.

The passing assertions cover the transmittance, multi-scattering and sky-view CPU
oracles, positive scattering, static-cache invalidation, independent moon and seam,
sky and water reflections, all opaque receiver paths with AA off and Temporal
Native, effect blend modes, absolute-height origin rebasing, shared/fresh parity,
and byte-identical atmosphere-disabled rendering against the existing scene grid.
The ordinary filter leaves the measurement body inactive. Its separate opted-in
run executed and passed once.

Shader emission tables were regenerated once. The subsequent verification without
update variables passed all nine selected byte-equality and corpus tests. Only
the 30 atmosphere fragment entries changed in each backend table. Vertex entries,
Line, Distortion, shadows and historical corpus files stayed unchanged.

## Defects exposed by the device

- The three LUT producers used vertically reversed fullscreen vertex UVs on Metal.
  The upright transmittance oracle measured a maximum channel error of 0.407175.
  Producers now derive their UVs from upper-left fragment coordinates.
- View integration reused a conservative source-shadow penetration margin and
  could miss shallow ground hits. Its horizon sky texel exceeded the 0.02 oracle
  tolerance. View termination now compares against the factored tangent projection
  with a local float-roundoff margin. Source-shadow handling stays separate.
- Bilinear sky sampling crossed the sky/ground boundary at the exact horizon. A
  textured alpha fog row exceeded the 0.03 post-tonemap tolerance, reaching 0.052720
  blue-channel error. Each half of the 108-row sky LUT now owns a horizon endpoint,
  mirrored in the CPU mapping. The upper endpoint constructs its tangent direction
  directly to avoid acos/cos roundoff. The numerical GPU rows pass at the original
  tolerances.

## Captures inspected

All 53 local atmosphere `.got.png` captures were inspected, including contact
sheets for receiver/control pairs. Noon is blue with a bright small solar disc.
Sunset has a warm horizon and red solar disc. Moonlit night is very dim blue with
a visible moon. Distant terrain loses contrast toward the horizon colour. Water
changes from blue reflection to the fog horizon colour. The untextured additive
controls remain visible without fog and lose energy with fog.

The complete absolute capture paths and local test logs are in the Task 12 report
at `.superpowers/sdd/2026-10-12-physical-atmosphere-phase-1/task-12-report.md`.
These captures are inspection evidence, not committed reference grids.

## One completed-frame measurement

| Field | Value |
| --- | --- |
| Adapter/backend | Apple M2 Max, `metal-native` |
| OS | macOS 27.0.1, build 26A434 |
| Tested HEAD | `ac34cf93394ffef3e74f8852fa4de68de2cca1fe` plus the Task 12 changes |
| Build/target | Release, 1600 by 900, AA off, identical floor and box draws |
| Atmosphere off | 1.127700 ms |
| Atmosphere on | 1.931700 ms |
| On minus off | 0.804000 ms |
| Cold enabled frame | 44.289400 ms |
| Samples | One prime, one off frame, one on frame |

`Golden_OneShotFrameMeasurement` ran exactly once with
`KE_MEASURE_ATMOSPHERE=1`. Timing covers command recording and `RenderInternal`
through `Submit` and `WaitForIdle`. Construction, draw submission,
`PrepareFrame`, initial CPU lighting preparation and readback are excluded.
The cold number is the first enabled completed render after preparation,
including first-use render resources and GPU LUT production.

This is completed-frame wall time on the shared development Mac. The single delta
is over the 0.3 ms target. It is neither isolated GPU cost nor statistical proof.
Byte-equality verification was also active during part of the measurement tool
run. No retry, loop or stress run was performed.

## Hosted provenance pending

No local golden update variable was set and no grid was generated or committed.
The controller owns the hosted Metal, Direct3D 11 and Vulkan bake, inspection of
the twelve new grids and their bake PNGs, recording the exact head and workflow
run ID, and the subsequent non-bake native/FXC/Vulkan validation run. Local Metal
success does not establish those hosted results. The full suite belongs to Task 13.
