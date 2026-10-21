# Task 7g.4 visibility gathers

The Task 7g.4 candidate `fix/taa-vis-gathers`, based on `cb2a97e9e`, is integrated into
`fix/taa-shadow-visibility` by Task 7g.5. No bounds, tolerances, frozen D text, package version or goldens change.
No timing, billed workflow, main merge, tag or release is part of the merge round.

## Merged policy, Task 7g.5

Direct3D 11 and Vulkan use all twelve visibility gathers. Metal retains nine current-block loads and gathers both
history blocks, as in e72569270. Task 7g.5 reverts a43599546. The lavapipe colour failures used to justify the
withdrawal occurred at identical frames and bytes with gathers in 39550935418 and without gathers in 39544536428
and 39554929620. The raw gather oracle had zero mismatches. They are llvmpipe's pre-existing fused versus split
colour divergence, bounded on main by amendment 23, rather than evidence against gathers. The merged r1 restores
that software-Vulkan rule, bound 0.0625 and share 0.0025. Large-frame and moving-shadow comparisons remain exact.
The historical run sections below retain the decisions and results at their original checkpoints.
Task 7g.5 recaptured the seven gathered shipped DXBC listings in free run 39561655851, while every D listing stayed
byte-identical. All fourteen listings have zero precise markings. The existing shader and corpus writers reproduced
the merged tables without drift. Free all-leg run 39571324204 passed 1740 selected tests on each renderer backend,
with one opt-in dump skip each, and 622 sync tests. The five lavapipe colour cases match the no-gather values at
reported precision, and all eight software costs are unchanged at full precision. The [merge-round record](7g5-hosted.md)
owns the current gates and hosted result.

## Footprints and arithmetic

Only the fast path's sources of texel values change. The existing consecutive-base, interior-block and binade
preconditions stay intact. D's helper is still the verbatim slow path. Current-mask acceptance, current addition
order, history acceptance, all 36 history terms and their two explicit `fma` operations per term are unchanged.

For a 2x2 footprint with integer low corner `o`, gather samples `(o + 1) / textureSize(texture, 0)`. This is the
texel corner at the middle of the footprint, half a texel from a footprint-selection boundary. Actual texture
dimensions are used independently for each input. Component order in texture coordinates is `x = (0,1)`,
`y = (1,1)`, `z = (1,0)`, `w = (0,0)`, with row 1 at higher V.

The 4x4 previous-visibility and previous-depth blocks each use four disjoint 2x2 gathers, whose low corners are
`block + (0,0)`, `(2,0)`, `(0,2)`, `(2,2)`. On enabled backends the current 3x3 uses four overlapping 2x2 gathers, whose low corners
are `at0 + (0,0)`, `(1,0)`, `(0,1)`, `(1,1)`. Every returned current texel lies inside the original 3x3, including
when the centre lies one texel from an edge. No extra border rejection or clamp is introduced.

| Visibility path | Before | Candidate |
| --- | --- | --- |
| Fast, complete history, Direct3D 11 | 41 loads | 12 gathers, zero loads |
| Fast, R4 range skip, Direct3D 11 | 25 loads | 8 gathers, zero loads |
| Fast, complete history, Metal | 41 loads | 9 loads and 8 gathers |
| Fast, R4 range skip, Metal | 25 loads | 9 loads and 4 gathers |
| Fast, complete history, Vulkan | 41 loads | 12 gathers, zero loads |
| Fast, R4 range skip, Vulkan | 25 loads | 8 gathers, zero loads |
| R1 zero-lock skip | Zero | Zero |
| D slow path | Up to 101 loads | Up to 101 loads, unchanged |

These are source texture instructions per display pixel inside the visibility decision. A gather returns four
components. The current gathers return sixteen components covering nine unique texels. These counts do not claim
physical memory transactions or a measured speedup. The four depth gathers remain after the R4 skip.

Canonical Direct3D-source SPIR-V contracts require 30 `OpImageFetch`, 12 `OpImageGather` and 14 `OpLoopMerge` in each split second-pass
program. The 30 fetches and fourteen loops belong to the surrounding program and the retained D slow path.
The isolated decision requires seven fetches, twelve gathers and four loops. Metal's selected second-pass program
requires 39 fetches, eight gathers and fourteen loops, its isolated helper sixteen fetches, eight gathers and four
loops. Vulkan matches Direct3D 11 with 30 fetches, twelve gathers and fourteen loops per second-pass program, and seven
fetches, twelve gathers and four loops in the decision helper. Frozen D's contracts stay unchanged.
The new contracts first failed on all five production rows, showing 71/0/14 and 48/0/4 respectively.

## Exact device oracle

`TemporalVisibilityGatherGpuTests.EveryGatherComponentEqualsLoadBits` compares separately compiled gather and
load programs over every interior 2x2 footprint of 2560x1440, 1707x960 and 3342x2160 textures. All four components
are encoded as raw bits in four RGBA8 targets before readback. The load program independently fixes the component
order with literal offsets, and its output is checked against the uploaded texels, so an unwritten fragment or a
wrong footprint cannot pass by matching the other program's mistake.

Each format contributes 50,126,696 component comparisons, 100,253,392 per backend. R8 covers every value from
0 through 255 in every component and compares the exact backend UNORM-to-float bits. R32F includes distinct finite
values, positive and negative zero, both infinities, signed quiet NaNs and signalling payloads. All interior
origins are visited, including footprints touching each edge. The oracle admits no tolerance.

The equivalence, frozen D A/B and entry-identity oracles are unchanged. They additionally exercise the actual
generated component selection, ordered sums, fast-path edge preconditions and slow-path fallback.

## Task 7g.4 verification history

Release solution build passed with zero warnings and errors. The documented writers passed all nine facts and
changed exactly ten temporal fragment rows in each backend table and thirty matching corpus rows. TemporalPrepare,
vertex rows and layout rows are unchanged. The device-free gate passed 165 cases with zero failures and skips,
including the compiled form, generator, frozen D, binade, DXBC reader and mutation, lock release, hash and corpus
checks, with writer variables unset for the verification run.

Broad Metal discovery selected 1,690 cases. The corrected selection excludes Perf and Bench names in both its
temporal and shadow/golden arms and explicitly includes MotionTargetLifecycleGpuTests. No timing class or method
was selected. The initial all-gather Metal candidate completed with 1,676 passed, fourteen failed and zero skipped.
All six raw gather cases passed, with 100,253,392 component comparisons and zero mismatches. All eight entry-identity
cases passed. Six equivalence cases and eight threshold A/B cases failed, while the four random A/B cases passed.
The differences appear after the raw reads, in the subsequent visibility arithmetic. All-load, current-only,
previous-visibility-only, previous-depth-only and both-history-gather variants isolate the rejected substitution.
The five substitution draws each used 262,144 cases. The all-load control, previous-visibility-only,
previous-depth-only and both-history-gather variants had zero decision, change-bit and path mismatches. The
current-only gather variant had 699 decision and 30,874 change-bit mismatches, with zero path mismatches. This
isolates the current substitution as sufficient to trigger the subsequent arithmetic drift, despite its raw values
being exact. The native compiler's precise instruction change is not known.

TemporalVisibilityGatherPolicy therefore retains the original nine current loads on Metal, selected before
SPIR-V compilation. Both history blocks still gather. The policy applies to fused, split, display-size split,
debug and count programs, after test hooks so frozen D remains frozen and path exports share production's choice.
MSL hash and corpus writers apply the same program selection. Direct3D 11 retains all twelve gathers. Vulkan retains loads as described below.
The fallback handles LF and CRLF checkouts. No comparison bound changes.

The final Release solution build passed with zero warnings and errors. The nine shader writer facts passed, then
222 device-free facts passed with writer flags unset. The final broad local Metal run passed all 1,700 selected
cases with zero failures and skips on Apple M2 Max. This includes all six raw gather cases, five substitution
cases, six equivalence cases, twelve frozen D A/B cases, eight entry-identity cases and both motion lifecycle cases.
The twelve A/B cases are byte-identical to both D prime and shipped D in colour, state, R8 and depth. Equivalence
has zero decision, validity, change-bit or reported-path mismatches. The withdrawn current-only candidate remains
a reported diagnostic in the substitution fact, while every enabled substitution is held exact.

| Local gate | Result |
| --- | --- |
| Release solution | Zero warnings, zero errors |
| Shader and corpus writers | 9 passed, zero failed or skipped |
| Device-free verification | 222 passed, zero failed or skipped |
| Broad Metal correctness | 1,700 passed, zero failed or skipped |
| Raw gather bits | 100,253,392 comparisons, zero mismatches |
| Focused fallback proof | 77 passed, zero failed or skipped |

The corrected broad filter is reproduced below. Every command runs through build-slot, with the GPU and shader
bake commands in the heavy lane. Shader tables are written only by the existing documented writers with
KE_WRITE_SHADER_CORPUS=1, KE_UPDATE_MSL_HASHES=1, KE_UPDATE_SPIRV_HASHES=1 and KE_UPDATE_HLSL_HASHES=1. The later
normal verification unsets those flags.

```bash
TAA_CORRECTNESS='((FullyQualifiedName~KhaozEngine.Tests.Gpu.Temporal&FullyQualifiedName!~Perf&FullyQualifiedName!~Bench)|((FullyQualifiedName~Shadow|FullyQualifiedName~Golden)&FullyQualifiedName!~Perf&FullyQualifiedName!~Bench)|FullyQualifiedName~MotionTargetLifecycleGpuTests)'
build-slot --heavy --label taa-vis-gathers-metal -- env KE_GPU_TESTS=1 KE_GRAPHICS_BACKEND=metal-native dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release --no-build --filter "$TAA_CORRECTNESS"
```

## Historical hosted correctness and Vulkan withdrawal

The one authorized free all-leg run is [39550935418](https://github.com/APKiwiOrg/KhaozEngine/actions/runs/39550935418),
source e725692704e86de3f0d952a5df968b63d66aa0aa. Its concurrency group is specific to workflow_dispatch and
refs/heads/fix/taa-vis-gathers. No NuGet failure or retry occurred.

| Device | Selected cases | Raw gather comparison |
| --- | --- | --- |
| Hosted Metal, Apple Paravirtual device | 43 passed, zero failed or skipped | Six cases, 100,253,392 comparisons, zero mismatches |
| WARP, Microsoft Basic Render Driver | 43 passed, zero failed or skipped | Six cases, 100,253,392 comparisons, zero mismatches |
| lavapipe, LLVM 20.1.2 | 39 passed, four failed, zero skipped | Six cases, 100,253,392 comparisons, zero mismatches |

All three devices passed all six helper-equivalence cases and all twelve A/B cases. Metal and WARP passed all
eight entry-identity cases. Lavapipe passed the four moving-shadow entry cases and failed the four real-scene walk
cases in colour. Quality, Native and the outlined Quality case failed on frame 0, before history or the visibility
check ran. Performance failed on frame 2. The Vulkan sync-validation job passed. Windows also passed the real FXC
validation step, without opting into the DXBC dump.

Vulkan gathers are therefore withdrawn at program build. All three source blocks use the original loads on that
backend, including debug and count programs. The selected whole-program SPIR-V is checked against ten independent
pre-gather hashes copied from cb2a97e9e's documented Vulkan table. This preserves the baseline compilation rather
than changing an entry-identity bound. The approved follow-up comparison below establishes that the same colour failures are present in the no-gather baseline. All ten selected modules match those baseline hashes. The focused fallback gate passed 69 cases. The later normal
device-free run passed 236 cases with zero failures or skips, and the nine writers passed before it. A focused Metal
run then passed all 26 equivalence, A/B and entry-identity cases. The selected Metal MSL and Direct3D HLSL hashes
remain the same as the green hosted run. Vulkan's shader table is byte-identical to cb2a97e9e. Its updated fallback was then exercised by the approved Vulkan-only follow-up below.

The initial full format check reported whitespace in two new test files. Those edits were corrected, and the
already-failing scan was stopped after collecting its errors. Changed-file whitespace verification passed with dotnet format whitespace in folder mode. The interrupted initial full-solution format run is not completion evidence. The completed final run is recorded
below. Whole-tree dashes, prose, file-size and doc-version checks passed.
No timing test, billed workflow, stress run, merge, tag or release was run.
T4 hardware proof remains with the owning thread's separately approved work. This worker uses only the free
hosted Metal, WARP and lavapipe correctness matrix.

## Historical DXBC recapture handoff

Thirteen source pins are deliberately stale relative to the inherited fixtures. The six full programs were
already stale on both sides after 7g.3a. Gathers also stale their shipped sides and the shipped decision helper.
The D decision helper remains current. `EachFixtureWasCompiledFromTheCurrentGlsl` requires exactly these stale
sides and prints the post-integration recapture instruction. It still checks the old listings and mutation tests
as reader-logic evidence. No fixture or source hash is recaptured here. The owning thread must recapture after
merging this branch with 7g.3b.


## Historical approved Vulkan-only follow-up

The owner authorized exactly one additional free Vulkan-only workflow_dispatch, with the same filtered selection
as the all-leg run. The branch was verified against the commit resolved with git rev-parse before dispatch.
[Run 39554929620](https://github.com/APKiwiOrg/KhaozEngine/actions/runs/39554929620) checked a435995460cf1798093d336d07d4a3169766027a
on fix/taa-vis-gathers. No dispatch targeted fix/taa-shadow-visibility, and its active baseline run was not cancelled.

The selected render job completed with 38 passed, four failed and zero skipped, out of 42 cases. There is one fewer
case than the first run because the Vulkan policy no longer asserts that it retains the gathered program. The raw
oracle again passed all six cases and 100,253,392 exact component comparisons. Equivalence passed all six cases,
A/B passed all twelve, and the four moving-shadow entry-identity cases passed. The Vulkan sync job passed too.

The four real-scene failures are identical across the no-gather baseline
[39544536428](https://github.com/APKiwiOrg/KhaozEngine/actions/runs/39544536428) at 2cf9c4820,
the all-gather Vulkan candidate 39550935418 at e72569270, and the load-fallback run 39554929620 at a43599546.
Every failure is production fused versus production split colour, with 921,600 compared bytes.

| TemporalEntryIdentityGpuTests case | Frame in all three runs | First differing byte in all three runs |
| --- | ---: | ---: |
| Both_entry_points_write_the_same_history_on_every_frame, Native | 0 | 18101 |
| Both_entry_points_write_the_same_history_on_every_frame, Quality | 0 | 147045 |
| Both_entry_points_write_the_same_history_with_the_edge_outline_on, Quality | 0 | 147045 |
| Both_entry_points_write_the_same_history_on_every_frame, Performance | 2 | 321841 |

The baseline's rendering implementation differs from cb2a97e9e only in a generator XML documentation update.
The case, frame and byte tuples were parsed from each job's log and compared for exact equality. The failures
therefore predate gathers and are not caused by this task. Per the owner's stop condition, no attempt is made to
fix that separate full-program arithmetic issue and no comparison bound is changed. The conservative Vulkan load
fallback remains on this branch. T4 hardware and performance are unverified, and DXBC fixture recapture remains
with the orchestrator after integration.


## Task 7g.4 final full-solution format gate

The required command completed through build-slot with exit code 0 after the Vulkan-only follow-up. It reported
only the workspace-loading warning, with no format errors. The interrupted initial attempt is not used as evidence.

```bash
build-slot --label taa-vis-gathers-full-format-final -- dotnet format KhaozEngine.slnx --verify-no-changes --no-restore
```

Both implementation commits and this evidence are on fix/taa-vis-gathers. There was no merge, tag or release.
The lavapipe full-scene colour failures remain the identical no-gather baseline failures documented above.
