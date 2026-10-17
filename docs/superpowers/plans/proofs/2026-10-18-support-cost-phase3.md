# SC-20 exact stair stratum prototype

Task base: `976c487d7102f1e04c4e958c545fd578b2822745`.
Measured implementation: `043c5bf59cf6e2102e38645f4037c4f4f403b306`.
Spec: [SC-20](../../../design/CONTACT-CONTROLLER-SUPPORT-COST-2026-10-17.md).

## Stop gate

**PASS.** Stairs box Find saves **8.042 us**, exceeding the required 6 us saving.
The comparison uses exactly two local one-shot artifacts, base followed immediately by head.
Each artifact has one warm observation for each requested row. Its singleton median is reported below.
Both processes exited 0 on Apple M2 Max, Arm64, .NET 10.0.13, Release, with tiered compilation disabled.

| Scene | Operation | Base warm median us | Head warm median us | Saving us |
| --- | --- | ---: | ---: | ---: |
| stairs box | Find | 62.792 | 54.750 | 8.042 |
| stairs hull | Find | 63.791 | 54.333 | 9.458 |
| stairs mesh | Find | 191.875 | 338.375 | -146.500 |
| stairs box | DryTick | 139.958 | 122.750 | 17.208 |

All four rows match their geometry oracle in both artifacts. Each artifact has 45 Find rows and all
45 allocate 0 warm managed bytes. Stairs box DryTick allocates the same 64 bytes in both artifacts.

| Scene | Operation | Base Extrema | Head Extrema | Head StairStratum |
| --- | --- | ---: | ---: | ---: |
| stairs box | Find | 18 | 18 | 6 |
| stairs hull | Find | 18 | 18 | 6 |
| stairs mesh | Find | 26 | 26 | 0 |
| stairs box | DryTick | 38 | 38 | 14 |

The unchanged Extrema totals count real Range.Add calls. StairStratum counts certified extrema calls,
including both expanded admission and original footprint radii.

## Certified domain and bit identity

The stratum requires four exact represented vertices with bit-equal Y coordinates, exact zero normal X
and Z, positive orientation, alternating nonzero axis-aligned edges, exact vertex differences, equal
opposite edges and upward winding. Every vertex-to-axis difference must also be exactly representable.
Nonzero differences and the positive exact radius lie in `[2^-100, 2^100]`. These certificate bounds keep
the interior's nonzero products normal and finite. Other inputs use the unchanged generic loop.

- Centre admission uses closed coordinate bracketing. Each generic interior test has one nonzero
  product with the same sign. The candidate copies the original axis XZ and first vertex Y exactly,
  as OnPlane does for this normal.
- Vertex admission uses the existing distance expression and source fallback. It emits the original
  FeaturePoint in index order. Recomputing the workspace's expression does not change its operands.
- An edge is skipped only when neither endpoint is on the circle and the identical generic
  discriminant is negative. Certified exact edges reconstruct the next represented vertex exactly.
  Every possible contributing root, endpoint tie and closed tangency uses the original Edge and
  Parameter arithmetic in edge order.
- The generic uphill direction is exact zero, so it contributes no candidates in this domain.
  The unchanged Range.Add therefore receives the same ordered points and preserves exact-height
  witness skips, interval unions and per-candidate Extrema accounting.
- SupportReferenceEvaluation bypasses the dispatch before certification. No candidates or results
  are cached. Raw members, mesh triangles, owner provenance and the existing pose checks remain separate.

The implementation adds only the partial stratum file, Extrema dispatch and StairStratum diagnostics.
SupportSegmentPolygon, admission tolerances, SG-12 budgets, version and changelog are unchanged.

## Verification

- RED: the new tests fail compilation with CS1061 for the missing StairStratum counter.
- Focused final run: 13 passed, 0 failed, 0 skipped.
- SupportFootprint, SupportGateCost and SupportCost families: 635 passed, 0 failed, 0 skipped.
  This includes existing cold, warm, interleaved, rebuilt and concurrent reference equivalence cases
  and the warm Find allocation assertions.
- New cases cover closed edge and vertex tangency, adjacent-float boundaries, first-witness order,
  represented hull tilt by one double ulp, installed rotated and enclosed source poses, reference
  bypass, below-band retention at 256 members and atomic capacity refusal at 257.
- Both env-enabled one-shot artifact runs passed, one test each. No timing run was repeated.
- Final Release solution build: passed, 0 warnings, 0 errors.
- Full Game.Tests suite, run once: 5291 passed, 0 failed, 1 skipped. The existing skipped diagnostic is
  `WallContactOwnColumnTests.The_perturbation_control_for_every_sensitivity_claim_in_this_chain`.
  Its declared skip explains that it is a measurement requiring an engine perturbation, not an assertion.

The first capacity fixture incorrectly expected isolated below-band rectangles to be retained.
Both paths correctly discarded them. The corrected fixture joins those rectangles to an in-band slope.
The test analyzer also required the predicate overload of Assert.Single. Both corrections are in the
passing focused run. Production tolerances and behavior were not changed to satisfy either failure.

## Artifact provenance

Raw artifacts are retained under `.superpowers/sdd/2026-10-18-support-cost-phase3/` locally.
Their relevant numbers are committed here instead of the multi-megabyte JSON.

| Artifact | Captured UTC | Bytes | SHA-256 |
| --- | --- | ---: | --- |
| base-cost.json | 2026-10-17 22:52:14 | 2372257 | `a3a402584d52108fae59a520f67f319bdc23ed52a8121610ae6a0d8fedaf118d` |
| head-cost.json | 2026-10-17 22:53:53 | 2396721 | `43faa0bb1349e13a2b22b65dfbc09233841783b04c78682f87ec36632cbbd43e` |

Both commands used `build-slot --heavy`, then `env DOTNET_TieredCompilation=0
KHAOZ_SUPPORT_COST_ARTIFACT=<absolute artifact path> dotnet test
KhaozEngine.Game.Tests/KhaozEngine.Game.Tests.csproj -c Release --filter
FullyQualifiedName~SupportGateCostTests.OneShotCostArtifact`. Base ran in the detached worktree at the
task base. Head ran in the task worktree at the measured implementation commit. Each exit was finalized
with `scripts/record-support-cost-exit.py`. Build-slot reported base exit 0, 94.106 s, and head exit 0,
99.786 s. Those durations include build and test overhead and are not the measured query times.

## Concerns and next gate

Mesh Find rose by 146.500 us despite zero stratum hits and unchanged Extrema count. Both mesh results
match their oracle and allocate 0 warm bytes. This single warm observation per artifact does not
establish the cause of the increase. Further attribution remains necessary, with no additional timing
run performed by this task.

The local stairs box and hull Finds remain above the 50 us ordinary limit. SC-20 authorizes the next
plan amendment after this prototype gate passes. This record does not close SG-12 or authorize a
fourth hosted run.
