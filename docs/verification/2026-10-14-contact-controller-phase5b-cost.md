# Phase 5b contact ground navigation cost record

## Scope and method

Single local observations at `3296038d8`, staged 20.38.0, on arm64 macOS 27.0.1 with SDK 10.0.401.
No game repository or Hollowmere assets were opened. This is phase 6 sizing evidence, not the hosted SG-12
cost gate, a load run, a latency guarantee or an adoption verdict.

One diagnostic test performed one cold fixture pass followed by one warm pass against the same immutable
captures and contexts. There was no preliminary contact bake, repeat run or timing loop. The first lip build
was process-cold for contact navigation. Later cold rows already shared JIT and thread caches from that row.
World construction, registration, raw capture, serialization and load are outside these profile timers.

Fixtures reuse the existing format 2 round-trip inputs:

- `ContactRoundTripIsByteExact`: the 25 mm low lip, radius 0.3 m, half-height 0.75 m, DryOnly.
- `LoadMatrix`: the single flat column, radius 0.2 m, half-height 0.75 m, DryOnly.
- `MixedLegacyAquaticAndContactRoundTrip`: only its contact WadeOnly profile over flat ground with 0.1 m
  water, radius 0.2 m, half-height 0.75 m. The legacy aquatic companion was not timed.

The lip and wade captures have X `[-1.7, 1.3)`, Z `[-1, 1)`, 0.25 m cells, 96 columns, one raw surface
per column and a 160-step edge budget. The wade capture records 96 water entries. The single flat column
uses X/Z `[-0.5, 0.5)`, a 1 m cell, no water and a 64-step budget. All use `1 / 30` second proof slices,
zero traction hysteresis and unchanged fixture slope, step, placement and arrival settings.

Temporary timestamps surrounded anchor acquisition/query/disposal, placement acquisition/query/disposal,
candidate Hold proofs and directed edge proofs. Edge time includes its initial Hold and all travel slices,
but the separately reported Hold column is candidate admission only. Counters classify the first refusal
without changing predicate order. The diagnostic checks raw count equals capture count and admitted plus
refused equals raw count. Counter bookkeeping and timing overhead are included in whole-profile figures.

The diagnostic had `Category=ContactNavigationCost`, ran alone, and was removed after measuring. All temporary
production instrumentation was restored byte for byte before documentation commits and full verification.
Only this record is committed. Raw JSON, harness source and the log remain in the task's ignored SDD workspace.

```sh
P5B_COST_OUTPUT=<task-workspace>/task-6-cost.json build-slot --heavy --label p5b-6-cost -- \
  dotnet test KhaozEngine.Movement.Tests/KhaozEngine.Movement.Tests.csproj -c Release \
  --filter 'Category=ContactNavigationCost'
```

Exit 0. Passed 1, failed 0, skipped 0. The diagnostic took 16 seconds. Build-slot admitted it without
queuing and recorded 37.614 seconds including build and restore.

## Observed work and elapsed time

Calls are actual calls in that profile build. Anchor, placement and Hold time per raw surface divide by
all raw surfaces, including refused candidates. Edge time divides by actual directed edge proofs. Times
are elapsed milliseconds, not CPU time. No time was subtracted for instrumentation or other machine activity.

| Pass | Profile | Raw | Admitted | Whole build ms | Anchor calls | Placement calls | Hold calls | Edge proofs | Edge steps |
| --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| cold | lip-player | 96 | 60 | 5274.9840 | 96 | 60 | 60 | 388 | 4032 |
| cold | flat-player | 1 | 1 | 2.2797 | 1 | 1 | 1 | 0 | 0 |
| cold | mixed-contact-wade | 96 | 60 | 2420.9088 | 96 | 60 | 60 | 388 | 4032 |
| warm | lip-player | 96 | 60 | 5331.6935 | 96 | 60 | 60 | 388 | 4032 |
| warm | flat-player | 1 | 1 | 0.5560 | 1 | 1 | 1 | 0 | 0 |
| warm | mixed-contact-wade | 96 | 60 | 3164.3021 | 96 | 60 | 60 | 388 | 4032 |

Every candidate Hold used exactly one movement step. Both 96-column profiles proved 388 directed same-layer
edges with 4,032 total edge steps, averaging 10.392 steps per proof. No edge or candidate Hold was refused.
All profiles have one layer, no candidate Stair links and no accepted Stair links.

| Pass | Profile | Anchor total ms | Anchor ms/raw | Placement total ms | Placement ms/raw | Hold total ms | Hold ms/raw | Edge total ms | Edge ms/proof |
| --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| cold | lip-player | 273.524628 | 2.849215 | 305.047042 | 3.177573 | 186.844999 | 1.946302 | 4423.495927 | 11.400763 |
| cold | flat-player | 0.574334 | 0.574334 | 1.075125 | 1.075125 | 0.347791 | 0.347791 | 0.000000 | n/a |
| cold | mixed-contact-wade | 8.576791 | 0.089342 | 33.924873 | 0.353384 | 12.133418 | 0.126390 | 2353.862915 | 6.066657 |
| warm | lip-player | 41.932408 | 0.436796 | 61.212793 | 0.637633 | 51.072707 | 0.532007 | 5176.379623 | 13.341185 |
| warm | flat-player | 0.093375 | 0.093375 | 0.216833 | 0.216833 | 0.139459 | 0.139459 | 0.000000 | n/a |
| warm | mixed-contact-wade | 14.490419 | 0.150942 | 87.690627 | 0.913444 | 58.047705 | 0.604664 | 3003.183090 | 7.740163 |

The cold lip row includes cold path/JIT cost. The warm lip and wade rows were slower overall than cold,
so this sample cannot establish a warm speedup. Other admitted build-slot work was active on the shared Mac.

## Candidate refusals and arrays per profile

Lip and wade each refused 36 of 96 raw candidates at `raw-footprint`, on both passes. The flat profile refused
none. Counts for raw headroom, non-walkable support, nonfinite anchor, ownership band, next raw surface,
anchor headroom, anchor footprint, placement and candidate Hold refusals are all zero in these fixtures.
These are first-failure counts, not counts of every condition a rejected candidate might violate. Non-Known
anchor queries would abort rather than become candidate refusals. These fixtures produced no such abort.

The array peak column is a conservative **incremental managed-array resident upper bound**, not sampled RSS
or a measured exact live-array peak. Synchronous `GC.GetAllocatedBytesForCurrentThread` during `BuildProfile`
bounds every newly allocated managed array even if none is collected. Adding the immutable captured array
payload accounts for the arrays retained before the build. It intentionally also counts non-array objects,
lease tokens and instrumentation allocations. It excludes pre-existing physics/environment buffers, native
memory, JIT memory and array headers of the shared capture. Add those separately for process sizing.
No no-GC allocation assertion was introduced.

Retained payload is exact for the primitive capture and graph fields in these link-free fixtures:
`4(C + 1) + 12S + 12W + 11Q` bytes, where C is columns, S raw surfaces, W captured water entries,
and Q is layer cells. The 11 bytes per layer cell are clearance (1), raw height (4), resting height (4),
accepted-node flag (1) and exit mask (1). Reference containers, headers, binding caches and links are excluded.

| Pass | Profile | Allocated during build B | Array peak upper B | Retained primitive array payload B |
| --- | --- | ---: | ---: | ---: |
| cold | lip-player | 1,892,872 | 1,894,412 | 2,596 |
| cold | flat-player | 107,496 | 107,516 | 31 |
| cold | mixed-contact-wade | 1,712,016 | 1,714,708 | 3,748 |
| warm | lip-player | 1,642,088 | 1,643,628 | 2,596 |
| warm | flat-player | 67,808 | 67,828 | 31 |
| warm | mixed-contact-wade | 1,642,304 | 1,644,996 | 3,748 |

The peak upper bounds include at most three shared capture arrays and the profile's five dense graph arrays
per layer, plus all newly allocated scratch and collection backing arrays. They are deliberately much looser
than the retained payload. They do not claim zero allocation for contact proofs. Every anchor, placement and
movement slice acquires fresh ordinary lease tokens. Legacy zero-allocation proof evidence is a separate path.

## Conservative static bounds

For one contact ground profile, let C be captured columns, M the configured maximum raw surfaces per column,
S the actual raw surface count, A admitted candidates, N `MaxEdgeProbeSteps`, L extracted layers and Q `C * L`.
Then S <= C * M, A <= S, and Q <= `MaxLayerCells` for a successful bake. One or two raw surfaces per column
does not guarantee one or two global layers. Fragmentation can require more layers and hit the layer-cell cap.

There is at most one anchor, one placement and one candidate Hold per raw surface. Each attempted candidate Hold takes
at most one movement step, including a refused Hold. Each admitted node has at most eight neighbouring columns with at most M targets
in each. Same-layer edges plus directed Stair proofs E are therefore bounded by `8 * M * A`. Each edge uses
at most N movement steps including its initial Hold. Thus:

- Anchor and placement calls each <= S.
- Candidate Hold calls and steps each <= S.
- Directed edge proofs <= `8 * M * S`.
- Movement steps <= `S + 8 * M * S * N`.
- Ordinary movement lease acquisitions <= `1 + 3 * S + 8 * M * S * N`, including profile preflight.

These count bounds do not bound query elapsed time. Static capture, support certification, region extraction,
footprint tests and link regeneration also cost work. In particular, extraction can compare region sets,
and link discovery scans layer pairs. They are not proved linear in C.

For navigation-owned reachable array sizing on 64-bit .NET 10, use the deliberately loose allowance
`1024 * (C + S + Q + P + J + 1)` bytes, plus physics/environment storage. P <= `8 * M * S` bounds
region adjacency pairs, and J <= `8 * M * S` bounds candidate Stair links. The allowance includes dense
capture/extraction/grid fields, anchor dictionary storage, region membership arrays, simultaneous region-pair
collections and link copies. It assumes array headers up to 32 bytes and collection capacity no greater than
`4 * (live count + 7)`. The 1 KiB per counted unit covers the primitive/reference widths, duplicate relation
sets and transient backing-array resize copies conservatively. This is a sizing envelope for live arrays,
not a total-allocation bound or an executable memory guarantee. Dead uncollected arrays can add heap pressure.

With C = 1,000,000, N = 160 and Q = C for M = 1, the count ceiling is 8 million edge proofs and
1.281 billion movement steps. For M = 2 and Q = 2C, it is 32 million edge proofs and 5.122 billion steps.
The loose live-array envelopes at maximum P and J are 19.456 GB and 70.656 GB respectively. They are
not expected usage. With no region-pair or Stair arrays, the same envelopes fall to 3.072 GB and 5.120 GB.
The exact final dense payload for one all-dry, one-layer profile is about 27 MB, or 50 MB for two raw
surfaces and two layers. Capturing one water entry per column adds 12 MB shared across profiles.

## Conditional Hollowmere-scale extrapolation

Hollowmere is the game's world. Assume about one million columns, one or two raw surfaces per column,
the same capsule geometry and cell size, at most one or two coherent global layers, no Stair links and
one sequential profile bake. Assume every surface admits and has eight directed neighbours, with each edge
requiring the observed mean 10.392 steps. These assumptions remove the fixture's boundary refusal benefit.
No actual Hollowmere scene, catalog, navigation, profile count or water distribution was inspected.

For elapsed sizing, use the warm anchor, placement and Hold **per-call** averages plus eight warm edge
averages per admitted surface. This is an inference from tiny fixtures, not a static time ceiling.
It excludes capture, serialization/load, additional profiles and any nonlinear extraction or physics growth.

| Warm reference | Estimated ms/admitted surface | One million raw surfaces | Two million raw surfaces |
| --- | ---: | ---: | ---: |
| lip-player | 109.038 | 30.288 hours | 60.576 hours |
| mixed-contact-wade | 64.501 | 17.917 hours | 35.834 hours |

At that density, expected proof counts are one or two million anchors, placements and candidate Holds,
eight or sixteen million directed edges, and about 84.134 or 168.268 million total movement steps.
The wet and lip rows have different geometry and timing noise. Neither establishes a universal upper time.
Warm whole-build allocation was about 1.64 MB per 96-column profile, mostly short-lived allocations rather
than the roughly 2.6/3.7 KiB final primitive arrays. Linear scaling of that traffic would already be about
17.1 GB per million fixture-equivalent raw surfaces, without a claim that they all remain resident.

Phase 6 should retain the hard step and layer-cell budgets and decide partitioning and reuse using these
counts, the conditional elapsed estimates and the hosted cost gate. These samples do not price aquatic
navigation, footprint-only seeds or explicit bake profiles, all deferred to
[#1389](https://github.com/APKiwiOrg/KhaozEngine/issues/1389).
