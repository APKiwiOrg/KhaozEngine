# Task 7g.5 merged visibility correctness

The merge round integrates the barrier-release fixtures, r1 entry identity and the visibility gathers. Hosted
correctness is pending. No comparison bound, accepted software cost, frozen D text, golden or version changes.

## Integration

The starting branch was fix/taa-shadow-visibility at 7628f382b. The clean-tree and fetched source guards matched
64e1940fb for r1 and 329475cab for gathers.

| Commit | Change |
| --- | --- |
| e2b242b72 | Merge r1, retaining its seam removal, large-frame entry case and amendment 23 software-Vulkan colour rule |
| 50c8f39f7 | Merge the gathers candidate |
| 33bbb69d8 | Restore Vulkan visibility gathers by reverting a43599546 |
| ea9b398c1 | Recapture all seven gathered shipped DXBC listings and GLSL pins |

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

One free all-leg run on the pushed integrated candidate is pending. Its selection is 7g.3b's corrected filter plus
the explicit raw gather class.

```text
((FullyQualifiedName~KhaozEngine.Tests.Gpu.Temporal&FullyQualifiedName!~CostPerf)|((FullyQualifiedName~Shadow|FullyQualifiedName~Golden)&FullyQualifiedName!~Perf&FullyQualifiedName!~Bench)|FullyQualifiedName~MotionTargetLifecycleGpuTests|FullyQualifiedName~D3D11FxcValidationTests|FullyQualifiedName~TemporalLockReleaseProbe|FullyQualifiedName~TemporalVisibilityGatherGpuTests)
```

Lavapipe follow-walk colour is held to the restored bound 0.0625 and share 0.0025. State, R8 and depth stay exact.
The no-gather comparison values from 39559768823 are Native 0.0073 / 0.007%, Quality 0.0171 / 0.026%,
Performance 0.0439 / 0.172%, outline control 0.0171 / 0.026% and outline on 0.0112 / 0.022%.
Large-frame and moving-shadow cases must be exact. The eight accepted software values are checked against the
full-precision table in owner-release-only.md. No remeasurement is authorized unless one changes, and a worsening
over 1 percent stops the round.

No billed workflow, timing, benchmark, stress test, main merge, tag or release is part of this round.
