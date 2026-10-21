# Task 7h K, certified fully lit specialization

**Status:** Blocked by strict colour-byte identity on WARP. The implementation and failing oracles are pushed at
code commit d140310d950df04093880d2e3c302e84f32c8cfc. No code change, tolerance or new exception has been added after
that result. Vulkan remains in progress. This is a candidate and reproduction, not a validated exact optimization.

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
The scene comparisons assert every raw history byte on every backend without adding a tolerance or exception.

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
| lavapipe | 119317681783 | Running | Pending |
| Vulkan sync validation | 119317681712 | 622 / 0 / 0 | Not selected |

Metal and WARP each have one intentional opt-in DumpVisibilityDisassembly skip. All new cases execute. Metal's
job completed in 18m37s. WARP completed in 27m58s. On both legs the existing entry-identity cases pass 10/10,
frozen D A/B 12/12, equivalence 6/6 and gather 7/7. All 21 certificate cases, all ten no-check module cases and all
eight headless policy/lifetime cases pass on WARP. The four failures are exclusively in the new strict scene oracle.

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
rerun, optional DXBC dump, timing, benchmark or stress run has been started.

### Not changed

The pointer gesture allocation failure above is outside this task and has not been investigated. The public Cost
paragraph in docs/USING-KHAOZENGINE.md also still says incremental visibility cost has not been measured, although
the parent branch now holds T4 measurements. Its reconciliation is left to the parent closeout. The new certificate
paragraph makes no timing claim.
