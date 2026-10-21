# Task 7g.4 visibility gathers

Candidate branch `fix/taa-vis-gathers`, based on `cb2a97e9e`. This branch is separate from the owning
`fix/taa-shadow-visibility` worktree. No bounds, tolerances, frozen D text, package version or golden changes.
No timing, billed workflow, merge, tag or release is part of this task.

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
| Fast, complete history, Direct3D 11 and Vulkan | 41 loads | 12 gathers, zero loads |
| Fast, R4 range skip, Direct3D 11 and Vulkan | 25 loads | 8 gathers, zero loads |
| Fast, complete history, Metal | 41 loads | 9 loads and 8 gathers |
| Fast, R4 range skip, Metal | 25 loads | 9 loads and 4 gathers |
| R1 zero-lock skip | Zero | Zero |
| D slow path | Up to 101 loads | Up to 101 loads, unchanged |

These are source texture instructions per display pixel inside the visibility decision. A gather returns four
components. The current gathers return sixteen components covering nine unique texels. These counts do not claim
physical memory transactions or a measured speedup. The four depth gathers remain after the R4 skip.

Static SPIR-V contracts require 30 `OpImageFetch`, 12 `OpImageGather` and 14 `OpLoopMerge` in each split second-pass
program. The 30 fetches and fourteen loops belong to the surrounding program and the retained D slow path.
The isolated decision requires seven fetches, twelve gathers and four loops. Metal's selected second-pass program
requires 39 fetches, eight gathers and fourteen loops, its isolated helper sixteen fetches, eight gathers and four
loops. Frozen D's contracts stay unchanged.
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

## Verification status

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
MSL hash and corpus writers apply the same program selection. Direct3D 11 and Vulkan retain all twelve gathers.
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

Hosted correctness and formatting verification are pending.
T4 hardware proof remains with the owning thread's separately approved work. This worker uses only the free
hosted Metal, WARP and lavapipe correctness matrix.

## DXBC recapture handoff

Thirteen source pins are deliberately stale relative to the inherited fixtures. The six full programs were
already stale on both sides after 7g.3a. Gathers also stale their shipped sides and the shipped decision helper.
The D decision helper remains current. `EachFixtureWasCompiledFromTheCurrentGlsl` requires exactly these stale
sides and prints the post-integration recapture instruction. It still checks the old listings and mutation tests
as reader-logic evidence. No fixture or source hash is recaptured here. The owning thread must recapture after
merging this branch with 7g.3b.
