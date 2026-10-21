# Task 7h K, certified fully lit specialization

**Status:** Round 3 is in progress under the owner's 2026-10-22 device ruling. Rounds 1 and 2 below are
historical strict-oracle failures. Round 3 integrates Z's conditional positive-zero bypass and verifies K under
the owner-approved rules.

Source branch fix/taa-vis-lit-skip starts at 6c0382161. This record covers correctness only. No timing,
benchmark, stress, merge, tag or release is part of the task.

## Certificate and compiled programs

ModelRenderer snapshots the strength in the actual frame upload shared by rigid models, GPU and CPU skinning,
foliage, splat terrain and tile ground. Scene3D supplies that evidence after all writers complete. Nonpositive
strength takes sampleKeyShadow's exact-one return. White clear, discard and destination preservation, plus MSAA
resolve, retain exact R8 255. Active cached static shadows do not certify. Synthetic supplied textures stay unknown.
The owned null-input all-white texture certifies directly.

TemporalVisibilityHistory stores one certificate per index, only after its R8 CopyTexture is recorded. Its existing
owner, device, generation, readiness and once-per-resolve-frame gates apply. The renderer latches the read-index
certificate beside previous jitter before the write is published. Both current and read certificates are required.
On-to-off and off-to-on frames run the ordinary check whenever either certificate is absent.

The receiver snapshot is ModelRenderer.FrameUbo.cs line 61. Scene3D.TemporalResolve.cs line 230 supplies it after
all writers. TemporalVisibilityHistory.cs line 23 gates the actual read index, and lines 68 and 69 publish only
after CopyTexture. TemporalResolveRenderer.Visibility.cs line 59 latches the read certificate. Selection is
TemporalResolveRenderer.cs line 207. TemporalVisibilityPipelinePair.cs line 15 owns both cached programs and their
matching layouts, including partial-construction cleanup.

TemporalVisibilityPipelinePair builds checked and certified shaders together. The certified source makes only the
visibility release false in the shared accumulation. The compiler removes both decision helpers and footprint loads.
The fused and debug/count core uses nine bindings instead of twelve. Split accumulation uses twelve instead of
fifteen. Both layouts and both read-index sets are cached and follow the existing retirement owner and generation.
Native and both precision forms are covered. Test-only fragment rewrites can independently restore the ordinary
check, with its matching full layout, without changing production selection.

All R8 resources and stores remain. Devices below five colour attachments retain their existing fallback. Colour
shading release, carried-lock classification, ordered lock release, hold and clipping stay intact. Shadowed and
uncertified frames retain the ordinary check. No performance budget is claimed here.

## Local evidence

Every local build and test ran through build-slot, one command at a time. GPU facts were off for the device-free
selection. The Metal gate used KE_GPU_TESTS=1 and KE_GRAPHICS_BACKEND=metal-native. Writer flags were unset for
normal verification. No timing or stress test was selected.

| Gate | Exit and result |
| --- | --- |
| Certificate test before implementation | 1, two expected selection failures |
| Constructor cleanup test before the fix | 1, both created shaders remained undisposed |
| Final Release solution build | 0, zero warnings and errors |
| Hash and corpus writers | 1, seven passed, two stale catalog-count assertions failed, then corrected from 111 to 121 programs and 230 to 250 stage rows |
| Normal hash and corpus facts | All nine facts passed inside the device-free gate |
| Device-free Temporal, ShaderSources, ShippedShader, Spirv, Corpus and Allocation selection | 1, 976 passed, one out-of-scope allocation failure, 237 GPU cases skipped with GPU facts off |
| Complete focused Metal selection | 0, 161 passed, zero failed or skipped |
| Targeted whitespace application | 0 |
| Full solution format verification | 0, completed in 177.544 seconds, historical workspace-loading warning only |
| Dashes, prose and file size, each with --tree | 0 each, file-size scan completed in 679.044 seconds |
| Doc versions, git diff --check and capture manifest | 0 each |

The device-free failure is
`KhaozEngine.Tests.Windowing.PointerGestureTapToleranceAllocationTests.HeldTolerancePressAllocatesNothing`.
It reports 88 bytes against zero on both the initial broad run and the post-fix run. Its test and production code
are unchanged. It was not rerun to seek a pass and was not fixed in this scoped task. The initial broad run also
had ten stale single-variant expectations, including catalog counts, set retirement counts, selected shader names
and the now-unbound 16-byte buffer. Those assertions now cover the checked and certified forms while keeping their
ordering, lifetime and byte contracts. All selected temporal allocation cases pass.

The first GPU attempt exposed the compact binding requirement. Metal rejected a no-check program reflecting nine
resources against a twelve-element layout. Separate full and compact layouts and cached sets repaired the cause.
The focused Metal gate includes 10 entry-identity cases, 12 frozen D A/B cases, six equivalence cases, seven gather
cases, 39 lit-writer cases, eight visibility-history cases and the temporal steady-allocation facts. The optional
TemporalResolveCostPathShareGpuTests class is absent at this source.

The four repo writers added 20 rows to each backend hash table and 80 corpus rows. Every existing row is unchanged,
and none was removed. All ten certified modules cross-compile to MSL and HLSL without either visibility texture,
either decision helper or stored00 footprint code. Their SPIR-V has no OpImageGather. The lock-release compiled
facts now cover all twenty checked and certified includers, each retaining twelve ordered barrier operations.
Original checked GLSL and the fourteen DXBC fixtures are unchanged. The new forms contain no visibility position
chains, so no new DXBC association listing or scratch dump dispatch is needed. Hosted FXC still validates every
shipped program, including the new forms.

## New tests

The new tests contribute 53 cases, all passing in the complete Metal selection.

| Class and method | Cases | Contract |
| --- | ---: | --- |
| TemporalVisibilityFullyLitTests.ReceiverEvidenceRequiresBothCurrentAndReadIndexToBeFullyLit | 2 | Both entries select from current and read-index evidence, including both transitions and zero strength |
| TemporalVisibilityFullyLitTests.SyntheticWhiteInputIsUnknownAndOnlyTheOwnedFallbackCertifies | 2 | Supplied input defaults to unknown, only the owned fallback certifies |
| TemporalVisibilityFullyLitTests.CertificatesPublishWithTheCopyAndCannotLeakAcrossFramesResetsOwnersOrDevices | 1 | Write index, once-per-frame publication, reset, resize, owner and device metadata |
| TemporalVisibilityFullyLitTests.ResolveDebugAndCountUseCachedProgramsForTheSameCertificate | 2 | Actual recorded pipelines agree, switching certificates creates no new shaders or sets after warm-up |
| TemporalVisibilityFullyLitTests.AFailedVariantConstructionReleasesTheShadersItAlreadyCreated | 1 | Partial construction releases returned resources, RED to GREEN |
| TemporalVisibilityFullyLitCompiledTests.CertifiedModulesHaveNoVisibilityReadsDecisionHelperOrGathers | 10 | Fused, split, Native, debug/count and both precision modules have no compiled decision footprint |
| TemporalVisibilityFullyLitGpuTests.CertifiedScenesMatchTheCheckAndBothEntriesOnEveryHistoryByte | 6 | Boxes, field and outlined field at Native and Quality, 24 lockstep frames, raw colour, confidence, lock, mark, R8 and depth bytes |
| TemporalVisibilityFullyLitGpuTests.DebugViewsAndCountReevaluationMatchTheForcedCheck | 4 | Every debug view and count readback through both entries and presets, including transitions |
| TemporalVisibilityFullyLitGpuTests.OnlyTheOwnedFallbackCanCertifyAnAllWhiteSyntheticResolve | 4 | Actual R8 white and zero uploads stay unknown, the owned all-white input certifies and stores exact bytes |
| TemporalVisibilityCertificateGpuTests.EveryOpaqueFamilyCertifiesOnlyItsActualFullyLitReceiver | 6 | Rigid, GPU/CPU skinning, foliage, splat and tile ground, cached atlas, zero strength and both transitions against R8 readback |
| TemporalVisibilityCertificateGpuTests.PreserversAndDiscardsKeepTheFullyLitCertificate | 11 | Transparent, additive, cutout and dissolve preservation against R8 readback |
| TemporalVisibilityCertificateGpuTests.MsaaResolvesTheCertifiedReceiverToExactWhite | 1 | Every supported MSAA sample count explicitly resolves to exact R8 255 |
| TemporalVisibilityCertificateGpuTests.RepeatedRenderAndEntryChangeKeepTheCertificateOfTheStoredReadIndex | 1 | A later render with shadows cannot change the stored certificate, entry changes and camera reset keep the gates |
| TemporalVisibilityCertificateGpuTests.SharedSceneCertificateMatchesAFreshSceneAfterLayoutChanges | 1 | Aged scene metadata and visibility match a fresh scene |
| TemporalVisibilityCertificateGpuTests.ResetResizeOwnerAndDeviceChangesRequireAFreshCopiedCertificate | 1 | Real-device copies certify only after each reset, resize, owner or device transition, with exact white history readback |

`ForceCheck` is test-only. It restores the ordinary visibility call in the selected specialization through the
existing fragment rewrite hook. The no-check and check programs compile independently and run in lockstep.
Round 1 asserted every raw history byte on every backend. Round 2 retains that requirement for certified versus
ordinary check, and applies the existing software Vulkan colour rule only between the production entries.

## Hosted correctness

The single all-leg correctness run is [39627795369](https://github.com/APKiwiOrg/KhaozEngine/actions/runs/39627795369),
at d140310d950df04093880d2e3c302e84f32c8cfc. The SHA was resolved with git rev-parse, checked against the remote
branch before dispatch and verified against the run's headSha. It uses legs all, tier push and bake false. The selection is 7g.5's filter plus the four new
classes. No timing class, billed workflow or stress test is selected.

```text
((FullyQualifiedName~KhaozEngine.Tests.Gpu.Temporal&FullyQualifiedName!~CostPerf)|((FullyQualifiedName~Shadow|FullyQualifiedName~Golden)&FullyQualifiedName!~Perf&FullyQualifiedName!~Bench)|FullyQualifiedName~MotionTargetLifecycleGpuTests|FullyQualifiedName~D3D11FxcValidationTests|FullyQualifiedName~TemporalLockReleaseProbe|FullyQualifiedName~TemporalVisibilityGatherGpuTests|FullyQualifiedName~TemporalVisibilityFullyLitTests|FullyQualifiedName~TemporalVisibilityFullyLitCompiledTests|FullyQualifiedName~TemporalVisibilityFullyLitGpuTests|FullyQualifiedName~TemporalVisibilityCertificateGpuTests)
```


| Leg | Job | Passed / failed / skipped | New cases |
| --- | --- | --- | --- |
| Hosted Metal, Apple Paravirtual device | 119317681651 | 1793 / 0 / 1 | 53 / 0 |
| WARP, Microsoft Basic Render Driver | 119317681549 | 1789 / 4 / 1 | 49 / 4 |
| lavapipe, LLVM 20.1.2, 256 bits | 119317681783 | 1788 / 5 / 1 | 48 / 5 |
| Vulkan sync validation | 119317681712 | 622 / 0 / 0 | Not selected |

Each renderer has one intentional opt-in DumpVisibilityDisassembly skip. All new cases execute. Metal's job
completed in 18m37s, WARP in 27m58s and lavapipe in 2h11m6s. The run was awaited with gh run watch --exit-status,
which returned 1. The final run status is completed/failure at the same verified source SHA. Only one dispatch was
started and no job was rerun. The conditional full-suite placeholder job is not selected by this filtered dispatch.

On every renderer the existing entry-identity cases pass 10/10, frozen D A/B 12/12, equivalence 6/6, gather 7/7 and
DXBC facts 108/108. FXC validation completes on Windows. The existing software-Vulkan colour rule stays confined
to its existing entry cases. All 21 certificate cases, all ten no-check module cases and all eight headless
policy/lifetime cases pass on every renderer. All nine hosted failures are in the new strict scene oracle.

### WARP blocks exactness

CertifiedScenesMatchTheCheckAndBothEntriesOnEveryHistoryByte fails four cases. Scene 1 is the field and scene 2
is the outlined field. Both presets use a 320 by 180 display, so each colour history comparison has 460800 bytes.

| Preset | Scene | First frame | First differing byte | Comparison |
| --- | --- | ---: | ---: | --- |
| Native | Field | 8 | 5084 | Fused certified versus ordinary check |
| Native | Outlined field | 8 | 5084 | Fused certified versus ordinary check |
| Quality | Field | 3 | 1938 | Fused certified versus ordinary check |
| Quality | Outlined field | 3 | 1938 | Fused certified versus ordinary check |

Every fixture's resolved R8 is asserted to be 255 and its current and read-index policy is asserted before these
comparisons. The failure is a colour byte difference, not a failed certificate assertion. The root cause is not
yet diagnosed. The fact stops at its first colour assertion, so later channels and fused/split comparisons on the
failing frame are not proved. The boxes cases, debug/count comparisons and synthetic/fallback cases pass.
No comparison bound, software exception, frozen D text or visibility decision is changed to accept this result.
The candidate is blocked and must not be integrated as satisfying Task 7h K. Further diagnosis and a new exact-head
GPU proof are needed beyond the single correctness dispatch authorized for this brief. No second dispatch, job
rerun, optional DXBC dump, timing, benchmark or stress run was started.

### Lavapipe blocks strict entry identity

The same scene oracle fails five cases on VulkanNative llvmpipe (LLVM 20.1.2, 256 bits). The comparison labelled
certified fused versus split is strict on this task's new facts. No legacy software colour tolerance is applied.

| Preset | Scene | First frame | First differing byte | Comparison |
| --- | --- | ---: | ---: | --- |
| Quality | Boxes | 3 | 179524 | Fused versus split colour |
| Native | Field | 0 | 220664 | Fused versus split colour |
| Native | Outlined field | 0 | 220664 | Fused versus split colour |
| Quality | Field | 0 | 276404 | Fused versus split colour |
| Quality | Outlined field | 0 | 276404 | Fused versus split colour |

Each comparison has 460800 colour bytes. The four frame-zero cases run the ordinary checked programs because
the read-index certificate is not yet seeded. The no-check program is not selected on those failing frames. The
Quality boxes failure is on a selected no-check frame. Both preceding per-entry comparisons against their own
ordinary-check reference pass on that frame before fused/split colour fails. This establishes the failing
comparison and selected policy, not its root cause. Later channels on the failing comparison are not proved.
The Native boxes case, debug/count cases and synthetic/fallback cases pass. The old entry-identity facts retain
their existing software rule and pass. This task's new byte-exact requirement remains unmet, so no exception,
tolerance or code workaround is added.

All three renderer logs and the sync-validation log were read. The source code is unchanged from d140310d9.
The implementation is left on the named branch as a blocked candidate with reproducible failing oracles. The
orchestrator needs further diagnosis of the WARP colour drift and the lavapipe strict entry differences, followed
by a newly authorized exact-head GPU proof before integration. No merge, tag or release was performed.

### Not changed

The pointer gesture allocation failure above is outside this task and has not been investigated. The public Cost
paragraph in docs/USING-KHAOZENGINE.md also still says incremental visibility cost has not been measured, although
the parent branch now holds T4 measurements. Its reconciliation is left to the parent closeout. The new certificate
paragraph makes no timing claim.

## Round 2 diagnosis

The oracle change is bdc306bb5637742562f523e6788bda9f27b59bd9. `TemporalFullyLitHistoryOracle` reads each history once
per frame, reports all three pairs for all 24 frames, then asserts. Each channel reports differing values, compared
values, share, maximum absolute error and maximum half steps. Its first three examples include frame, texel,
component, both values and raw storage bits. Confidence and the stored lock are separate. The mark is decoded from
the lock using the existing entry-identity mark classes. The raw lock comparison still covers every stored bit.

Certified fused versus checked fused and certified split versus checked split remain byte-exact on every device.
Production fused versus split also remains exact except for colour on `TemporalDeviceClass.IsLavapipe`. That pair
alone uses `TemporalSoftwareVulkanColourRule`, as the existing entry-identity facts do. Its maximum absolute error
is 0.0625 and its maximum differing share is 0.0025 over the whole walk. Non-finite differences fail. Confidence,
lock, mark, R8 and depth remain exact there too. Reporting and the final verdict are separate.

### Round 2 local gates

All builds, tests, shader generation and repository checks used build-slot. Builds and test runs were serial.
Two initial text scans briefly overlapped, then both were rerun serially after staging the new helper.

| Gate | Exit and result |
| --- | --- |
| Release solution build | 0, zero warnings and errors |
| Focused Metal GPU and existing contracts | 0, 149 passed, zero failed or skipped, Apple M2 Max |
| Device-free Temporal, ShaderSources, ShippedShader, Spirv and Corpus | 0, 898 passed, 256 GPU cases skipped |
| Changed-file format application | 0 |
| Full solution format verification to completion | 0, historical workspace-loading warning only, no Contour errors |
| Dashes and prose, serial with --tree | 0 |
| Whole-tree file size, plus the new helper checked separately | 0 each |
| Doc versions | 0 |
| Capture manifest verifier | 0, historical sources, packages and capture hashes preserved |
| Staged and unstaged git diff --check | 0 each |

Metal has zero differences in all six channels of every scene pair. No test loop, timing, benchmark or stress test
was run. The device-free filter does not select the known PointerGestureTapToleranceAllocationTests failure #1529.

### Generated HLSL colour path

The local scratch tool enumerated `ShippedShaderPrograms.GraphicsPrograms`, compiled both stages with
`SpirvFrontEnd.ToSpirv`, then called `SpirvCrossCompile.VertexFragmentToHlsl`. It generated the full-precision fused,
split Quality and split Native pairs. The first scratch invocation lacked the bundled native libraries and exited
134. Supplying the test output's existing macOS native libraries repaired that scratch setup, and generation exited
0. No production seam or production source changed.

| Program | Generated fragment HLSL SHA-256 |
| --- | --- |
| TemporalResolve | 5120b488fad79b19a9ff07cea6c8f7c8b46423601ce89d5b2c0ea28f380bee05 |
| TemporalResolveFullyLit | a5cc9e825e9cca9f44bafd17fa6c64e27cc5333e3121e6cc7ac63d8ad2dd10b9 |
| TemporalAccumulate | 4e99f769fee0f5dfc193afd3e276fde34e8f630d9735b5a616bdc7ac08b1251d |
| TemporalAccumulateFullyLit | fadbc20708dbe99eaa675407e48489d8a620fe30cc3327f81282231817594d45 |
| TemporalAccumulateAtDisplaySize | 0db60866a663eee80253eeb73146839bb99624dda78bc60f7a455136f5478b2b |
| TemporalAccumulateAtDisplaySizeFullyLit | 2227eda2ef33bc8fdf2f9bf200d9697b69b11ba6eb55ec059f8655509282e417 |

For example, the checked fused HLSL computes `_16164 = _8719 ? 0.0f : _10073`, where `_8719` is the visibility
release, then its hold from `(_15034 || _8719) ? 0.0f : clamp(_16164 * 2.0f, 0.0f, 1.0f)`. The certified HLSL
retains the same barrier-ordered lock result, with no visibility zeroing, and computes its hold from
`(_9985 || false) ? 0.0f : clamp(_8073 * 2.0f, 0.0f, 1.0f)`. `_15034` and `_9985` are the unchanged shading release.
The subsequent luma clipping uses the same subtraction, maximum, division and `lerp` expressions in both programs.
The clipped-luma blend is `_13373.x = lerp(_15073.x, _12261, _16165)` versus
`_9061.x = lerp(_10024.x, _8099, _10752)`. The final weighted-colour blend is likewise the same three-component
`lerp`, followed by the same YCoCg conversion and weighted division.

These colour expressions have no precise qualifier or arithmetic barrier. Removing the visibility dependency
allows FXC's existing optimization-level-3 compile to simplify the hold feeding the clipped-luma `lerp` and reorder
or contract its multiply/add arithmetic. This identifies an unchanged colour operation exposed to specialization,
not a measured instruction-level DXBC bisect. The shader's explicit lock arithmetic barriers are retained.

### Round 2 hosted gate

The one authorized run is [39652578932](https://github.com/APKiwiOrg/KhaozEngine/actions/runs/39652578932), at
bdc306bb5637742562f523e6788bda9f27b59bd9. The local full SHA, remote branch tip and run headSha agree. It uses
legs all, tier push and bake false, with this filter only. The workflow also retains its own compiler and sync legs.

```text
FullyQualifiedName~TemporalVisibilityFullyLit|FullyQualifiedName~TemporalVisibilityCertificateGpuTests|FullyQualifiedName~TemporalEntryIdentityGpuTests
```

| Leg | Job | Passed / failed / skipped |
| --- | --- | --- |
| Hosted Metal, Apple Paravirtual device | 119404536470 | 63 / 0 / 0 |
| WARP, Microsoft Basic Render Driver | 119404536201 | 59 / 4 / 0 |
| lavapipe, LLVM 20.1.2, 256 bits | 119404535915 | 63 / 0 / 0 |
| Vulkan sync validation | 119404536175 | 622 / 0 / 0 |

The Windows compiler and struct-layout step separately passed 31 cases with one intentional opt-in dump skip.
All other steps on that job passed. Its sole failed step was the filtered test suite. The workflow completed
with failure because of the four strict WARP scene cases. Every selected entry-identity case passed.

Each rendering device supplied 2592 unique frame/channel reports and 108 aggregate channel reports for the six
scene cases. WARP prints a failed case twice. The two copies agree and the tables below count each case once.
Colour and state are display-sized. R8 and depth are internal-sized, so their denominators are smaller at Quality.

### WARP per-channel totals

| Pair | Channel | Differing / compared values | Worst absolute difference | Max half steps | Differing share |
| --- | --- | ---: | ---: | ---: | ---: |
| fused certified vs check | colour | 88 / 33177600 | 0.011230469 | 23 | 0.000265239198% |
| fused certified vs check | confidence | 18 / 8294400 | 0.00024414062 | 1 | 0.000217013889% |
| fused certified vs check | lock | 0 / 8294400 | 0 | 0 | 0% |
| fused certified vs check | mark | 0 / 8294400 | 0 | 0 | 0% |
| fused certified vs check | R8 | 0 / 5987520 | 0 | 0 | 0% |
| fused certified vs check | depth | 0 / 5987520 | 0 | 0 | 0% |
| split certified vs check | colour | 88 / 33177600 | 0.011230469 | 23 | 0.000265239198% |
| split certified vs check | confidence | 18 / 8294400 | 0.00024414062 | 1 | 0.000217013889% |
| split certified vs check | lock | 0 / 8294400 | 0 | 0 | 0% |
| split certified vs check | mark | 0 / 8294400 | 0 | 0 | 0% |
| split certified vs check | R8 | 0 / 5987520 | 0 | 0 | 0% |
| split certified vs check | depth | 0 / 5987520 | 0 | 0 | 0% |
| certified fused vs split | colour | 0 / 33177600 | 0 | 0 | 0% |
| certified fused vs split | confidence | 0 / 8294400 | 0 | 0 | 0% |
| certified fused vs split | lock | 0 / 8294400 | 0 | 0 | 0% |
| certified fused vs split | mark | 0 / 8294400 | 0 | 0 | 0% |
| certified fused vs split | R8 | 0 / 5987520 | 0 | 0 | 0% |
| certified fused vs split | depth | 0 / 5987520 | 0 | 0 | 0% |

### lavapipe per-channel totals

| Pair | Channel | Differing / compared values | Worst absolute difference | Max half steps | Differing share |
| --- | --- | ---: | ---: | ---: | ---: |
| fused certified vs check | colour | 0 / 33177600 | 0 | 0 | 0% |
| fused certified vs check | confidence | 0 / 8294400 | 0 | 0 | 0% |
| fused certified vs check | lock | 0 / 8294400 | 0 | 0 | 0% |
| fused certified vs check | mark | 0 / 8294400 | 0 | 0 | 0% |
| fused certified vs check | R8 | 0 / 5987520 | 0 | 0 | 0% |
| fused certified vs check | depth | 0 / 5987520 | 0 | 0 | 0% |
| split certified vs check | colour | 0 / 33177600 | 0 | 0 | 0% |
| split certified vs check | confidence | 0 / 8294400 | 0 | 0 | 0% |
| split certified vs check | lock | 0 / 8294400 | 0 | 0 | 0% |
| split certified vs check | mark | 0 / 8294400 | 0 | 0 | 0% |
| split certified vs check | R8 | 0 / 5987520 | 0 | 0 | 0% |
| split certified vs check | depth | 0 / 5987520 | 0 | 0 | 0% |
| certified fused vs split | colour | 96 / 33177600 | 0.0009765625 | 2 | 0.000289351852% |
| certified fused vs split | confidence | 0 / 8294400 | 0 | 0 | 0% |
| certified fused vs split | lock | 0 / 8294400 | 0 | 0 | 0% |
| certified fused vs split | mark | 0 / 8294400 | 0 | 0 | 0% |
| certified fused vs split | R8 | 0 / 5987520 | 0 | 0 | 0% |
| certified fused vs split | depth | 0 / 5987520 | 0 | 0 | 0% |

### Per-case colour and confidence

Every row compares 5529600 colour values and 1382400 confidence values. WARP has the same differences in the fused
and split certified-versus-check comparisons. Its production entries match each other exactly. On lavapipe both
certified-versus-check pairs match exactly, and only the production-entry colour comparison differs.

| Preset | Scene | WARP colour count | WARP max colour error | WARP max half steps | WARP confidence count | lavapipe entry colour count | lavapipe max colour error |
| --- | --- | ---: | ---: | ---: | ---: | ---: | ---: |
| Native | Boxes | 0 | 0 | 0 | 0 | 0 | 0 |
| Native | Field | 14 | 0.0048828125 | 10 | 0 | 21 | 0.0009765625 |
| Native | Outlined field | 14 | 0.0048828125 | 10 | 0 | 21 | 0.0009765625 |
| Quality | Boxes | 0 | 0 | 0 | 0 | 20 | 0.00048828125 |
| Quality | Field | 30 | 0.011230469 | 23 | 9 | 17 | 0.00048828125 |
| Quality | Outlined field | 30 | 0.011230469 | 23 | 9 | 17 | 0.00048828125 |

### Verdict and first differing texels

The WARP diagnosis remains unresolved. No production defect in (a) has been established, and the full walk does not
establish (b) under the brief's required few-ulp, identical-decision, unstructured-colour signature. Compiler
specialization remains a plausible explanation, including for confidence arithmetic, but it is not proved by the
generated HLSL or by matching lock bytes. No production fix or new device exception was added.

The certificate and selection checks pass. Every current R8 sample is 255, frame 0 selects the ordinary check,
and frames 1 through 23 select the certified form only with both certificates present. Lock, mark, R8 and depth
match in every pair on every frame. Both production entries also match each other on every history byte on WARP.
That narrows the problem to the shared accumulation as specialized against its checked form. It does not establish
that every intermediate hold, clip, shading-release or moving-share decision is identical.

| WARP example, either field case and either entry | First value and bits | Checked value and bits | Half steps |
| --- | --- | --- | ---: |
| Native frame 8, texel (315,1), blue | 0.4650879, 0x3771 | 0.46484375, 0x3770 | 1 |
| Quality frame 3, texel (242,0), green | 0.86621094, 0x3AEE | 0.86572266, 0x3AED | 1 |
| Quality frame 6, texel (154,12), confidence | 0.24682617, 0x33E6 | 0.2467041, 0x33E5 | 1 |
| Quality frame 6, texel (4,46), red | 0.671875, 0x3960 | 0.68310547, 0x3977 | 23 |
| Native frame 18, texel (241,3), green | 0.54248047, 0x3857 | 0.5473633, 0x3861 | 10 |

The first two examples are exactly round 1's first differing bytes 5084 and 1938. The later results are not a few
isolated ulps. Native follows the texels (245,3), (243,3), (241,3) and (239,3) over frames 16 through 19. Quality
follows (4,46), (2,46) and (0,46) over frames 6 through 8. At frame 6, texel (4,46) also differs in green and blue.
This is spatially and temporally coherent. History amplification is plausible, but those observations alone do not
prove that it starts only in arithmetic rounding rather than an intermediate decision difference.

Confidence first differs at Quality frame 6, with two pixels per field case. It differs on frames 6 through 10,
nine values per case, always by one half step, at most 0.000244140625. It never differs at Native. The first
confidence values above both exceed 1/15. `temporalGather` clamps `sampleWeight` to at most one,
`MaxAccumulation` is 15, and `MovingShareConfidence` is zero. Therefore both first differing confidence pixels take
the non-moving-share, admitted-history, non-reset output path. A reset would write at most 1/15. This rules out a
moving-share zero or a history/shading reset as the explanation at those pixels. It does not isolate which weight
operation first rounded differently.

The source candidates are the bilinear confidence `dot` in `ShaderSources.TemporalAccumulate.cs`, the accumulated
weight's multiplication by the reactive factor, and the final `(accumulated + sampleWeight) / MaxAccumulation`.
The generated checked HLSL writes `min(_16166 + _13699, _8646) * 0.066666670143604278564453125f`. The certified
HLSL writes `min(_10753 + _9387, _6676) * 0.066666670143604278564453125f`. The expressions and constants agree
after naming their corresponding values. Like the luma `lerp`, these weight operations have no arithmetic barrier.
This identifies operations the optimizer can reassociate, but does not show the actual differing FXC instruction
or the intermediate values it received on WARP.

The next proof needs shared-input WARP traces at the first divergent pixels for history admission, hold, clipping,
shading release, moving share and the accumulated/sample weights, together with the actual checked and certified
FXC disassembly. An instrumented probe must first show it preserves the original outputs. Another hosted run is
outside this round's exactly-one-run authorization. Guessing a production arithmetic fix or relaxing the strict
comparison would not settle the cause, so neither was done.

The strict certified-versus-check assertion now shows exact identity on local and hosted Metal and on lavapipe,
for all six cases and every frame. It still fails four WARP cases, including both per-entry comparisons and
Quality confidence. The lavapipe entry oracle is fixed within the existing amendment 23 rule. K remains blocked.

### Reporter coordinate correction after the hosted run

The complete tables made the internal/display dimension distinction visible. The initial reporter used the display
width for every example. R8 and depth use the internal width at Quality. The reporter now stores that width and
uses it for those two channels' texel coordinates, while colour, confidence, lock and mark retain the display width.
Both snapshots' widths must agree. No byte comparison, numerical bound, count or verdict was relaxed.

The hosted SHA above predates this diagnostic coordinate correction. Every hosted R8 and depth comparison has zero
differences, so none of its printed differing texels is affected. The hosted measurements and conclusions remain
valid for the comparison logic, but they are not an exact-head hosted proof of the later reporter commit. The
coordinate correction receives fresh local verification. No second hosted run was dispatched.

The coordinate correction is pushed at 5fd003f8ee4a5134b3067c903bb9c31095f92bba. Its repeated Release solution build
exited 0 with zero warnings and errors. The same focused Metal selection exited 0 with 149 passed and no skips.
The same device-free selection exited 0 with 898 passed and 256 GPU cases skipped. Full solution format verification
completed with exit 0 and only the workspace-loading warning. The whole-tree file-size check, serial whole-tree
dashes/prose checks and staged/unstaged diff checks also exited 0. The earlier capture-manifest and doc-version gates
remain valid because their source pins, assets, packages and version declarations did not change.

No merge, tag, release or other branch change was performed. Raw hosted logs and generated HLSL scratch are removed
after extracting this evidence. The workflow retains its artifacts. The remaining handoff is an authorized WARP
decision/weight and disassembly probe, not an assertion change.

## Round 3

The starting guards passed on fix/taa-vis-lit-skip at 3d5f0b500, clean, with the integrated remote at 5c2de244c.
The no-fast-forward merge retains both tasks. Generated table conflicts are resolved only by the four repository
writers, which passed all nine cases. The plan retains both K and Z. The compiled-lock comment retains twenty
includers and Z's branch contract. The manifest keeps both branches' evidence and hashes every current file.

Z's GL_EXT_control_flow_attributes header and `[[dont_flatten]]` positive-zero fallback are shared by every
checked and certified program. K still compiles out only the visibility decision. The source/SPIR-V/HLSL release
facts cover all twenty includers. DXBC coverage is extended from the six checked resolve/accumulate programs to
all twenty release-bearing shipped programs, including ten certified and four ordinary debug/count programs.
The fourteen additional listings are shipped-only. Existing D-prime association fixtures retain their rules.

The certified-versus-check oracle reuses TemporalNvidiaHistoryOracle.Compare on hardware NVIDIA Direct3D 11.
Locks, marks, R8 and depth stay exact. Finite colour and confidence differences are diagnostic only and non-finite
classes must match. WARP uses the existing TemporalDeviceClass.IsWarp predicate. Its colour and confidence each
use TemporalSoftwareCompileDivergenceRule, with AbsoluteBound 0.0625 and DifferingShareBound 0.0025. The same
implementation and named constant pair serve llvmpipe's existing production-entry colour comparison. llvmpipe's
certified pairs and every other device's pairs stay exact. Production entries remain exact except llvmpipe colour.
Every frame and aggregate pair prints each channel's worst absolute difference and differing share.

The device-free WARP and NVIDIA oracle facts first failed, 0 passed and 2 failed, against the strict oracle.
After the rule change both pass, including over-bound, over-share, non-finite, exact stored-channel and exact
production-entry mutations. The focused rule and compiled-release gate passes 30 cases with no skips.

The solution build caught CA1416 after moving FXC compilation into the dump printer. Marking that private helper
Windows-only, like its sole caller and the existing compiler helper, fixes the platform contract at its source.
The Release solution build then exits 0 with zero warnings and errors. The extended DXBC red gate has seven
passing cases and fifteen expected missing-fixture/coverage failures before the authorized recapture.
