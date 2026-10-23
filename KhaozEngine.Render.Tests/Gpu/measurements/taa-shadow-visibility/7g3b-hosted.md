# Task 7g.3b hosted barrier lock release proof

The DXBC recapture is complete. Hosted correctness is blocked by four lavapipe follow-walk entry-identity colour failures. All eight accepted software cost values are unchanged, so no bound update is needed. The final run has 1721 passed, 4 failed and 1 expected skip on lavapipe, with the other legs green.

The starting guards passed on `fix/taa-shadow-visibility` at `cb2a97e9e`, with a clean tree. Fixture commit `2cf9c4820d737889ed9b2be7e894865e5f373f37` was pushed and its remote SHA verified before the correctness dispatch. Production shaders and hardware accepted-cost rows were unchanged. No timing, perf or bench test ran. No billed workflow, merge, tag or release was dispatched.

## DXBC recapture

Free hosted run [39543431188](https://github.com/APKiwiOrg/KhaozEngine/actions/runs/39543431188), job [119036194789](https://github.com/APKiwiOrg/KhaozEngine/actions/runs/39543431188/job/119036194789), ran on diagnostic branch `fix/taa-vis-dxbc-dump4` at `84ddcae7d6ea5af793b25124f79cf434cd4c7926`. Only the Windows FXC step gained `KE_DUMP_VISIBILITY_DXBC=1`, and the dispatch selected `legs=direct3d11-native` with `FullyQualifiedName~D3D11FxcValidationTests`. Its intentional dump failure was the only failure, with 31 passed, 1 failed and zero skipped. No NuGet retry was needed. The diagnostic branch was deleted locally and remotely, and its worktree was removed.

The runner image was `windows-2025-vs2026 20261007.264.1`. FXC's banner was `Microsoft (R) HLSL Shader Compiler 10.1`. Every numbered dump line was captured through `gh api --allow-escape-sequences`, with contiguous line indices checked and carriage returns removed. Both sides of all six full programs and their GLSL hashes were replaced. The two helper listings and source pins matched the existing fixtures exactly. `StaleUntil7g3b` is empty.

`R` means reassociated `(at - J) + 0.5`, and `W` means written `(at + 0.5) - J`. The in-loop forms are `RR` in both D prime and shipped for every program. Every shipped program has `RRRRWW` in straight-line code. D prime has no straight-line chain. There is no third form. Every one of the fourteen listings has zero `[precise` occurrences.

| Program | D / shipped temps | D / shipped unread positions | D / shipped precise count |
| --- | ---: | ---: | ---: |
| DecisionHelper | 12 / 28 | 0 / 0 | 0 / 0 |
| TemporalAccumulateFrag | 29 / 45 | 10 / 10 | 0 / 0 |
| TemporalAccumulateHalfFrag | 32 / 46 | 10 / 10 | 0 / 0 |
| TemporalAccumulateAtDisplaySizeFrag | 26 / 43 | 10 / 10 | 0 / 0 |
| TemporalAccumulateAtDisplaySizeHalfFrag | 29 / 44 | 10 / 10 | 0 / 0 |
| TemporalResolveFrag | 41 / 47 | 10 / 10 | 0 / 0 |
| TemporalResolveHalfFrag | 42 / 48 | 10 / 10 | 0 / 0 |

The ten unread positions have no `itof` behind them. Six are sample positions, and four are bicubic weights. The unchanged `TheRealProgramPassesTheCheck` passes for all seven programs. New device-free facts require zero precise markings in both listings and matching D in-loop head forms across each fused/split pair, including display-sized variants. The marking mutation now adds a precise marking to a real unmarked head, so the same rule still has a failing mutation on every program.

The zero-precise fact first failed on the six stale full programs, with 6 failed and 5 passed across the new facts. After recapture all 108 DXBC reader, fixture, comparison and mutation cases passed through build-slot. The fast-path binade documentation now states the unmarked full-program contract.

## Hosted correctness selection

Free run [39544536428](https://github.com/APKiwiOrg/KhaozEngine/actions/runs/39544536428) used the fixture commit above, `legs=all`, and this corrected selection. Local discovery through build-slot listed 1,726 cases in 213 classes, with no timing, perf or bench class.

```text
((FullyQualifiedName~KhaozEngine.Tests.Gpu.Temporal&FullyQualifiedName!~CostPerf)|((FullyQualifiedName~Shadow|FullyQualifiedName~Golden)&FullyQualifiedName!~Perf&FullyQualifiedName!~Bench)|FullyQualifiedName~MotionTargetLifecycleGpuTests|FullyQualifiedName~D3D11FxcValidationTests|FullyQualifiedName~TemporalLockReleaseProbe)
```

The run was awaited with `gh run watch --exit-status`, which returned 1. The four walk cases below are the only failures. Each job log was read with `gh api --allow-escape-sequences`. The opt-in dump is the only expected skip on each main leg. The synchronization-validation job passed 622/622 with zero skips.

| Leg | Job | Main passed / failed / skipped | Entry identity | A/B vs D prime | Equivalence | Probe B vs C | FXC step |
| --- | --- | --- | --- | --- | --- | --- | --- |
| WARP | 119039633362 | 1725 / 0 / 1 | 8/8 exact | 12/12 exact | 6/6, DecisionMismatch 0 | 0 mismatches in 4194304 | 31 passed, dump skipped |
| hosted Metal | 119039633406 | 1725 / 0 / 1 | 8/8 exact | 12/12 exact | 6/6, DecisionMismatch 0 | 0 mismatches in 4194304 | Windows only |
| lavapipe | 119039633317 | 1721 / 4 / 1 | 4 moving-shadow passed, 4 walk failed | 12/12 exact | 6/6, DecisionMismatch 0 | 0 mismatches in 4194304 | Windows only |

Metal also matches shipped D exactly in all twelve A/B cases. The probe's P vs B mismatch count is 0 on Metal and 268335 on WARP and lavapipe, as in the approved probe. B equals the CPU twin C on every leg. Lifecycle tests passed 2/2 on every leg. The Linux shader-cache cold, warm and disabled checks each passed their one case.

## Lavapipe entry-identity blocker

The four follow-walk cases fail at `TemporalEntryIdentityGpuTests.Compare`, line 143, in `TemporalHistorySample.Exact`. Each reports a production fused/split colour byte mismatch in two 921600-byte readbacks on `llvmpipe (LLVM 20.1.2, 256 bits)`.

| Case | First failing frame | First differing byte |
| --- | ---: | ---: |
| Performance | 2 | 321841 |
| Quality | 0 | 147045 |
| Native | 0 | 18101 |
| Quality with outline | 0 | 147045 |

The colour assertion precedes the state assertion, so state equality at the failing frame and later frames is not established by these four cases. The four separate moving-shadow entry cases pass exactly. The twelve synthetic A/B cases and all six equivalence cases also pass. The log does not report the walk colour difference's magnitude, and this task does not bisect its introducing change or establish a root cause. Related open [#1501](https://github.com/APKiwiOrg/KhaozEngine/issues/1501) records the earlier entry-identity issue and software-Vulkan colour-exception context. The current exact assertion is retained.

No confirming run is dispatched with these identity failures unfixed. Fixing or relaxing this production contract is outside this task's authorization. No billed workflow ran.

## Accepted costs

All eight accepted software cost values match their old full-precision measurements. No row or bound changed, no 1 percent guard fired, and no hardware row changed. The [owner-accepted cost record](owner-release-only.md#barrier-lock-release-remeasurement-2026-10-21) contains the full old/new table and the raw paired readbacks.

## Local verification

The final local gate ran through build-slot. The earlier focused DXBC red/green cycle is recorded above. An initial manifest invocation omitted its required path argument and was corrected before verification.

| Check | Result |
| --- | --- |
| Release solution build | Exit 0, 0 warnings, 0 errors |
| Device-free DXBC and rule tests | Exit 0, 185 passed, 0 failed, 0 skipped |
| dotnet format --verify-no-changes --no-restore | Exit 0, historical workspace-loading warning |

The final repository guards below gate the evidence commit. The capture manifest also verifies its new artifact entry and the updated owner-record hash.

```text
sh scripts/check-dashes.sh --tree
sh scripts/check-prose.sh --tree
sh scripts/check-file-size.sh --tree
bash scripts/check-doc-versions.sh
python3 KhaozEngine.Render.Tests/Gpu/measurements/taa-shadow-visibility/verify_capture_manifest.py KhaozEngine.Render.Tests/Gpu/measurements/taa-shadow-visibility/manifest.json
git diff --check
```

No manual validation is needed for this task. Integration remains held on the lavapipe identity failures. Hardware NVIDIA correctness and performance were not run under the no-billed-runs boundary.
