# TAA Shadow Visibility Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use subagent-driven-development or executing-plans to implement this plan task-by-task after owner/orchestrator approval. Steps use checkbox (`- [ ]`) syntax for tracking. This planning handoff does not select or start execution.

**Goal:** Ship bilinear 3x3 key-shadow comparison filtering and visibility-informed temporal history release together, eliminating held bright shadow-edge history preserving detail and entry identity, with only the three measured synthetic costs accepted by the owner in spec section 8.

**Architecture:** The existing lit passes export their exact `keyShadow` variable to an optional R8 MRT. The resolve owns two internal-resolution R8 visibility histories and compares depth-qualified current and reprojected previous 3x3 means in shared GLSL. Visibility changes release locks and hold without changing accumulated weight or display share. The owner selected D, with no large-change response. Establish the unchanged 21.4.0 resolve's bilinear-input baselines before introducing any visibility behavior.

**Tech Stack:** C# / .NET 10, xUnit, GLSL 450, the existing shaderc/SPIRV-Cross/FXC pipeline, native Metal, Direct3D 11 and Vulkan, `KhaozEngine.Render3D`, `KhaozEngine.Render.Tests`.

**Spec:** [2026-10-19-taa-shadow-visibility-design.md](../specs/2026-10-19-taa-shadow-visibility-design.md). Read it with the pinned archive links before execution. This plan starts at `a1e826635`, with tagged `21.5.1` and no version staged on this branch.

**Amended task order:** Task 7b, Task 8 (exact fused/split identity, #1501), Task 10 (Grimhollow capture and owner look), Task 7c tuning and owner selection, Task 7d (land D and the three accepted costs), Task 9 (docs and version), Task 11 (full gate), then Task 12 (bench request). After the billed result, Task 7f cuts the visibility check cost before integration. Task numbers below remain stable for existing briefs and evidence.

## Global Constraints

- Owner ruling. Both parts ship together in one engine release. Do not integrate or release the filter alone.
- Work only in the supplied `taa-shadow-visibility` worktree on `fix/taa-shadow-visibility`. Before every write, check `git rev-parse --show-toplevel` against the supplied worktree and `git branch --show-current` against that branch. Run `git status --short` and preserve unrelated changes. All committed paths and evidence locations are repository-relative.
- Treat `fix/taa-grain-switches` at `3ca76f11b` and `fix/taa-game-shadow` at `264bbc11c` as read-only archives. Do not merge or cherry-pick their renderer or diagnostic framework wholesale. The real-scene record is `KhaozEngine.Render.Tests/Gpu/measurements/taa-game-shadow/real-scene/README.md` on the former.
- The design-only handoff changes two Markdown files, commits and pushes the named branch, then stops. Tasks below are future implementation, not permission to execute them now.
- New visibility storage is `GpuPixelFormat.R8UNorm`. Release is strictly `D > 0.1`. Current and previous depth qualification is `0.02 * max(referenceDepth, 1e-5)`. The owner selected D on 2026-10-19. No large-change response ships. Rising and falling visibility release locks without changing accumulated weight or display share. Only the three measured costs accepted in spec section 8 may change their bounds.
- Five lit attachments exceed the guaranteed four-attachment capability. The spec section 5 orchestrator ruling settles it: a device below five keeps the four-attachment layout without visibility release, with the bilinear filter and a one-time capability report. Do not assume switching split to fused fixes this.
- No colour-only luma kill. Preserve all unrelated temporal, local-light, point-shadow and thin-feature behavior. No fallback to the resolve without visibility on devices that report five or more colour attachments.
- Rebaseline the filter input with the unchanged 21.4.0 resolve in its own commit before any signal implementation. Keep tolerance `0.1`, rounding `0.0000005` and [pinned-reference tolerance `0.000001`](../../../KhaozEngine.Render.Tests/Gpu/TemporalReviewBaseline.cs#L283), applied as `2 * Rounding` by the [rim GPU test](../../../KhaozEngine.Render.Tests/Gpu/TemporalMovingShadeRimGpuTests.cs#L37). No other bound changes without a measured absolute value that did not regress and a written rationale.
- All local builds, tests, packs and captures use `build-slot --label <task> -- <command>`. Use `--heavy` for wide GPU selections, captures, packs and final full suites. One command at a time, no retry loop, stress or timing run. Focused RED/GREEN checks run per changed task. The final local broad gate runs once, after integration of the tasks.
- Tests precede behavior. A missing API compile failure is setup evidence, not a behavioral RED. Introduce the compilable contract first where necessary, then prove the intended assertion fails before implementing it. An expected baseline regression is recorded, never hidden with skip or a raised bound.
- Keep CPU and GPU evidence distinct. Plain `dotnet test` skips GPU facts. Every selected GPU run sets `KE_GPU_TESTS=1`, records device/backend, and proves zero skipped selected GPU facts from TRX.
- Do not grow file-size baselines, change golden tolerances, broaden the software-Vulkan identity exception, or hide warnings. A new cohesive concern gets its own partial/type.
- Only Task 9 changes the package version/changelog, once for the batch. Reconcile with current main/tags at that point. No automatic tags, release, shared-feed pack, consumer repin or main integration by task workers.
- Billed T4 cost verification is an explicit stop in Task 12. Fresh owner approval is required. Ordinary hosted correctness and baseline collection are future orchestrator steps, not authorized by this design-only request.
- Before every commit stage explicit paths, run `sh scripts/check-dashes.sh staged`, `sh scripts/check-prose.sh staged`, `sh scripts/check-file-size.sh staged` and `bash scripts/check-doc-versions.sh`. Use an explicit commit pathspec and `area(scope): summary`, without attribution. Do not bypass hooks.

## Review Focus

1. R8 at location 3 looks like motion to the current count-only check. A visibility-only framebuffer must stay motionless, and a four-attachment device must take only its approved supported policy (`VisibilityLayoutDoesNotImpersonateMotion`, `FourAttachmentDeviceUsesApprovedVisibilityPolicy`, Task 4).
2. A transparent or discarded fragment crosses an opaque shadow receiver. Visibility must remain the opaque surface's value (`TransparentAndDiscardedWritersPreserveOpaqueVisibility`, Task 4).
3. Jitter changes while light, receiver and camera are still. The visibility signal must not release a missed real thin feature (`JitterOnlyVisibilityDoesNotReleaseThinDetail`, Task 6).
4. A nearer moving silhouette changes the footprint or leaves no qualified history texels. Reject cross-surface history before interpolation, and avoid divide-by-zero or false release (`OtherSurfaceAndEmptyFootprintsDoNotRelease`, Task 6).
5. AA, size, device or entry changes after a completed resolve, including a second render in the same frame. Visibility and previous jitter must keep the colour history's generation and frame ownership (`VisibilityGenerationAndRepeatedRenderStayAligned`, Task 5, `EntryFlipKeepsVisibilityBytesExact`, Task 8).

---

## File structure and shared interfaces

Existing files are extended only at their integration seams. New files below have one responsibility.

| File | Responsibility |
| --- | --- |
| `KhaozEngine.Render3D/Internal/ShaderSources.ShadowVisibility.cs` | Shared comparison-filter GLSL, included by `LightingGlsl` |
| `KhaozEngine.Render3D/Internal/ShadowVisibilityTargets.cs` | Format and actual attachment-layout facts, distinct from motion |
| `KhaozEngine.Render3D/Rendering/TemporalVisibilityHistory.cs` | R8 history allocation, copy and retirement keyed to the colour owner |
| `KhaozEngine.Render3D/Rendering/TemporalResolveRenderer.Visibility.cs` | Visibility binding lifecycle, fully lit fallback and previous-jitter buffer |
| `KhaozEngine.Render3D/Internal/ShaderSources.TemporalVisibility.cs` | Shared depth-qualified mean and signed change |
| `KhaozEngine.Render.Tests/Gpu/TemporalShadowFilterBaseline.cs` | Bilinear-input rim baseline and provenance, leaving historical diagonal baselines intact |
| `KhaozEngine.Render.Tests/Gpu/TemporalVisibilityReference.cs` | Test-only CPU mean/qualification oracle, including explicit history weights |
| `KhaozEngine.Render.Tests/Gpu/measurements/taa-shadow-visibility/` | Relative-path manifests, raw measurements, comparisons and reviewed proof |

New internal contracts, defined by their owning tasks:

```csharp
// Task 4, Internal/ShadowVisibilityTargets.cs
internal const GpuPixelFormat Format = GpuPixelFormat.R8UNorm;
internal static int Attachment(bool motion); // 3 without motion, 4 with motion
internal static bool HasVisibility(in GpuOutputDescription outputs);

// Task 5, extend the existing internal record. Null is the standalone fully lit case.
internal readonly record struct TemporalResolveInputs(IGpuTexture SceneColor,
    IGpuTexture OpaqueColor, IGpuTexture SceneDepth, IGpuTexture Motion,
    IGpuTexture? KeyShadowVisibility = null);

// Task 5, Rendering/TemporalVisibilityHistory.cs
internal bool Ensure(IGpuDevice device, TemporalHistory owner, GpuRetireQueue? retired);
internal IGpuTexture Texture(int index);
internal void Store(IGpuCommandList commands, IGpuTexture source, int writeIndex);
internal void Release(GpuRetireQueue? retired);
```

`Ensure` returns true on generation replacement, causing visibility validity to reset. `Store` copies only, without flipping indices. The owner remains `TemporalHistory`. Helpers outside these signatures are private implementation choices. An approved four-attachment policy may add one narrowly specified companion-pass interface before Task 4 starts.

The shared GLSL contract for Task 6 is:

```glsl
bool temporalVisibilityChange(vec2 uv, ivec2 centre, ivec2 last,
    vec2 previousUv, float expectedDepth, out float signedChange);
```

It returns false for empty qualified samples and otherwise returns current mean minus previous mean. The caller gates history/background validity. One helper and reduction order serve fused, split, Native specialization, debug and count evaluation.

## Verification commands used by tasks

`FOCUS` below is the task's explicit filter. From the guarded task root, focused CPU runs use:

```bash
build-slot --label taa-focus -- dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release --filter "$FOCUS"
```

Focused Metal readback runs use this command after a Release build:

```bash
build-slot --label taa-focus-gpu -- env KE_GPU_TESTS=1 KE_GRAPHICS_BACKEND=metal-native dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release --no-build --filter "$FOCUS" --logger 'console;verbosity=detailed'
```

Before GPU verification after a source change:

```bash
build-slot --label taa-render-build -- dotnet build KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release
```

Task 11 owns the one final wide local gate. Do not repeat it for every tuning arm. Each task records actual assertion failures, command exit codes, device identity and its commit.

### Task 1: Port useful guards without the failed release rule

**Files:**
- Create from the tested source at `264bbc11c`: `KhaozEngine.Render.Tests/Gpu/GameShadowGrainScene.cs`, `GameShadowGrainMeasure.cs`, `TexturedShadowGrainCase.cs`, `TemporalGameShadowGrainGpuTests.cs`, `TemporalTextureLightGpuTests.cs`.
- Create: `KhaozEngine.Render.Tests/Gpu/measurements/taa-shadow-visibility/guards.md`.
- Read only: archive `TemporalResolveLumaLockGpuTests.cs` and `TemporalShadeEvidence.cs`. Do not port implementation-specific luma-rule assertions.

**Interfaces:** Consumes existing `TemporalFixture`, `TemporalAcceptance` and `TemporalDeviceClass`. Produces the textured receiver assertion and dark/flicker/moving-light fixture used by later tasks, with a shared-scene parity fact.

- [ ] Read the source guard and its baseline measurement record. Copy only deterministic fixture, measurement and useful assertions. Remove environment switches, release-mask assumptions and diagnostic token dependencies. Preserve local-light state reset and fresh-scene parity.
- [ ] Verify `Texture_ridges_under_a_coarse_moving_shadow_do_not_hold_bright_grain` fails on unchanged behavior at Native `0.0113` or Quality `0.008`. Record the absolute result. Balanced remains report-only for this added guard, with existing Balanced coverage retained elsewhere.
- [ ] Verify dark mean luma is between `0.001` and `0.08`. Retain the paired `1.1 * before` error, error-image grain and temporal-error bounds for dark, flicker and moving local lights. Hardware controls come from the archive. Before gating another device class, obtain its unchanged-resolve controls, never candidate controls.
- [ ] Run the focused filters `FullyQualifiedName~TemporalGameShadowGrainGpuTests|FullyQualifiedName~TemporalTextureLightGpuTests`. Non-shadow guard and scene-parity facts pass. The textured-grain RED is an explicitly tracked open prerequisite, not a passing-suite claim. Do not merge the intermediate branch to main.
- [ ] Commit `test(taa): preserve shadow grain and local light guards` with the outcome record.

**Definition of done:** The relevant RED is reproducible and retained, non-shadow guards are green, and no renderer or bound changed.

### Task 2: Implement bilinear 3x3 PCF and evaluate gather portability

**Files:**
- Modify: `KhaozEngine.Render3D/Internal/ShaderSources.Lighting.cs`.
- Create: `KhaozEngine.Render3D/Internal/ShaderSources.ShadowVisibility.cs`.
- Create: `KhaozEngine.Render.Tests/Gpu/TemporalShadowFilterGpuTests.cs`, `TemporalShadowGatherCompileTests.cs`.
- Extend as needed: `KhaozEngine.Render.Tests/Render3D/ShadowShaderContractTests.cs`, `KhaozEngine.Render.Tests/Gpu/ShaderCorpus.cs`, `SpirvCrossCompileTests.cs`, `D3D11FxcValidationTests.cs`.
- Evidence: `KhaozEngine.Render.Tests/Gpu/measurements/taa-shadow-visibility/filter.md`.

**Interfaces:** Preserve `pcfCascade(texture2D, sampler, int cascade, int count, vec2 uv, float depth, float texelStep)`. It now implements 16 compare-first samples weighted as nine bilinear taps. `sampleKeyShadow` call sites do not change.

- [ ] Add a fractional-texel readback against a CPU 16-comparison oracle. Include lit/occluded atlas texels, clear holes, exact depth equality, negative and positive offsets, all cascade borders and cascade blending. Prove the point path fails fractional cases. Pin `[1-f, 1, 1, f]` weights and `/9` normalization.
- [ ] Add a compile-only gather test with the real cross-compiler, and an asymmetric 2x2 component-order/readback case. Inspect MSL, HLSL/FXC and SPIR-V. No external assumption of gather availability counts as proof.
- [ ] Implement explicit shared 4x4 comparison PCF, clamping inside the selected column. Keep bias, normal offset, strength, selection and blend untouched. If gathers compile everywhere, prove four non-overlapping gathers reproduce the explicit footprint at interior and borders before choosing them. Retain explicit fetches if proof fails. Report source operation counts without a timing claim.
- [ ] Run CPU `FOCUS='FullyQualifiedName~TemporalShadowGatherCompileTests|FullyQualifiedName~ShadowShaderContractTests|FullyQualifiedName~SpirvCrossCompileTests'`, then GPU `FOCUS='FullyQualifiedName~TemporalShadowFilterGpuTests|FullyQualifiedName~CascadeHandoffBlendGoldenTests'`. Defer Windows FXC and backend readback confirmation to hosted correctness, retaining the explicit oracle until then.
- [ ] Compare the archive's softness procedure and the existing shadow/golden selection without rebaking. Record known old-input rim/reference failures as the reason for Task 3. Do not touch their assertions here.
- [ ] Commit `fix(shadows): interpolate the shared 3x3 comparison footprint`.

**Definition of done:** Continuous PCF matches the comparison oracle, shadow semantics are unchanged, gather has a documented proved or rejected disposition, and no resolve behavior changed.

### Task 3: Freeze bilinear-input references in a separate baseline commit

**Files:**
- Create: `KhaozEngine.Render.Tests/Gpu/TemporalShadowFilterBaseline.cs`.
- Modify: `KhaozEngine.Render.Tests/Gpu/TemporalReviewBaseline.cs`, `TemporalMovingShadeRimGpuTests.cs`.
- Evidence: `KhaozEngine.Render.Tests/Gpu/measurements/taa-shadow-visibility/filter-baseline.md` and raw per-device control outputs.

**Interfaces:** `TemporalReviewBaseline.Check` keeps its public-to-tests signature. Route only `kind == "rim"` to a new filter-specific measured table. Preserve historical diagonal checks. Missing device/case evidence fails rather than borrowing a different class's numbers.

- [ ] Pin the control source and verify resolve/tuning files still match `v21.4.0`. No visibility helper, input, storage or reset is allowed. Record source hashes, filter commit and test inventory.
- [ ] Capture both point-PCF and bilinear-PCF controls with the unchanged resolve, using matching settings and deterministic frame phases. Use the existing supersampled non-temporal reference path. Record old/new absolute reference and temporal error for every affected row, including leading/trailing, flat/textured, M/MS, speed and preset.
- [ ] Measure local Metal with `FOCUS='FullyQualifiedName~TemporalMovingShadeRimGpuTests|FullyQualifiedName~TemporalMovingShadeLagGpuTests|FullyQualifiedName~TemporalTextureLightGpuTests'`. Retain raw six-decimal output and the exact derived bounds. This is a measurement round, not an attempt to make old-input assertions pass.
- [ ] Have the orchestrator collect matching control rows through filtered `cross-platform-gpu.yml` for `metal-native`, `direct3d11-native` and `vulkan-native`. Retain both source refs and artifacts. Use hardware, WARP and lavapipe rows separately. Finish this control collection before proceeding to signal code. A hosted dispatch is not performed by the design writer.
- [ ] Re-record pinned spatial-reference values and the bilinear-input rim acceptance baselines from those controls. Keep [reference tolerance `0.000001`](../../../KhaozEngine.Render.Tests/Gpu/TemporalReviewBaseline.cs#L283), baseline tolerance `0.1`, rounding `0.0000005` and formula `(baseline + 0.0000005) * 1.1` unchanged. Keep case inventory, sample-count assertions and metric definitions unchanged.
- [ ] Write this reason in the test and proof: “The PCF filter changed the scene input. These rows measure the unchanged 21.4.0 resolve on bilinear 3x3 PCF. The signal is judged against this frozen input-matched control.” Preserve old measurements as history, without rewriting unrelated baselines.
- [ ] Verify the control passes its new reference and rim checks on each measured class. Confirm the Quality M 0.5 `0.010485` versus old `0.01023825` miss is recorded as input movement, not used as a signal improvement. Review every changed table row against its raw value.
- [ ] Commit only baseline tests and measurement evidence as `test(taa): freeze unchanged resolve on bilinear shadow input`. This commit must precede every signal implementation commit. Do not merge the filter-only branch.

**Definition of done:** A reviewed, reproducible per-device control exists in its own commit. Later tasks cannot re-record it to fit themselves. The Task 1 textured-grain RED remains an explicit pending fix if bilinear alone does not satisfy it.

### Task 4: Export R8 key-shadow visibility across lit and preserving passes

**Files:**
- Create: `KhaozEngine.Render3D/Internal/ShadowVisibilityTargets.cs`.
- Modify target seams: `KhaozEngine.Render3D/Internal/RenderResources.cs`, `MotionMath.cs`, `KhaozEngine.Render3D/Scene3D.RenderTargets.cs`, `Scene3D.ResolveTargets.cs` and the existing model-target clear call site.
- Modify shader seams: `KhaozEngine.Render3D/Internal/ShaderSources.Model.cs`, `ShaderSources.Terrain.cs`, `ShaderSources.TileGround.cs`, `ShaderSources.Motion.cs`, `ShaderSources.MotionPreserve.cs`, and generated foliage/skinned variants where their construction requires it.
- Modify pipeline seams: `KhaozEngine.Render3D/Rendering/ModelRenderer.cs`, `ModelRenderer.Motion.cs`, `ModelRenderer.Foliage.cs`, `ModelRenderer.Splat.cs`, `ModelRenderer.TileGround.cs`, `ModelTargetBlends.cs`, `TexturedBillboardRenderer.cs`, `BeamRenderer.cs`, `TrailRenderer.cs`, `OverlayMeshRenderer.cs`, `SilhouetteRenderer.cs`.
- Create tests: `KhaozEngine.Render.Tests/Render3D/TemporalVisibilityLayoutTests.cs`, `KhaozEngine.Render.Tests/Gpu/TemporalLitVisibilityGpuTests.cs`.

**Interfaces:** Produce `ShadowVisibilityTargets` as above and an optional lit visibility texture on `RenderResources`. Append a visibility-request argument to target creation/resize with a false default for existing standalone callers. Treat motion and visibility as separate layout facts. No resolve behavior changes in this task.

- [ ] Apply the spec section 5 ruling. Below five colour attachments, select the existing four-attachment layout with no visibility output, report the capability once, and keep the bilinear filter. Add `FourAttachmentDeviceUsesApprovedVisibilityPolicy` with a fake four-attachment device, asserting no pipeline failure and frames identical to the filter-only resolve.
- [ ] Write CPU layout tests for three attachments, four with RG16 motion, four with R8 visibility, and five with both. Write GPU pixel tests for all six lit families, motion, cutout, dissolve/inverted dissolve, background clear, shadows disabled, shadow strength, cascade handoff and applicable MSAA layouts.
- [ ] Add `TransparentAndDiscardedWritersPreserveOpaqueVisibility`, including transparent model, billboard, beam, trail, overlay, silhouette and later colour-only draws. Assert opaque R8 values survive, not merely that the pipeline creates. Assert the approved max-four policy is selected on a fake four-attachment device before pipeline creation.
- [ ] Implement attachment 3 without motion or 4 with motion. Assign the exact existing `keyShadow` to it, without a new sample. Clear to 1. Match all output/blend arrays. Preserve destination for transparent writers and leave discarded pixels untouched. Explicitly resolve MSAA R8 before sampling. Keep off/FXAA layouts free of unused signal allocations.
- [ ] Run CPU `FOCUS='FullyQualifiedName~TemporalVisibilityLayoutTests|FullyQualifiedName~ModelMotionPipelineTests|FullyQualifiedName~TransparentMotionPipelineTests'`, then GPU `FOCUS='FullyQualifiedName~TemporalLitVisibilityGpuTests|FullyQualifiedName~MsaaResolveTargetGoldenTests'`. Force all existing MSAA-supported sample counts in the parametrized fixture and assert nontrivial visibility.
- [ ] Commit `feat(render): export lit key shadow visibility to R8` with the supported capability policy and shader inventory.

**Definition of done:** Every actual writer has pixel evidence and correct slot semantics, target generations/rebuilds include visibility, and no supported layout creates an over-limit framebuffer.

### Task 5: Align visibility histories and previous jitter with both entries

**Files:**
- Create: `KhaozEngine.Render3D/Rendering/TemporalVisibilityHistory.cs`, `TemporalResolveRenderer.Visibility.cs`.
- Modify: `KhaozEngine.Render3D/Rendering/TemporalResolveRenderer.cs`, `TemporalResolveRenderer.Entry.cs`, `TemporalSplitResolve.cs`, `KhaozEngine.Render3D/Scene3D.TemporalResolve.cs`.
- Create: `KhaozEngine.Render.Tests/Render3D/TemporalVisibilityHistoryTests.cs`, `KhaozEngine.Render.Tests/Gpu/TemporalVisibilityHistoryGpuTests.cs`.
- Extend: `KhaozEngine.Render.Tests/Render3D/TemporalHistoryWiringTests.cs`, `TemporalUboLayoutTests.cs`, `KhaozEngine.Render.Tests/Gpu/TemporalSteadyFrameAllocationTests.cs`, `TemporalEntryFlipMemoryGpuTests.cs`.

**Interfaces:** Produce the `TemporalResolveInputs` extension and `TemporalVisibilityHistory` methods above. Add a distinct std140 16-byte previous-jitter buffer. Existing resolve uniforms remain 256 bytes. Both entries bind the same read history and current signal.

- [ ] Write tests for same-size replacement, owner/device change, AA off/on, resize/preset/camera reset and repeated frame index. Assert history texture bytes follow the colour read/write indices, previous jitter comes from the last completed resolve and a second render never advances them.
- [ ] Write stale-binding tests where only the visibility input changes, plus R8 fallback tests for standalone rigs without lit input. Assert steady-frame allocation remains zero and retired generations are drained through the existing queue.
- [ ] Implement the owner/size/generation keyed pair and common post-entry `CopyTexture`. Preserve the read texture for debug/count re-evaluation. Publish saved jitter only after the copy. Guard comparison readiness independently until a generation has been seeded. Avoid an early split return that skips visibility storage.
- [ ] Run CPU `FOCUS='FullyQualifiedName~TemporalVisibilityHistoryTests|FullyQualifiedName~TemporalHistoryWiringTests|FullyQualifiedName~TemporalUboLayoutTests'`, then GPU `FOCUS='FullyQualifiedName~TemporalVisibilityHistoryGpuTests|FullyQualifiedName~TemporalSteadyFrameAllocationTests|FullyQualifiedName~TemporalEntryFlipMemoryGpuTests'`.
- [ ] Commit `feat(taa): retain aligned visibility history and jitter`.

**Definition of done:** R8 storage, bindings and lifecycle are proved for fused and split, with unchanged colour output before the comparison is enabled.

### Task 6: Add the depth-qualified mean and lock release

**Files:**
- Create: `KhaozEngine.Render3D/Internal/ShaderSources.TemporalVisibility.cs`.
- Modify: `KhaozEngine.Render3D/Internal/ShaderSources.TemporalAccumulate.cs`, `ShaderSources.TemporalResolve.cs`, `ShaderSources.TemporalSplit.cs`, `ShaderSources.TemporalAtDisplaySize.cs`, `ShaderSources.TemporalDebug.cs`, `TemporalResolveTuning.cs`.
- Modify binding seams from Task 5 where the shared helper adds resources, including debug/count re-evaluation.
- Create: `KhaozEngine.Render.Tests/Gpu/TemporalVisibilityReference.cs`, `TemporalVisibilityComparisonGpuTests.cs`, `KhaozEngine.Render.Tests/Render3D/TemporalVisibilityComparisonTests.cs`.
- Extend: `KhaozEngine.Render.Tests/Render3D/TemporalResolveShaderTests.cs`, `TemporalResolveWiringTests.cs`.

**Interfaces:** Produce `temporalVisibilityChange` from the shared-interface section. Named C#/GLSL constants are `VisibilityReleaseThreshold = 0.1f` and `VisibilityDepthTolerance = 0.02f`. The CPU oracle is test-only. Release-only changes lock/hold, never implicitly the existing `released` flag or accumulation policy.

- [ ] Write CPU and GPU oracle cases for exact 0.1 and its neighbours, rise/fall sign, current depth exactly at 2%, rejected previous bilinear corners, empty current/history denominators, border-clamped duplicate offsets, offscreen history, background and unmatched depth.
- [ ] Write `JitterOnlyVisibilityDoesNotReleaseThinDetail` and `OtherSurfaceAndEmptyFootprintsDoNotRelease`. Include current-to-previous motion and previous jitter independently, then together. A constant visible surface with a changing jitter phase must preserve lock and detail. An actual visibility change must release a carried lock even without a current ridge.
- [ ] Implement the spec formula `h(o)`, separate current count and aggregate accepted previous weights, and strict threshold. Use existing reprojection/expected depth, not a second motion classifier. Clear lock before hold, also ensuring hold is zero after any later release block. Keep unrelated shading release behavior.
- [ ] Add a test-only matched R32Float format control using the same shader math. Read R8 conversion boundaries and compare release decisions, signed differences and rendered metrics. Report discrepancies rather than treating R8 equivalence as established by the archive. Retain R8 unless evidence forces an owner-reviewed design amendment.
- [ ] Run CPU `FOCUS='FullyQualifiedName~TemporalVisibilityComparisonTests|FullyQualifiedName~TemporalResolveShaderTests|FullyQualifiedName~TemporalResolveWiringTests'`, then GPU `FOCUS='FullyQualifiedName~TemporalVisibilityComparisonGpuTests|FullyQualifiedName~TemporalGameShadowGrainGpuTests|FullyQualifiedName~TemporalTextureLightGpuTests|FullyQualifiedName~TemporalResolveLockGpuTests'`.
- [ ] Record release-only absolute 1/8-pixel tails with `TemporalMovingShadeLagGpuTests`, including any known RED for Task 7. Do not change the frozen baseline or call release-only shippable. Commit `fix(taa): release held locks on qualified visibility changes`.

**Definition of done:** Mean/reprojection/format semantics match their oracles, useful grain/detail guards pass, and any tail/reset work remains explicitly open for the next task.

### Task 7: Land the owner's release-only selection (Task 7d)

Task 7b's rising-only zero response was provisional. Task 7c and the final real-scene clips showed that D removes the same back dots while restoring ground change to the bilinear-control level. The owner selected D on 2026-10-19 and accepted the absolute 8 px fast-shadow tail cost, Quality textured grain and Quality M 1.0 leading textured rim costs. Spec sections 7 and 8 hold the table, exact values and rationale. After hosted run 39215298727, the owner accepted the same three costs separately for WARP and lavapipe. Each device class uses its own measurements, never a hardware fallback. Quality grain is hardware 0.008032, lavapipe 0.008040 and WARP 0.008014. Filtered run 39221506634 measured WARP 0.008013324991270224 on 0e48f0c36. Its own bound is the smallest upward six-decimal value that includes it, within the owner-authorized 0.008032 cap.

**Files:**
- Remove response code and constants from `KhaozEngine.Render3D/Internal/ShaderSources.TemporalAccumulate.cs`, `ShaderSources.TemporalVisibility.cs`, `ShaderSources.TemporalResolve.cs` and `TemporalResolveTuning.cs`.
- Update response, format, debug/count and moving-shadow identity tests, including outlined Quality. Preserve exact entry comparisons and proof that lock release fires.
- Update only the accepted bound rows in the lag, grain and rim tests. Record D measurements in `KhaozEngine.Render.Tests/Gpu/measurements/taa-shadow-visibility/owner-release-only.md`. Keep previous measurements as history.

**Interfaces:** Lock release stays strictly `abs(d) > 0.1`, with depth tolerance `0.02`. It clears lock and hold without changing accumulated weight or display share on either sign. Preserve independent shading release and Task 8's #1501 precision boundary exactly. No large-change helper, constants, GLSL twins, multipliers or tuning switch remains.

- [ ] Prove behavioral RED for retained weight and display history, then remove only the Task 7b response. Retain strict boundary, invalid footprint, R8/R32, debug/count and both-entry coverage.
- [ ] Remeasure the three owner-accepted costs on D in one focused `--heavy` run. Restate only the failing accepted rows from measured D using each existing formula and rounding precision. Include the owner's dated reason and the per-driver ruling after hosted run 39215298727 in each affected test. Scope accepted tails by device, case and region and rim changes to fields 1 and 3 of the single accepted row per driver. Quality grain rounds each accepted driver's own exact value upward to six decimals. Native 0.0113 and Balanced report-only remain unchanged. Stop on any other regression, without changing its bound.
- [ ] Run the Release solution build and every CPU test class matching `Temporal|Motion|Binding|Uniform|Ubo|Wiring|History|Shader`. Locally rebake moved shader hash tables and list the changed programs.
- [ ] Run `TemporalEntryIdentityGpuTests|TemporalEntryFlipMemoryGpuTests|TemporalVisibilityComparisonGpuTests|TemporalMovingShadeLagGpuTests|TemporalMovingShadeRimGpuTests|TemporalGameShadowGrainGpuTests|TemporalTextureLightGpuTests` once with `--heavy`. Run the existing non-shadow guard once with `--heavy`. Require every selected fact to pass with zero skips.
- [ ] Commit `fix(taa): ship visibility lock release without a weight response`, `test(taa): record owner accepted visibility costs` and `docs(taa): record owner choice of lock release only`. Stage explicit paths, push only the task branch and verify the remote SHA.

**Definition of done:** D is the only production policy. The response and moving-shadow identity cases prove lock release with retained weight/display history for both signs, including outlined Quality. Only the three accepted costs change their bounds. Required local checks pass with zero skips, the amendment and measured values are durable, and the three commits are pushed. The initial Task 7d implementation had no hosted dispatch. Fix round 1 is authorized to run one filtered WARP grain dispatch after local verification, then one final all-leg dispatch for lag, rim, game grain and only the textured-grain fact in TemporalVisibilityComparisonGpuTests. Do not select its other facts because the separate WARP NaN-depth defect is outside this fix. Require zero selected skips and obtain exact grain values from detailed passing console output. WARP keeps 0.008 if it passes that historical limit, otherwise accepts only its own upward six-decimal bound at or below 0.008032. A larger value is a stop. No timing run, version change, release or main integration belongs to Task 7d.

### Task 7f: Cut the visibility check cost

In reading order this task follows Task 7d and its Task 7e non-finite depth fix and precedes Task 8. It runs after Task 12's billed result, [benchmark-result.md](../../../KhaozEngine.Render.Tests/Gpu/measurements/taa-shadow-visibility/benchmark-result.md). That bench measured head adding 2.1 to 2.9 ms on both Vulkan legs and 3.9 to 4.8 ms on Direct3D 11 to resolve and sharpen at 1440p on a T4. All of it sits in the split's second pass and grows with the display size. The Task 7f design review traces it to `temporalVisibilityChange`, about 101 point fetches and 950 ALU operations per display pixel, and names the restructures R0 to R5 used below.

**2026-10-21 correction, binding:** R3-lite stays. Its withdrawal at `d9d72abbd` rested on a misreading: the
decomposition's V1 minus V2 figure measured the mask's overhead with the check already removed, so it never saw
the mask's benefit, fewer fetches inside the check. Outside-cost run `39519219228` put the check at 1.3 to 1.9 ms
in the lock-dense scene without the mask against 1.0 to 1.2 ms with it. T4 Direct3D 11 correctness run
`39519233674` showed the same confidence difference against frozen D, up to 32 half steps, with and without the
mask, so the mask never caused it. R3-lite was exact on Metal, WARP, lavapipe and both NVIDIA Vulkan legs. The
withdrawal is reverted and the eleven temporal fragment programs return to their `323efaba8` hashes. The frozen D
texts and pins never change.

The comparison rule, final. NVIDIA's Direct3D 11 driver compiles confidence arithmetic unrelated to the check
differently whenever the visibility code changes, even on frame 0 where the check does not run, while WARP running
the same DXBC matches D bit for bit. So on hardware NVIDIA Direct3D 11 only, `TemporalEntryIdentityGpuTests` holds
production fused equal to production split exactly, its original contract, and prints its comparisons with D
without asserting them. `TemporalVisibilityResolveAbGpuTests` holds stored locks, R8 and depth exactly equal to
D's and prints colour and confidence differences, count and maximum half steps per channel, without asserting
them. Every other backend and device keeps the strict byte comparison. This replaces the fixed two-step bound of
`91196cdde` and the measured-envelope rule of `e639ef2d6`.

The next outside-cost request interleaves V0 shipped, V1 check removed, V5 visibility infrastructure off,
V4 V5 without the shared lock-release precise markings and V3 frozen D. Both scenes and Native and Quality stay
at 2560x1440, fused and split. Print V0 minus V1, V1 minus V5, V5 minus V4 and V0 minus V3. Author and compile
these variants only. The next decomposition and T4 D3D11 correctness dispatches each need fresh owner approval.

**Rulings, binding:**

- Owner, 2026-10-20: optimize before merging. The budget is at most 0.5 ms resolve growth over `v21.7.1` (`6c6529589`) at 1440p on every T4 backend, Direct3D 11, Windows Vulkan and Linux Vulkan. Any further billed bench needs fresh owner approval.
- Phase 1 preserves D under the corrected comparison contract above. Phase 1 implements R1 (gate the check on the post-release `lockValue` bits, exact on outputs), R2 (shared 4x4 history block, separable positions, a generated straight-line fast path, D's function verbatim as the slow path), R3-lite (a 9-bit current acceptance mask and a centre-finite flag from pass one in `PreparedSurface.w`, computed inline by the fused entry) and R4 (value-range early-out with the `2^-13` margin). All of them use loads.
- R5 (gathers) is deferred until a T4 oracle proves gather equals load. R0 (CPU constant-input skip) is rejected because it would only make the bench free.
- The shadows-on whole-frame cost case, the request's Proposal B, is authored in 7f.4 and not run.
- If any oracle shows a mismatch, the affected restructure is withdrawn, never adjusted toward close.

**Global for Task 7f:** only the comparison-rule changes above are authorized, with no other bound, baseline, golden or tolerance changes, no timing, perf or benchmark run before the 7f.6 approval, no merge, tag or release. The frozen D fixture never changes after 7f.1. Each sub-step commits explicit paths, pushes `fix/taa-shadow-visibility` and verifies the remote SHA. Approximations (Phase 2 of the design review) need a new owner look and are outside this task.

#### Task 7f.1: Freeze the D oracles

**Files:**
- Create: `KhaozEngine.Render.Tests/Gpu/TemporalVisibilityFrozenD.cs` (D's `TemporalVisibilityGlsl`, call-site block and hold release copied verbatim at `583a3ae7b`, their SHA-256 pins, the D program rewrite and the renamed copy).
- Create: `KhaozEngine.Render.Tests/Render3D/TemporalVisibilityFrozenDTests.cs`, `KhaozEngine.Render.Tests/Gpu/TemporalVisibilityCompiledFormTests.cs`.
- Create: `KhaozEngine.Render.Tests/Gpu/TemporalVisibilityEquivalenceProbe.cs`, `TemporalVisibilityEquivalenceInputs.cs`, `TemporalVisibilityEquivalenceGpuTests.cs`, `TemporalVisibilityResolveAbGpuTests.cs`.
- Modify: `KhaozEngine.Render.Tests/Gpu/TemporalResolveRig.cs` (optional fragment source rewrite).
- Test hook only: `KhaozEngine.Render3D/Rendering/TemporalResolveRenderer.cs`, `TemporalResolveRenderer.Entry.cs`, `TemporalSplitResolve.cs`, `TemporalSplitResolve.AtDisplaySize.cs`. No shader text, binding or default behaviour changes.

**Interfaces:**

```csharp
// Internal, null in production. Applied to every fragment program the renderer and its split compile.
internal TemporalResolveRenderer(IGpuDevice gd, Func<string, string>? fragmentSourceForTests = null);

internal static class TemporalVisibilityFrozenD
{
    internal const string VisibilityGlsl, CallSiteGlsl, HoldReleaseGlsl;   // verbatim D, never edited
    internal const string VisibilitySha256, CallSiteSha256, HoldReleaseSha256;
    internal static string ToD(string program);        // production program to D program, throws on a lost anchor
}

// Added by the review 7f r1 fix round: what 7f.3 changed in the first pass, the surface and the fused entry, frozen.
internal static class TemporalVisibilityFrozenDMask
{
    internal static readonly Hunk[] All;                              // verbatim D pieces with SHA-256 pins
    internal static readonly (string Program, string Sha256)[] ProgramSha256;   // every rig program as D shipped it
    internal static string Restore(string program);                   // called by ToD
}
```

- [ ] Copy D's text verbatim and pin SHA-256 over LF-normalized UTF-8. `FrozenDTextHashesToTheShippedDPin` is permanent. `ProductionStillShipsFrozenD` asserts the production text equals the fixture at this commit. The D rewrite replaces the production visibility block, the call-site block between its first and last lines and the hold release, leaves programs without them untouched and throws when a program carries only some anchors. The review 7f r1 fix round extended the fixture, at the orchestrator's direction, with `TemporalVisibilityFrozenDMask`: the 583a3ae7b text of everything 7f.3 changed in the first pass, the surface struct and its store and read-back, and the fused entry's 3x3 loop, each pinned. `ToD` restores those too and drops the mask functions, so every program the D rig compiles hashes to the program D shipped, pinned per program. The three original texts and pins are unchanged.
- [ ] Proof 2, `TemporalVisibilityEquivalenceGpuTests`: frozen D, the production decision wrapper (`ProductionDecisionGlsl`, D itself at this commit) and an independent classifier. Since the review 7f r1 fix round each is its own program over cases a first pass stores in single-float targets, so no compiler can share a value between D and production. The combined program was dropped, since it covered nothing the separate programs do not. Each fragment of a 1024x1024 target (256x256 on software adapters) builds its own case from a hash PRNG against full-size textures at 2560x1440, 1707x960 and 3342x2160, with Halton consecutive, Halton wrap and zero jitter. The cases cover edge and corner centres, fractions on, within ulps of and away from integers, history near the border, depths exactly at, and 1 ulp either side of, both tolerances, NaN and both infinities counted apart, fast-path cases meeting a non-finite neighbour depth and a non-finite stored depth, the background sentinel, and R8 lattices straddling 0.1 and `0.1 - 2^-13`. Assert zero decision and validity mismatches, bitwise-equal change wherever both compute it, every fragment written, input coverage minimums, the classifier's predicted fast path, slow path reasons and R4 skip counts, and that no predicted R4 skip meets a D release. The reported path counters are named placeholders that 7f.2 fills. The review 7f r3 fix round added binade draws, centres beside or on a power-of-two column, row or both, from 1 to 2048, under jitter of each sign on each axis, zero, the smallest positive float and the float before one half. A fitting block must take D's path exactly where a positive axis holds a power of two and the fast path otherwise, line by line, with zero mismatches. A fifth program, the decision with the binade rule rewritten out, runs on those draws and its differences from D are reported, never asserted.
- [ ] Proof 3, `TemporalVisibilityResolveAbGpuTests`: the production rig and a D rig built through the hook, fused and split, Native and Quality, random colour, depth, motion, moving R8 edges and random stored locks including exact zero and ridged pixels, six frames. History colour, state and R8 bytes must match every frame. A mutant D that never releases must differ, which proves the hook reaches the compiled program and the frames exercise release. The review 7f r1 fix round added threshold frames, tiles whose current levels sum to c L plus or minus 25.5 c over a uniform previous level L on a shared depth, so the fast path decides a true change of 0.1 exactly, one R8 lattice step either side and either side of the R4 margin, at both entries and both presets. A fourth rig runs the production programs with a test-only call-site rewrite that exports the shipped path bits through the colour output, and the fact asserts its stored locks equal production's and minimum counts of fast-path and range-skip pixels. The review 7f r2 fix round added tolerance-edge tiles to the threshold frames, one neighbour of the tile centre at the NDC depth of the centre's view depth times one plus or minus 0.02 and up to three ulps either way, with levels that make that one acceptance bit decide the release, and a floor on the fast-path pixels centred there with both acceptances seen. It also compares the stored previous depth with D's every frame, and adds a fifth rig whose export leaves the visibility functions as shipped, whose locks and path words must equal the full export's. The review 7f r3 fix round added large threshold frames, 1104x612 internal, so columns 512 and 1024 and row 512 lie inside the frame with threshold and tolerance-edge tiles centred beside them, at both entries and both presets, under Halton offsets ordered so the designed frames meet both jitter signs on both axes. Every frame stays byte-identical to D, and every exported path word whose block fits carries the binade bit exactly where the rule predicts it.
- [ ] Proof 4, `TemporalVisibilityCompiledFormTests`: device-free SPIR-V counts of `OpImageFetch`, `OpImageGather` and `OpLoopMerge` for the four pass-two programs and for D's function alone, pinned as the D baseline.
- [ ] Verify `build-slot --label taa-7f1-build -- dotnet build KhaozEngine.slnx -c Release` with zero warnings, the new device-free classes headless, one focused Metal run `build-slot --heavy --label taa-7f1-metal -- env KE_GPU_TESTS=1 KE_GRAPHICS_BACKEND=metal-native dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release --no-build --filter "FullyQualifiedName~TemporalVisibilityEquivalence|FullyQualifiedName~TemporalVisibilityResolveAb"` with zero failed and zero skipped, then `dotnet format KhaozEngine.slnx --verify-no-changes --no-restore`, the dash, prose and file-size tree guards and `git diff --check`.
- [ ] Commit `test(taa): freeze visibility oracles before the cost cut`.

**Definition of done:** every oracle passes with the production side equal to D, the D baseline opcode counts and text hashes are pinned, and no production shader or behaviour changed.

#### Task 7f.2: Ship the R2 and R4 fast path and the R1 call-site gate

**Files:**
- Modify: `KhaozEngine.Render3D/Internal/ShaderSources.TemporalVisibility.cs` (D's function kept verbatim as the slow path, the decision function added).
- Create: `KhaozEngine.Render3D/Internal/TemporalVisibilityFastPath.cs` (C# generator of the straight-line fast path, compile-time block indices, explicit `fma()` for D's emitted `Fma` pattern).
- Modify: `KhaozEngine.Render3D/Internal/ShaderSources.TemporalAccumulate.cs` (R1 gate `floatBitsToUint(lockValue) != 0u` evaluated first, then the decision).
- Modify tests: `TemporalVisibilityEquivalenceProbe.cs` (`ProductionDecisionGlsl` calls the shipped decision and reports its path), `TemporalVisibilityEquivalenceGpuTests.cs` (fill the path placeholders), `TemporalVisibilityCompiledFormTests.cs` (target counts), `TemporalVisibilityFrozenDTests.cs` (delete `ProductionStillShipsFrozenD` only). Update the production anchors in `TemporalVisibilityFrozenD.ToD` only if a production anchor line moves. The frozen texts and their pins stay untouched.
- Rebake only the moved shader hash tables.

**Interfaces:** the shape is binding, the names are the implementer's.

```glsl
// Path bits for the oracle. The call site ignores them.
const uint VisibilityFastPath = 1u, VisibilitySlowPath = 2u, VisibilityRangeSkip = 4u;
bool temporalVisibilityReleases(vec2 uv, ivec2 centre, ivec2 last, vec2 previousUv, float expectedDepth,
    out float signedChange, out uint path);
```

- [x] Write the compilable contract first, then the behavioural RED: point the wrapper at the new function while it still calls D, set the fast path and R4 skip minimums and the reported-equals-predicted assertions, and record their failure on Metal.
- [x] Implement the fast path under the design review's preconditions (consecutive separable bases on both axes, block inside `[0, last]`), and since the review 7f r3 fix round the binade rule, D verbatim otherwise, and R4 with the `2^-13` margin, tested on the current sum and count: the bound and the margin both times the count, the same bound as the mean's. The first form on the mean shifted Metal's compile of D's final division by an ulp, and the sum-and-count form leaves the current mean its one use, in D's change. Gate the call site on the post-release `lockValue` bits, never on the stored lock or the hold.
- [x] The binade rule, added by the review 7f r3 fix round after hosted run 39362804399. FXC at the shipped flags compiles each chain's head either as written, `(at + 0.5) - J`, or reassociated, `(at - J) + 0.5`. In the decision helper D's chain came out reassociated, and of the fast path's six components two came out as written and four reassociated. Hosted run 39374926165 later showed every chain of the full accumulate and resolve programs as written, since FXC marks every instruction there precise. For integer `at` below `2^22` and `J` in (-0.5, 0.5) the written form rounds once. The reassociated form rounds `at - J` first, and the two agree unless `at - J` and `at + 0.5 - J` lie in different binades, which happens only where `at` is a power of two and `J` is positive. So a block any of whose three columns (rows) is a power of two, by the integer test `at > 0 && (at & (at - 1)) == 0`, under jitter whose bits read positive on that axis takes D's slow path, reported as `VisibilitySlowBinade`. `TemporalVisibilityBinadeTests` replays both orders in float32 for every `at` from 0 to 16384 against 67870 jitters, since the review 7f r4 fix round every one of the 256 offsets `TemporalJitter.Offset` gives at any phase count among them, and finds no difference wherever the rule keeps the fast path. The real offsets meet the binade in 201 pairs, all at a power of two under positive jitter. On Metal the shadowed yard's path-share fact shows the slow share at Quality going from 0.06% to 0.69% of display pixels (0.63% at a binade) and the fast share from 4.02% to 3.39%, with Native unchanged at 0.01% slow. The binade share depends on the scene, about 15% of the 4.07% of pixels the check runs on in this yard, so the bench quotes it beside its timing rows.
- [x] Assert the target SPIR-V counts: zero `OpImageGather` and no `OpLoopMerge` added by the fast path. Author a hosted Windows compile-only fact that disassembles the shipped `TemporalAccumulate` DXBC, compares its `dcl_indexableTemp` declarations with those of D's rewrite of the same program compiled the same way, and checks every fast path position chain against a signature D's own chains show, in FXC's reassociated order and, since the review 7f r1 fix round, with the provenance of each register operand beside the chain. Since the review 7f r3 fix round every chain head in D's program and the shipped one must be one of the two analysed orders with one of D's jitter elements, every shipped remainder from the division on must be one of D's, the shipped program holds D's chains plus six, and each program's chains are logged by form with any -0.5 position the reader could not read. The reader accepts a register beside the head through its provenance, and its facts run on two real FXC listings of the probe as well as synthetic text. Since the review 7f r4 fix round the fact also covers the fused `TemporalResolve` and `TemporalResolveHalf` programs, and it splits chains by loop depth: the shipped program's in-loop chains, by head form, jitter element and remainder, must equal D's exactly, and the fast path adds exactly six straight-line chains, three per jitter element, each written or reassociated. The reader also reads the precise form of the full programs, where a chain ends in `mul S` and `add -0.5` rather than one `mad`, which the r3 reader missed. The whole check runs device-free on the full programs' real FXC text from hosted runs 39374926165 and 39376184230, where every chain of the six full programs is written in D and shipped alike, and on mutated copies of it that must fail. Since review 7f r4 the in-loop comparison includes each head step's precise marking, each straight-line chain must carry the head markings, jitter element and remainder of one of D's chains together, each fixture is pinned to the SHA-256 of the GLSL it was compiled from, and one mutation per rule must fail on that rule alone. It runs in 7f.5. Static `OpImageFetch` counts rise, to 89 per accumulate program against D's 30, because D's function stays whole as the slow path with its looped 7 and the straight-line fast path adds its own 59. Per pixel they fall. A fast-path pixel issues 59 fetches against D's 101, or 43 under the range skip.
- [x] Run the Release build, the device-free classes of 7f.1 plus `TemporalResolveShaderTests|ShaderCorpusTests|VulkanSpirvByteEqualityTests`, then one `--heavy` Metal run of `TemporalVisibilityEquivalence|TemporalVisibilityResolveAb|TemporalVisibilityComparisonGpuTests|TemporalEntryIdentityGpuTests|TemporalCountProbeGpuTests|TemporalDebugViewGoldenTests`.
- [x] Commit `perf(taa): share the visibility history block and skip uniform footprints`.

**Definition of done:** zero mismatches, bitwise-equal change, every path covered, reported paths equal the predicted ones, the A/B meets the corrected comparison rule, entry identity stays exact, and the frozen pins are unchanged.

#### Task 7f.3: Move the current acceptance mask to pass one (R3-lite)

Withdrawn at `d9d72abbd` and restored by the 2026-10-21 correction above, with every fact below.

**Files:**
- Modify: `KhaozEngine.Render3D/Internal/ShaderSources.TemporalSplit.cs` (`TemporalPrepareFrag` writes the mask and the centre-finite flag to `oSurface.w`), `ShaderSources.TemporalPrepare.cs` (store and decode), `ShaderSources.TemporalVisibility.cs` (decision takes the mask), `ShaderSources.TemporalResolve.cs` (fused computes it inline with the same function).
- Modify tests: the equivalence wrapper derives the mask through the shipped mask function, and the A/B and entry identity facts run unchanged.

**Interfaces:** one GLSL function computes the mask from D's tolerance arithmetic in D's 3x3 order for both entries. The stored value is an integer below 1024, exact in half.

- [x] RED: a split-only readback fact compares the pass-one mask with the inline mask for every texel of a random frame and fails until pass one writes it. On Metal before the first pass wrote it, 2509 of 2560 stored masks differed.
- [x] Implement, keeping pass two at 9 current visibility loads and D's summation order. A fast-path pixel issues 41 fetches against D's 101, or 25 under the range skip. Static counts are 71 `OpImageFetch` and 14 `OpLoopMerge` per accumulate program. The 71 are D's 30 and the fast path's 41, above D's own 30 for the same reason as in 7f.2. 7f.3 measured pass one at about 75 to 120 ALU operations per internal texel, against the design review's estimate of 30.
- [x] Run the same build, device-free and single Metal selection as 7f.2.
- [x] Commit `perf(taa): carry the current visibility mask from the first pass`.

**Definition of done:** fused and split stay exact, the oracles stay at zero mismatches, and pass two's current-side fetch count matches the target.

#### Task 7f.4: Author the shadows-on whole-frame cost case, not run

**Files:**
- Create: `KhaozEngine.Render.Tests/Gpu/TemporalShadowedFrameCostPerfGpuTests.cs`.
- Modify: `KhaozEngine.Render.Tests/Gpu/measurements/taa-shadow-visibility/benchmark-request.md` (Proposal B as authored).

**Interfaces:** the existing cost block structure of `TemporalResolveCostPerfGpuTests` (warm frames, 12 timed frames, 15 rotating rounds, `Off` drift control) over a whole lit frame with `ShadowSettings.Mode` on and a moving sun-shadow edge, so PCF, the R8 MRT, the history copy and the resolve carry a real signal. It uses only API present at `v21.7.1`, so the same file can be placed on a `probe/` branch cut from `6c6529589` as the base ref.

- [ ] Write it and prove discovery only, with `dotnet test --list-tests` under the bench filter. Do not execute it.
- [ ] Commit `test(taa): author the shadowed whole-frame cost case`.

**Definition of done:** the case compiles at head and against the base API, it is discoverable by the bench filter, and nothing ran.

#### Task 7f.5: Run the gates

- [x] Full headless: `build-slot --heavy --label taa-7f-headless -- env KE_GPU_TESTS=0 dotnet test KhaozEngine.slnx -c Release --no-build --filter "Category!=LiveSocket"`, zero failures and zero warnings.
- [x] Broad local Metal: the Task 11 `TAA_CORRECTNESS` selection once with `--heavy`, which includes the new classes. Zero failed and zero skipped in the TRX. Since the review 7f r2 fix round the selection excludes every `CostPerf` class, and `--list-tests` under it names no cost, perf or bench class.
- [ ] Hosted correctness: the orchestrator dispatches `cross-platform-gpu.yml` on the candidate with `renderTestFilter` equal to that selection, legs all, plus the DXBC compile-only fact. Entry identity is exact on every backend. The frozen-D A/B follows the final comparison rule of the 2026-10-21 correction.
- [x] Shipping capture: rerun back15 and ground15 with the commands in [real-scene.md](../../../KhaozEngine.Render.Tests/Gpu/measurements/taa-shadow-visibility/real-scene.md) under the consumer-probe authorization. Contact-sheet pixels and region masks must be byte-identical to the shipping D record.
- [ ] Review by the task reviewer over 7f.1 to 7f.4. Record the gate in `verification.md` and commit `test(taa): record visibility cost cut correctness`.

**Definition of done:** every gate is green with evidence and the shipping capture is byte-identical to D.

#### Task 7f.6: Stop for owner approval of the billed T4 bench

- [ ] Add a proposal to `benchmark-request.md`: `gpu-hosted-bench.yml`, legs all, one dispatch per ref. The head is the 7f candidate. The base is `6c6529589`, or a `probe/` branch of it carrying only the shadows-on case file. The filter carries the existing resolve and outline cost cases, the shadows-on case and, on the head only, `TemporalVisibilityEquivalenceGpuTests` and `TemporalShadowedFramePathShareGpuTests`, which reads the shadowed yard's lock and check share outside the timed class. State the runs, legs, duration and estimated bill.
- [ ] Verify the request against the workflow inputs with `git diff --check`, commit `docs(taa): request the cost cut benchmark`, then stop. No dispatch without fresh explicit owner approval.

**Definition of done:** the owner has a reviewable request. The budget verdict, R5 and any Phase 2 approximation are later decisions.

### Task 8: Prove exact entry identity and integration edges

**Files:**
- Extend: `KhaozEngine.Render.Tests/Gpu/TemporalEntryIdentityGpuTests.cs`, `TemporalEntryFlipMemoryGpuTests.cs`, `TemporalVisibilityHistoryGpuTests.cs` and `TemporalVisibilityComparisonGpuTests.cs`.
- Modify only the shared arithmetic/binding seams implicated by a reproduced difference, including `ShaderSources.TemporalAccumulate.cs`, `ShaderSources.TemporalVisibility.cs`, `ShaderSources.TemporalAtDisplaySize.cs` and `TemporalSplitResolve.cs`.
- Evidence: `KhaozEngine.Render.Tests/Gpu/measurements/taa-shadow-visibility/entry-identity.md`.

**Interfaces:** Both entries call one helper with identical inputs and reduction order. Visibility histories, lock/confidence/marks and release/reset decisions are exact. Hardware/WARP colour is exact. Preserve only main's existing measured software-Vulkan colour exception, with no new epsilon.

- [ ] Resolve the four exact fused/split identity failures recorded in #1501 without widening any tolerance. Add a moving-shadow entry comparison which actually fires visibility release and the chosen weight response. Compare stored R8 bytes, previous jitter, colour, confidence and locks every resolving frame. Include Native, Quality, Performance and the outlined Quality path already covered by main.
- [ ] Add `EntryFlipKeepsVisibilityBytesExact`, interleaving entries, resolution changes and a repeated render. Verify shared debug/count evaluation reads the same pre-write history the resolve used.
- [ ] Prove the test fails with the reproduced archive arithmetic discrepancy. Fix shared operation ordering and precision boundaries rather than adding entry-specific rules. Preserve main's existing half-rounding construction. If `precise` is needed, constrain it to the affected shared calculation and document why.
- [ ] Run GPU `FOCUS='FullyQualifiedName~TemporalEntryIdentityGpuTests|FullyQualifiedName~TemporalEntryFlipMemoryGpuTests|FullyQualifiedName~TemporalVisibilityHistoryGpuTests|FullyQualifiedName~TemporalVisibilityComparisonGpuTests'` with `--heavy`.
- [ ] Commit `test(taa): require exact visibility decisions across entries` with any demonstrated arithmetic repair.

**Definition of done:** No inherited archive state or hardware-colour identity discrepancy remains. Hosted confirmation is still required, especially FXC and software Vulkan.

### Task 9: Document amendment 35 and stage one engine version

**Files:**
- Modify: `docs/design/TEMPORAL-RESOLVE-UPSCALING-DESIGN-2026-09-24.md`, `docs/INDEX.md`, this spec and plan with measured decisions.
- Sweep/update relevant descriptions in `docs/USING-KHAOZENGINE.md`, `KhaozEngine.Render3D/README.md`, `docs/CROSS-PLATFORM.md` and `README.md`.
- Modify together: `Directory.Build.props`, `CHANGELOG.md` and every version declaration named by `scripts/check-doc-versions.sh`.

**Interfaces:** Amendment 35 is the new visibility rule, with only the affected key-shadow decisions of 33/34 superseded. One release version contains both filter and signal. There is no new public tuning API.

- [x] Re-read current main, tags and version. Confirmed main `6c6529589`, version/tag `21.7.1` and free amendment 35. Staged `21.8.0` under the additive-minor rule for the new MRT/history/resolve-input contract. The original `21.6.0` proposal was already released. One bump contains the batch.
- [x] Append the amendment with the owner-selected lock release only, R8 qualification math, lifecycle, capability policy, baseline rationale and links to measured proof. Update algorithm and memory sections, living target/capability descriptions and the index. Preserve historical measurements as history.
- [x] Write one changelog entry explaining the shadow stability fix and any approved capability change. Put the version bump and newest entry in the same commit. Update guarded versions and do the full Markdown name/behavior sweep.
- [x] Verify `sh scripts/check-dashes.sh --tree`, `sh scripts/check-prose.sh --tree`, `sh scripts/check-file-size.sh --tree`, `sh scripts/check-agent-instructions.sh --tree` and `bash scripts/check-doc-versions.sh`. Run `git diff --check`. These are documentation checks, not a GPU proof.
- [x] Commit `docs(taa): record visibility release and its resource contract`, with the selected version/changelog change included or as the immediately associated `release(<version>): ...` commit under the engine convention.

**Definition of done:** Canonical design and living documentation describe the measured implementation, amendment numbering is reconciled, and one untagged version contains the batch. No release is started.

### Task 10: Confirm the full fix in the Grimhollow capture

**Files:**
- Create evidence: `KhaozEngine.Render.Tests/Gpu/measurements/taa-shadow-visibility/real-scene.md`, relative-path source/package/capture manifest and comparison tables. Create `KhaozEngine.Render.Tests/Gpu/measurements/taa-shadow-visibility/verify_capture_manifest.py` to validate source/package/loaded-binary hashes, required conditions/modes/regions and normal/overlay pairing without rendering.
- Read only: Grimhollow `fix/shadow-grain-capture` at `06601de4` and the archive's probe/private-feed instructions.
- Any consumer probe changes belong to a separately authorized probe worktree and durable probe commit. Do not modify Grimhollow main or its existing capture branch.

**Interfaces:** Consume `shadowgrain` headless captures and the archive's analysis definitions. Evidence-only overlays expose actual stored locks after the post chain. They never enter history or normal-image metrics. A diagnostic private build must prove its normal images match the shipping candidate.

- [x] Obtain the orchestrator's consumer-probe authorization and actual isolated probe location. Give each changed package set a unique private prerelease/feed. Do not overwrite the shared feed or reuse a stale same-version extraction. Record source SHA, package hashes and loaded renderer hash before interpreting images.
- [x] Prepare point-PCF historical context, the frozen bilinear-only/21.4.0-resolve control and the final candidate. Use the archive `shadowgrain` command shape, `--start-time 0.49777777777777776`, 80 warm-up and 16 measured frames. Each capture runs through `build-slot --heavy`.
- [x] Capture `back15` (back-edge, `1/15`, 5-minute day), `day30` (back-edge, `1/15`, 30-minute day), `back60` (back-edge, `1/60`, 5-minute day) and `ground15` (ground-edge, `1/15`, 5-minute day). Each condition gets Native, Quality and own FXAA, plus matching locked overlays for both TAA presets.
- [x] Run the preserved analysis definitions on saved normal frames. Report character and ground grain mean/p95, change mean, dots and locked share in every row. Retain the `0.05` dot threshold above both median and own FXAA. FXAA locked share is not applicable. Retain crops and complete metrics, including regressions.
- [x] The owner reviewed the real-scene clips on 2026-10-19 and selected D. The accepted costs are the absolute 8 px fast-shadow tails, Quality textured grain and Quality M 1.0 leading textured rim. Task 7d records measured D values and restates only those accepted bounds, as defined in spec section 8.
- [x] Require lower back15 character grain and dots than bilinear-only at both presets. Present every other region/condition regression with absolute values and images for an owner decision. If the owner rejects the remaining sparkle or the reset trades away too much stability, amend the design and return to the owning code task. Never rebaseline against that candidate.
- [x] Verify manifests with `python3 KhaozEngine.Render.Tests/Gpu/measurements/taa-shadow-visibility/verify_capture_manifest.py KhaozEngine.Render.Tests/Gpu/measurements/taa-shadow-visibility/manifest.json`. Require exit zero, matching hashes, all four conditions, three modes and both regions per build, and paired lock overlays for TAA. Rebuild tables from saved captures with the preserved archive report helper through `build-slot --heavy`, without rendering again. Commit `test(taa): record real scene visibility acceptance` with owner acceptance or an explicit blocked outcome. Do not mark this task done without the acceptance.

**Definition of done:** All four conditions and both regions have provenance-complete metrics, the owner has accepted the look, and game shipping branches remain untouched.

### Task 11: Run the complete local and hosted correctness gate

**Files:**
- Create evidence: `KhaozEngine.Render.Tests/Gpu/measurements/taa-shadow-visibility/verification.md`, test inventory and per-backend result summaries.
- Update only reviewed shader-corpus hash/golden artifacts required by the actual changed programs, using the documented relevant backend bake procedure.
- Read: `.github/workflows/cross-platform-gpu.yml`, `scripts/gpu-test-shards.py`, `docs/CROSS-PLATFORM.md`.

**Interfaces:** Consume the final candidate and immutable Task 3 controls. Compiler validation, real-scene acceptance, temporal correctness and golden checks are independent gates.

- [ ] Reconcile current main into the task branch through the orchestrator before final proof. Resolve there and review source/test baseline identities again. If reconciliation changes the frozen filter/control semantics, remeasure the affected controls explicitly. Never resolve on main or overwrite another worktree.
- [ ] Run `build-slot --label taa-final-build -- dotnet build KhaozEngine.slnx -c Release`, then `build-slot --heavy --label taa-final-headless -- env KE_GPU_TESTS=0 dotnet test KhaozEngine.slnx -c Release --no-build --filter "Category!=LiveSocket"`. Require zero failures and warnings. GPU skips in this headless pass are expected and do not satisfy the GPU gate. Run the single final broad Metal correctness process below through `build-slot --heavy`. Inventory discovered names before execution. It includes every temporal correctness GPU class, not only classes ending exactly in `GpuTests`, plus shadows/goldens. Every temporal cost class is excluded from the temporal branch by `FullyQualifiedName!~CostPerf`, which since the review 7f r2 fix round also covers `TemporalShadowedFrameCostPerfGpuTests`.

```bash
TAA_CORRECTNESS='((FullyQualifiedName~KhaozEngine.Tests.Gpu.Temporal&FullyQualifiedName!~Perf&FullyQualifiedName!~Bench)|((FullyQualifiedName~Shadow|FullyQualifiedName~Golden)&FullyQualifiedName!~Perf&FullyQualifiedName!~Bench))'
build-slot --heavy --label taa-final-metal -- env KE_GPU_TESTS=1 KE_GRAPHICS_BACKEND=metal-native dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release --no-build --filter "$TAA_CORRECTNESS" --logger 'console;verbosity=detailed' --logger 'trx;LogFileName=taa-final-metal.trx' --results-directory artifacts/taa-shadow-visibility/local-metal
```

- [ ] Check explicitly that `TemporalMovingShadeRimGpuTests` and `TemporalEntryIdentityGpuTests` are in the TRX, all expected temporal facts are discovered, and no selected GPU fact is skipped. Check every pass/failure and absolute tail/rim value. Re-run only after a source or test correction that justifies it. Do not retry unchanged failures.
- [ ] After local acceptance, the orchestrator dispatches `cross-platform-gpu.yml` on the exact candidate with `renderTestFilter` equal to that selection and all native backends. Retain run URL, commit, device class, validation, compiler output, per-class absolute results and zero-skip inventories for Metal, Direct3D 11 and Vulkan. A push's golden-only Windows/Vulkan coverage is insufficient.
- [ ] For filter/gather and visibility pixel tests, require all backend results. Preserve per-device controls and exact entry-state identity. If shader corpus hashes changed, verify emitted source deltas and update only the relevant backend artifacts. Goldens already within tolerance need no rebake. Any necessary golden bake is reviewed against the prior image before acceptance.
- [ ] Run whole-tree governance and doc-version guards, required Release checks and `git diff --check`. Commit `test(taa): record cross backend visibility correctness`. Keep the later full default-input `ci` and `cross-platform-gpu` release gates separate under the engine release ritual.

**Definition of done:** Every selected correctness fact and shadow/golden check passes on all three backends with evidence, the Task 1 RED is closed, entry identity meets main's exact contract, and no result depends on raised bounds. Nothing has been tagged or published.

### Task 12: Stop for fresh owner approval of the billed T4 bench

**Files:** Evidence request only in `KhaozEngine.Render.Tests/Gpu/measurements/taa-shadow-visibility/benchmark-request.md`. Read `.github/workflows/gpu-hosted-bench.yml`, `TemporalResolveCostPerfGpuTests.cs` and `TemporalOutlineCostPerfGpuTests.cs`. Do not change or dispatch them yet.

**Interfaces:** Consume the passing final candidate, frozen bilinear-only resolve control and historical point-PCF context. Report logical R8 payloads separately from measured future timing.

- [ ] Report current correctness/real-scene evidence and memory: signal plus two histories `10.546875 MiB` Native and `4.688416 MiB` Quality at 1440p, excluding alignment, retirement and MSAA. Add approved fallback-pass overhead if applicable. State that gather speed, bandwidth and frame cost remain unmeasured.
- [ ] Prepare a concrete paired benchmark proposal with immutable base/head refs, actual workflow `legs`, exact cost-test filter, resolutions/presets, run count, approximate duration and estimated bill from current runner rates. Cover the whole lit frame, including PCF, MRT traffic and history copy, as well as the existing resolve timers. If the existing cost fixtures omit those stages, propose the required isolated cost case for separate approval rather than claiming resolve-only timing measures the whole fix. Use no stress loop and no local timing substitute. Do not invent a cost acceptance budget if the owner has not set one.
- [ ] Verify the request with `git diff --check -- KhaozEngine.Render.Tests/Gpu/measurements/taa-shadow-visibility/benchmark-request.md` and a readback against the workflow input names. Require clean diff output and refs/filters/legs that exist, with no dispatched run. Present that proposal as its own approval request and stop. The wording must name what will run, where, how many runs and roughly how long. No dispatch until fresh explicit owner approval. If approval is declined, record the hold and leave integration/release with the orchestrator.

**Definition of done:** The owner has a reviewable cost request and the plan has stopped before billed work. Approval, a benchmark result, release, tag, publication and consumer adoption are separate follow-on actions.
