# Water prism coverage census

Issue [#1755](https://github.com/APKiwiOrg/KhaozEngine/issues/1755) needs a reproducible baseline before a faster
nonuniform water kernel is designed. These authored fixtures are shared by `TileWaterPrismCensusTests` and the
benchmark's `--water-prism` mode. They use the actual `PlayerMoveSimulator`, ordinary Contact binding, tile
colliders and Bepu query source. A measured authority operation includes `BeginExplicitRead`, `Step` and read
disposal. Replay starts from the same basis under a fresh read. The next tick starts from the preceding result.

## Geometry and commands

Each wet patch is a 10 by 7 m rectangle in a grass region. Tiles are 1 m and authored corner heights are whole
centimetres. A distant -0.35 m corner sets the common -0.37 m free surface. None of these fixtures loads consumer
content. They omit the whole lake, its ellipse, objects, shoreline curvature and region transitions.

The capsule has 0.30 m radius and 0.75 m half height. Walking is 2 m/s, running 5 m/s and swimming 3 m/s. The water
entry and exit fractions are 0.65 and 0.55, resting submersion is 0.60, buoyancy stiffness is 8, wade floor is 0.60
and backpedal share is 0.65. Commands run at 30 Hz. Four idle settling steps precede observation.

| Case | Bed and starting station | Command |
|---|---|---|
| bank-stand | South bank falls from -0.35 to -1.30 m over tile row 236, X 110.375, Z -236.365 | Idle |
| bank-along | Same bank and station | East along the contour |
| bank-across | Same bank and station | North down the bank |
| swim-entry | Bed falls 0.18 m per metre north from -1.30 m at row 246, Z -246.23 | North across the entry gate |
| deep-swim | Bed falls 0.44 m per metre north from -3.69 m at row 260, Z -260.568 | North |
| deep-tread | Same deep slope and station | Idle at surface rest |
| flat-neighbour | Flat -2.37 m bed through row 253, then a 0.07 m/m slope, Z -252.85 | North within radius of the slope |
| flat-control | Flat -2.37 m bed clear of the distant rim | North |
| dry-control | Dry ground 1.5 m south of the bank | Idle |

The shallow bank's centre-column water depth is 0.327 m. Support-disc seating raises grounded feet above the
axis bed, so feet submersion is smaller. The deep station's axis depth is about 3.570 m. It approximates one
local bowl grade with a plane rather than the bowl's changing two-dimensional gradients.

## Deterministic observations

Normal binaries compile out `TileWaterCensus` and all its observation sites. The existing `TileWaterQueryWork`
opt-in stays unchanged. Build with `-p:WaterPrismCensus=true` to include the research-only thread-local callbacks.
Census builds write binaries under each project's `bin/water-prism-census/` and all intermediates, including
restore assets, under `obj/water-prism-census/`. Normal output paths stay separate, including for later
`--no-build` operations. Every census project is nonpackable. Pack and publish fail with `KECENSUS001`, including
solution and standard pack-script calls. Before restore or compilation, the guard checks effective `OutDir`,
`TargetDir`, `TargetPath`, intermediate and reference assembly destinations, restore output, assets and project
extensions paths against the census-only roots. An escaping destination fails with `KECENSUS002`, including
overrides from properties, environment variables and response files. Layout switches are allowed when the
computed destinations remain isolated. This enforces assembly and restore isolation for the SDK paths, not
arbitrary file writes from custom build targets. Use a normal build for timing and distribution.

```bash
build-slot --label water-prism-census -- dotnet test KhaozEngine.TileWorld.Physics.Tests/KhaozEngine.TileWorld.Physics.Tests.csproj -c Release -p:WaterPrismCensus=true --filter FullyQualifiedName~TileWaterPrismCensusTests
```

The test records authority tick 0, its independent authority replay and authority tick 1. Integer work snapshots
pin each trace's selected prisms and faces, intersections at selection, span reassembly, Append overlap and dry
classification, attempted projection bases by rank, projection dot products, other dot products, retained pieces
and faces, spans, contacts, witness fallbacks and seam proof activity. Construction of immutable prism faces and
projection basis lists happens before a step and is excluded. `bases` counts per-intersection projection attempts,
not list construction. `projection-dots` includes repeated normal-normal coefficients as well as path terms.
`other-dots` includes clipping, containment, witness lookup and seam proof dot products.

`TileWaterQueryWork.ConvexIntersections` counts actual fraction-window evaluations. `ShapePreparations` counts
preparations and `LazyCoefficientFills` counts first-use coefficient computation. The latter two remain zero
until the prepared evaluator is introduced. Their census keys are `shape-preparations` and
`lazy-coefficient-fills`. Logical prism traces, memo hits and memo misses are separate diagnostics owned by
the sampler memo. A memo hit must not fabricate evaluation, preparation or coefficient work. Frozen-reference
execution suppresses production diagnostic counters and is excluded from the census.

SHA-256 input hashes use shape identity, every interval endpoint of start, drive and core, radius, the closed
fraction window and representation errors. The shape identity binds immutable planes and seam certificates.
BinaryWriter preserves signed zero bits. Different objects with equal geometry are deliberately treated as
different inputs, making repeat counts conservative. No result is cached. Repeated whole queries are reported
separately from repeated exact intersection calls. Caller stacks are explanatory data rather than golden values.

The height culling census expands every bound by representation error and coverage skin. A prism occupies its
triangle bed minimum through the free surface, a bed face its minimum through maximum bed height, and a surface
face the surface height. A swept capsule AABB disjoint from that Y interval can reject the solve. The whole-trace
submerged bound additionally checks complete tile ownership across the swept XZ rectangle, one surface, a bottom
above every candidate bed maximum and a top below the surface. The dry bound excludes every candidate prism.
These are geometry eligibility bounds. They do not prove that omitting a solve preserves contact enumeration,
witnesses, order, error certificates or refusals. Surface swimmers breach the free surface and are not fully
submerged under this bound.

Every counted local rejection is checked against the actual solve. It must be a resolved miss, with no earlier
refusal. This validates the reported removals for the recorded inputs. It is not a proof for other fixtures.
Both moving-bank cases contain a fully dry motion-resolve trace inside an otherwise wet gameplay step. Each can
remove that trace's eight solves, or 8 of 202 solves in the step. No recorded trace qualifies as fully submerged.

Coverage output compares bitwise with the frozen `WaterTraceReference` through `WaterCoverageComparison` and
`WaterCoverageDigest`. This reference shares the prism solver. Independent box and seam tests remain necessary.
No timing assertion is installed.

## Frozen differential kernel contract

`Fixtures/FrozenWater` in the physics test project holds the evaluator and complete prism trace frozen from
`85e484e87`. They read copied ordered shape geometry, recompute the original expressions and suppress production
diagnostic counters. Source hashes pin the deliberate snapshot. A helper changed by a later optimisation must
also be frozen in that change. The reference shares interval arithmetic, root uncertainty, immutable bed seams,
witness lookup, candidate indexes, prism/face geometry and captured ground. It preserves the old evaluator's
behavior, including numerical behavior that might later receive a separately reviewed correction.

`Category=WaterKernelContract` selects the differential corpus and shape/trace matrix, including Diagnostic
storage boundaries, the 252-sample patch sweeps and all 510 existing bank-lift inputs. These include every trace
over four consecutive authority ticks, stratified samples, translated origins and adjacent floats, with
sentinel-filled caller buffers. The normal CI filter skips Diagnostic cases. Its small corpus instead adds
frozen comparisons to the existing census observations, avoiding another construction of all nine worlds.

```bash
build-slot --label water-kernel-contract -- dotnet test KhaozEngine.TileWorld.Physics.Tests/KhaozEngine.TileWorld.Physics.Tests.csproj -c Release --filter 'Category=WaterKernelContract'
```

The independent `water-kernel-contract` CI job builds only this headless test project on hosted ubuntu-26.04
x64 and macos-26 arm64. Each job runs the entire contract with `DOTNET_TieredCompilation=0` and `1` and retains
both TRX files. It runs separately from the selective runner's Foundation/Server pass, so an affected-project
selection cannot omit this matrix. Each execution compares frozen against production on the same runtime.
There is no single numeric digest asserted across architectures.

## Regenerate the work-count baseline

Set `CENSUS_RESULTS` to an absolute capture directory outside the checkout, then record a census test run:

```bash
build-slot --label water-prism-census -- dotnet test KhaozEngine.TileWorld.Physics.Tests/KhaozEngine.TileWorld.Physics.Tests.csproj -c Release -p:WaterPrismCensus=true --filter FullyQualifiedName~TileWaterPrismCensusTests --logger 'trx;LogFileName=census.trx' --results-directory "$CENSUS_RESULTS"
python3 scripts/regenerate-water-prism-census.py "$CENSUS_RESULTS/census.trx"
git diff -- KhaozEngine.TileWorld.Physics.Tests/Fixtures/WaterPrismCensusBaseline.json
```

Regeneration is an explicit tool invocation. Ordinary tests only read the golden. The formatter writes
`KhaozEngine.TileWorld.Physics.Tests/Fixtures/WaterPrismCensusBaseline.json`, preserving one JSON line per trace.
Use `--output "$CENSUS_RESULTS/candidate.json"` to write a candidate separately. It requires complete observations
for all nine cases and refuses changed coverage digests. A count change makes the census test fail after emitting
its observations. Check the reference comparisons and failure first, then regenerate and review the golden diff
as the evidence of an optimisation. A new golden is not permission to accept changed results or tolerances.

## Indicative timing

```bash
build-slot --label water-prism-build -- dotnet build KhaozEngine.Benchmarks/KhaozEngine.Benchmarks.csproj -c Release
build-slot --heavy --label water-prism-timing -- dotnet KhaozEngine.Benchmarks/bin/Release/net10.0/KhaozEngine.Benchmarks.dll --water-prism
```

One invocation uses 8 warm-up and 96 measured authority operations per fixture, each from the same fixed basis.
This keeps moving cases on their named grade. It is not a trajectory or a load test. It prints median microseconds
per authority operation and per individual trace, cold-read and Step phases, the sum of water time per operation
and the residual. Phase medians are not additive. Per-trace stopwatches are coarse enough for the millisecond
kernel, but trace and residual measurements include clock overhead. Inner projection phases have no clocks.
The census build refuses to run the timing mode. Shared-machine timings are indicative, with no budget gate.
