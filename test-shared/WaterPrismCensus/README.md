# Water prism coverage census

Issue [#1755](https://github.com/APKiwiOrg/KhaozEngine/issues/1755) tracks exact output and executed work in the prepared
nonuniform water kernel and sampler memo. These authored fixtures are shared by `TileWaterPrismCensusTests` and the
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

The sampler memo is reset after the four settling steps. The test records cold authority tick 0, its
independent authority replay and authority tick 1 against the resulting warm table. Geometry caches are already
warm from settling. A moving next tick can introduce new keys and candidate geometry. Integer work snapshots
pin each trace's selected prisms and faces, intersections at selection, span reassembly, Append overlap and dry
classification, attempted projection bases by rank, projection dot products, other dot products, retained pieces
and faces, spans, contacts, witness fallbacks and seam proof activity. Construction of immutable prism faces and
projection basis lists happens before a step and is excluded. `bases` counts visited basis/window pairs,
excluding retained full-window clip rejections skipped by valid subwindows. It does not count list construction.
`projection-dots` counts executed query and geometry dots,
excluding values reused from preparations or compatible shape entries.
`other-dots` includes clipping, containment, witness lookup and seam proof dot products.

`TileWaterQueryWork.ConvexIntersections` counts actual fraction-window evaluations. `ShapePreparations` counts
preparations and `LazyCoefficientFills` counts first-use clip, post-feasibility, root and plane-value groups.
Their census keys are `shape-preparations` and `lazy-coefficient-fills`. `PrismTraces`, `MemoHits` and `MemoMisses`
are logical sampler work, recorded as `prism-traces`, `memo-hits` and `memo-misses`. The explicit
`convex-intersections` count equals the number of observed window inputs. A memo hit must not fabricate
evaluation, preparation or coefficient work. Frozen-reference
execution suppresses production diagnostic counters and is excluded from the census.

SHA-256 input hashes use shape identity, every interval endpoint of start, drive and core, radius, the closed
fraction window and representation errors. The shape identity binds immutable planes and seam certificates.
BinaryWriter preserves signed zero bits. Different objects with equal geometry are deliberately treated as
different inputs, making repeat counts conservative. Memo hits retain their logical query row but have no
kernel input hashes. Repeated whole queries are reported
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
Both moving-bank cases contain a fully dry motion-resolve trace inside an otherwise wet gameplay step.
No recorded trace qualifies as fully submerged. These bounds remain observations and are not applied as culling.

Coverage output compares bitwise with the frozen flat and prism evaluators through `WaterCoverageComparison` and
`WaterCoverageDigest`. Its shared helpers and snapshot boundary are listed below. Independent box and seam
tests remain necessary. No timing assertion is installed.

## Cold and warm memo work

| Case | Cold prep / eval | Cold small-tier hits / misses | Next-tick prep / eval | Next-tick small-tier hits / misses |
| --- | --- | --- | --- | --- |
| bank-across | 32 / 148 | 3 / 4 | 32 / 148 | 3 / 4 |
| bank-along | 32 / 148 | 3 / 4 | 32 / 129 | 3 / 4 |
| bank-stand | 8 / 18 | 3 / 1 | 0 / 0 | 4 / 0 |
| deep-swim | 18 / 39 | 3 / 3 | 24 / 61 | 4 / 2 |
| deep-tread | 6 / 13 | 3 / 1 | 0 / 0 | 4 / 0 |
| dry-control | 0 / 0 | 0 / 0 | 0 / 0 | 0 / 0 |
| flat-control | 0 / 0 | 0 / 0 | 0 / 0 | 0 / 0 |
| flat-neighbour | 36 / 57 | 3 / 3 | 24 / 61 | 4 / 2 |
| swim-entry | 48 / 137 | 3 / 4 | 30 / 100 | 3 / 3 |

The table is the retained census snapshot from before flat/large-result reuse, not a current cost or timing
estimate. Its hit/miss columns describe the original small-prism tier. Flat and large replays now have separate
internal ResultHits, ResultMisses, ResultStores and ResultBypasses counters, while MemoHits and MemoMisses keep
their small-tier meanings. Executed geometry counts exclude work avoided by either tier. All nine coverage
digests and published span/contact counts retain their pre-optimization values. Moving bodies can miss despite
warm tables. See the [current water reuse contract](../../KhaozEngine.TileWorld.Physics/README.md#exact-water-query-reuse)
for lifetimes, bounds and diagnostic meanings. The output golden is not a claim that old kernel counts still execute.

## Frozen differential kernel contract

`Fixtures/FrozenWater` in the physics test project holds the evaluator and complete prism trace frozen from
`85e484e87`, plus the indexed flat evaluator frozen from `1599ccd75` for
[#1756](https://github.com/APKiwiOrg/KhaozEngine/issues/1756). The flat reference owns separate interval arithmetic,
capsule intersection, flat candidate indexes and dispatch bounds. It reads copied column and boundary records and
never calls the production flat evaluator, index selection or numerical helpers. The older pre-index flat
reference also uses the frozen capsule helper. It resets its own per-thread intersection count for each trace,
so brute-force work remains measurable without changing production counters or the guarded helper snapshot.
The older prism dispatch reference also uses the frozen evaluator and a fixture-owned convex evaluation count.
Its legacy seam assertions retain the same before/after solve-count equality.
Prism assembly uses the frozen outward float rounding helpers. For #1755, prism projections now use the same
independent interval arithmetic as the flat oracle. Frozen copies also own ordered rank construction, prism
and face construction, drawn-ground error arithmetic, witness lookup, root uncertainty and bed-seam certification.
The frozen evaluators invoke no live prepared query, preparation storage, geometry terms, projection arithmetic,
root arithmetic or seam arithmetic. The direct solver remains the oracle for both initial and retained
preparations, so their optimizations cannot change both sides together.

Authored moving-shore references rebuild prism and face geometry from captured columns, drawn triangles and
tile coordinates. Synthetic storage and defensive fixtures instead copy their deliberate represented planes
and basis lists, then evaluate those inputs independently. Certified seams are reconstructed from the captured
pair of prism owners. No seam object is borrowed by the numerical oracle. The immutable candidate index and
its path-bounds input, raw record validation, captured ground, the generic drawn-height expression, triangle
normals, lattice addressing and triangulation remain shared input boundaries.
A later change to those helpers must freeze them first. The references recompute the original expressions and
suppress production diagnostic counters. LF-pinned source hashes and a separate resource roster guard every
frozen evaluator and capture adapter. Endpoint conversions only copy bits into the other side's value type.
The hashes preserve deliberate source snapshots, including behavior that might later receive a reviewed correction.

`WaterCoverageComparison` compares every result field and refusal capacity/identity, every span byte and every
contact field, including float sign and NaN payload bits in caller storage. Caller-owned identity and space strings
retain reference identity. The existing exception for freshly allocated joined-triangle boundary IDs remains
value-only. Both written prefixes and the complete sentinel-filled suffixes are compared. Comparator mutations
cover result identifiers, contact identifiers/classifications, span classifications, signed zeros and NaN payloads.

`Category=WaterKernelContract` selects the differential corpus and shape/trace matrix, including Diagnostic
storage boundaries, the 252-sample patch sweeps and all 510 existing bank-lift inputs. These include every trace
over four consecutive authority ticks, stratified samples, translated origins and adjacent floats, with
sentinel-filled caller buffers compared by bits through the entire supplied arrays. Sampler and synthetic
trace-matrix cases run bypassed, cold and repeated warm reads. The seeded corpus also replays in shuffled order,
and memo tests retain their bypassed-sampler oracle plus frozen census checks. Shape windows exercise the kernel
directly below the sampler boundary. The normal CI filter skips Diagnostic cases. Its small corpus instead adds
frozen comparisons to the existing census observations. `ContactQueryCostOracleTests` adds the six cost cases below,
with two fixed-basis operations and 32 advancing ticks per case. Every recorded water query is checked bypassed,
cold and warm, including short-span and short-contact refusals. The collider case must select real statics and the
shore walk must publish results beyond the small memo limits. `WaterQueryCostMutationTests` proves that cold suffix
corruption, warm stale identity, large-output truncation and stale moving output are rejected. These are evaluator
and comparator controls. Cache-key mutation tests require the later cache implementation.

`MovingShoreProjectionOracleTests` checks the bank-along outward/return path and the full shore traverse
through swimming entry and exit. Actual authority outputs and complete bypassed, cold and warm replay buffers
match the frozen reference. Reverse-order replays also check short span and contact refusals under every
supported native FP mode. `WaterProjectionWindowTests` pins both signed zeros through prepared and retained
windows and the FZ subnormal-enter and subnormal-exit regressions. Unsupported native modes are explicit
capability cases, not substitute results from another mode.

`WaterProjectionMutationTests` injects only projected Q, V and multiplier values into test-owned preparation
buffers. Its four controls replay an old drive, projections under a different FP token, a wrong basis ordinal
and a different shape owner. Each first passes the unmodified range comparison, then must fail
`FrozenRangeBitsEqual`, including witness endpoints and certified error. The tests introduce no production fault
seam. They prove propagation sensitivity.

`WaterSharedProjectionTests` covers the production sharing owner. Each real face hits its parent's copied or
borrowed rows with frozen range parity over three windows, copying exactly its mapped bases. A one-ULP change to
any key field, a different control mode or a geometry-identical parent from another capture misses and keeps parity
beside an unchanged-key hit. Its four mutation controls first take a real hit, then show the real key refusing an
old drive, another control token or a stale owner, and finally hand the production evaluator rows that defeat only
that check, or a wrong parent map, which must fail `FrozenRangeBitsEqual`. It also pins reached and resolved rows
only, an unevaluated parent, trace-end forgetting, nested and reentrant fallbacks, a full table and the reserved
payload. Three stale-source tests (ten rows) hold a lent source past the point its storage stopped belonging to it:
through a reentrant trace that retains another parent with the same raw inputs at the same slab offset, past its
trace's end and a later republish into its slot, and past its own settle and another face's lend. Each must copy
nothing, count every basis as stale and keep frozen output. `WaterSharedProjectionCoverageTests` runs whole prism
traces shared and bypassed with frozen parity, overflows the table, measures zero warm allocation shared, exhausted
and bypassed, and collects a discarded capture after a sharing trace. The moving-history oracle also requires the
advancing histories to copy shared projections.

Setting `KHAOZENGINE_WATER_SHARED_PROJECTIONS=bypass` for a contract run forces every test that does not hold the
bypass itself onto the original projection path, through a module initializer both corpus assemblies carry. The moving-history
oracle then requires zero shared copies. The two sharing classes keep sharing on, so a forced-off run still checks
them. Run the corpus with and without it under both tiering settings to compare the paths on identical inputs.

Set `CENSUS_RESULTS` to an absolute results directory outside the checkout before running the contract command.

The corpus spans two test assemblies. Tests that change floating point controls run in
`KhaozEngine.TileWorld.Physics.FloatingPoint.Tests`, in their own process, and a class split between the two keeps its
name in both.

```bash
build-slot --label water-kernel-contract -- dotnet test KhaozEngine.TileWorld.Physics.Tests/KhaozEngine.TileWorld.Physics.Tests.csproj -c Release --filter 'Category=WaterKernelContract' --logger 'trx;LogFileName=water-kernel.trx' --results-directory "$CENSUS_RESULTS"
build-slot --label water-kernel-contract -- dotnet test KhaozEngine.TileWorld.Physics.FloatingPoint.Tests/KhaozEngine.TileWorld.Physics.FloatingPoint.Tests.csproj -c Release --filter 'Category=WaterKernelContract' --logger 'trx;LogFileName=water-kernel-fp.trx' --results-directory "$CENSUS_RESULTS"
python3 scripts/check-water-kernel-trx.py "$CENSUS_RESULTS/water-kernel.trx" "$CENSUS_RESULTS/water-kernel-fp.trx"
```

The independent `water-kernel-contract` CI job builds only these two headless test projects on hosted ubuntu-26.04
x64 and macos-26 arm64. Each job runs the contract with `DOTNET_TieredCompilation=0` and `1` and retains every
TRX file. Its filter excludes `TileWaterPrismCensusTests`, whose pre-existing census digests remain in the
ordinary test slice. The extended corpus still records all nine fixtures over four ticks on each runtime.
The TRX guard takes both assemblies' files for one setting. Each must execute a positive number of tests, execute
its entire selected total and report zero failures, and each assembly must arrive exactly once. The guard also
requires the query-cost oracle, mutation, comparator, frozen-source, projection map and both shared projection
classes, the projection oracle in the floating point assembly, and the window, mutation and shared projection
classes in both. A mistyped filter that runs only earlier tests cannot silently omit the new controls.
Local qualification runs the complete category, including all nine result-bearing census rows.
It runs separately from the selective runner's Foundation/Server pass, so an affected-project selection
cannot omit this matrix. Each execution compares frozen against production on the same runtime. The runtime
gate asserts no single numeric digest across architectures.

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
build-slot --heavy --label water-prism-timing -- env DOTNET_TieredCompilation=0 dotnet KhaozEngine.Benchmarks/bin/Release/net10.0/KhaozEngine.Benchmarks.dll --water-prism
```

One invocation uses 8 warm-up and 96 measured authority operations per fixture, each from the same fixed basis.
This keeps moving cases on their named grade. It is not a trajectory or a load test. Every repeat of a fixed
basis after the first is served by the sampler memo where the result fits it, so a memo-eligible case reads
far faster here than a body that actually moves. The mode requires `DOTNET_TieredCompilation=0` and refuses to
run without it, because unoptimized first-run code otherwise lands inside the measured window. It prints median microseconds
per authority operation and per individual trace, cold-read and Step phases, the sum of water time per operation
and the residual. Phase medians are not additive. Per-trace stopwatches are coarse enough for the millisecond
kernel, but trace and residual measurements include clock overhead. Inner projection phases have no clocks.
The census build refuses to run the timing mode. Shared-machine timings are indicative, with no budget gate.

### Paired shared projection timing

```bash
build-slot --heavy --label shared-projection-timing -- env DOTNET_TieredCompilation=0 dotnet test KhaozEngine.TileWorld.Physics.Tests/KhaozEngine.TileWorld.Physics.Tests.csproj -c Release --filter 'FullyQualifiedName~WaterSharedProjectionPairedDiagnosticTests' --logger 'console;verbosity=normal'
```

`WaterSharedProjectionPairedDiagnosticTests` times the bank-along and shore-traverse advancing histories on four
separate captures, two forced onto the original path and two sharing, so neither path warms the other. Every tick
steps all four from the same state in alternating order and requires bit-identical next states. It prints each
tick and, per history, side means, medians and P10/P90, the mean and median savings and each repeat floor, the larger
relative difference between a path's two identical replicates. A saving is resolved only when it exceeds that floor.
It lives in the test project because the shipped physics assembly grants the benchmark project no friend access
outside the census build, which instruments the timed kernel. It is Diagnostic, refuses the census build at compile
time before any scene or clock, refuses to time with tiering on and asserts no time.

## Contact query cost diagnostic

The benchmark's separate `--query-cost` mode uses the same six-case roster as the exact-output corpus.
Dry standing and walking use `dry-control`. Dry collider walking adds 24 corridor boxes and a registered support
platform under the support disc. Flat open water uses `flat-control`. Shore standing and walking use `bank-stand`,
with the walking command east along the contour. Each controller owns its own world, sampler and simulator and
settles its own basis. Contact uses SurfaceSwimmer and Legacy uses its required default water policy. Their
outputs are not a semantic parity claim.

The mode runs eight warm-up operations, 96 fixed-basis samples and a separate 32-tick advancing history per case.
Contact and Legacy execution order alternates. `--quick` reduces this to one warm-up and two samples per history
for structural smoke checks. No stopwatch runs unless `--timers` is supplied. `--trace-timers` additionally enables
the recording field's per-trace stopwatches and requires `--timers`. `--counters` separately enables the existing
support work capture and reports the combined Contact and Legacy counts. It does not enable timers. Timed runs
require a normal Release build and `DOTNET_TieredCompilation=0`. Raw elapsed samples are report-only. Default CI
does not run this mode or compare elapsed times. Per-trace clocks add overhead and should be left off for whole-step
comparisons. This diagnostic adds no production timer, probe type or cache.

```bash
build-slot --label query-cost-build -- dotnet build KhaozEngine.Benchmarks/KhaozEngine.Benchmarks.csproj -c Release
build-slot --label query-cost-smoke -- dotnet KhaozEngine.Benchmarks/bin/Release/net10.0/KhaozEngine.Benchmarks.dll --query-cost --quick
build-slot --heavy --label query-cost-timing -- env DOTNET_TieredCompilation=0 dotnet KhaozEngine.Benchmarks/bin/Release/net10.0/KhaozEngine.Benchmarks.dll --query-cost --timers
```
