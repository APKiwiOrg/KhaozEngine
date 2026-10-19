# Temporal visibility non-finite depth qualification

Status is DONE_WITH_CONCERNS. The required fix and proofs are complete. The permitted Vulkan grain guard remains red.

Task 7e fixes the shared visibility footprint. It does not change the Direct3D compiler policy, release threshold, accumulation, display share or any other temporal guard.

## Root cause and evidence

The failure is in optimized FXC comparison lowering. GLSL and emitted HLSL both retain `!(abs(depth - referenceDepth) <= tolerance)`. At the shipped `D3D11ShaderDebug.Optimized` flags, FXC turns that into an ordered greater-than test. A NaN then skips neither current samples nor history corners.

The original report is [run 39200900599](https://github.com/APKiwiOrg/KhaozEngine/actions/runs/39200900599), Windows job 117893515499. On Microsoft Basic Render Driver, the original `empty current` case reports `valid 1, signed 1, release 1`. Both the shared-helper fact and the invalid-footprint fact fail. The original facts stop at that case, so later cases have no baseline WARP assertion evidence.

That run's artifact listing contains no `hlsl-evidence-*` artifact. No substitute artifact was downloaded. The unchanged shader was emitted locally through `KE_WRITE_SHADER_CORPUS=1 KE_SHADER_CORPUS_DUMP=<scratch>`, the established corpus path. Its current and history guards are still negated `<=` expressions. The diagnostic commit below also logs the exact emitted HLSL given to FXC.

[Diagnostic run 39216044678](https://github.com/APKiwiOrg/KhaozEngine/actions/runs/39216044678), Windows job 117940080797, runs commit `d1f76b6899623770bafce9467e7f2545021e2400`. The temporary `TemporalVisibilityFxcDiagnosticTests` in that commit compiles the actual probe's emitted HLSL with three flag sets, then disassembles the resulting DXBC. It uses the repository's SPIRV-Cross and FXC path without a cache. The same run executes all 15 new depth cases as independent GPU theory rows.

The optimized current guard disassembles as follows. `r1.z` is current tolerance and `r6.z` is the difference.

```text
add r6.z, -r0.z, r6.z
lt r6.z, r1.z, |r6.z|
if_nz r6.z
  ...
  continue
```

The history guard uses the same lowering with `r2.y` and `r9.w`. In both cases the comparison is false for NaN, so the continue is lost. With `Optimized | IeeeStrictness`, the corresponding instructions are instead as follows.

```text
add [precise(z)] r6.z, -r0.z, r6.z
ge [precise(z)] r6.z, r1.z, |r6.z|
if_z r6.z
  ...
  continue
```

That implements the original unordered rejection. Flag values are 32768 (`0x8000`, shipped optimized), 5 (debug plus skip optimization) and 40960 (`0xa000`, optimized plus IEEE strictness). The unoptimized compilation also retains the ordered `ge` and zero branch for both guards. This is compiler evidence, not a claim that the strict or debug variants were rendered. Only the shipped optimized variant was rendered in the diagnostic run.

Microsoft documents that [IEEE strictness avoids optimizations that break IEEE rules](https://learn.microsoft.com/windows/win32/direct3dhlsl/d3dcompile-constants), and that [ordered comparisons with NaN return false](https://learn.microsoft.com/windows/win32/direct3d11/floating-point-rules). The emitted HLSL, DXBC flag comparison and executed WARP failures together locate the defect after HLSL emission and in the optimized comparison lowering.

An additional finite-qualification gap is independent of that rewrite. A positive-infinite centre or expected depth gives an infinite tolerance, so even ordered comparisons can admit finite samples. The baseline scalar oracle and Metal both expose it. Explicit finite checks on both reference values are required as well as on the samples.

## Change and regression cases

`ShaderSources.TemporalVisibility.cs` tests `(floatBitsToUint(depth) & 0x7f800000u) != 0x7f800000u`. This integer exponent predicate rejects every NaN and both infinities without an ordered floating-point comparison. The helper returns invalid for a non-finite centre or expected history depth. A non-finite current neighbour or stored history corner is rejected before contributing to its independent denominator.

Finite references and samples retain the same tolerance, arithmetic, offsets, bilinear multiplicity and reductions. The emitted fixed HLSL contains four live `asuint(value) & 2139095040u` guards, covering the two references and the two sample sites. The final source has no compiler flag change or diagnostic path. The temporary disassembly test and its probe exposure are removed, with their reproducible source preserved in the diagnostic commit.

The 15 added cases cross NaN, positive infinity and negative infinity with five placements. They cover an invalid current centre, an invalid current neighbour with visibility 1 among eight zero-valued qualified neighbours, an invalid expected history depth, entirely invalid stored history, and mixed stored history with one finite bilinear corner. Expected results are literals. The neighbour contributes zero, and the mixed history yields signed change 0.25 by retaining only the finite corner. The independent scalar oracle uses `float.IsFinite`, rather than mirroring the shader bit test.

The existing original cases remain, including exact and adjacent strict release boundaries, the two percent depth boundaries, border multiplicity, aggregate weights, jitters, offscreen, background and unseeded history. The new GPU theory reports each added case separately, so an early failure cannot conceal the others.

## RED evidence

| Check | Passed | Failed | Skipped | Exit |
| --- | --- | --- | --- | --- |
| Scalar oracle with new cases, before finite checks | 0 | 1 | 0 | 1 |
| Metal, 15 new cases, before finite checks | 13 | 2 | 0 | 1 |
| WARP diagnostic, 15 GPU cases plus compiler diagnostic | 8 | 8 | 0 | 1 |

The scalar assertion fails on positive-infinite centre. Metal fails positive-infinite centre and expected history depth, reporting valid 1, signed 1, release 1. The device is MetalNative on Apple M2 Max.

WARP fails all five NaN placements, positive-infinite centre and expected depth, and negative-infinite centre. A NaN current neighbour produces signed change 0.11111111 instead of zero. Mixed NaN history produces -0.3125 instead of 0.25. Empty NaN history incorrectly reports valid 1. WARP passes the seven other GPU cases and the disassembly diagnostic. Its separate pre-suite FXC validation passes 30 tests with zero skips.

## Final verification

The full solution Release build passes with zero warnings and errors. Local final results are below. TRX counters confirm every selected test executed.

| Local check | Passed | Failed | Skipped | Exit |
| --- | --- | --- | --- | --- |
| Required CPU classes | 1347 | 0 | 0 | 0 |
| Metal visibility comparison and entry identity | 56 | 0 | 0 | 0 |
| Shader corpus and all three backend hash writers | 9 | 0 | 0 | 0 |

The CPU selection matches class names against `Temporal|Motion|Binding|Uniform|Ubo|Wiring|History|Shader` and enumerates their ordinary Fact and Theory methods, excluding GPU attributes so no disabled GPU case appears as a skip. It includes ordinary shader hash validation after baking. Metal runs on Apple M2 Max. Both the new cases and all original scalar-footprint cases pass. Fused and split colour, confidence, lock and mark identity remain exact, including outlined Quality. The grain guard passes locally, with Native grain 0.010346 against bound 0.011300.

The bake changes only the fragment rows for ten programs in each backend hash table and the corresponding 30 fragment emission rows in the live corpus. Vertex and layout rows are unchanged. Changed C# whitespace verification passes after correcting one fixture initializer. The full solution formatter reports workspace-load warnings, while the Release compiler reports zero warnings. The warning is retained as a coverage qualification, not a compiler diagnostic. [Task 8's formatter investigation](entry-identity.md#repository-verification) identifies an analyzer project reference without matching metadata for `KhaozEngine.Localization.Analyzers/KhaozEngine.Localization.Analyzers.csproj` in the formatter's Debug workspace. An exit-zero formatter result with that warning does not independently prove that every project loaded. The Release build and selected CPU/GPU outcomes above provide their own, separate evidence.

The final hosted proof is [run 39217436001](https://github.com/APKiwiOrg/KhaozEngine/actions/runs/39217436001) at `a030add659efd50ff0c05bcc85d607939ea88e0f`, dispatched with `legs=all` and `renderTestFilter=FullyQualifiedName~TemporalVisibilityComparisonGpuTests|FullyQualifiedName~TemporalEntryIdentityGpuTests`. The workflow completes with failure solely because of the permitted Vulkan grain fact. Its selected GPU results are below.

| Hosted leg | Device | Job | Passed | Failed | Skipped |
| --- | --- | --- | --- | --- | --- |
| `direct3d11-native` | Microsoft Basic Render Driver (WARP) | 117944545971 | 56 | 0 | 0 |
| `vulkan-native` | llvmpipe, LLVM 20.1.2, 256 bits (lavapipe) | 117944545780 | 55 | 1 | 0 |
| `metal-native` | Apple Paravirtual device | 117944545830 | 56 | 0 | 0 |
| Total selected | | | 167 | 1 | 0 |

Every leg executes all 56 selected cases. These comprise 48 visibility comparison cases, including all 15 new non-finite rows, and 8 entry identity cases. Detailed console logs show 56 pass/fail records per leg, with no skipped selected records or unaccounted cases. All 45 new non-finite rows across the three backends pass. Both original failing WARP facts pass. The cases after `empty current` now run and pass on every leg, including unmatched history, zero weights, clamped duplicates, offscreen, background, unseeded history, motion and jitters. Entry identity passes on every backend using its unchanged policy.

The only selected failure is `TemporalVisibilityComparisonGpuTests.TexturedRidgesMeetTaskOneGrainGuard` on Vulkan. Quality grain is 0.008040 against 0.008032, a miss of 0.000008 at the printed precision. Native passes at 0.010344 against 0.011300. Balanced is report-only at 0.007531. WARP Quality passes at 0.008013, and hosted Metal Quality passes at 0.008031 against the same unchanged 0.008032 bound. No acceptance bound is relaxed.

The Windows prerequisite FXC/struct validation also passes all 30 cases with zero skips. Vulkan cold, warm and disabled shader-cache checks each pass their one case. The workflow's separate synchronization validation job 117944545941 passes all 621 cases across its selected projects and reports zero validation messages at every severity. Its log also contains 46 `VulkanDebugMessenger` warnings that the loader offers no `VK_EXT_debug_utils` entry points, so no messenger was created. The zero count is therefore not independent validation evidence, and validation coverage is unproven despite the passing cases. This extra job is separate from the 168-case filtered matrix. No selected GPU fact other than the expressly permitted grain fact fails.

The diagnostic dispatch finishes before the final dispatch starts. No run is repeated to hunt for flakes, and no artifact is downloaded for the final proof. Results are read from each job's console log. The final evidence commit changes this record only, after the exact code commit has completed its hosted proof.

All local builds, tests and shader bakes use `build-slot`, one command at a time. GPU runs and shader bakes use `--heavy`. There are no local repeat loops, stress or timing runs. Shader tables are generated only by the existing writer tests with `KE_WRITE_SHADER_CORPUS=1`, `KE_UPDATE_MSL_HASHES=1`, `KE_UPDATE_SPIRV_HASHES=1` and `KE_UPDATE_HLSL_HASHES=1`. No golden, tolerance, file-size baseline, package version or changelog is changed.

## Inventory of other temporal comparison guards

These are source-level candidates, not additional proven compiler defects. They are unchanged. A production path can reach a raw float without a finite barrier even if ordinary scenes supply finite values. No new live scene failure is claimed for this inventory.

| Source | Comparison and consequence | Non-finite production reachability |
| --- | --- | --- |
| `KhaozEngine.Render3D/Internal/ShaderSources.TemporalAccumulate.cs:106` | `!(stored < expectedDepth * (1 - tolerance))` adds visible history weight. Replacing it with `>=` changes unordered handling. | Yes structurally. `PrevDepth` and expected reprojection depth have no finite barrier before this footprint. Ordinary finite camera depths are finite. |
| `KhaozEngine.Render3D/Internal/ShaderSources.TemporalAccumulate.cs:408` | `!(abs(shown.x) > MotionSentinel)` admits motion into the followed-surface comparison. Replacing it with `<=` changes unordered handling. | Yes structurally through non-finite motion texels. Motion is half float and the sentinel test is not a NaN test. No ordinary-scene occurrence is established. |
| `KhaozEngine.Render3D/Internal/ShaderSources.TemporalAccumulate.cs:417` | `!(expectedDepth > 1e-6)` rejects a point that has no positive expected depth. Replacing it with `<=` loses NaN rejection. | Yes structurally. `temporalReprojectSurface` assigns `-previousView.z` without a finite check. Extreme or non-finite transforms can propagate here. |
| `KhaozEngine.Render3D/Internal/ShaderSources.TemporalAccumulate.cs:419` | `!(expectedDepth < footprint.nearest * (1 - tolerance))` controls carried-band disocclusion. | Yes through the same expected or stored depth paths. Ordinary finite inputs are unaffected by comparison inversion. |
| `KhaozEngine.Render3D/Internal/ShaderSources.TemporalAccumulate.cs:525` | `!(motionPixels < LockMotionStartPixels)` selects strict shading release. | Conditionally reachable from malformed reprojection or motion. The earlier UV bounds are floating-point comparisons, rather than an integer finite barrier. This is not a dedicated finite guard. |
| `KhaozEngine.Render3D/Internal/ShaderSources.TemporalPrepare.cs:149` | The negation of the two depth-separation comparisons classifies equal-depth runs for narrow features. De Morgan comparison inversion changes NaN handling. | Yes structurally through current linearized or stored depth. There is no finite check at this read. |
| `KhaozEngine.Render3D/Internal/ShaderSources.TemporalDisplayKernel.cs:21` | `!(Jitter.z > 1)` rejects Native or smaller scales. | No with normal production `BuildUniforms`. The ratio comes from positive integer dimensions and is finite. Malformed manually supplied uniforms would be a different entry contract. |
| `KhaozEngine.Render3D/Internal/ShaderSources.TemporalAccumulate.cs:310` | Positive ordered lower and upper UV bounds reject NaN only if their unordered semantics survive lowering. | Conditionally reachable through malformed motion or background reprojection. No compiler failure was measured for this expression. |
| `KhaozEngine.Render3D/Internal/ShaderSources.TemporalAccumulate.cs:435` | Positive ordered history colour and state bounds implement an explicit finite/range guard. The comment at line 429 claims it survives fast math. | Yes structurally, since these are loaded history values and rejection of bad values is this guard's purpose. No compiler failure was measured here. The unconditional fast-math claim warrants separate review. |

For the depth rows, `TemporalResolveMath.DepthParams` divides by `projection.M33 + 1` for perspective far depth without a finite check, and the shader linearization and reprojection have no general finite sanitizer. Degenerate or extreme projections therefore provide a concrete propagation path. `BackgroundLinearDepth` itself is finite (`1e30`). This inventory does not claim normal finite-camera rendering generates NaN depths.

The visibility helper's final `!(currentCount > 0 && previousWeight > 0)` is also unchanged. With valid finite coordinates and weights, its inputs are bounded sums of finite increments, so depth NaNs cannot reach it after this fix. Boolean-only negations and integer loop guards are not floating-point candidates.

## Concerns and scope limits

The full workflow is red because of the known, expressly permitted Vulkan grain failure. This record does not label the whole matrix green. The other temporal guards above remain source-level follow-up candidates and are not fixed or claimed to be independently reproduced defects. Correctness GPU evidence covers the requested default backend compiles and devices. Debug and IEEE-strict variants are disassembled in the baseline diagnostic, not rendered, and no timing or cost conclusion is drawn.

The first diagnostic setup build failed CA1416 because its Windows-only disassembly method needed a platform annotation. That annotation was added and the diagnostic build passed before behavioral RED. A final fixture initializer also needed a whitespace-only line break. The subsequent whitespace verification passed. Neither setup error is counted as behavioral RED. All commit hooks and staged guards pass without bypasses, as do the whole-tree dashes, prose and file-size guards and the doc-version guard. Build warnings stay at zero.

Raw emitted shader dumps are removed after their evidence is transcribed. Scratch logs and local TRX files are removed after this final record and the private task report are written. The pushed diagnostic commit and hosted console logs retain the reproducible compiler evidence. No manual playtest is required for this bounded task.

## Integration boundary

The branch is for orchestrator integration. No merge, release, tag, shared-feed pack or consumer repin is performed. No subagent is dispatched. The known grain guard remains part of the selected verification, and any failure is reported with its actual value rather than hidden or relaxed.
