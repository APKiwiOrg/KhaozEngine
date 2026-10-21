# Task 7g.5 merged visibility correctness

The merge round integrates the barrier-release fixtures, r1 entry identity and the visibility gathers. Hosted
correctness passed on all three renderer legs and Vulkan sync validation. No comparison bound, accepted software cost, frozen D text, golden or version changes.

## Integration

The starting branch was fix/taa-shadow-visibility at 7628f382b. The clean-tree and fetched source guards matched
64e1940fb for r1 and 329475cab for gathers.

| Commit | Change |
| --- | --- |
| e2b242b72 | Merge r1, retaining its seam removal, large-frame entry case and amendment 23 software-Vulkan colour rule |
| 50c8f39f7 | Merge the gathers candidate |
| 33bbb69d8 | Restore Vulkan visibility gathers by reverting a43599546 |
| ea9b398c1 | Recapture all seven gathered shipped DXBC listings and GLSL pins |
| 211e42544 | Record the merged policy and completed local gates, the hosted run source |

The r1 merge was conflict-free. The gathers merge conflicted only in TemporalVisibilityDxbcRealProgramTests.cs.
It retains 7g.3b's strict current-source pins rather than the parallel branch's temporary stale-fixture allowance.
Those seven shipped pins failed before recapture, as expected. The renderer entry file merged automatically,
keeping r1's restored comment and the backend-aware gather selection.

The revert conflicted in gathers.md and manifest.json. Both historical evidence sets were retained, with a new
current-policy explanation and refreshed artifact hashes. No generated hash or corpus conflict was hand merged.
All nine existing writers were run after the revert and reproduced the committed tables without drift. The
production gather policy is e72569270's, with all gathers on Direct3D 11 and Vulkan and current-block loads on
Metal. Its fast-path implementation differs from that commit only in the inherited barrier-release XML comment.

## DXBC recapture

The one free scratch run is [39561655851](https://github.com/APKiwiOrg/KhaozEngine/actions/runs/39561655851), on
fix/taa-vis-dxbc-dump5 at 7c0e91fadc773a9221a3ff30d1be74255d2d2237. Only its Windows FXC step gains
KE_DUMP_VISIBILITY_DXBC=1. The dispatch selects direct3d11-native, tier push and
FullyQualifiedName~D3D11FxcValidationTests. Its intentional dump failure exposes all fourteen listings.

The dump job 119094161538 completed with 31 passed, one intentional dump failure and zero skipped. The image is
windows-2025-vs2026 20261007.264.1 and the banner is Microsoft (R) HLSL Shader Compiler 10.1. Numbered lines were
read with gh api --allow-escape-sequences, stripped of carriage returns and checked for contiguous indices. The
initial import detected a missing terminal newline before writing any fixture. Matching the retained 7g.3b
newline convention made all seven D listings byte-identical. Seven shipped listings and their GLSL pins changed.
Every new shipped listing contains twelve gather4 instructions. Every one of the fourteen listings has zero
precise markings. The temporary stale-fixture allowance stays empty.

| Program | D / shipped temps | D / shipped unread positions | D / shipped precise count |
| --- | ---: | ---: | ---: |
| DecisionHelper | 12 / 18 | 0 / 0 | 0 / 0 |
| TemporalAccumulateFrag | 29 / 36 | 10 / 10 | 0 / 0 |
| TemporalAccumulateHalfFrag | 32 / 38 | 10 / 10 | 0 / 0 |
| TemporalAccumulateAtDisplaySizeFrag | 26 / 34 | 10 / 10 | 0 / 0 |
| TemporalAccumulateAtDisplaySizeHalfFrag | 29 / 36 | 10 / 10 | 0 / 0 |
| TemporalResolveFrag | 41 / 46 | 10 / 10 | 0 / 0 |
| TemporalResolveHalfFrag | 42 / 47 | 10 / 10 | 0 / 0 |

All 108 device-free DXBC reader, fixture, comparison and mutation cases pass. D and shipped retain RR in-loop
forms, and shipped retains RRRRWW straight-line forms for every program. No reader or comparison rule is relaxed
for gathers. The seven compiled lock-release facts pass, including twelve barrier ORs in each of the ten
including programs and zero in TemporalPrepare. The dump branch is deleted from origin and locally, and its
worktree is removed after the fixtures were committed.

## Local gates

Every build and test runs synchronously through build-slot, one local command at a time. Writer flags are unset
for normal verification. The focused Metal run uses KE_GPU_TESTS=1 and KE_GRAPHICS_BACKEND=metal-native.

| Gate | Exit and result |
| --- | --- |
| Release solution build | 0, zero warnings and errors |
| Backend hash and corpus writers | 0, 9 passed, zero failed or skipped, no file drift |
| Pre-recapture source-pin check | 1, exactly the seven gathered shipped pins stale |
| Focused Metal correctness | 0, 35 passed, zero failed or skipped |
| Final Release build after fixture updates | 0, zero warnings and errors |
| Device-free temporal, shader and corpus gate | 0, 878 passed, 223 GPU cases skipped with GPU tests off |
| Initial full format scan | 2, thirteen whitespace diagnostics, completed without interruption |
| Final full-solution format verification | 0, completed in 2207.733 seconds, historical workspace-loading warning only |
| Dashes, prose and file size, each with --tree | 0 each |
| Doc versions and git diff --check | 0 each |
| Capture manifest verification | 0, all artifact hashes and historical package/capture identities verified |

The initial full format scan reported thirteen whitespace diagnostics in two test files restored by the revert.
a43599546 had also formatted those lines. Only that whitespace was restored, with identical non-whitespace text
before and after. The initial scan completed with exit 2 after 1410.135 seconds. The final full-solution gate runs on the corrected files.

The Metal selection includes TemporalEntryIdentityGpuTests, TemporalVisibilityResolveAbGpuTests,
TemporalVisibilityEquivalenceGpuTests and TemporalVisibilityGatherGpuTests. No timing case is selected.
The device-free selection is Temporal, ShaderSources, ShippedShader, Spirv and Corpus, plus the Metal MSL and
Direct3D HLSL byte-equality classes, excluding Perf and Bench names. All nine writer checks pass again with writer
variables unset. Discovery of the hosted selection lists 1,389 entries in 214 classes with GPU facts off, with no
timing class selected. Theory rows gated off at discovery expand when GPU tests are on.

## Hosted correctness

The one free all-leg run is [39571324204](https://github.com/APKiwiOrg/KhaozEngine/actions/runs/39571324204), at
pushed commit 211e425449f664065c516b885752c70bdd3f7862. Its source SHA was resolved with git rev-parse and verified against the run's headSha.
The dispatch uses legs all, tier push and bake false. No second dispatch was started. The run was awaited with
gh run watch --exit-status, which returned 0. Its selection is 7g.3b's corrected filter plus the explicit raw gather
class.

```text
((FullyQualifiedName~KhaozEngine.Tests.Gpu.Temporal&FullyQualifiedName!~CostPerf)|((FullyQualifiedName~Shadow|FullyQualifiedName~Golden)&FullyQualifiedName!~Perf&FullyQualifiedName!~Bench)|FullyQualifiedName~MotionTargetLifecycleGpuTests|FullyQualifiedName~D3D11FxcValidationTests|FullyQualifiedName~TemporalLockReleaseProbe|FullyQualifiedName~TemporalVisibilityGatherGpuTests)
```

| Leg | Job | Main passed / failed / skipped | Entry identity | A/B | Equivalence | Raw gather oracle | DXBC facts |
| --- | --- | --- | --- | --- | --- | --- | --- |
| Hosted Metal | 119125998541 | 1740 / 0 / 1 | 10/10 | 12/12 | 6/6 | 6/6 exact | 108/108 |
| WARP | 119125998478 | 1740 / 0 / 1 | 10/10 | 12/12 | 6/6 | 6/6 exact | 108/108 |
| lavapipe | 119125998458 | 1740 / 0 / 1 | 10/10 | 12/12 | 6/6 | 6/6 exact | 108/108 |
| Vulkan sync | 119125998375 | 622 / 0 / 0 | Separate golden and compute selection | | | | |

Each renderer leg's one skip is the opt-in DXBC dump. The Windows FXC step separately passed 31 cases with its
one dump skip. Linux's shader-cache cold, warm and disabled checks each passed their case. Each backend's six raw
readbacks compare 100,253,392 components with zero mismatches. The seventh gather-class fact compiles both oracle
programs for every backend. The lock-release probe also passes on every leg. WARP and lavapipe report B versus C
bit mismatches 0 in 4,194,304 cases, with the historical P versus B count 268335 unchanged.

The sync validation artifact was downloaded by its exact name, vulkan-validation-sync. It contains one
VulkanValidationLogHost producer marker, explicitly arming KE_VULKAN_VALIDATION=sync, and zero warning or error
severity lines. This confirms the clean-log annotation has an armed producer. The artifact was deleted after
reading. Hosted Metal uses the Apple Paravirtual device, WARP the Microsoft Basic Render Driver and lavapipe
llvmpipe (LLVM 20.1.2, 256 bits).

### Lavapipe colour against the no-gather run

The five follow-walk results match run 39559768823 at its reported precision. The current worst column preserves
the test's printed value. The baseline worst column is the four-decimal value supplied for comparison. Main's
restored amendment 23 bound remains 0.0625 and share 0.0025, or 0.25%. State, R8 and depth are exact every frame.
Both large-frame presets have zero colour differences for production and D prime, as well as exact state, R8 and
depth. All four moving-shadow cases have exact colour, confidence, locks, marks, R8, depth and previous jitter.

| Follow-walk case | Current colour worst | Current share | No-gather worst / share |
| --- | ---: | ---: | ---: |
| Native | 0.0073242188 | 0.007% | 0.0073 / 0.007% |
| Quality | 0.017089844 | 0.026% | 0.0171 / 0.026% |
| Performance | 0.043945312 | 0.172% | 0.0439 / 0.172% |
| Outline control | 0.017089844 | 0.026% | 0.0171 / 0.026% |
| Outline on | 0.011230469 | 0.022% | 0.0112 / 0.022% |

### Accepted software costs

All eight values are unchanged at full precision. No bound, baseline or hardware row changed, no separate
remeasurement run was needed and no 1 percent stop guard fired. Grain is the Quality textured grain, tail is the
Native S 8.0 px retreating textured tail, and both rim metrics use Quality M 1.0 leading textured.

| Device | Metric | Accepted value | Merged value | Change |
| --- | --- | ---: | ---: | ---: |
| WARP | grain | 0.008013324991270224 | 0.008013324991270224 | 0% |
| lavapipe | grain | 0.008039903208144449 | 0.008039903208144449 | 0% |
| WARP | tail | 0.007222797931709032 | 0.007222797931709032 | 0% |
| lavapipe | tail | 0.007245504857927269 | 0.007245504857927269 | 0% |
| WARP | rim error | 0.003134108234483462 | 0.003134108234483462 | 0% |
| WARP | rim temporal | 0.004831370554127536 | 0.004831370554127536 | 0% |
| lavapipe | rim error | 0.0030749369871157866 | 0.0030749369871157866 | 0% |
| lavapipe | rim temporal | 0.0047599525602309255 | 0.0047599525602309255 | 0% |

### Handoff

The follow-up evidence commit changes only this record, gathers.md, manifest.json, the spec and the Task 7g plan.
Code, shader tables, corpus and DXBC fixtures remain those of tested commit 211e42544. The historical workspace
loading warning and GitHub's macOS runner-capacity notice are not changed. No unrelated finding was actioned.
No manual validation is needed. The owning orchestrator retains main integration.

No billed workflow, timing, benchmark, stress test, main merge, tag or release is part of this round.
