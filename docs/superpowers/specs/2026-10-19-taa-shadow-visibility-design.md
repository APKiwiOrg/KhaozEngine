# TAA shadow visibility production design

**Status:** Implemented on the task branch and staged for 21.11.0 as temporal-resolve amendment 35. The owner selected D on 2026-10-19. Final integration, correctness gates, billed benchmark approval, release and consumer adoption remain separate steps.

**Decision basis:** Owner ruling of 2026-10-19. Hold for the full fix. Bilinear 3x3 PCF and a lit visibility signal ship together in one engine release. A filter-only release or another colour-only lock heuristic does not satisfy the ruling.

**Baseline:** `a1e826635`, with `KhaozEngineVersion` at the already tagged `21.5.1`. This documentation change does not bump the version. The implementation plan is [2026-10-19-taa-shadow-visibility.md](../plans/2026-10-19-taa-shadow-visibility.md).

## 1. Problem and success

TAA retains isolated bright pixels on moving sun-shadow edges, especially at low frame rates. Grimhollow reproduces them at Native and Quality, while its FXAA control does not. The consumer issue is [Grimhollow #558](https://github.com/APKiwiOrg/Grimhollow/issues/558). The fix belongs in the shared lighting and temporal renderer.

Success requires all of the following together:

- Continuous comparison filtering preserves the existing shadow softness and cascade behavior.
- A change in the key-light visibility used to shade a surface releases inappropriate thin-feature history without sacrificing real missed thin features, dark texture or animated local lighting.
- Fast-shadow absolute tails and leading and trailing silhouette rims pass on the same filtered scene input as their control. An improved ratio denominator does not excuse an increased absolute tail. The owner-approved measured exception is recorded in section 8.
- Fused and split execution make identical visibility decisions and write identical visibility histories. Existing exact state and hardware colour identity remain exact.
- Every lit family, transparent writer, motion/dissolve variant and MSAA layout is covered. Lifecycle changes preserve reset, resize, repeated-render and zero steady managed-allocation contracts.
- Real-scene reports cover all four conditions at Native, Quality and own FXAA. Correctness on all three native backends precedes any separate cost decision.

Residual noise is not automatically a failed implementation, nor does the archive establish an FXAA-parity promise. The owner must accept the final measured look. No archive arm is itself accepted production code.

## 2. Evidence and limitations

Read-only sources, pinned to immutable archive commits:

- [Full real-scene record at 3ca76f11b](https://github.com/APKiwiOrg/KhaozEngine/blob/3ca76f11b/KhaozEngine.Render.Tests/Gpu/measurements/taa-game-shadow/real-scene/README.md), especially “Lit visibility signal diagnostic” and “Low-pass visibility comparison diagnostic”.
- [Visibility comparison tables](https://github.com/APKiwiOrg/KhaozEngine/blob/3ca76f11b/KhaozEngine.Render.Tests/Gpu/measurements/taa-game-shadow/real-scene/runs/visibility-mean/comparison-tables.md), [absolute tails](https://github.com/APKiwiOrg/KhaozEngine/blob/3ca76f11b/KhaozEngine.Render.Tests/Gpu/measurements/taa-game-shadow/real-scene/runs/visibility-mean/absolute-tails.md) and [individual local failures](https://github.com/APKiwiOrg/KhaozEngine/blob/3ca76f11b/KhaozEngine.Render.Tests/Gpu/measurements/taa-game-shadow/real-scene/runs/visibility-mean/local-failures.md).
- [Blocked colour-only attempt at 264bbc11c](https://github.com/APKiwiOrg/KhaozEngine/blob/264bbc11c/KhaozEngine.Render.Tests/Gpu/measurements/taa-game-shadow/lock-luma-kill/README.md). Port its guards, never its release rule. The final local run failed 11 tests, and hosted run `39119425055` failed 11 on each of Metal, Direct3D 11 and Vulkan. The failures include thin-feature, detail, flicker and fast-tail regressions. Counting failing facts differs from counting their failed measurements.

About 97% of the diagnosed dots coincide with stored thin-feature locks. Those locks keep lit history luma out of the clip after a shadow arrives. The point-compare 3x3 PCF seeds some locks. Continuous filtering reduces the seeds but does not remove the history mechanism. Colour statistics alone cannot distinguish a shadow change from a real thin feature missed by jitter.

At back15, baseline character grain is `0.014287 / 0.014156` at Native / Quality, with `1742 / 1733` pooled dots. Bilinear-only gives `0.008027 / 0.011053`, with `851 / 1166` dots. Useful signal arms reach roughly `0.0054 / 0.0085` with hundreds of dots, still above bilinear own FXAA grain `0.002607`. These rounded signal figures summarize candidates, not a new acceptance bound. Exact per-arm rows remain in the tables.

The selected filter measured character FXAA width `12.33` against `12.41` pixels and stayed within all 58 grid golden tolerances. It nevertheless changes the input to temporal acceptance tests. Bilinear-only misses Quality M 0.5 leading-flat error at `0.010485` against `0.01023825` and moves pinned spatial references. That is addressed by a controlled rebaseline, not by pretending the old scene input is unchanged.

The depth-qualified mean release-only arm increases Native 8-pixel advancing and retreating tails to `0.004627341 / 0.008277088`, from unset `0.004441862 / 0.006978498`. The large-change reset reduces them to `0.003520131 / 0.004718931`. It also adds trailing-flat rim misses, including Quality M 1 error `0.000273` against `0.00024585`, and temporal error `0.000297` against the same bound. Across the reported flat rims the errors are around `0.0003` to `0.001`, sometimes 10% to 30% over their bounds. The reset is a tuning basis, not permission to waive these misses.

Four inherited archive entry-identity facts fail. A typical stored-lock difference is `0.00048828125`, with colour as much as `0.010253906` apart. Production must fix the arithmetic and wiring, not import diagnostic tolerances. Jitter invariance and quantization equivalence remain proof obligations.

### Alternatives and final dispositions

| Alternative | Disposition and reason |
| --- | --- |
| Filter-only shipping fix | Rejected by the owner. Bilinear-only leaves 851 / 1166 back15 character dots and the lock mechanism remains |
| Colour-only mean/luma release | Rejected. Missed thin features and shadow changes can have identical statistics, and the production attempt fails the full suite |
| Disable or globally weaken thin-feature locks | Rejected. It removes the protection that the existing subpixel line and stable-detail tests require |
| Single bilinear comparison tap | Rejected. It leaves more dots and sharpens the ground edge from 6.27 to 4.68 pixels, changing the authored softness |
| Maximum of nine per-offset visibility differences | Control only. It compares individual offsets rather than the chosen low-pass means and retains the diagnostic's identity and shadow-guard failures |
| Release-only depth-qualified mean without a large-change response | Selected by the owner as D on 2026-10-19 after real-scene clips. The measured synthetic costs are explicitly accepted in section 8 |
| Unqualified mean or centre-only visibility | Control only. The owner selected depth-qualified 3x3 means to reject other surfaces while retaining neighborhood evidence |
| R32Float shipping storage | Matched numerical control only. It costs four times the R8 payload. The R8 proof must still isolate quantization rather than assume equivalence |

## 3. Grounded code boundaries

All paths below are relative to the engine repository.

| Existing owner | Change it owns |
| --- | --- |
| `KhaozEngine.Render3D/Internal/ShaderSources.Lighting.cs`, `pcfCascade` | Continuous comparison filter. `sampleKeyShadow` retains bias, normal offset, strength, selection, coverage fallback and cascade blend |
| `Internal/ShaderSources.Model.cs`, `ShaderSources.Terrain.cs`, `ShaderSources.TileGround.cs` under `KhaozEngine.Render3D/` | Base lit fragments. Skinned and foliage variants reuse these bodies, with motion construction in `ShaderSources.Motion*.cs` |
| `KhaozEngine.Render3D/Internal/RenderResources.cs`, `Scene3D.RenderTargets.cs`, `Scene3D.ResolveTargets.cs` | Optional R8 attachment, clear, MSAA resolve, target generation, framebuffer/pipeline rebuild and retirement |
| `KhaozEngine.Render3D/Rendering/ModelTargetBlends.cs`, `ModelRenderer*.cs`, transparent renderers | Correct attachment layout and destination preservation |
| `KhaozEngine.Render3D/Rendering/TemporalResolveRenderer*.cs`, `TemporalSplitResolve.cs` | Resolve inputs, bindings, history copy and previous-jitter publication |
| `KhaozEngine.Render3D/Internal/ShaderSources.TemporalAccumulate.cs`, `ShaderSources.TemporalSplit.cs`, `ShaderSources.TemporalAtDisplaySize.cs` | One shared comparison and release operation, called by fused, split and Native specialization |
| `KhaozEngine.Render3D/Internal/TemporalResolveTuning.cs`, `TemporalResolveMath.cs` | Named constants and CPU reference contract |
| `KhaozEngine.Render.Tests/Gpu/TemporalReviewBaseline.cs`, `TemporalMovingShadeRimGpuTests.cs` | Device-class control measurements and pinned spatial reference checks |

Create cohesive `ShaderSources.ShadowVisibility.cs`, `ShadowVisibilityTargets.cs`, `ShaderSources.TemporalVisibility.cs`, `TemporalResolveRenderer.Visibility.cs` and `TemporalVisibilityHistory.cs` for their new concerns. Do not paste the archive switch framework into shipping code or increase file-size baselines. Existing canonical owners retain their responsibilities.

## 4. Bilinear 3x3 comparison PCF

Keep nine comparison-filter taps at offsets `{-1,0,1}²`. Each tap bilinearly interpolates four point depth-comparison results. Never interpolate stored depths before comparing. Share the overlap as a 4x4 footprint, requiring 16 raw depth comparisons per cascade lookup instead of nine.

For texel fraction `f`, the shared one-dimensional weights are `[1-f, 1, 1, f]`. Multiply X and Y weights, sum the 16 comparison results, then divide by nine. Clamp every sampled texel centre inside its own cascade column. Preserve the same coordinate convention, depth comparison, bias, normal displacement, coverage, cascade strength and handoff blend. A blended receiver can require two footprints, or 32 comparisons.

Start with the explicit point-fetch implementation as the readable oracle. Evaluate `textureGather` through the actual GLSL to SPIR-V toolchain, SPIRV-Cross MSL and HLSL, and FXC validation. Four suitably placed 2x2 gathers can express the shared 4x4 footprint, but component ordering, sample placement and atlas-edge clamping must be proved with readback. A source gather operation is not proof of four physical memory transactions or a speedup.

Use gathers only if all three native backends compile and produce the oracle's comparisons and interpolated result, including cascade edges and half-texel cases. Otherwise ship the 16-fetch path. No new dependency or assumed hardware comparison-sampler feature is needed. Point-light shadow filtering is outside this change.

## 5. Lit visibility attachment

The signal is the exact local `keyShadow` returned by the existing `sampleKeyShadow` call, after all its strength, coverage and cascade operations. Assign that variable directly to an `R8UNorm` output. There is no second shadow lookup and no reconstruction from scene colour, normal or depth. “Exact” refers to the variable before the intentional UNorm quantization.

| Lit path | Required variants and semantics |
| --- | --- |
| Rigid models | Plain, keyed motion, alpha cutout, dissolve and inverted dissolve |
| GPU-skinned models | Skinning and motion palettes, cutout and dissolve |
| CPU-skinned models | Current/previous streamed vertices, cutout and dissolve |
| Foliage | Displaced geometry, motion, cutout and applicable dissolve variants |
| Splat terrain | Plain and motion variants, same lit visibility as its colour |
| Tile ground | Plain and motion variants, same lit visibility as its colour |
| MSAA layouts | Matched sample count, plus explicit single-sample R8 resolve before temporal sampling |
| Transparent model, billboard, beam, trail, overlay and silhouette writers | Explicit harmless output, `PreserveDestination` on visibility. Never erase the opaque receiver's signal |

Visibility occupies location 3 in a visibility-only layout and location 4 when motion already occupies location 3. All output and blend arrays must name their actual framebuffer slots. Colour-only passes use colour-only framebuffers. Audit shader-generated variants rather than searching only the base fragment strings.

Clear visibility to 1. Shadow-disabled geometry and out-of-coverage geometry write 1. A discarded cutout or dissolve fragment writes nothing and therefore preserves the revealed surface's signal. A frame without temporal use owns no visibility targets unless a test explicitly requests a visibility-only layout. Non-temporal pixels still change with the intended new PCF filter.

`MotionMath.IsTemporal` currently detects any fourth attachment. Adding R8 in that position without motion would misclassify it. Replace that inference with an explicit layout/format check, retain the motion slot and format, and test three, four and five attachment descriptions.

### Attachment-capacity decision before implementation

`GpuCapabilities.MinimumColorAttachments` is four. The motion-plus-visibility layout requires five. The split resolve's existing fallback to fused does not fix a five-output lit pass. No shipping code may attempt an unsupported framebuffer or silently use the old defective resolve.

This spec does not reduce the engine's supported Vulkan device floor.

**Orchestrator ruling, 2026-10-19.** A device whose `MaxColorAttachments` is below five keeps today's four-attachment lit layout, and the visibility release is unavailable there. The bilinear filter still applies. The resolve runs without visibility evidence, and the engine reports that capability once through its existing capability logging. This follows the degrade-rather-than-fail policy `GpuCapabilities.MaxColorAttachments` already documents. Metal and Direct3D 11 allow eight on every device, and the desktop Vulkan drivers the engine ships on, lavapipe included, report eight. So the degraded path covers rare hardware rather than a shipping target. A visibility companion pass was rejected: it would add a second geometry pass and shadow lookups to serve devices outside the engine's actual targets. Packing visibility into an existing attachment was also rejected. The motion target is two-channel `R16G16Float`, linear depth is single-channel `R32Float`, normal alpha carries the dynamic-geometry mask through MSAA resolve, and colour alpha is real coverage for transparent captures. Tests describe a four-attachment device. They assert that the degraded layout is selected without pipeline failure and that its frames match the filter-only resolve.

## 6. Visibility history, validity and lifetime

`TemporalResolveRenderer` owns a `TemporalVisibilityHistory` pair of internal-resolution R8 textures. It follows the same `TemporalHistory.ReadIndex`, `WriteIndex`, owner, device and target generation as colour and previous depth. It does not independently flip indices. Include the lit input's identity in `SameInputs` and in both fused and split binding caches.

After either entry point completes, one shared finish path copies the current lit signal into the visibility write texture with `CopyTexture`. Keep the read texture untouched through debug/count re-evaluation. Do not add a third fullscreen pass or change either entry point's two draw count just to store visibility.

Store previous jitter in a separate 16-byte uniform buffer, with XY in internal pixels. Keep the existing 256-byte resolve uniform contract intact. Update saved jitter only after a completed resolving render, with the same frame ownership as the copied visibility. Later renders in the same frame must not advance either. Reset, first use, changed owner/device, resized or recreated targets, AA/preset changes and camera cuts invalidate visibility wherever colour is invalid. Also gate a newly created or unseeded visibility generation even if the colour owner was valid.

Release sets before retiring the resources they name. Use the existing `GpuRetireQueue`, never free an in-flight texture directly. Retire both histories on TAA off or scene disposal. Steady frames create no resources and allocate zero managed bytes. Standalone synthetic resolves that omit a lit input bind an owned fully lit R8 fallback and copy it through the same path, preserving their previous behavior without null bindings.

R8 has a `1/255` step. Its maximum nearest conversion error is `1/510`, but backend conversion rules must be measured rather than assumed. Add a matched R32Float evidence control with the identical mean algorithm and thresholds. Compare threshold decisions and image metrics at R8 rounding boundaries. There is no single-channel R16Float format in the current GPU seam. Do not expand the seam merely to avoid this proof.

## 7. Depth-qualified mean comparison

Use the archive's `vis-mean-dq-0.1` basis in one shared GLSL helper. Let:

- `c` be the current internal centre texel, `o` an offset in `{-1,0,1}²`, `a = clamp(c+o, 0, S-1)`.
- `S` be internal size, `J` current jitter and `Jp` previous resolved jitter, both in internal pixels.
- `u` be current display UV and `up` the resolve's existing reprojected UV.
- `V` and `Vp` be current and previous visibility. Use the same existing expected previous surface depth as history validity, without inventing a second motion or depth rule.

The history position for each offset is:

```text
h(o) = up + (a + 0.5 - J) / S - u + Jp / S
```

Both reference depths, current centre and expected history, must be finite. Otherwise there is no visibility decision. Non-finite current samples and stored history samples never contribute to either mean or denominator. The shared helper enforces this with integer exponent tests, including under optimized FXC (see [non-finite-depth proof](../../../KhaozEngine.Render.Tests/Gpu/measurements/taa-shadow-visibility/non-finite-depths.md)).

Accept a finite current sample only when:

```text
abs(currentLinearDepth(a) - currentCentreLinearDepth)
    <= 0.02 * max(currentCentreLinearDepth, 1e-5)
```

For each accepted offset, expand history bilinear sampling into its four texel weights. Accept each finite stored history texel independently only when:

```text
abs(storedPreviousDepth - expectedDepth) <= 0.02 * max(expectedDepth, 1e-5)
```

Reject other surfaces before interpolation. Accumulate current visibility and its accepted count. Separately accumulate accepted previous visibility times bilinear weight and accepted history weight across those same offsets. Divide each sum by its own denominator. Do not normalize each history offset separately, pair unrelated extrema, or use a max-of-nine difference. Clamped duplicate offsets retain their normal footprint multiplicity.

If current count or previous weight is zero, make no visibility decision. If history is invalid, off screen or unseeded, perform no visibility release. Keep background on the existing temporal background path. Otherwise define signed `d = meanCurrent - meanPrevious` and absolute change `D = abs(d)`.

For `D > 0.1`, clear stored lock before hold is computed and set clip hold to zero. Do this regardless of ridge status or surface speed. The threshold applies to means of stored R8 values. Under round-to-nearest UNORM conversion, an exact input 0.1 stores as `26/255`, so its change from stored zero exceeds the threshold. A driver that truncates stores `25/255` instead. Release compares stored values, so that exact boundary case differs. An exact difference of 0.1 in the comparison domain does not release. This format quantization ruling preserves the measured R8/R32 boundary distinction. Preserve the existing colour-based shading release for its other responsibilities. Visibility release alone must not be folded into its `released` Boolean, because that would also reset accumulation and display share unconditionally.

### Owner selection, lock release only

The owner selected candidate D on 2026-10-19 after viewing side-by-side real-scene clips. No large-change response ships. Qualified visibility changes strictly above `abs(d) > 0.1` release the lock and clip hold, with depth tolerance `0.02`. Visibility release changes neither accumulated history weight nor display share, for rising or falling visibility. The existing independent shading release retains its own accumulation and display behavior. Task 8's shared precision boundary for carried-lock arithmetic remains unchanged.

Task 7b's rising-only reset at `d > 0.5` is superseded. Remove its helper, C# constants, GLSL twins and accumulated/display multipliers. Both entries, Native specialization, debug and count evaluation retain the shared lock-release rule. Invalid, unseeded, offscreen, background or empty qualified history makes no visibility decision. No public tuning API or experimental branch remains.

Task 7c measured these candidates with identical captures. A resets weight and display share to zero on a large rise, B retains 0.25, C retains 0.5, and D retains both unchanged.

| Candidate | Retained share on a large rise | Ground change Native | Ground change Quality | Back dots Native | Back dots Quality |
| --- | ---: | ---: | ---: | ---: | ---: |
| A | 0 | 0.006519368 | 0.007835931 | 364 | 760 |
| B | 0.25 | 0.005118248 | 0.005879304 | 364 | 759 |
| C | 0.5 | 0.004386340 | 0.004862258 | 364 | 759 |
| D | 1, no response | 0.003688205 | 0.003739381 | 364 | 759 |

The final clip capture confirmed D ground change at `0.003685 / 0.003770` Native/Quality, against bilinear-only `0.003665 / 0.003757` and A `0.006552 / 0.007902`. Back change was identical to A, and back dots remained `364 / 759` against A `364 / 760`. The response added ground shimmer without removing more held white dots. D restores ground change to the bilinear-control level, so the owner chose it and explicitly accepted the three synthetic costs in section 8.

The original eight-arm comparison remains historical evidence in `KhaozEngine.Render.Tests/Gpu/measurements/taa-shadow-visibility/reset-tuning.md`. Task 7d's D measurements and accepted bounds are in `KhaozEngine.Render.Tests/Gpu/measurements/taa-shadow-visibility/owner-release-only.md`.

## 8. Test methodology and acceptance

### Rebaseline the changed scene input first

The invariant is “the resolve is no worse than before on the same input”. An old point-PCF input and a new bilinear-PCF input are not the same experiment.

1. Freeze the 21.4.0 resolve behavior and shader/tuning source identities. At `a1e826635`, its accumulation, preparation, display reconstruction and tuning files are unchanged from `v21.4.0`. Confirm this again at execution. No visibility resources, helper or reset may be present in this control.
2. Measure old point-PCF plus that resolve, then bilinear-only plus that same resolve, with identical fixture settings, seeds, phase, frame counts, dimensions and device class. Capture spatial references again with the new filter and the existing non-temporal supersampled reference procedure.
3. Save raw figures for every affected rim row at hardware, WARP and lavapipe. Re-record the pinned spatial-reference values and re-measure the temporal acceptance baseline on the bilinear input. A local Metal number cannot stand in for WARP or lavapipe.
4. Commit these measurements and the new filter-specific rim baseline separately, before any signal implementation lands. Write in the test why the input changed. Keep the existing tolerance `0.1`, rounding `0.0000005`, reference-identity tolerance `0.000001`, metric definitions, masks and case inventory. The derived acceptance remains `(baseline + 0.0000005) * 1.1`.
   The [rim GPU test](../../../KhaozEngine.Render.Tests/Gpu/TemporalMovingShadeRimGpuTests.cs#L37) applies the reference-identity tolerance through [TemporalReviewBaseline.Check](../../../KhaozEngine.Render.Tests/Gpu/TemporalReviewBaseline.cs#L283) as `2 * Rounding`.
5. Preserve the historical v21.2.0 tables for unrelated diagonal and other tests. Update only measured filter-affected rows and their provenance, not the shared helper's margin. Judge all later signal/reset changes against the frozen bilinear control.

This is not loosening a bound. It preserves its meaning on the same input. The signal cannot supply its own baseline. Outside the explicit owner-approved costs below, no bound may change without a measured absolute value that did not regress and a written rationale. A ratio passing is insufficient. Do not rebake a golden to conceal a changed look or incorporate a failed signal into the control.

### Regression and identity gates

Port the textured receiver, dark surface and flickering/moving local light guards from `264bbc11c`. Retain Native textured grain `<= 0.0113` and per-device Quality grain bounds (hardware `<= 0.008032`, lavapipe `<= 0.008040`, WARP `<= 0.008014`), dark non-black proof, and the paired error/grain/temporal-error detail bounds. Collect per-device controls where needed, without using candidate output to raise them. Report Balanced and exercise every existing preset in the full suite.

Add actual R8 pixel readbacks for every lit path and preserved transparent path, shadow-disabled and cutout cases, MSAA resolve, strength/cascade handoff and four-attachment capability handling. Add deterministic tests for jitter-only motion, reprojection, near/far rejection, no accepted weights, image edges, reset lifecycle and unchanged visibility over a missed thin feature. CPU arithmetic tests complement, never replace, the rendered result.

Run every `Temporal*GpuTests` correctness class, excluding only temporal timing-cost benchmarks, plus `TemporalMovingShadeRimGpuTests`, `TemporalEntryIdentityGpuTests` and the shadow/golden selection. Inventory discovered tests and TRX outcomes, with zero skipped selected GPU facts. Never use the archive's original eight-test shortlist as the production gate. Keep absolute 1-pixel and 8-pixel tails, both directions and presets, alongside ratio and rim figures.

Local Metal correctness comes first. Then dispatch `cross-platform-gpu.yml` on the exact candidate for Metal, Direct3D 11 and Vulkan. A filtered correctness run excludes timing, retains compiler/validation checks and records device-class outputs. The ordinary full default-input workflow remains the later release gate under the release ritual. New or changed goldens and shader-corpus baselines follow the relevant hosted bake procedure, with reviewed deltas and no tolerance changes.

Entry identity is exact on Metal, on the T4 on Direct3D 11 (under the NVIDIA D3D11 rule) and Vulkan, and on WARP. There production's fused and split entries store byte-identical colour, confidence, locks, marks, R8 visibility and depth, with the same release and reset decisions. On llvmpipe (software Vulkan) the state, R8 and depth are exact and the colour is within main's amendment 23 bound, `0.0625` on at most `0.0025` of the values compared (`TemporalSoftwareVulkanColourRule`), since llvmpipe's compiler orders the two programs' arithmetic differently from frame 0. A large-frame case runs both entries on the frozen-D A/B's large threshold frames, which reach columns 512 and 1024 and row 512 under positive jitter, and holds D prime's fused and split entries byte-identical on the same inputs under the same llvmpipe colour rule (review 7g r1).

The frozen-D A/B of the cost cut has two references. D prime (`TemporalVisibilityFrozenD.ToD`) is frozen D with production's barrier-ordered lock release, and every rule below compares against it. Shipped D (`ToShippedD`) keeps D's original `precise` lock release. On Metal, which lowered that helper as the barrier form computes, production must also equal shipped D byte for byte every frame. Elsewhere shipped D is reported only, since WARP and lavapipe divide by three where the barrier form multiplies by its rounded reciprocal. The A/B compares production's history with D prime's byte for byte on every backend except hardware NVIDIA Direct3D 11. That driver compiles confidence arithmetic unrelated to the visibility check differently whenever the visibility code changes, even on frame 0 where the check does not run, while WARP running the same DXBC matches D bit for bit (T4 run `39519233674`). There stored locks, R8 and depth must equal D's. Every colour and confidence value must keep D's non-finite class (NaN, positive infinity, negative infinity or finite), and only finite colour and confidence differences are printed as counts and maximum half steps without being asserted. Entry identity on that device stays exact between production's fused and split entries, and its production against D comparisons assert only the same non-finite classes. Orchestrator rulings of 2026-10-21 under the owner's delegation.

Task 7g.5 integrates the barrier lock release, the r1 entry-identity cases and the 7g.4 gather policy. Direct3D 11 and Vulkan use twelve gathers for the exact current and history footprints. Metal retains nine current-block loads and uses eight history gathers, since the current-block substitution changes subsequent arithmetic there despite exact raw reads. The Vulkan withdrawal a43599546 is reverted because identical lavapipe follow-walk colour failures occur with and without gathers, while its raw gather oracle has zero mismatches. Main's amendment 23 software-Vulkan colour bound above applies unchanged. Large-frame and moving-shadow comparisons must remain exact. DXBC was recaptured after integration, and the existing writers reproduced all backend hashes and corpus rows without drift. Free all-leg correctness run 39571324204 at 211e42544 passed all three renderer legs and Vulkan sync validation, with exact large-frame and moving-shadow cases and unchanged accepted software costs. The [merge-round record](../../../KhaozEngine.Render.Tests/Gpu/measurements/taa-shadow-visibility/7g5-hosted.md) holds the per-case proof. Gather instruction counts do not establish a measured speedup.

The outside-cost variants V5 (visibility infrastructure off) and V4 (V5 without the lock release's precise markings, redefined by Task 7g.3a as barriers removed and never run in that form) are retired. V5 needed a visibility-disable switch inside the shipped renderer and scene, which the production delta forbids. Their one run, `39519219228`, is kept as history in `benchmark-result-2.md`. The decomposition keeps V0 shipped, V1 check removed and V3 D prime, all rewrites through the existing fragment source hook.

### Owner-accepted synthetic costs, 2026-10-19

The owner accepted these three costs after the real-scene D clips. One focused MetalNative run on Apple M2 Max remeasured D. It had exactly the three expected failing facts and no other failure, with zero skips. Only the rows below change. Historical point-PCF tables, the other bilinear controls, all reference values, case inventories and tolerances remain unchanged.

| Accepted cost | Measured D value | Old bound | New bound |
| --- | ---: | ---: | ---: |
| Hardware Native 8 px retreating textured absolute tail | 0.007221076276167364 | 0.007138970657410495 | 0.007221576276167364 |
| Hardware Quality textured grain | 0.008031049526854536 | 0.008 | 0.008032 |
| Hardware Quality M 1.0 leading textured rim error | 0.0030106876217401947 | 0.00271205 | 0.00331265 |
| Same rim, temporal error | 0.004819472651560228 | 0.00432135 | 0.00530145 |

The absolute-tail check retains its existing `baseline + 0.0000005` formula. Only the failing hardware Native 8 px retreating textured row takes D's measured baseline instead of paired control `0.007138470657410495`. Other tails keep their paired controls, both presets and directions, flat/textured regions and actual 1 px denominators. The grain check remains a fixed target, rounded upward to the six-decimal reporting place so the measured `0.008031049526854536` is included. Native stays at `0.0113` and Balanced remains report-only. The rim row retains `(baseline + 0.0000005) * 1.1`, with six-decimal baselines `0.003011 / 0.004819` replacing `0.002465 / 0.003928`. Its change and spatial-reference values do not change. No unmeasured WARP or lavapipe row borrows the hardware cost.

Hosted run [39215298727](https://github.com/APKiwiOrg/KhaozEngine/actions/runs/39215298727) subsequently measured the software costs on `3f7b8749f`. The owner accepted each driver separately with this reason: "Owner accepted per driver on 2026-10-19 after hosted run 39215298727: WARP and lavapipe show the same lock-release costs." Hardware keeps its original rows. The added software rows are below. WARP grain comes from filtered run 39221506634 on `0e48f0c36`, as recorded after the table.

| Driver and accepted cost | Measured D | Old bound | New bound |
| --- | ---: | ---: | ---: |
| WARP Native 8 px retreating textured absolute tail | 0.007222797931709032 | 0.007152903227222303 | 0.007223297931709032 |
| lavapipe Native 8 px retreating textured absolute tail | 0.007245504857927269 | 0.00716823283842791 | 0.007246004857927269 |
| WARP Quality textured grain | 0.008013324991270224 | 0.008 | 0.008014 |
| lavapipe Quality textured grain | 0.008039903208144449 | 0.008 | 0.008040 |
| WARP Quality M 1.0 leading textured rim error | 0.003134108234483462 | 0.00278355 | 0.00344795 |
| WARP same rim temporal error | 0.004831370554127536 | 0.00428835 | 0.00531465 |
| lavapipe Quality M 1.0 leading textured rim error | 0.0030749369871157866 | 0.00286275 | 0.00338305 |
| lavapipe same rim temporal error | 0.0047599525602309255 | 0.00438845 | 0.00523655 |

The WARP/lavapipe paired tail controls remain `0.007152403227222303 / 0.00716773283842791`. The same single tail case per driver takes measured D, using the unchanged additive rounding allowance. WARP rim error/temporal baselines become `0.003134 / 0.004831`, lavapipe `0.003075 / 0.004760`, at the existing six-decimal convention. Fields 2 and 4 and the shared formula remain unchanged. Grain is scoped per driver, with hardware `0.008032`, lavapipe `0.008040` and WARP `0.008014`. Filtered hosted run 39221506634 on `0e48f0c36` measured WARP `0.008013324991270224` in both grain guard callers. It exceeds the historical `0.008` but stays below the owner-authorized `0.008032` cap. Its own bound is the smallest upward six-decimal value that includes that readback. Native stays `0.0113` and Balanced stays report-only. The durable record is `KhaozEngine.Render.Tests/Gpu/measurements/taa-shadow-visibility/owner-release-only.md`.

Each affected test records this reason: "Owner accepted on 2026-10-19 after real-scene clips: the visibility lock release removes held shadow sparkle, and this synthetic value is the measured cost of that release. No large-change response ships."

Task 8 repaired exact fused/split identity with the shared precision boundary, tracked in #1501 until integration. Task 7d retains that fix and changes the response tests and all moving-shadow identity cases, including outlined Quality, to prove rising and falling lock release with unchanged weight and display share. Every per-frame colour/state bit comparison, R8 byte comparison, previous-jitter comparison and proof that release fires remains required.

The amended order after the owner's look and Task 7c is Task 7d, Task 9 docs/version, Task 11 full gate, then Task 12 bench request. Selecting D does not authorize a release, version change or billed benchmark.

### Real-scene acceptance

Use the headless `shadowgrain` capture introduced on [Grimhollow 06601de4](https://github.com/APKiwiOrg/Grimhollow/commit/06601de4), from a separately authorized disposable consumer probe. Read the archive's private-feed and probe instructions. Never repin a shipping game branch for this evidence.

Use a unique private-feed prerelease for each changed package set. Record source SHA, package identity/hash, vendored and loaded renderer hashes, capture settings, region masks and control identities. Capture-only instrumentation may expose stored locks and release decisions after history. It must not change shipping resolve arithmetic or enter normal measurements. Keep instrumentation/probe commits separate and prove normal frames match the uninstrumented candidate.

| Condition | Spot | Frame time | Day length |
| --- | --- | --- | --- |
| back15 | back-edge | `1/15` | 5 minutes |
| day30 | back-edge | `1/15` | 30 minutes |
| back60 | back-edge | `1/60` | 5 minutes |
| ground15 | ground-edge | `1/15` | 5 minutes |

All use start time `0.49777777777777776`, 80 warm-up frames and 16 measured frames, TAA Native, TAA Quality and each build's own FXAA. Report character and ground grain mean/p95, change mean, dots and locked share for the unchanged-resolve control and final candidate, with old point-filter results for context. FXAA locked share is not applicable.

Keep the archive's dot definition, luma more than `0.05` above both the local 3x3 median and own FXAA, pooled over pixel-frames. Overlay pixels never contribute to normal metrics. Retain full tables and crops, not only favourable back15 character rows. Back15 character grain and dots must improve over bilinear-only at both presets. Any other regional grain, change or dot regression needs an explicit owner decision after seeing absolute values and images. Lower locked share alone is not quality. The owner accepted D and the three measured synthetic costs above after these real-scene clips on 2026-10-19. Exact entry identity and all other correctness gates remain required.

## 9. Cost and risks

At a 2560x1440 display, Native is 2560x1440 internally and Quality is 1707x960.

| R8 resource | Native MiB | Quality MiB |
| --- | ---: | ---: |
| Lit attachment | 3.515625 | 1.562805 |
| One history | 3.515625 | 1.562805 |
| Both histories | 7.031250 | 3.125610 |
| Signal plus history pair | 10.546875 | 4.688416 |

These are logical non-MSAA payloads, excluding alignment, retirement overlap, fallback and the 16-byte previous-jitter buffer. MSAA adds a multisampled R8 attachment. A four-attachment companion pass, if chosen, needs separate accounting.

Lighting adds a scalar store and no repeated shadow lookup. PCF adds seven raw comparisons/fetches per unblended lookup in the explicit implementation. The comparison examines nine current visibility/depth samples and up to four previous visibility/depth texels per accepted offset. History copy reads and writes one byte per internal pixel. Cache behavior, render-target compression, bandwidth, frame cost and gather benefit are unmeasured.

Material risks are five-attachment support, per-sample R8 MSAA support/resolve behavior, threshold quantization, depth qualification on moving silhouettes, doubled jitter correction, stale history generations, and fused/split compiler reassociation. The finite tuning round may fail to remove both tail and rim regressions. A failed round blocks shipment and requires evidence-led design revision.

The billed T4 `gpu-hosted-bench.yml` is a separate stop. After correctness and real-scene proof, present exact refs, device legs, test filter, paired run count, expected duration and estimated bill to the owner. Obtain fresh explicit approval before dispatch. No local timing/load loop substitutes for it. This plan does not authorize any timing or load run.

### Measured resolve cost and the Phase 1 rule

The owner-approved T4 bench is recorded in [benchmark-result.md](../../../KhaozEngine.Render.Tests/Gpu/measurements/taa-shadow-visibility/benchmark-result.md). Against `v21.7.1` the head adds 3.9 to 4.8 ms on Direct3D 11 and 2.1 to 2.9 ms on Windows and Linux Vulkan to resolve and sharpen at a 2560x1440 display, about twice that at 3456x2234. The growth is all in the split's second pass and follows the display size. Pass one, the outline cost and the `Off` drift control stay within noise. The fixtures run with shadows off, so PCF and a real shadow signal are not in these numbers.

On 2026-10-20 the owner ruled to optimize before merging, with a budget of at most 0.5 ms resolve growth over `v21.7.1` at 1440p on every T4 backend. Phase 1 of the cost cut is exact: every output pixel and every observable visibility decision stays bit-identical to D, proved against a frozen copy of D's text. The one exception is hardware NVIDIA Direct3D 11, where finite colour and confidence differences against D are reported rather than asserted, as section 8 records. Any approximation is a separate owner look. The work and its oracles are plan Task 7f. A further billed bench needs fresh owner approval.

## 10. Amendment, version and handoff

This is **amendment 35** in [the canonical temporal-resolve design](../../design/TEMPORAL-RESOLVE-UPSCALING-DESIGN-2026-09-24.md). Task 9 reconciled main and the document before allocation. The amendment records the shared visibility-release contract, history/format/lifetime rules, controlled rebaseline rationale, owner-selected lock release and measured evidence. Only the affected key-shadow decisions from amendments 33/34 are superseded. Their independent colour-based shading behavior remains. Algorithm and memory sections, living docs and `docs/INDEX.md` carry the current contract.

Task 9 stages one **minor**, `21.8.0`, for the combined package-bearing batch. Fresh reconciliation found main at `6c6529589` with version `21.7.1` and newest tag `v21.7.1`, so no version was staged. The contributor rule is additive minor, fix patch and breaking major. Although consumer calls need not change, the new lit MRT, persistent history pair and resolve input add a rendering resource and capability contract. The original `21.6.0` proposal was already released by concurrent work and is historical, not a second bump.

The version and matching newest changelog entry land together once in Task 9, with guarded declarations updated. The original spec and intermediate evidence-only commits did not bump. Integration, release, tag, durable publication and Grimhollow adoption belong to the owning orchestrator and their separate gates. Both parts remain on the task branch until the combined fix is approved and verified.

The four-attachment policy is settled by the ruling in section 5. Candidate D and the three synthetic costs are accepted by the owner on 2026-10-19. Fresh billed-benchmark approval remains open. Gather support is an implementation proof question, not an assumption requiring the owner to guess.
