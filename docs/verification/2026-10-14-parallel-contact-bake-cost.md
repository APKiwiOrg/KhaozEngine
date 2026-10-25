# Parallel contact bake cost record

## Scope and method

Single local observations of [#1401](https://github.com/APKiwiOrg/KhaozEngine/issues/1401) at `9436f7d2d`
(staged 20.40.0) on arm64 macOS 27.0.1, SDK 10.0.401, .NET 10.0.12, 12 cores (8 performance and 4 efficiency).
No game repository or Hollowmere assets were opened. This is sizing evidence for PB-5 of the
[parallel contact bake design](../design/PARALLEL-CONTACT-NAVIGATION-BAKE-2026-10-14.md), not the hosted cost
gate, a load run, a latency guarantee or an adoption verdict.

The fixture is the low-lip bank of the Task 1 baselines widened in Z so 12 workers have more bands than a 9-row fixture
allows: a 0.25 m cell, X `[-1.7, 1.3)`, Z `[-6, 6)`, 12 by 48 = 576 columns with one raw surface each, a floor box and a
25 mm lip over X `[0, 2]`, radius 0.3 m, the fixture's contact tuning, DryOnly and a 160-step edge budget. The phase
5b record's lip row uses the same geometry over 8 rows.

One temporary diagnostic test (`Category=ParallelContactBakeCost`) captured once, then built the profile twice
through `BuildProfile` with one `NavBakeExecution`: a cold pass and a warm pass against the same capture. Each degree
ran in its own test process, so both cold passes are process-cold. There was no preliminary bake, repeat run or loop.
Temporary timestamps surrounded proof calls (`Admit` and `TryEdge`), worker setup (snapshot, envelope validation,
binding construction), each worker phase and every band, the anchor merge, worker release, and the serial layered,
edge and link sections. Managed allocation is the `GC.GetTotalAllocatedBytes(precise: true)` delta across all threads.
Native scratch is the Bepu pool capacity still held by every constructed reader `QueryScratch`, read by reflection
after each pass. All temporary production instrumentation was removed and the files restored byte for byte from git
before the documentation commit. Only this record is committed. Raw JSON, the harness and logs stay in the task's
ignored SDD workspace.

```sh
PB4_COST_OUTPUT=<workspace>/degree-1.json PB4_COST_DEGREE=1 build-slot --heavy --label pb-4-cost -- \
  dotnet test KhaozEngine.Movement.Tests/KhaozEngine.Movement.Tests.csproj -c Release \
  --filter "Category=ParallelContactBakeCost"
PB4_COST_OUTPUT=<workspace>/degree-12.json PB4_COST_DEGREE=12 build-slot --heavy --label pb-4-cost -- \
  dotnet test KhaozEngine.Movement.Tests/KhaozEngine.Movement.Tests.csproj -c Release \
  --filter "Category=ParallelContactBakeCost"
```

Both exited 0 with one test passed. Build-slot admitted each without queuing and recorded 167.9 s and 90.9 s
including build. Every pass at both degrees produced the same graph digest
(`723d8200e8b8fc6e3b15ffcfeb04c3586a48ed6d6e85938a9eeb8e7723ede694` over link acceptance, clearance, raw and
resting height bits, accepted nodes and exit masks). Other build-slot work may have shared the Mac, and the degree 1
warm pass ran 62 percent slower than its own cold pass, so treat every figure as one noisy sample.

## Whole build and speedup

Every pass made 576 `Admit` calls, 460 candidate Holds (the 116 refused candidates are the one-cell boundary ring,
refused at the raw footprint before their Hold), 3,348 directed edge proofs and no Stair candidates, in one layer.

| Pass | Degree 1 wall ms | Degree 12 wall ms | Speedup | Efficiency at 12 |
| --- | ---: | ---: | ---: | ---: |
| cold | 54,745.5 | 32,841.5 | 1.67 | 13.9 percent |
| warm | 88,549.4 | 19,291.6 | 4.59 | 38.3 percent |

Wall time is the caller's `BuildProfile` call, including setup and disposal. The measured speedup is 1.7 times cold
and 4.6 times warm. Neither pair is clean. The cold pair includes JIT on 12 threads at once, and the warm pair compares
against a degree 1 pass that ran slower than its cold one. Against the cleaner degree 1 cold pass, degree 12 warm is
2.8 times faster.

## Per-phase time and utilization

Degree 1 runs the serial path, so it has no worker setup, merge or release. Its admission and layering run inside the
layered section, and its edges inside the edge section.

| Pass | Degree | Setup ms (snapshot, envelope, bindings) | Admission ms | Layered total ms | Merge ms | Edges ms | Release ms |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| cold | 1 | n/a | in layered | 3,337.9 | n/a | 51,381.6 | n/a |
| warm | 1 | n/a | in layered | 1,131.7 | n/a | 87,416.7 | n/a |
| cold | 12 | 1.1, 1.3, 18.0 | 4,622.3 | 4,648.0 | 0.5 | 28,157.9 | 0.3 |
| warm | 12 | 0.0, 0.0, 0.3 | 268.8 | 276.4 | 0.1 | 19,014.4 | 0.0 |

Worker phases at degree 12, 12 workers over 48 bands each. Utilization is summed band time over workers times phase
wall time.

| Pass | Phase | Wall ms | Utilization | Busiest worker ms | Idlest worker ms |
| --- | --- | ---: | ---: | ---: | ---: |
| cold | admission | 4,622.3 | 0.935 | 4,533 | 3,794 |
| cold | edges | 28,157.9 | 0.971 | 28,153 | 24,520 |
| warm | admission | 268.8 | 0.897 | 268 | 231 |
| warm | edges | 19,014.4 | 0.888 | 19,014 | 12,326 |

## Summed proof time

Summed per-proof time is the elapsed time inside proof calls on every thread, measured separately from the wall time.
`Admit` contains its candidate Hold. At degree 12 the Hold was timed in its own bucket. At degree 1 the Hold rode in
the edge bucket, so the degree 1 edge figure below is the serial edge section, which holds only edge proofs, and the
Hold time is the remainder.

| Pass | Degree | Admit ms (Hold inside) | Hold ms | Edge proofs ms | Edge ms per proof | Summed proof ms |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| cold | 1 | 3,304.3 | about 1,039 | 51,381.6 | 15.35 | 54,685.9 |
| warm | 1 | 1,125.0 | about 278 | 87,416.7 | 26.11 | 88,541.7 |
| cold | 12 | 51,761.5 | 16,332.4 | 328,055.7 | 97.99 | 379,817.2 |
| warm | 12 | 2,853.1 | 1,062.0 | 202,697.8 | 60.54 | 205,550.9 |

Proofs are at least 99.8 percent of serial wall time, so the work is almost all parallel. The speedup is lost to per-proof
slowdown, not to scheduling: a warm edge proof took 2.3 times as long on 12 workers as serially, and a cold one 6.4
times. Candidate causes are the 4 efficiency cores, shared caches and memory bandwidth, garbage collection while 12
threads allocate, cold JIT on 12 threads and other machine work. This sample does not separate them.

## Allocation and native scratch

| Pass | Degree | Managed allocated B | Reader `QueryScratch` | Retained native scratch B |
| --- | ---: | ---: | ---: | ---: |
| cold | 1 | 15,844,752 | 1 | 0 |
| warm | 1 | 13,681,664 | 1 | 0 |
| cold | 12 | 31,453,408 | 12 | 0 |
| warm | 12 | 17,829,648 | 12 | 0 |

Degree 12 allocated 1.3 to 2.0 times as much as degree 1, from per-worker bindings, environment contexts, anchor maps,
band results and thread start. The reader pools built one scratch per concurrent reader and retained no native Bepu
pool capacity after either pass. This box-only fixture used no pooled Bepu path (no collision batcher, simulation
sweep or mesh contacts), so native scratch kept per worker on worlds with meshes is not measured here. These figures
are not comparable with the 2026-10-11 support-gates baseline. `ScratchHighWaterBytes` now counts native pool capacity
and every pooled query scratch, which that baseline did not.

## Band rule

The band rule `min(rows, workers * 4)` was reasoned, not measured, when ruling T3-R1 adopted it: more bands than
workers, so a worker that finishes a cheap band claims the next instead of idling behind a costly one. Here 48 rows
gave exactly 48 bands for 12 workers. Workers stayed 89 to 97 percent busy in every phase. In the warm edge phase two
workers ran out of bands about 6.5 s before the phase ended, roughly one band's work on a fast worker. Rows in this
fixture cost the same, so that idle comes from worker speed differences and end-of-phase granularity, not costly rows.

The measurement supports the rule as adequate on uniform rows: scheduling lost about 11 percent at most, while per-proof
slowdown lost far more. Finer bands could recover part of that tail. It does not test the uneven Hollowmere rows that
motivated the rule, so the rule stays reasoned for that case.

## Updated Hollowmere extrapolation

The [phase 5b cost record](2026-10-14-contact-controller-phase5b-cost.md) estimated 18 to 61 hours on one thread for
one contact profile at about one million columns with one or two raw surfaces each. The same assumptions apply: every
surface admits with eight directed edges, no Stair links, one profile, and no capture, serialization, extraction
growth or additional profiles. No Hollowmere scene, catalog or profile set was inspected.

Rate per admitted surface from this fixture at degree 12: the non-edge wall time per surface reaching its Hold, plus
eight times the edge phase wall time per edge proof.

| Source | ms per surface | One million surfaces | Two million surfaces |
| --- | ---: | ---: | ---: |
| Degree 12 warm, this fixture | 46.0 | 12.8 hours | 25.6 hours |
| Degree 12 cold, this fixture | 77.5 | 21.5 hours | 43.0 hours |
| Phase 5b range divided by this record's speedups | n/a | 3.9 to 36.6 hours | n/a |

The last row divides 18 hours by 4.59 and 61 hours by 1.67. A square million-column capture has about 1,000 rows, so
12 workers get 48 bands of about 21 rows each. On a 12-core Mac a contact profile therefore still bakes in roughly
half a day to two days. That is better than the serial bake but not routine for every world edit or engine pin. Option
B of #1401 (proof reuse or cheaper proofs), a profile of the per-proof slowdown under parallel load, and a hosted
measurement on more cores are the next levers. These samples do not price aquatic navigation, footprint-only seeds
or explicit bake profiles, all deferred to [#1389](https://github.com/APKiwiOrg/KhaozEngine/issues/1389).
