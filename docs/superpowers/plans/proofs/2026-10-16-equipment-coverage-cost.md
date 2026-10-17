# Equipment coverage cost evidence

## Status

Two separately authorized one-actor CPU pilots completed. Their measured coverage frames were valid and
allocated zero managed bytes. The repeat after cost units 1 and 2 measured 79.542417 ms added preparation
per frame. CPU performance acceptance remains open. The native GPU gate remains unproved.

This pilot does not satisfy the 2 ms added-preparation gate for 32 typical actors. It instead reveals
substantial cost in construction and certification that needs investigation before a larger measurement.
Do not extrapolate this hosted one-actor result linearly to 32 actors or to a native game renderer.

## Authorized pilot on 2026-10-17

- Source commit `136873874ddc1179b2b3afb3f715737112d28ed0`.
- [Completed hosted run](https://github.com/APKiwiOrg/KhaozEngine/actions/runs/38990534474), one iteration,
  no added load, Release, ubuntu-26.04, shared workflow `1a176109d8598a9991de98101d6d29b51d4331c9`.
- Manifest `tools/EquipmentCoverage.Cost.Tests/authorized/typical-a1-cpuskin-full.json`, SHA-256
  `54c1cfe51ee0a373b21b4dad3cc83ea316ecc9fe61f960f340da088d752ab017`.
- One actor, typical geometry, CPU skinning, full pass configuration, recording CPU device with no GPU.
  Coverage off then on, four warm-up and eight measured frames per condition.
- Shared 300 second cooperative unit deadline and 10 second cooperative frame limit. Neither was exceeded.
- AMD EPYC 7763 host, four reported processors, .NET 10.0.13, default tiered compilation and PGO.
  Both conditions share the same process, environment and topology fingerprint.

The [first dispatch](https://github.com/APKiwiOrg/KhaozEngine/actions/runs/38990328399) selected zero tests
because embedded quotes were stripped while forwarding the unit filter. Its TRX recorded zero executed
tests and no measurement reports existed. Removing those quotes allowed the still-unexecuted approved
pilot to run. There were two workflow attempts and exactly one executed pilot, with no workload increase.

## Results

All eight measured coverage-on actor frames were accepted and Covered. There were zero declines,
fallbacks and errors. Both conditions completed all requested warm-up and measured frames. The TRX
records one executed and passed test, with zero skipped tests.

| Mean CPU span, ms per frame | Coverage off | Coverage on |
| --- | ---: | ---: |
| Preparation | 0.006755 | 63.021824 |
| Submission | 0.021714 | 0.084963 |
| Reservation | 0.000925 | 0.015216 |
| Render resolution and certification | 0.015253 | 39.027469 |
| Complete skinned preparation | 1.686912 | 2.155921 |
| Motion history commit | 0.000460 | 0.003595 |
| Sum of disjoint preparation spans | 1.732019 | 104.308989 |
| Whole frame wall time | 2.591628 | 104.678628 |

The disjoint preparation sum increased by **102.576970 ms per frame**. Whole frame wall time increased
by 102.087001 ms. These are separate observed quantities, not interchangeable GPU timings.

Coverage-on geometry evaluation averaged 61.445761 ms, comprising 39.443802 ms of construction and
22.001959 ms of geometry certification. Late render certification averaged 38.958721 ms. These are
nested diagnostic spans, already included in the table's preparation and resolution rows, not extra costs
to add again. They identify the next source investigation.

Coverage-on whole-frame median was 93.342288 ms and maximum was 142.250453 ms. Its warm-up
maximum was 851.711715 ms. Eight measured frames are sizing evidence, not a stable performance estimate.
The final condition's elapsed unit clock, before its report publication, was 3459.706788 ms.

Every measured allocation window, including the whole frame, reported zero managed bytes for both
conditions. The allocation budget passes. The report correctly leaves the CPU gate unproved because it
requires 32 actors, and leaves the native GPU gate unproved because there is no authorized GPU timing route.

## Evidence and limits

Unmodified condition reports, final unit report, log and TRX are retained under
[`equipment-coverage-cost/2026-10-17-a1`](equipment-coverage-cost/2026-10-17-a1).
`sha256.json` records their file hashes. Root checked report identities against the manifest and run head,
the shared workflow SHA, outcome counters, frame counts, allocation values, matching environment and
topology, and equality between the separately published conditions and the unit's embedded copies.

Observed topology was 1092 source vertices, 2000 source triangles, 200 eligible triangles, 96 refined
source triangles, 1258 final vertices and 2332 final triangles, with two commands and materials at level 2.
The fixture is synthetic public geometry, but execution uses the real CPU coverage path rather than a fake
session. Recorded pass contexts are certification evidence, not executed GPU work.

Instrumentation overhead remains included. Same-process ordering shares JIT, GC and cache state. The
point light radius is 3 around actor zero. [The wider point-light refusal limit](https://github.com/APKiwiOrg/KhaozEngine/issues/1425)
remains open. No larger actor-count measurement or native GPU proof follows from this pilot's approval.

## Repeat after cost units 1 and 2

The owner separately approved one repeat with the same workload and limits. The
[repeat run](https://github.com/APKiwiOrg/KhaozEngine/actions/runs/39002519327) completed on
`1cf91b18d16f73bebf538a3b5ab793f4bf3b316b`, containing the reviewed render-separation optimization
`3912d4f80` and exact-overlay arithmetic reuse `4d5bd0df9`. Only the authorization metadata changed in the
manifest, whose new SHA-256 is `1b411e0ce3d90420039b58f678fbfe7fcef4b0eb52e784066f4f45fb85a64fd4`.

One iteration executed and passed, with zero skipped tests. Both conditions completed four warm-up and
eight measured frames. All eight measured coverage-on actor frames were accepted and Covered, with zero
declines, fallbacks and errors. Every allocation window, including the whole frame, remained zero. The
unit deadline and frame limits were not exceeded. The same-process environment and topology matched
between conditions, and the topology also matched the first pilot.

| Mean CPU span, ms per frame | First pilot, coverage on | Repeat, coverage on |
| --- | ---: | ---: |
| Preparation | 63.021824 | 56.523196 |
| Geometry construction, nested in preparation | 39.443802 | 29.623813 |
| Geometry certification, nested in preparation | 22.001959 | 25.942948 |
| Render certification, nested in resolution | 38.958721 | 22.203852 |
| Complete skinned preparation | 2.155921 | 2.495231 |
| Sum of disjoint preparation spans | 104.308989 | 81.359519 |
| Whole frame wall time | 104.678628 | 81.752620 |

Repeat off-condition preparation sum was 1.817101 ms, giving **79.542417 ms added preparation per frame**.
Whole frame wall time was 2.758988 ms off and 81.752620 ms on, a paired difference of 78.993631 ms.
Coverage-on frame median was 57.955524 ms and maximum was 193.213461 ms. Warm-up maximum was
312.692930 ms. The final condition's unit elapsed clock before publication was 2855.446605 ms.

This is not a controlled before/after speedup comparison. The first runner reported AMD EPYC 7763,
while the repeat reported AMD EPYC 9V74. Both reported four processors, Ubuntu 26.04.1, .NET 10.0.13,
and default tiered compilation and PGO, but they were different machines and processes. Each run contains
only eight measured samples per condition with the same off-first ordering. The data identifies remaining
cost and validates the workload, but cannot isolate a precise percentage improvement from the code changes.

The repeat's allocation gate passes. The CPU gate remains unproved because it requires 32 actors, and the
native GPU gate remains unproved. Further source optimization and suitable benchmark conditioning are
needed before the larger gate. No additional measurement is authorized by this completed repeat.

Unmodified reports, log and TRX with their hashes are retained under
[`equipment-coverage-cost/2026-10-17-a1-after-unit2`](equipment-coverage-cost/2026-10-17-a1-after-unit2).
Root verified the run head and workflow SHA, manifest hash, exact frame and outcome counts, zero
allocations, limits, environment and topology identity, and equality of the standalone condition reports
with the unit report's embedded copies. The hosted job completed in 1 minute 58 seconds.
