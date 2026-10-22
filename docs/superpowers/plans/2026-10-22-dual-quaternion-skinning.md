# Dual Quaternion Skinning Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use subagent-driven-development or executing-plans to implement this plan task by task after the owner selects execution. Steps use checkbox (`- [ ]`) syntax for tracking. This planning assignment authorizes no implementation or subagents.

**Goal:** Add explicitly selected rigid dual quaternion skinning through every ordinary skin consumer, then a separately gated exact equipment coverage method without weakening NC2.

**Architecture:** An immutable skin profile selects LBS or rigid DQ before upload. CPU preparation produces one validated palette whose words are reused by colour, shadows, outlines and history. Coverage adds a separate versioned precise source graph and continues publishing one selected final surface.

**Tech Stack:** C#, System.Numerics storage, xUnit, generated GLSL and native Metal, Direct3D11 and Vulkan, headless Blender.

**Spec:** [Approved design](../specs/2026-10-22-dual-quaternion-skinning-design.md), DQ1 to DQ6 approved as written on 2026-10-22 ("yes to all").

**Planning baseline:** `dfa175aa595dd1cbfda52fabc0ea30fb0d376f54`, `feature/dual-quaternion-skinning`, `/Users/antonio/KhaozEngine/.worktrees/dq-skinning`. Paths below are relative to this checkout. Existing files were inspected here. `EC:` paths exist only in the read-only equipment coverage snapshot `8be362eecff2a28c6798e6f59bbb116fe1eb8268`. `Carry:` means the read-only carry research snapshot `575472e3a57145fcac3409befd61d20922144ffb`.

## Global Constraints

- LBS is the default. Preserve existing LBS APIs, results, scale support and frozen coverage expectations. `BlendSkinMatrix` remains LBS.
- One immutable method/version per source skin, all its material parts and compatible garment bindings. A changed profile creates a distinct asset/handle, fit and history identity.
- `UseGpuSkinning` selects execution location, not deformation method. No mutable global, per-vertex method mix or automatic per-pose LBS fallback.
- Four stored influences, X/Y/Z/W order, at most 128 joints, finite attributes and integer in-range indices including zero-weight slots. Weights are finite and nonnegative. The left-associated three-add total is either below `1e-8` or within `2^-20` of one.
- Reject more than four positive source influences before import reduction. Do not infer Preserve Volume from glTF skins or arbitrary `extras`.
- Composed matrices use `inverseBind * jointWorld`. Require finite affine form, positive determinant and `max(abs(A*A_transpose - I)) <= 2^-16`. Reject uniform joint scale, nonuniform scale, reflection, shear, collapsed bases and projective transforms outside that rounding allowance. Model placement remains independent.
- Running-sum hemisphere alignment flips both halves only for a negative dot. Zero dots do not flip. Preserve stored influence order and exact identity/unweighted paths. Never snap near-identity poses.
- Ordinary model-space position error is componentwise `abs(cpu - gpu) <= 2e-5 * L`, `L = max(1, maximum source-position norm, maximum prepared joint-translation norm)`. Nonzero normalized directions need vector error at most `2e-5` and angular error at most `1e-4` radians. Blender rigid comparisons use the position bound after coordinate conversion.
- NC2 compares every selected coverage output word exactly, including signed zeros and the inclusive flush boundary. Ordinary tolerances never enter that comparison.
- Preserve 72 frozen LBS cases and the 144-hash manifest. New DQ fixtures are public synthetic data, never private consumer meshes or extracted numerical datasets.
- Ordinary DQ is an intermediate engine milestone. Covered Grimhollow bodies wait for stage 4, actual Preserve Volume/export comparisons, new fit/oracle evidence and owner look approval.
- Keep the existing coverage GPU budget and no-clipping contract. Neither successful sample oracles nor native word equality establishes a continuous carry domain.
- Every local build, test, bake, export or render uses `build-slot`. Full suites, Blender exports/renders and bakes use `--heavy`. No local repetition loops or load tests. Stress and repeated performance workloads require separate owner authorization on hosted runners.
- This repository is public. Cross-platform checks and golden bakes run on hosted `metal-native`, `direct3d11-native` and `vulkan-native` legs. Never use a self-hosted runner.
- Scratch evidence goes under `/tmp/khaozengine-dq-skinning/`. Keep committed fixture sources and compact reports, delete raw frames after metrics/contact sheets. Do not launch a consumer client.
- Warnings are errors. Use `KhaozEngine.Tests.*` namespaces, existing allocation measurement/serial collection rules and focused new types rather than increasing KESIZE baselines.
- Stage explicit paths and use an explicit pathspec when committing. No version, tag, release, pack, consumer pin or integration action is part of this plan.

## Review Focus

- An almost rigid inverse-bind product can contain drift. Test both sides of the `2^-16` Gram boundary and a scaled bind that cancels exactly, rather than rejecting raw joint scale. S2.2 and S2.7.
- A zero-weight slot can still carry an invalid index. Reject it before upload without changing old LBS acceptance. S2.1 and S3.1.
- Export can reorder four ambiguous influences or omit additional weights. Record both orders, reject excess positives before truncation and distinguish order divergence from a failed rigid-parity claim. S2.7 and S2.8.
- A recycled mesh slot with the same bone count can reuse another source's temporal data. Bind generation/source/profile, seed only that actor and retain origin handling. S3.8 and S3.9.
- Independent outlines, shadow-only draws and coverage refusal can bypass the colour path. Each must show the DQ witness or explicitly refuse, never retain LBS. S3.4 to S3.6 and S4.5.

## File responsibilities and implementation interfaces

Names below are new API decisions, not claims that these symbols already exist. Keep them consistent across tasks.

| Home | Responsibility |
| --- | --- |
| `KhaozEngine.Render3D/Models/SkinningProfile.cs` | Public `SkinningMethod` and immutable, value-equal `SkinningProfile` with supported method/version pairs |
| `KhaozEngine.Render3D/Models/SkinningValidation.cs` | Source and composed-pose domain checks with asset/joint/clip/sample diagnostics |
| `KhaozEngine.Render3D/Models/RigidDualQuaternion.cs` | Sequential 32-byte real/dual storage only |
| `KhaozEngine.Render3D/Models/DualQuaternionPalette.cs` | Pinned scalar composition, rigid conversion and canonical signs, once per bone |
| `KhaozEngine.Render3D/Models/DualQuaternionSkinning.cs` | Pinned four-influence rational position/direction graph |
| `KhaozEngine.Render3D/Models/SkinningEvaluation.cs` | Profile-aware CPU dispatch, keeping old LBS methods intact |
| `KhaozEngine.Render3D/Models/DualQuaternionBounds.cs` | Source radius metadata and outward conservative ordinary DQ bounds |
| `KhaozEngine.Render3D/Internal/PreparedSkinningStore.cs` | Frame-owned prepared palettes and stable pose references, no caller-owned live spans |
| `KhaozEngine.Render3D/Internal/DualQuaternionGlsl.cs` | One generated ordinary DQ helper used by every vertex shader family |
| `KhaozEngine.Render3D/Rendering/DualQuaternionPaletteBuffer.cs` | DQ payload layout, packing and resource lifetime without reinterpreting matrix buffers |
| `KhaozEngine.Render3D/Rendering/*DualQuaternion*.cs` | Cohesive partials for DQ pipeline families beside the existing renderers |
| `KhaozEngine.Render3D/Coverage/` and `Coverage/Gpu/` | Stage 4 version identity, precise reference and source stage, oracle, bounds and final surfaces |
| `KhaozEngine.Render.Tests/Fixtures/dual-quaternion/` | Public binary32 fixtures, repeatable Blender source/script and provenance |

`SkinningMethod` assigns `LinearBlend = 0` and `DualQuaternionRigid = 1`. `SkinningProfile` is a sealed immutable value-equal record with private construction, `Method` and `AlgorithmVersion` getters, and static `LinearBlend` / `DualQuaternionRigid` instances, both version 1. There is no mutable selector or public arbitrary-version constructor. Existing mesh constructors remain callable unchanged. New explicit-profile overloads append `SkinningProfile profile` after the existing skeleton parameter for both index widths. `SkinnedGltfMesh.Profile` is get-only.

`RigidDualQuaternion` has constructor `(Vector4 real, Vector4 dual)` and exposes get-only `Vector4 Real` and `Vector4 Dual`. Arbitrary constructed values still require palette validation before presentation. `SkinningValidationContext` is a readonly record struct `(string Asset, string? Clip, int Sample)` used by cold validation and load diagnostics. Frame diagnostics add joint index/name and effective profile. Public evaluators consume spans, so no GPU or coverage dependency enters ordinary math.

### Numerical graph pinned by stages 2 and 3

Document the actual scalar operation inventory beside the implementation and lock its expected words. Do not use vector arithmetic, `Matrix4x4.Decompose`, library quaternion extraction or implicit FMA as numerical substitutes.

1. DQ preparation uses scalar left-associated four-product sums for every composed matrix coefficient, matching the existing coverage composition order. Leave LBS's `SkinningMath.Compose` unchanged. Prepare DQ once and reuse exactly those words.
2. For row-vector composed `M`, compute candidates in order W/X/Y/Z with the written parentheses: `cw=((1+M11)+M22)+M33`, `cx=((1+M11)-M22)-M33`, `cy=((1-M11)+M22)-M33`, `cz=((1-M11)-M22)+M33`. Select the greatest with strict `>` updates, so ties keep W, then X, then Y, then Z. Let `h=sqrt(candidate)`, selected component `0.5*h`, and `s=0.5/h`. Other components are sums/differences times `s`. W uses X=`M23-M32`, Y=`M31-M13`, Z=`M12-M21`. X uses W=`M23-M32`, Y=`M12+M21`, Z=`M13+M31`. Y uses W=`M31-M13`, X=`M12+M21`, Z=`M23+M32`. Z uses W=`M12-M21`, X=`M13+M31`, Y=`M23+M32`.
3. Normalize using `(((x*x)+(y*y))+(z*z))+(w*w)`, one square root, one reciprocal and four multiplications. Canonicalize by first nonzero W/X/Y/Z component positive, negating both halves. For `t=(M41,M42,M43)`, pin `Dx=0.5*(((tx*rw)+(ty*rz))-(tz*ry))`, `Dy=0.5*(((ty*rw)+(tz*rx))-(tx*rz))`, `Dz=0.5*(((tz*rw)+(tx*ry))-(ty*rx))`, `Dw=-0.5*(((tx*rx)+(ty*ry))+(tz*rz))`. This is the scalar expansion of `d=0.5*(t,0)*r`. Exact identity matrices bypass conversion to produce exact `(0,0,0,1)` / positive-zero halves.
4. Start `R,D` at positive zero. Skip nonpositive slots after validation. For each positive X/Y/Z/W slot compute the left-associated four-term dot with the running `R`. Negate both input halves only if negative, then add each weighted component separately in that order. Do not sort or merge duplicates. `n=(((Rx*Rx)+(Ry*Ry))+(Rz*Rz))+(Rw*Rw)`.
5. Use the unnormalized Hamilton rotation numerator, not a unit-quaternion shortcut. Diagonal entries are `((w*w+x*x)-y*y)-z*z`, `((w*w-x*x)+y*y)-z*z`, `((w*w-x*x)-y*y)+z*z`. Off-diagonals are `2*(xy-wz)`, `2*(xz+wy)`, `2*(xy+wz)`, `2*(yz-wx)`, `2*(xz-wy)`, `2*(yz+wx)` in row order. Each matrix-vector row is a left-associated three-product sum. Translation X numerator is `2*((((Dx*Rw)-(Dw*Rx))-(Dy*Rz))+(Dz*Ry))`, with cyclic permutations for Y/Z. Multiply rotation and translation numerators separately by the single `1/n`, then add for position. Directions omit translation.
6. A total below `1e-8` or an exactly identity contributing palette returns source position exactly. Normal/tangent normalization follows existing `1e-8` direction fallback and `1e-12` source tangent squared threshold, preserving tangent W and exact zero tangent. No near-identity test.
7. Proposed internal fail-closed floor is `n >= 1/8`. S2.6 must prove this loose lower bound for the admitted rounded weights, prepared quaternion errors and specified graph before it becomes an admission lemma. Nonfinite arithmetic or an unproved bound refuses DQ. Do not promote the exact-arithmetic `1/4` observation into a binary32 claim.

The precise NC2 graph in stage 4 still needs a jointly reviewed transcription with flush/read rules and corrected division/root primitives. Its outputs need not equal ordinary shader words. Its prepared palette words must be the same, and its selected exact reference is its zero-tolerance authority.

## Verification and commit protocol

Every detailed task has a failing test, a red command, a minimal implementation, a green command and a commit. Run each quoted local test command once for red and once after the implementation for green, with additional runs only for a new change or actual failure. Require a nonzero matched-test count. A skipped GPU test or zero tests is not evidence.

S4.7 is the final verification/documentation task. Its aggregate test can already pass after S4.1 to S4.6. Record that result rather than manufacturing a failure or changing correct code to demonstrate red.

Assertion excerpts below omit the setup specified in the preceding test step. Their variables are fixture inputs or captured outputs, not additional helper APIs. They pin decisive assertions without writing each test body in this document.

GPU tasks first add a failing headless contract/wiring test and actual `[GpuFact]` witness. The later execution orchestrator publishes the exact test revision for a hosted red proof, implements, then collects hosted green evidence at the implementation SHA. This planning session publishes nothing. Do not use shader-text checks as native numerical proof.

Hosted focused command, with `FILTER` set to the task's literal filter:

```bash
gh workflow run cross-platform-gpu.yml --ref feature/dual-quaternion-skinning -f legs=all -f bake=false -f renderTestFilter="$FILTER"
```

Record the run URL, `headSha`, hosted runner labels, native backend/adapter, pass/fail and matched/skipped counts. Confirm `headSha` equals the intended execution commit, not a later branch tip. Use the relevant hosted bake only for new DQ golden families, review its images and keep existing LBS goldens unchanged. No workflow is dispatched by this planning assignment.

At each stage exit run one Release solution build and full headless suite after reconciliation in the execution checkout, never once per task:

```bash
mkdir -p local-feed
build-slot --label dq-stage-build -- dotnet build KhaozEngine.slnx -c Release
build-slot --heavy --label dq-stage-suite -- dotnet test KhaozEngine.slnx -c Release --no-build --filter "Category!=LiveSocket"
sh scripts/check-dashes.sh --tree
sh scripts/check-prose.sh --tree
sh scripts/check-file-size.sh --tree
sh scripts/check-agent-instructions.sh --tree
bash scripts/check-doc-versions.sh
```

Every commit step below means stage only that task's exact Files paths and commit those paths with the stated subject, no body or attribution. New paths are marked Create, all others Modify unless marked Test or Read. A milestone is not complete while its hosted proof is pending.

## Stage 2: Headless rigid DQ and authoring proof (8 tasks)

Entry: approved design and execution choice. The current assignment writes this plan only.

### S2.1: Immutable profile and source input domain

**Files:** Create `KhaozEngine.Render3D/Models/SkinningProfile.cs`, `KhaozEngine.Render3D/Models/SkinningValidation.cs`, `KhaozEngine.Render.Tests/Render3D/DqProfileValidationTests.cs`. Modify `KhaozEngine.Render3D/Models/SkinnedGltfMesh.cs`. Test existing `KhaozEngine.Render.Tests/Render3D/SkinnedMeshTypesTests.cs` unchanged.

**Interfaces:** Produce the profile/context/mesh overloads defined above and `SkinningValidation.ValidateSource(ReadOnlySpan<SkinnedVertex> vertices, int boneCount, SkinningProfile profile, in SkinningValidationContext context)`. Existing constructors delegate to LBS and do not acquire DQ-only rejection behavior.

- [ ] Write `Old_constructors_select_LBS`, `Profile_is_immutable_and_value_equal` and a table-driven `DQ_rejects_invalid_source`. Assert all attribute components finite, all four indices finite/integral/in-range, negative/NaN/infinite weights rejected, 128 joints accepted and 129 rejected, total boundaries around `1e-8` and `1 +/- 2^-20` pinned by adjacent float words. Include fractional and invalid zero-weight indices, duplicate valid indices, all-zero weights and both index widths. Snapshot an existing LBS raw-weight case unchanged.

```csharp
Assert.Equal(SkinningMethod.LinearBlend, mesh.Profile.Method);
Assert.Equal(1, mesh.Profile.AlgorithmVersion);
```

- [ ] Red: `build-slot --label dq-profile -- dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release --filter "FullyQualifiedName~DqProfileValidationTests|FullyQualifiedName~SkinnedMeshTypesTests"`. Expect missing profile API or failed domain assertions.
- [ ] Implement immutable supported version-1 instances and DQ-only source validation using the pinned total order. Carry context in exceptions without repairing weights or indices. Check callers cannot mutate a profile.
- [ ] Green: run the exact red command. Expect all new cases and unchanged mesh type tests to pass.
- [ ] Commit `render3d(skinning): define immutable skin profiles and DQ input validation`.

### S2.2: Scalar composed-palette preparation and rigid validation

**Files:** Create `KhaozEngine.Render3D/Models/RigidDualQuaternion.cs`, `KhaozEngine.Render3D/Models/DualQuaternionPalette.cs`, `KhaozEngine.Render.Tests/Render3D/DqPaletteTests.cs`. Modify `KhaozEngine.Render3D/Models/SkinningValidation.cs`.

**Interfaces:** Produce `DualQuaternionPalette.Prepare(ReadOnlySpan<Matrix4x4> jointWorld, ReadOnlySpan<Matrix4x4> inverseBind, Span<RigidDualQuaternion> destination, in SkinningValidationContext context)` and `FromComposed(in Matrix4x4 composed, in SkinningValidationContext context, int joint)`. Both use the graph above and fail before writing a caller-visible partial result. Storage size is 32 bytes.

- [ ] Write `Composition_order_is_inverse_bind_then_joint`, `All_extraction_branches_and_ties_are_pinned`, `Equivalent_signs_are_canonical` and `Only_composed_rigidity_controls_eligibility`. Assert exact identity and representable translations, analytic positive Z quarter-turn orientation, oblique pivot transforms, W/X/Y/Z largest-component ties and 180-degree cases. Freeze independently calculated expected words. Reject nonfinite/projective/reflected/collapsed/sheared matrices, both scale kinds and Gram values just outside `2^-16`. Accept exactly cancelling bind scale and the admitted boundary. Diagnostics name asset, joint and clip/sample.

```csharp
Assert.Equal(32, Marshal.SizeOf<RigidDualQuaternion>());
Assert.Equal(new Vector4(0f, 0f, 0f, 1f), identity.Real);
Assert.Equal(Vector4.Zero, identity.Dual);
```

- [ ] Red: `build-slot --label dq-palette -- dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release --filter FullyQualifiedName~DqPaletteTests`. Expect missing preparation API.
- [ ] Implement scalar composition and graph steps 1 to 3. Preserve exact affine last-column checks, positive determinant and Gram allowance. Do not call the existing index validator as a substitute for integral validation or use matrix decomposition.
- [ ] Green: repeat the command and require the full palette word/diagnostic corpus to pass. Preserve a compact ARM64/x64 input/output word fixture for later hosted cross-CPU execution.
- [ ] Commit `render3d(skinning): prepare validated rigid dual quaternion palettes`.

### S2.3: Four-influence position evaluator

**Files:** Create `KhaozEngine.Render3D/Models/DualQuaternionSkinning.cs`, `KhaozEngine.Render.Tests/Render3D/DqPositionTests.cs`, `KhaozEngine.Render.Tests/Fixtures/dual-quaternion/rigid-words.json`. Modify `KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj` to copy the fixture.

**Interfaces:** Produce `DualQuaternionSkinning.Position(in SkinnedVertex source, ReadOnlySpan<RigidDualQuaternion> palette) : Vector3` and internal `Blend(Vector4 indices, Vector4 weights, ReadOnlySpan<RigidDualQuaternion> palette, out Vector4 real, out Vector4 dual)`. Evaluation assumes validated sources and palettes and still refuses nonfinite/tiny norms.

- [ ] Write `Half_identity_half_Z90_is_DQ_not_LBS` with `(1,0,0)` producing `(sqrt(0.5),sqrt(0.5),0)` within the design bound and differing from `(0.5,0.5,0)` by over `0.2` on X/Y. Add pivot plus translation, every slot, duplicate indices, q/-q pairs, zero dots, adjacent signs, four-influence order witnesses and 180-degree cases. Exact identity and representable translation cases assert output words, including source signed zeros where identity promises preservation. Near-identity residuals must survive.

```csharp
Assert.InRange(MathF.Abs(actual.X - MathF.Sqrt(0.5f)), 0f, 2e-5f);
Assert.InRange(MathF.Abs(actual.Y - MathF.Sqrt(0.5f)), 0f, 2e-5f);
Assert.True(MathF.Abs(actual.X - 0.5f) > 0.2f);
```

- [ ] Red: `build-slot --label dq-position -- dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release --filter FullyQualifiedName~DqPositionTests`. Expect absent evaluator.
- [ ] Implement graph steps 4 to 7, with one reciprocal and the stated branch order. Write expected words from an independent scalar or high-precision derivation, never by blessing this implementation's output. Preserve unweighted identity behavior at the strict threshold.
- [ ] Green: repeat the command. Require both analytic-tolerance assertions and exact-word fixtures to pass.
- [ ] Commit `render3d(skinning): evaluate ordered rigid DQ positions`.

### S2.4: Directions, CPU dispatch and synthetic shape controls

**Files:** Create `KhaozEngine.Render3D/Models/SkinningEvaluation.cs`, `KhaozEngine.Render.Tests/Render3D/DqVertexTests.cs`, `KhaozEngine.Render.Tests/Render3D/DqFixtureMeshes.cs`. Modify `KhaozEngine.Render3D/Models/DualQuaternionSkinning.cs`. Test `KhaozEngine.Render.Tests/Render3D/SkinningMathTests.cs` and `KhaozEngine.Render.Tests/Render3D/SkinnedTangentTests.cs` unchanged.

**Interfaces:** Produce `DualQuaternionSkinning.Vertex(in SkinnedVertex source, ReadOnlySpan<RigidDualQuaternion> palette) : ModelVertex`, plus `SkinningEvaluation.Position` and `Vertex` taking `(in SkinnedVertex source, SkinningProfile profile, ReadOnlySpan<Matrix4x4> lbs, ReadOnlySpan<RigidDualQuaternion> dq)`. Dispatch LBS through existing functions. `DqFixtureMeshes` supplies a constant-weight tube, closed patch, varying-weight bend and ambiguous four-influence mesh.

- [ ] Write `Normal_and_tangent_follow_rotation`, `Zero_tangent_and_handedness_survive`, `Constant_weight_patch_is_rigid` and `Varying_weights_can_bulge`. Assert position bound, direction vector and angle bounds, `1e-8` fallback, `1e-12` source tangent threshold, exact zero tangent and unchanged colour/UV. In an opposite-twist constant-weight tube assert DQ radius is within the position bound while LBS shrinks, with a closed-patch volume bound derived from vertex error. Explicitly show the varying-weight mesh has no constant-volume guarantee.

```csharp
Assert.Equal(Vector4.Zero, withoutTangent.Tangent);
Assert.Equal(source.Tangent.W, withTangent.Tangent.W);
Assert.InRange(Vector3.Distance(expectedNormal, withTangent.Normal), 0f, 2e-5f);
```

- [ ] Red: `build-slot --label dq-vertex -- dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release --filter "FullyQualifiedName~DqVertexTests|FullyQualifiedName~SkinningMathTests|FullyQualifiedName~SkinnedTangentTests"`. Expect missing dispatch or direction behavior.
- [ ] Implement rotation-only directions and existing normalization fallback semantics. Share the blend result within a vertex. Keep the LBS body and expected results unchanged.
- [ ] Green: repeat the command. Require analytic shape controls as well as unchanged LBS tests.
- [ ] Commit `render3d(skinning): dispatch DQ vertices with matching directions`.

### S2.5: Grounding and base-pose measurement use the profile

**Files:** Modify `KhaozEngine.Render3D/Animation/SkinnedGrounding.cs`, `KhaozEngine.Render3D/Animation/ContractJointMap.cs`. Create `KhaozEngine.Render.Tests/Render3D/Animation/DqPresentationQueryTests.cs`. Test existing `KhaozEngine.Render.Tests/Render3D/Animation/SkinnedGroundingTests.cs` and `KhaozEngine.Render.Tests/Render3D/Animation/ContractJointMapTests.cs`.

**Interfaces:** Add `SkinnedGrounding.MinimumY(ReadOnlySpan<SkinnedVertex> vertices, SkinningProfile profile, ReadOnlySpan<Matrix4x4> composedLbs, ReadOnlySpan<RigidDualQuaternion> preparedDq, in Matrix4x4 model) : float`. Keep the existing inverse-bind/joint/scratch overload and LBS height-column fast path. `ContractJointMap.SkinAtBase(SkinnedGltfMesh mesh)` reads `mesh.Profile` and prepares once.

- [ ] Write `DQ_grounding_equals_independently_posed_world_minimum` for a bent/pivoted mesh, translated/nonuniformly placed model and empty vertices (`PositiveInfinity`). Assert the LBS shortcut gives a different witness result. Write `Base_stance_measurement_uses_the_skin_profile` and assert skeleton mismatch remains rejected. Compare all measured positions with independent DQ fixture expectations, not this same dispatcher.

```csharp
Assert.InRange(MathF.Abs(actualMinimum - independentlyPosedMinimum), 0f, 2e-5f * L);
Assert.Equal(float.PositiveInfinity, emptyMinimum);
```

- [ ] Red: `build-slot --label dq-queries -- dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release --filter "FullyQualifiedName~DqPresentationQueryTests|FullyQualifiedName~SkinnedGroundingTests|FullyQualifiedName~ContractJointMapTests"`.
- [ ] Add the prepared-palette path, evaluate DQ source positions then take world minimum Y, and route base fitting through `SkinningEvaluation`. Reuse caller-prepared palettes, do not convert per vertex. Preserve the old LBS shortcut.
- [ ] Green: repeat the command and require both profile paths and old skeleton checks to pass.
- [ ] Commit `render3d(skinning): use selected deformation for grounding and base fitting`.

### S2.6: Conservative DQ envelope and rounded denominator proof

**Files:** Create `KhaozEngine.Render3D/Models/DualQuaternionBounds.cs`, `KhaozEngine.Render.Tests/Render3D/DqBoundsTests.cs`, `docs/design/DUAL-QUATERNION-BOUNDS-2026-10-22.md`. Modify `docs/INDEX.md` for the proof reference.

**Interfaces:** Produce immutable `DqBoundsSource` source-radius metadata and `DualQuaternionBounds.TryBoundModel(in DqBoundsSource source, ReadOnlySpan<RigidDualQuaternion> palette, out MeshBounds bounds) : bool`, plus `TryBoundWorld(in MeshBounds modelBounds, in Matrix4x4 world, out MeshBounds worldBounds) : bool`. The latter encloses general admitted affine placement using outward interval corner transforms, not the current maximum-axis sphere shortcut under shear. False is an explicit admission failure, never permission to reuse a rest sphere or LBS influence union.

- [ ] Write `DQ_escapes_the_LBS_convex_union_but_is_enclosed`, `Rounded_norm_has_a_positive_outward_lower_bound` and `Nonfinite_or_unproved_envelope_refuses`. Cover weight-total endpoints, four equal weights, hemisphere ties, huge representable translations, exact/near identity and all-zero weights. Compare an independently enumerated posed-vertex box with the conservative envelope and prove the rounding inequalities symbolically in the companion document. A finite test corpus alone does not prove the domain.

```csharp
Assert.True(DualQuaternionBounds.TryBoundModel(in sourceBounds, palette, out var bounds));
for (int axis = 0; axis < 3; axis++)
    Assert.InRange(independentlyPosed[axis], bounds.Min[axis], bounds.Max[axis]);
```

- [ ] Red: `build-slot --label dq-bounds -- dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release --filter FullyQualifiedName~DqBoundsTests`.
- [ ] Derive outward binary32 error bounds for prepared quaternion norm, ordered blend, branch decisions and rational transform. Prove the proposed `1/8` floor for the entire admitted domain or stop this task and return the failed lemma. Bound rotation by the outward source radius and translation by `2*norm(Dsum)/sqrt(nLower)`, including weight-total and arithmetic error. Use binary64 outward interval calculations and outward final conversion, not sampled maxima. O(vertices) exact source evaluation is an independent control, not the claimed O(joints) proof.
- [ ] Green: repeat the command and review the written proof against the implemented scalar graph. Downstream admission remains blocked on any missing inequality.
- [ ] Commit `render3d(skinning): bound rigid DQ deformation conservatively`.

### S2.7: Explicit import and clip/sample eligibility audit

**Files:** Modify `KhaozEngine.Render3D/Models/GltfLoader.Skinned.cs`, `KhaozEngine.Render3D/Models/GltfLoader.SkinnedMaterialParts.cs`. Create `KhaozEngine.Render3D/Models/GltfLoader.SkinningValidation.cs`, `KhaozEngine.Render.Tests/Render3D/DqImportValidationTests.cs`.

**Interfaces:** Add explicit-profile overloads for `LoadSkinned(string path, SkinningProfile profile)`, `LoadSkinned(ReadOnlyMemory<byte> glb, string identity, SkinningProfile profile)`, both corresponding `LoadSkinnedPartsWithMaterials` overloads and `LoadSkinnedWithMaterial(string path, SkinningProfile profile)`. Produce `SkinningValidation.ValidatePose(SkinnedGltfMesh mesh, ReadOnlySpan<Matrix4x4> jointWorld, in SkinningValidationContext context)` for authoring clip/sample audits and procedural poses. Old overloads select LBS.

- [ ] Write `Opt_in_is_explicit_and_all_material_parts_agree`, `Extra_positive_source_influences_are_not_truncated` and `Sample_audit_reports_the_effective_composed_joint`. Use synthetic glTF with `JOINTS_1/WEIGHTS_1`, five positives, zero extras, raw nonfinite/negative weights and indices, scale/shear samples and cancelling bind scale. Count source positives before normalization/reduction, reject invalid raw DQ input, then normalize imported valid weights and validate the stored total. Assert old import behavior and arbitrary `extras` do not change. A static sample audit must not claim unsampled clip or procedural-pose validity.

```csharp
Assert.All(parts, part => Assert.Equal(SkinningProfile.DualQuaternionRigid, part.Mesh.Profile));
Assert.Throws<InvalidOperationException>(() => GltfLoader.LoadSkinned(fivePositiveGlb, "five-positive", SkinningProfile.DualQuaternionRigid));
```

- [ ] Red: `build-slot --label dq-import -- dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release --filter "FullyQualifiedName~DqImportValidationTests|FullyQualifiedName~GltfLoaderSkinnedTests|FullyQualifiedName~GltfSkinnedMaterialPartsTests"`.
- [ ] Thread the explicit profile through the shared decoder. Validate all source influence sets before the current four-slot import can lose evidence. Because the existing importer supports only `JOINTS_0/WEIGHTS_0`, reject any positive contribution in later sets even when the combined positive count is four or fewer. Zero-only extra sets are harmless. Do not add a new compaction/reordering importer or silently discard those positives. Preserve the single-skin limitation, weight normalization behavior for valid inputs and original LBS path. Audit composed samples with names and indices, leaving runtime pose validation mandatory.
- [ ] Green: repeat the command. Confirm all parts retain the same profile and the byte/path overloads have equivalent diagnostics.
- [ ] Commit `render3d(skinning): validate explicit DQ imports and authored samples`.

### S2.8: Reproducible Blender rigid-subset proof

**Files:** Create `KhaozEngine.Render.Tests/Fixtures/dual-quaternion/blender-rigid/author.py`, `KhaozEngine.Render.Tests/Fixtures/dual-quaternion/blender-rigid/source.blend`, `KhaozEngine.Render.Tests/Fixtures/dual-quaternion/blender-rigid/rigid.glb`, `KhaozEngine.Render.Tests/Fixtures/dual-quaternion/blender-rigid/expected.json` and `KhaozEngine.Render.Tests/Fixtures/dual-quaternion/blender-rigid/README.md`. Create `KhaozEngine.Render.Tests/Render3D/DqBlenderParityTests.cs`. Modify `KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj` for fixture copying.

**Interfaces:** `expected.json` records Blender and exporter versions, coordinate transform, joint/inverse-bind words, source vertex-group order, exported order, weights, sample times and evaluated positions. `author.py --output <dir>` generates the single public synthetic rig and its provenance. No private corpus is consumed.

- [ ] Write `Rigid_export_matches_preserve_volume_with_matching_inputs` and `Ambiguous_reordering_is_reported_not_hidden`. Assert the design's position bound after coordinate conversion on two-influence, pairwise-unambiguous four-influence and pivot fixtures. Include an ambiguous four-influence reordering with an explicitly recorded divergent outcome. Scale/shear, five-positive-weight and excluded modifier fixtures must be rejected or labelled outside the promise, never included as passing parity.

```csharp
for (int axis = 0; axis < 3; axis++)
    Assert.InRange(MathF.Abs(enginePosition[axis] - blenderPosition[axis]), 0f, 2e-5f * L);
Assert.False(ambiguousSourceOrderPosition == ambiguousExportOrderPosition);
```

- [ ] Red: `build-slot --label dq-blender-tests -- dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release --filter FullyQualifiedName~DqBlenderParityTests`. Expect missing fixture or failed parity.
- [ ] Author one synthetic rig and keep its repeatable script beside the editable blend. Generate once with `build-slot --heavy --label dq-blender-export -- blender -b --factory-startup -P KhaozEngine.Render.Tests/Fixtures/dual-quaternion/blender-rigid/author.py -- --output /tmp/khaozengine-dq-skinning/blender-rigid`. Pin the installed Blender/exporter version in provenance. Inspect exporter order explicitly. The live Blender MCP session is forbidden unless a needed feature fails headless and that limitation is reported. This is a numerical fixture, not owner character art.
- [ ] Green: copy only the named fixture artifacts into their committed paths, repeat the red command and run the stage-exit verification. Require analytic tests, Blender rigid proof and unchanged LBS tests green. Record ARM64/x64 palette-word proof on the hosted matrix before accepting stage 2.
- [ ] Commit `test(skinning): prove the public Blender rigid DQ subset`.

Stage 2 exit: headless math, validation, bounds proof, profile-aware presentation queries and public Blender evidence accepted. No renderer completeness, coverage or private wardrobe claim.

## Stage 3: Coherent ordinary CPU/GPU rendering (11 tasks)

Entry: stage 2 accepted. During these implementation commits DQ draw activation is internal/test-only until S3.11. Public DQ submission must explicitly reject an unavailable consumer before mutation. Never render an incomplete DQ actor through an LBS shader to make an intermediate task look usable.

### S3.1: Upload identity and transactional pose submission

**Files:** Modify `KhaozEngine.Render3D/Scene3D.SkinnedMeshLoading.cs`, `KhaozEngine.Render3D/Scene3D.DrawDescriptors.cs`, `KhaozEngine.Render3D/Scene3D.SkinnedTargetOutlines.cs`, `KhaozEngine.Render3D/Scene3D.cs` (field ownership only). Create `KhaozEngine.Render3D/Internal/PreparedSkinningStore.cs`, `KhaozEngine.Render3D/Scene3D.SkinningProfiles.cs`, `KhaozEngine.Render.Tests/Render3D/DqSubmissionTests.cs`.

**Interfaces:** `SkinnedMeshEntry` retains `Profile`, source identity including handle generation, DQ bounds metadata and a validated DQ source/inverse-bind snapshot. `PreparedSkinningStore.Prepare(SkinnedGltfMesh mesh, ReadOnlySpan<Matrix4x4> joints, in SkinningValidationContext context) : PreparedSkinningPose` appends only after complete validation. The readonly pose reference exposes profile, offset and bone count. `Lbs(in PreparedSkinningPose)` and `Dq(in PreparedSkinningPose)` return read-only spans valid until frame reset. Existing draw APIs resolve selection from the mesh handle.

- [ ] Write `Rejected_DQ_pose_changes_no_queue_palette_or_history`, `Upload_retains_profile_and_source_generation` and `Invalid_outline_pose_is_atomic_without_colour_draw`. Snapshot all counts, then submit a bad final joint, mismatched count, overflowed finite input, unavailable DQ consumer and stale handle. Assert no visible partial mutation and named diagnostics. Mutating the caller's joint array after submission must not change the prepared words. A DQ uploaded source snapshot must agree between CPU and GPU paths even if its original CPU arrays are later edited.

```csharp
Assert.Equal(queueBefore, scene.QueuedSkinnedInstancesForTests.Count);
Assert.Equal(paletteCountBefore, paletteCountAfter);
Assert.Equal(historyCountBefore, historyCountAfter);
```

- [ ] Red: `build-slot --label dq-submission -- dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release --filter "FullyQualifiedName~DqSubmissionTests|FullyQualifiedName~Scene3DSkinnedQueueTests|FullyQualifiedName~SkinnedTargetOutlineApiTests"`.
- [ ] Prepare into temporary bounded scratch, validate finite results/envelope and the requested consumer set, then append the immutable frame payload and queue/history entries. Dispose partial upload resources on failure. Bind profile/source identity to handles, not a mutable draw descriptor. Keep stale-handle behavior and unchanged LBS submission semantics.
- [ ] Green: repeat the command. Confirm ordinary and independent outline queues fail atomically.
- [ ] Commit `render3d(skinning): retain profiles and prepare poses transactionally`.

### S3.2: Explicit DQ palette layouts and upload accounting

**Files:** Create `KhaozEngine.Render3D/Rendering/DualQuaternionPaletteBuffer.cs`, `KhaozEngine.Render.Tests/Render3D/DqPaletteLayoutTests.cs`. Modify `KhaozEngine.Render3D/Scene3D.GpuSkinning.cs`, `KhaozEngine.Render3D/Scene3D.SkinnedDrawRecords.cs`, `KhaozEngine.Render3D/Rendering/ModelRenderer.Skinned.cs`, `KhaozEngine.Render3D/Rendering/ModelRenderer.cs` (construction/disposal delegation), `KhaozEngine.Render3D/Rendering/SkinnedBonePalette.cs` (retain LBS layout), `KhaozEngine.Render3D/Rendering/SkinnedMotionPalette.cs` (method-specific facade), `KhaozEngine.Render3D/Rendering/TargetOutlineSkinningStore.cs` (method-specific facade).

**Interfaces:** DQ uses interleaved `(real.xyzw,dual.xyzw)` pairs, 32 bytes per joint. `DualQuaternionPaletteBuffer.SlotBytes` is `4096u`, also the outline slot size. `SkinnedMotionPalette.DqPayloadBytes` is `4160u` (64-byte `PrevModel` plus 4096-byte palette), and `DqSlotBytes` is `4352u`. LBS remains 8192-byte current and 8448-byte previous slots. `DualQuaternionPaletteBuffer.EnsureCapacity(uint slots)`, `Pack(uint slot, ReadOnlySpan<RigidDualQuaternion> palette)`, `Upload(IGpuCommandList commands) : long` and `OffsetFor(uint slot) : uint` own separate DQ storage and resource ranges. Each draw record retains its prepared-pose reference and method-local compacted slot.

- [ ] Write `DQ_slots_have_explicit_stride_ranges_and_alignment`, `Mixed_methods_do_not_alias` and `Growth_preserves_all_payloads_and_retires_resources`. Assert byte offsets, unchanged exact palette words, 128-joint last element, at least two actors per method, compaction after culling and previous/outline layouts. Check actual full-buffer upload accounting separately from useful payload bytes, with alignment/capacity included. Exercise disposal and repeated capacity reuse without live-resource growth.

```csharp
Assert.Equal(4096u, DualQuaternionPaletteBuffer.SlotBytes);
Assert.Equal(4160u, SkinnedMotionPalette.DqPayloadBytes);
Assert.Equal(4352u, SkinnedMotionPalette.DqSlotBytes);
```

- [ ] Red: `build-slot --label dq-layout -- dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release --filter "FullyQualifiedName~DqPaletteLayoutTests|FullyQualifiedName~SkinnedMotionPaletteTests|FullyQualifiedName~MotionUploadStatsTests"`.
- [ ] Implement the dedicated 32-byte store and explicit variant layouts, adapting old stores without treating a DQ pair as a matrix. Upload each method's prepared current payload once per submitted pose, then reuse it across passes. Count what the command list actually uploads, not an assumed half-size saving.
- [ ] Green: repeat the command and inspect recorded byte images/resource lifetimes.
- [ ] Commit `render3d(skinning): add explicit DQ palette buffers and upload accounting`.

### S3.3: Ordinary CPU fallback and material GPU variants

**Files:** Create `KhaozEngine.Render3D/Internal/DualQuaternionGlsl.cs`, `KhaozEngine.Render3D/Rendering/ModelRenderer.DualQuaternion.cs`, `KhaozEngine.Render.Tests/Render3D/DqShaderContractTests.cs`, `KhaozEngine.Render.Tests/Gpu/DqMaterialGpuTests.cs`, `KhaozEngine.Render.Tests/Gpu/DqGpuReadback.cs`. Modify `KhaozEngine.Render3D/Internal/ShaderSources.Model.cs`, `KhaozEngine.Render3D/Rendering/ModelRenderer.Skinned.cs`, `KhaozEngine.Render3D/Rendering/ModelRenderer.cs` (lifecycle calls), `KhaozEngine.Render3D/Scene3D.GpuSkinning.cs`, `KhaozEngine.Render3D/Scene3D.SkinnedSubmission.cs`.

**Interfaces:** `DualQuaternionGlsl.Source(string paletteName)` emits one scalar ordinary helper for position, normal and tangent. `ModelRenderer` selects separate LBS/DQ pipeline families from `SkinningProfile`. CPU fallback calls S2.4's dispatcher with the exact prepared palette. `DqGpuReadback` runs a test vertex-output/storage capture using the same production helper and uploads the same fixture words, returning model-space position/normal/tangent records.

- [ ] Write `CPU_execution_still_uses_DQ`, `Material_variants_share_the_DQ_graph` and native `DQ_material_attributes_match_the_CSharp_reference`. Read back actual attributes for zero/nonzero tangents and large valid scale L, assert all three numerical bounds, identity words where promised and an LBS-separating twist witness. Render textured/normal-mapped, alpha-cutout, split-material and both dissolve senses. Include mixed LBS/DQ actors and a shared-scene-versus-fresh parity case.

```csharp
for (int axis = 0; axis < 3; axis++)
    Assert.InRange(MathF.Abs(native.Position[axis] - expected.Position[axis]), 0f, 2e-5f * L);
Assert.InRange(Vector3.Distance(native.Normal, expected.Normal), 0f, 2e-5f);
Assert.InRange(normalAngleRadians, 0f, 1e-4f);
```

- [ ] Red locally: `build-slot --label dq-material -- dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release --filter FullyQualifiedName~DqShaderContractTests`. Obtain native red proof with hosted `FILTER=FullyQualifiedName~DqMaterialGpuTests` before implementation.
- [ ] Implement the helper once and wire ordinary/dissolve material variants and CPU streaming together. Preserve the existing fragment/TBN, alpha and world-transform rules. No method branch per vertex and no per-vertex matrix conversion. Variant construction, output changes and disposal stay in the new cohesive renderer partial.
- [ ] Green: repeat the local command and the hosted filter on all three backends. Require actual deformed attribute readback, nonzero witness draws and no skipped DQ facts.
- [ ] Commit `render3d(skinning): render ordinary DQ materials on CPU and GPU`.

### S3.4: Every directional cascade and dissolve depth path

**Files:** Create `KhaozEngine.Render3D/Rendering/ShadowMapRenderer.DualQuaternion.cs`, `KhaozEngine.Render.Tests/Render3D/DqCascadeContractTests.cs`, `KhaozEngine.Render.Tests/Gpu/DqCascadeGpuTests.cs`. Modify `KhaozEngine.Render3D/Internal/ShaderSources.Shadow.cs`, `KhaozEngine.Render3D/Rendering/ShadowMapRenderer.cs` (lifecycle/variant delegation), `KhaozEngine.Render3D/Rendering/ModelRenderer.Skinned.cs`, `KhaozEngine.Render3D/Scene3D.ShadowCasters.cs`.

**Interfaces:** Extend the internal cascade bind/draw selection with `SkinningProfile` and the method-local current palette slot. Plain, dissolve and inverted-dissolve depth variants use `DualQuaternionGlsl`. CPU shadows reuse the already DQ-deformed surface.

- [ ] Write `All_cascade_variants_request_the_selected_palette` and native `DQ_cascade_silhouettes_follow_the_DQ_body`. Use the collapsed LBS control, all configured cascades, shadow-only and camera-culled actors, alpha cutout, both dissolve senses, both execution settings and origin changes. Assert every requested cascade rendered the witness and differs from its LBS control where expected.

```csharp
Assert.Equal(requestedCascadeCount, observedCascadeCount);
Assert.True(dqWitnessPixelCount > 0);
Assert.False(dqDepthWords.SequenceEqual(lbsDepthWords));
```

- [ ] Red: `build-slot --label dq-cascades -- dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release --filter FullyQualifiedName~DqCascadeContractTests`. Hosted red filter `FullyQualifiedName~DqCascadeGpuTests`.
- [ ] Add separate DQ depth pipelines and profile-aware binds, reusing S3.2's current palette without preparing/uploading it per cascade. Preserve mask, cull and dissolve selection.
- [ ] Green: repeat the local command and hosted filter. Require per-cascade evidence, not only main-frame similarity.
- [ ] Commit `render3d(skinning): use DQ in every directional shadow variant`.

### S3.5: Point faces, transient static rows and dynamic rows

**Files:** Create `KhaozEngine.Render3D/Rendering/PointShadowRenderer.DualQuaternion.cs`, `KhaozEngine.Render.Tests/Render3D/DqPointContractTests.cs`, `KhaozEngine.Render.Tests/Gpu/DqPointGpuTests.cs`. Modify `KhaozEngine.Render3D/Internal/ShaderSources.PointShadow.cs`, `KhaozEngine.Render3D/Rendering/PointShadowRenderer.Skinned.cs`, `KhaozEngine.Render3D/Rendering/PointShadowRenderer.cs` (lifecycle/variant delegation), `KhaozEngine.Render3D/Scene3D.SkinnedPointShadows.cs`.

**Interfaces:** Profile-aware point draw records select the DQ shader family and current DQ palette, preserving face scheduling and static-transient/dynamic classification.

- [ ] Write `Point_variant_selection_covers_all_face_row_kinds` and native `DQ_point_depth_uses_the_current_DQ_pose`. Exercise all six selected faces, static transient rows, dynamic rows, shadow-only actors, camera-culled casters, both dissolve senses and both execution settings. Assert face masks and native depth silhouettes from the DQ witness. Retain the existing LBS point golden families unchanged.

```csharp
Assert.Equal(0x3f, observedFaceMask);
Assert.True(staticTransientRowsObserved && dynamicRowsObserved);
Assert.False(dqDepthWords.SequenceEqual(lbsDepthWords));
```

- [ ] Red: `build-slot --label dq-points -- dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release --filter "FullyQualifiedName~DqPointContractTests|FullyQualifiedName~SkinnedPointShadowShaderTests"`. Hosted red filter `FullyQualifiedName~DqPointGpuTests`.
- [ ] Replace the independent matrix helper only in new DQ variants with the shared ordinary helper. Reuse the prepared palette and preserve all point cache/lifetime/scheduling rules.
- [ ] Green: repeat the command and hosted filter, requiring every requested face/row case to execute.
- [ ] Commit `render3d(skinning): use DQ in every skinned point shadow face`.

### S3.6: Independent outlines on CPU and GPU

**Files:** Create `KhaozEngine.Render3D/Rendering/TargetOutlineRenderer.DualQuaternion.cs`, `KhaozEngine.Render.Tests/Render3D/DqOutlineContractTests.cs`, `KhaozEngine.Render.Tests/Gpu/DqOutlineGpuTests.cs`. Modify `KhaozEngine.Render3D/Scene3D.SkinnedTargetOutlines.cs`, `KhaozEngine.Render3D/Rendering/TargetOutlineSkinningStore.cs`, `KhaozEngine.Render3D/Rendering/TargetOutlineRenderer.Skinned.cs`, `KhaozEngine.Render3D/Rendering/TargetOutlineRenderer.cs` (lifecycle/variant delegation), `KhaozEngine.Render3D/Internal/ShaderSources.TargetOutline.cs`.

**Interfaces:** The outline pose store retains profile/source/prepared DQ identity independently of colour submission. Full and visible-only mask variants select separate DQ pipelines, with the 4096-byte layout from S3.2.

- [ ] Write `Outline_only_submission_retains_profile_and_validates_first` and native `Both_outline_masks_follow_the_DQ_surface`. Submit only an outline, then a mixed ordinary/outline scene. Exercise both occlusion modes, both CPU/GPU settings, cutout and dissolve senses, independent actors/slots and origin changes. Assert the DQ witness contour and disagreement with the LBS outline control.

```csharp
Assert.True(outlineOnlyMaskPixels > 0);
Assert.False(dqMaskWords.SequenceEqual(lbsMaskWords));
Assert.Equal(0, colourDrawCount);
```

- [ ] Red: `build-slot --label dq-outlines -- dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release --filter "FullyQualifiedName~DqOutlineContractTests|FullyQualifiedName~SkinnedTargetOutlineApiTests"`. Hosted red filter `FullyQualifiedName~DqOutlineGpuTests`.
- [ ] Route CPU outlines through the profile evaluator and GPU masks through the common DQ helper. Prepare once per independently submitted outline pose. Do not require or borrow a colour draw. Retain atomic failure and resource retirement.
- [ ] Green: repeat the local command and hosted filter. Bake any new DQ mask goldens on their owning hosted backends only.
- [ ] Commit `render3d(skinning): preserve DQ in independent target outlines`.

### S3.7: Garment binding, cache identity and rigid socket control

**Files:** Modify `KhaozEngine.Attachments.Render3D/SkinnedAttachmentBinding.cs`, `KhaozEngine.Attachments.Render3D/AttachmentPartResources.cs`, `KhaozEngine.Attachments.Render3D/AttachmentSetBinding.cs`, `KhaozEngine.Attachments.Render3D/AttachmentSetCache.cs`, `KhaozEngine.Attachments.Render3D/SkinnedAttachmentUpload.cs`, `KhaozEngine.Attachments.Render3D/AttachmentDraw.cs`, `KhaozEngine.Attachments.Render3D/LoadedAttachmentSet.cs`. Create `KhaozEngine.Render.Tests/Render3D/DqAttachmentTests.cs`, `KhaozEngine.Render.Tests/Gpu/DqAttachmentGpuTests.cs`. Test existing `KhaozEngine.Render.Tests/Render3D/SkinnedAttachmentOutlineTests.cs` and `KhaozEngine.Render.Tests/Render3D/SkinnedAttachmentSubmissionTests.cs`.

**Interfaces:** Add `AttachmentSetCache.Get(string key, string expectedSha256, SkinningProfile profile)` for explicit pre-upload selection. The existing overload stays LBS. Add profile to cached definition/mesh identity, retained resources and `SkinnedAttachmentBinding`. `LoadedAttachmentSet.Bind(map, hostMesh)` requires a matching profile as well as names/inverse binds. Joint palette remapping and `BoneSocket.ComposeRigid` remain their existing operations.

- [ ] Write `Matching_host_and_garment_profiles_bind`, `Profile_mismatch_or_cached_reuse_refuses_before_draw` and `Rigid_socket_transform_is_unchanged`. Compare two loads of the same attachment bytes under LBS/DQ, require separate leases/identities and coherent material parts. Verify garment colour/outline and CPU fallback use DQ. Matching skeletons with mismatched profiles must fail with part/host context. Rigid item matrices must remain exactly equal, while their fit evidence identity is invalidated by a changed host profile.

```csharp
Assert.Throws<InvalidDataException>(() => dqGarment.Bind(map, lbsHost));
Assert.Equal(oldRigidSocketMatrix, newRigidSocketMatrix);
Assert.NotEqual(lbsFitIdentity, dqFitIdentity);
```

- [ ] Red: `build-slot --label dq-attachments -- dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release --filter "FullyQualifiedName~DqAttachmentTests|FullyQualifiedName~SkinnedAttachmentOutlineTests|FullyQualifiedName~SkinnedAttachmentSubmissionTests"`. Hosted red filter `FullyQualifiedName~DqAttachmentGpuTests`.
- [ ] Thread selection through attachment import/upload/cache and draw resources, validate agreement at binding and retain the selected profile for independent outlines. Do not transform rigid sockets by a weighted DQ. Intentional cross-method garments remain unsupported until separate authored fit evidence and an explicit contract are designed.
- [ ] Green: repeat the command and hosted filter. Require existing LBS and rigid attachment tests unchanged.
- [ ] Commit `attachments(skinning): bind garment profiles and invalidate mismatched fit identity`.

### S3.8: Source-aware history and previous CPU positions

**Files:** Modify `KhaozEngine.Render3D/Internal/MotionHistory.cs`, `KhaozEngine.Render3D/Internal/CpuSkinnedMotion.cs`, `KhaozEngine.Render3D/Scene3D.MotionTarget.cs`, `KhaozEngine.Render3D/Scene3D.MotionKeys.cs`, `KhaozEngine.Render3D/Scene3D.DrawDescriptors.cs`. Create `KhaozEngine.Render3D/Internal/SkinnedMotionIdentity.cs`, `KhaozEngine.Render.Tests/Render3D/DqMotionHistoryTests.cs`. Test existing `KhaozEngine.Render.Tests/Render3D/MotionHistoryTests.cs`, `KhaozEngine.Render.Tests/Render3D/CpuSkinnedMotionTests.cs`, `KhaozEngine.Render.Tests/Render3D/Scene3DMotionHistoryTests.cs` under `KhaozEngine.Render.Tests/Render3D/`.

**Interfaces:** `SkinnedMotionIdentity` is a readonly record struct `(int MeshIndex, int Generation, ulong SourceIdentity, SkinningProfile Profile)`. Source IDs are assigned once per uploaded snapshot. Add `MotionHistory.RecordSkinned(MotionKey key, in Matrix4x4 world, in SkinnedMotionIdentity identity, ReadOnlySpan<Matrix4x4> lbs, ReadOnlySpan<RigidDualQuaternion> dq, in MeshBounds worldBounds)` and `TryGetPreviousSkinned(MotionKey key, in SkinnedMotionIdentity expected, out Matrix4x4 world, out ReadOnlySpan<Matrix4x4> lbs, out ReadOnlySpan<RigidDualQuaternion> dq) : bool`. `TryGetPreviousBounds(MotionKey key, out MeshBounds worldBounds) : bool` retains replaced actors' bounds. Add `CpuSkinnedMotion.AppendPrevious(ReadOnlySpan<SkinnedVertex> source, SkinningProfile profile, ReadOnlySpan<Matrix4x4> lbs, ReadOnlySpan<RigidDualQuaternion> dq, in Matrix4x4 previousWorld, List<Vector3> destination)`. Preserve existing overloads for unchanged LBS callers.

- [ ] Write `Previous_DQ_words_are_copied_not_reextracted`, `Same_bone_count_replacement_seeds_only_the_affected_actor` and `CPU_previous_positions_use_the_previous_profile`. Assert independent known previous DQ positions for joint-only motion, placement-only motion and origin rebase. Cover absent history, method/source/generation replacement, duplicate keys, unsubmitted actors and temporal off/on. Assert actor B retains history when actor A changes and A's old/current bounds are retained for S3.10's projected rejection region.

```csharp
Assert.False(history.TryGetPreviousSkinned(keyA, in replacementIdentity, out _, out _, out _));
Assert.True(history.TryGetPreviousSkinned(keyB, in unchangedIdentity, out _, out _, out _));
Assert.Equal(expectedPreviousDqWords, storedPreviousDqWords);
```

- [ ] Red: `build-slot --label dq-history -- dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release --filter "FullyQualifiedName~DqMotionHistoryTests|FullyQualifiedName~MotionHistoryTests|FullyQualifiedName~CpuSkinnedMotionTests|FullyQualifiedName~Scene3DMotionHistoryTests"`.
- [ ] Extend grow-only history storage for profile/source identity and prepared DQ payloads. Seed current data when correspondence fails. Replace previous CPU matrix blending only on the DQ path, preserving absolute history transforms and reduction against the current render origin. Retain unrelated actors' history.
- [ ] Green: repeat the command. Require prior LBS collision/reset semantics and actor-local rejection assertions to pass.
- [ ] Commit `render3d(skinning): retain DQ source identity and previous CPU poses`.

### S3.9: Previous DQ GPU palettes and temporal material variants

**Files:** Create `KhaozEngine.Render3D/Rendering/ModelRenderer.DualQuaternionMotion.cs`, `KhaozEngine.Render.Tests/Render3D/DqMotionContractTests.cs`, `KhaozEngine.Render.Tests/Gpu/DqMotionGpuTests.cs`. Modify `KhaozEngine.Render3D/Internal/ShaderSources.MotionModel.cs`, `KhaozEngine.Render3D/Rendering/ModelRenderer.Motion.cs`, `KhaozEngine.Render3D/Rendering/SkinnedMotionPalette.cs`, `KhaozEngine.Render3D/Rendering/ModelRenderer.DualQuaternion.cs`, `KhaozEngine.Render3D/Scene3D.MotionTarget.cs`.

**Interfaces:** DQ temporal shaders use current DQ input plus the 4160-byte previous payload in a 4352-byte slot. Extend method-aware motion packing/binding with `(slot, previousWorld, previousDq)` while retaining LBS layout. `SkinningProfile` selects current and previous matching shader family together.

- [ ] Write `DQ_motion_layout_reads_both_prepared_poses` and native `DQ_motion_matches_known_current_and_previous_surfaces`. Read back previous/current model-space positions and motion vectors for stationary placement with joint motion, moving placement, absent history, same-count mesh replacement, method replacement, origin changes and mixed LBS/DQ actors. Require both plain and dissolved materials. Compare CPU and GPU execution to the independent previous-pose fixture, not only to one another.

```csharp
Assert.InRange(MathF.Abs(nativePrevious.X - expectedPrevious.X), 0f, 2e-5f * L);
Assert.Equal(Vector2.Zero, firstSightMotionWithStationaryCamera);
Assert.Equal(expectedPreviousDqWords, uploadedPreviousDqWords);
```

- [ ] Red: `build-slot --label dq-motion -- dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release --filter FullyQualifiedName~DqMotionContractTests`. Hosted red filter `FullyQualifiedName~DqMotionGpuTests`.
- [ ] Reuse the DQ helper for current and previous positions, upload stored previous DQ words and seed current payload on invalid correspondence. Never convert last frame's matrices again or pair previous LBS data with a current DQ pipeline. Keep descriptor-set limits and local temporal rejection behavior.
- [ ] Green: repeat the command and hosted filter. Require actual motion target readback and actor-local replacement evidence.
- [ ] Commit `render3d(skinning): evaluate previous DQ poses in temporal shaders`.

### S3.10: Actor-local temporal history rejection

**Files:** Create `KhaozEngine.Render3D/Rendering/TemporalHistoryRejection.cs`, `KhaozEngine.Render3D/Scene3D.SkinningHistoryRejection.cs`, `KhaozEngine.Render3D/Internal/ShaderSources.HistoryRejection.cs`, `KhaozEngine.Render.Tests/Render3D/DqHistoryRejectionTests.cs`, `KhaozEngine.Render.Tests/Gpu/DqHistoryRejectionGpuTests.cs`. Modify `KhaozEngine.Render3D/Scene3D.TemporalResolve.cs`, `KhaozEngine.Render3D/Rendering/TemporalResolveRenderer.cs`, `KhaozEngine.Render3D/Rendering/TemporalSplitResolve.cs`, `KhaozEngine.Render3D/Internal/ShaderSources.TemporalResolve.cs`, `KhaozEngine.Render3D/Internal/ShaderSources.TemporalAccumulate.cs`.

**Interfaces:** `TemporalHistoryRejection` owns an R8UNorm mask and exposes `Reserve(int count)`, `Clear()`, `Add(MeshBounds? currentWorld, MeshBounds? previousWorld)`, `Prepare(IGpuCommandList commands, in FrameView current, FrameView? previous)` and `Texture`. Add an optional rejection texture to `TemporalResolveInputs`, defaulting to a zero mask. Fused and split accumulation both assign zero previous-history weight where the mask is set. The ordinary seam has no coverage dependency.

- [ ] Write `Profile_or_source_change_projects_only_its_old_and_current_bounds` and native `Replaced_actor_rejects_history_without_resetting_the_other_actor`. Exercise identical bone counts, handle reuse, changed method, absent previous actor, near-plane crossings, viewport edges, camera movement, origin rebasing and a second render in the same frame. Assert an unchanged distant actor retains its history and both fused/split resolve reject the changed actor's old/current projected region. Near-plane uncertainty conservatively covers the affected projection, not an arbitrary smaller rectangle.

```csharp
Assert.Equal(1f, rejectionAtOldActorA);
Assert.Equal(1f, rejectionAtCurrentActorA);
Assert.Equal(0f, rejectionAtUnchangedActorB);
```

- [ ] Red: `build-slot --label dq-rejection -- dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release --filter FullyQualifiedName~DqHistoryRejectionTests`. Hosted red filter `FullyQualifiedName~DqHistoryRejectionGpuTests`.
- [ ] Render the union of outward projected old/current bounds into the mask before accumulation, preserving first-render history ownership, origin conversion and resource retirement. Seed current motion independently from rejection. Background motion sentinels are not a replacement for explicit history rejection. At stage-4 reconciliation adapt the existing `EC:KhaozEngine.Render3D/Rendering/CoverageTransitionMask.cs` behavior to this generic seam, preserving its GPU transition decisions rather than adding two conflicting masks.
- [ ] Green: repeat local and hosted commands. Require native history-weight/readback evidence for both resolve entries, warm reuse and unchanged unrelated actor history.
- [ ] Commit `render3d(skinning): reject changed skin history in its projected region`.

### S3.11: Posed culling, all-consumer gate and ordinary API documentation

**Files:** Modify `KhaozEngine.Render3D/Scene3D.SkinnedSubmission.cs`, `KhaozEngine.Render3D/Scene3D.SkinnedCasters.cs`, `KhaozEngine.Render3D/Scene3D.SkinningProfiles.cs`, `KhaozEngine.Render3D/README.md`, `KhaozEngine.Attachments.Render3D/README.md`, `docs/USING-KHAOZENGINE.md`, `docs/CROSS-PLATFORM.md`. Create `KhaozEngine.Render.Tests/Render3D/DqCullingTests.cs`, `KhaozEngine.Render.Tests/Gpu/DqConsumerMatrixGpuTests.cs`.

**Interfaces:** Ordinary DQ visibility classification, point retention and projected rejection consume the S2.6 posed envelope, transformed conservatively to the required frame. Public activation requires the complete tested consumer matrix. LBS retains its established heuristic unless the owning coverage reconciliation already replaced it.

- [ ] Write `DQ_bound_retains_a_surface_outside_the_rest_heuristic` and native `Every_consumer_uses_the_same_selected_DQ_surface`. Put a valid posed witness across camera/cascade/point frustum edges, change views/origins and exercise shadow-only/outline-only submissions. Assert actual readback surfaces are inside the bound, on CPU and GPU, and every pass uses the selected profile. Missing capability/envelope must refuse before queue mutation. Include scene reuse versus a fresh scene.

```csharp
for (int axis = 0; axis < 3; axis++)
    Assert.InRange(nativePosition[axis], worldBounds.Min[axis], worldBounds.Max[axis]);
Assert.True(mainObserved && cascadeObserved && pointObserved && outlineObserved);
```

- [ ] Red: `build-slot --label dq-culling -- dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release --filter "FullyQualifiedName~DqCullingTests|FullyQualifiedName~Scene3DSkinnedCullingTests|FullyQualifiedName~SkinnedPointShadowCullingTests"`. Hosted red filter `FullyQualifiedName~DqConsumerMatrixGpuTests`.
- [ ] Replace the DQ 1.5x rest-bound shortcut with the proved posed envelope for all ordinary retention/rejection decisions. Enable public DQ draw admission only when all variants exist. Sweep Markdown for `SkinningMath`, `UseGpuSkinning`, grounding, material parts, outlines, attachments and motion, documenting explicit opt-in, unsupported scale and the coverage adoption hold. No changelog/version/release work is included here.
- [ ] Green: repeat the local command, run the full focused hosted filter `FullyQualifiedName~Dq`, then the stage-exit build/full headless suite and whole-tree guards. Freeze inputs for parity and report real animation-input differences separately under #1530. Unchanged LBS goldens must pass.
- [ ] Commit `render3d(skinning): gate complete DQ consumers with posed bounds`.

Stage 3 exit: ordinary DQ works on both execution settings through all passes, fitting queries, attachments, bounds and previous poses, with native proof on all three backends. Covered actors remain gated on stage 4.

## Stage 4: Separate NC2 DQ coverage milestone (7 tasks)

**Dependency:** The equipment coverage program owns `feature/equipment-coverage`, inspected at `8be362eecff2a28c6798e6f59bbb116fe1eb8268`. Its code is not in this plan's baseline. All `EC:` files below are future reconciliation targets, not instructions to write in that other worktree. Its owning orchestrator must first provide an agreed compatible integration revision and preserve its existing acceptance gates. Reconcile through that program before any S4 implementation task. This planning assignment performs no merge.

### Joint design gate, before S4.1

DQ4 approves the direction, not an unwritten exact kernel inventory. Jointly review and record an amendment with the coverage owner covering all of the following:

- Supported profile/reference/schema version IDs and backward decoding rules. Bind method/version, source and ordered influence words, prepared palette identity, topology/recipes, oracle input identity, attachment fit and caches/certificates. Existing LBS identities must continue decoding with their old meaning.
- The literal scalar DQ skinning inventory, input-read placement, inclusive flush and signed-zero rules, hemisphere branches, denominator refusal, identity bypass, precise compiler/artifact checks and corrected reciprocal/root machinery. Ordinary and coverage share uploaded palette words, not necessarily output words. Specify their source producer's cross-CPU contract without changing LBS composition.
- Where precise DQ source and normal/tangent evaluation enters before refinement, how protected originals/aliases/zero-work rows behave, and the exact uncovered fallback under guard refusal or unavailable compute. Preserve recipe/gather order and the one selected current/previous final surface.
- DQ outward bounds for source, refined and conformed/fallback surfaces, placement/origin conditioning, maximum permitted recession, culling, all shadow retention and local temporal rejection. S2.6's ordinary envelope is not a certificate for these later surfaces.
- A separate reviewed DQ corpus and exact oracle/certificate migration, leaving all 72 LBS cases and 144 hashes unchanged. Agree the oracle sample domain and the distinction from continuous carry domains.
- Ownership and compatible signatures for the common prepared palette and new coverage entry points below. Record how the active program's already accepted Design B rulings, including safe alternatives, supported content domains and hard per-frame budget policy, constrain later DQ work. Carry feasibility remains stage 5, not permission to reuse LBS cells.

Create `docs/design/EQUIPMENT-COVERAGE-DQ-AMENDMENT-2026-10-22.md` in the eventual integration checkout and update the kernel contract only after that joint review. The owning orchestrator records acceptance before executing the seven tasks. If its current API differs from the inspected snapshot, revise exact paths/signatures here at that checkpoint rather than improvising during kernel work.

### S4.1: Versioned coverage identity and LBS compatibility

**Files:** Modify `EC:KhaozEngine.Render3D/Coverage/HostCoverageMap.cs`, `KhaozEngine.Render3D/Coverage/HostCoverageMapJson.cs`, `KhaozEngine.Render3D/Coverage/HostCoveragePart.cs`, `KhaozEngine.Render3D/Coverage/CoverageOracle.cs`, `KhaozEngine.Render3D/Coverage/CoverageSolidCertificate.cs` in that directory. Create `KhaozEngine.Render3D/Coverage/CoverageSkinningIdentity.cs`, `KhaozEngine.Render.Tests/Render3D/DqCoverageIdentityTests.cs`. Modify the jointly accepted DQ amendment and `docs/design/EQUIPMENT-COVERAGE-KERNEL-CONTRACT-2026-10-18.md` in the integration checkout.

**Interfaces:** `CoverageSkinningIdentity` is an immutable method/version plus ordered source/palette identity used by host/refinement/oracle/fit/cache identities. New DQ entry points require it explicitly. Preserve existing LBS constructors and old JSON semantic hashes. Reserve unsupported version rejection at decode/admission.

- [ ] Write `LBS_legacy_identities_and_manifest_are_unchanged`, `DQ_cannot_reuse_an_LBS_certificate` and `Changing_method_version_order_or_palette_invalidates_evidence`. Assert old LBS JSON round-trips retain their exact semantic digest. Mutated DQ version/method/source/influence order/palette words must reject the old certificate or oracle result, not just produce a different display label.

```csharp
Assert.Equal(legacyLbsSemanticHash, decodedLbsSemanticHash);
Assert.False(oldCertificateAcceptsDq);
Assert.False(certificateAcceptsMutatedVersion);
```

- [ ] Red: `build-slot --label dq-coverage-identity -- dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release --filter FullyQualifiedName~DqCoverageIdentityTests`.
- [ ] Implement the joint identity/schema decision, preserving old LBS meaning and binding all method-dependent evidence. Unknown DQ graph versions fail closed. Do not regenerate the frozen LBS manifest.
- [ ] Green: repeat the command and run the existing frozen-manifest check selected by the coverage program's accepted integration revision.
- [ ] Commit `coverage(skinning): bind versioned DQ source identities`.

### S4.2: Exact C# DQ source reference and independent corpus

**Files:** Create `KhaozEngine.Render3D/Coverage/CoverageReferenceDualQuaternion.cs`, `KhaozEngine.Render.Tests/Render3D/DqCoverageReferenceTests.cs`, `KhaozEngine.Render.Tests/Fixtures/dual-quaternion/coverage-v1-words.json`. Modify `EC:KhaozEngine.Render3D/Coverage/CoverageReferenceSkinning.cs`, `KhaozEngine.Render3D/Coverage/CoverageReferenceNormals.cs`, `KhaozEngine.Render3D/Coverage/CoverageOracle.cs`, and `KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj`.

**Interfaces:** Produce `CoverageReferenceDualQuaternion.TrySkin(CoverageReferenceArithmetic f, CoverageReferenceSkin skin, CoverageSourceTopology source, ReadOnlySpan<RigidDualQuaternion> palette, Span<ModelVertex> posed, out CoverageFailureCode code) : bool`. The joint identity dispatch chooses this reference or the unchanged LBS reference. Apply the accepted `[Binary32Kernel]` inventory/analyzer seam.

- [ ] Write `DQ_reference_matches_independently_derived_words`, `Refinement_is_after_DQ_source_skinning` and `DQ_reference_preserves_protected_and_alias_inputs`. Pin every position/direction/attribute output word, branch tally and refusal using identity, single-joint, twist, ambiguous order, signed zeros and below/equal/above inclusive flush-boundary cases. Demonstrate DQ of interpolated rest influences differs from interpolating already skinned sources and require the latter. Invalid tiny norm must refuse with the designated failure code.

```csharp
Assert.Equal(independentExpectedWords, referenceWords);
Assert.False(skinRestInterpolatedWords.SequenceEqual(refinePosedSourceWords));
Assert.Equal(expectedRefusalCode, actualRefusalCode);
```

- [ ] Red: `build-slot --label dq-coverage-reference -- dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release --filter "FullyQualifiedName~DqCoverageReferenceTests|FullyQualifiedName~CoverageReferenceSkinningTests"`.
- [ ] Transcribe the jointly pinned graph exclusively through reference arithmetic and exact read rules. Share CPU-prepared palette values, update the initial normal/tangent basis, then retain refinement and gather order. Derive corpus words independently with the established integer/high-precision oracle, with reviewable provenance. Do not call ordinary `SkinningEvaluation` as the NC2 reference.
- [ ] Green: repeat the command and require the entire unchanged LBS source-reference suite as well as the new word corpus.
- [ ] Commit `coverage(skinning): add an exact rigid DQ source reference`.

### S4.3: Precise native DQ source compute stage

**Files:** Create `KhaozEngine.Render3D/Coverage/Gpu/CoverageGpuDualQuaternionGlsl.cs`, `KhaozEngine.Render.Tests/Render3D/DqCoverageKernelContractTests.cs`, `KhaozEngine.Render.Tests/Gpu/DqCoverageKernelGpuTests.cs`. Modify `EC:KhaozEngine.Render3D/Coverage/Gpu/CoverageGpuSkinRefineGlsl.cs`, `KhaozEngine.Render3D/Coverage/Gpu/CoverageGpuLayout.cs`, `KhaozEngine.Render3D/Coverage/Gpu/CoverageGpuUpload.cs`, `KhaozEngine.Render3D/Coverage/Gpu/CoverageGpuPipeline.cs`, `KhaozEngine.Render3D/Coverage/Gpu/CoverageGpuResources.cs` in that directory.

**Interfaces:** `CoverageGpuDualQuaternionGlsl.Source` is the precise transcription of S4.2, selected by reference version outside the vertex work item. GPU frame upload carries eight words per DQ joint and validated integer source indices. Reuse the established corrected division/root and precise artifact validation seams.

- [ ] Write `DQ_compute_contract_contains_the_reviewed_operation_inventory` and native `All_DQ_source_words_equal_the_CSharp_reference`. Compare actual output words with zero tolerance across all DQ corpus cases, flush boundaries, negative/zero hemisphere dots, norm refusal and normal/tangent branches. Run the old frozen LBS corpus and exact-job counts unchanged. Add precise shader compilation/artifact checks rather than accepting approximate reciprocal code generation.

```csharp
Assert.Equal(csharpReferenceWords, nativeWords);
Assert.Equal(expectedBranchTally, nativeBranchTally);
Assert.Equal(0, toleratedMismatches);
```

- [ ] Red: `build-slot --label dq-coverage-kernel -- dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release --filter FullyQualifiedName~DqCoverageKernelContractTests`. Hosted red filter `FullyQualifiedName~DqCoverageKernelGpuTests`.
- [ ] Replace only the selected source stage, upload the exact palette words and preserve following refinement/conformation/ordered gathers. Prevent contraction/reassociation through the existing precise seam. Do not replace the corrected reciprocal with a native approximate reciprocal or consume driver reserve as a numerical tolerance.
- [ ] Green: repeat local and hosted commands, retaining exact-word/precise-artifact evidence for all three native backends and their CPU palette producers.
- [ ] Commit `coverage(skinning): compute precise DQ source vertices under NC2`.

### S4.4: Coverage and fallback outward bounds

**Files:** Create `KhaozEngine.Render3D/Coverage/CoverageDualQuaternionBounds.cs`, `KhaozEngine.Render.Tests/Render3D/DqCoverageBoundsTests.cs`. Modify `EC:KhaozEngine.Render3D/Coverage/CoverageActorBounds.cs`, `KhaozEngine.Render3D/Coverage/CoverageOrdinaryBounds.cs`, `KhaozEngine.Render3D/Coverage/CoverageSurfaceBounds.cs`, `KhaozEngine.Render3D/Scene3D.CoveragePaletteBounds.cs`, `KhaozEngine.Render3D/Scene3D.CoveragePrepare.cs`, plus the DQ bounds proof document.

**Interfaces:** `CoverageDualQuaternionBounds` consumes S2.6 metadata/palette and the joint precise source graph, then provides conservative source/refinement/world/origin bounds through the existing coverage bounds result types. Failed enclosures refuse coverage admission. The old matrix-convexity routines remain LBS-only.

- [ ] Write `DQ_source_refinement_recession_and_fallback_are_enclosed` and `Invalid_DQ_envelope_cannot_use_LBS_bounds`. Assert independent precise posed vertices, refined aliases, maximum allowed recession and ordinary CPU/GPU fallback vertices all lie inside their declared frame bounds. Cover world scaling, conditioning/origin limits, shadow retention and projected rejection. Include a DQ surface outside the old convex union and overflow/refusal cases.

```csharp
for (int axis = 0; axis < 3; axis++)
    Assert.InRange(maxRecessionPosition[axis], admittedBounds.Min[axis], admittedBounds.Max[axis]);
Assert.False(admittedWithoutProof);
```

- [ ] Red: `build-slot --label dq-coverage-bounds -- dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release --filter "FullyQualifiedName~DqCoverageBoundsTests|FullyQualifiedName~CoverageActorBoundsTests|FullyQualifiedName~CoverageOrdinaryBoundsTests"`.
- [ ] Extend the reviewed outward proof through precise flush/rounding, refinement, placement and allowed recession using the accepted coverage frame contract. Include ordinary shader rounding in fallback bounds. Reject if any required bound is nonfinite/unproved. Do not substitute the rest sphere or enlarge recession/clearance caps.
- [ ] Green: repeat the command and review the extended proof. Native enclosure assertions join the S4.5 integration tests, not a new sampled claim of continuous carry safety.
- [ ] Commit `coverage(skinning): enclose DQ source final and fallback surfaces`.

### S4.5: One selected final surface, fallback and history

**Files:** Modify `EC:KhaozEngine.Render3D/Scene3D.CoverageUpload.cs`, `KhaozEngine.Render3D/Scene3D.CoverageGpu.cs`, `KhaozEngine.Render3D/Scene3D.CoverageSurfaces.cs`, `KhaozEngine.Render3D/Scene3D.CoverageOutcomes.cs`, `KhaozEngine.Render3D/Scene3D.CoverageShadows.cs`, `KhaozEngine.Render3D/Scene3D.CoverageOutlines.cs`, `KhaozEngine.Render3D/Scene3D.CoverageMotion.cs`, `KhaozEngine.Render3D/Coverage/CoverageMotionHistory.cs`, `KhaozEngine.Render3D/Coverage/CoverageSubmissionState.cs`. Create `KhaozEngine.Render.Tests/Render3D/DqCoveragePresentationTests.cs`, `KhaozEngine.Render.Tests/Gpu/DqCoveragePresentationGpuTests.cs`.

**Interfaces:** Existing selected final-surface/current-history leases gain DQ identity. Every accepted pass reads the conformed final surface directly. Explicit guard/no-compute fallback selects current ordinary DQ plus the existing uncovered failure outcome. History uses the actually presented previous final surface and preserves Submitted/Abandoned transitions.

- [ ] Write `Coverage_passes_do_not_skin_the_final_surface_again`, `Guard_and_no_compute_fallback_stay_DQ` and native `All_passes_and_motion_share_the_selected_DQ_final_surface`. Assert protected originals, aliases, zero-work actors, abandonment, refusal attribution, no-compute devices, independent outlines, all cascade/point consumers, carried-frame history input if available and origin transitions. Fallback must be marked uncovered, never successful no-clipping. Mutated identity invalidates only the affected actor's history.

```csharp
Assert.Equal(selectedFinalWords, passInputWords);
Assert.Equal(actuallyPresentedPreviousWords, motionPreviousWords);
Assert.False(uncoveredFallbackCountsAsCoverageSuccess);
```

- [ ] Red: `build-slot --label dq-coverage-presentation -- dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release --filter FullyQualifiedName~DqCoveragePresentationTests`. Hosted red filter `FullyQualifiedName~DqCoveragePresentationGpuTests`.
- [ ] Preserve one final GPU surface selection and actually presented previous-position history. Adapt explicit fallback to the ordinary DQ evaluator/pipelines, retaining refusal and local rejection semantics. No second DQ skinning of conformed vertices and no reconstructed previous ordinary body for a covered surface.
- [ ] Green: repeat the commands and require native pass readbacks, exact selected source/final words where NC2 applies and bounds containment from S4.4.
- [ ] Commit `coverage(skinning): present coherent DQ final surfaces and fallback history`.

### S4.6: DQ geometry oracle and attachment fit evidence

**Files:** Modify `EC:KhaozEngine.Render3D/Coverage/CoverageOracle.cs`, `KhaozEngine.Render3D/Coverage/CoverageSolidCertificate.cs`, `KhaozEngine.Render3D/Coverage/HostCoveragePart.cs`, `KhaozEngine.Attachments.Render3D/SkinnedAttachmentBinding.cs`. Create `KhaozEngine.Render.Tests/Render3D/DqCoverageOracleTests.cs`, `KhaozEngine.Render.Tests/Fixtures/dual-quaternion/coverage-oracle-domain.json`.

**Interfaces:** Oracle inputs/results and solid/fit certificates bind `CoverageSkinningIdentity` and the declared sample domain. Synthetic DQ armour/support fixtures use unchanged clearance, collar and recession budgets. The consumer privately rebuilds real armour/clip/layer evidence later, never uploads it into this public corpus.

- [ ] Write `New_DQ_oracles_reject_old_LBS_fit_evidence`, `Whole_triangle_clearance_is_checked` and `Fuller_DQ_body_can_fail_real_fit`. Assert actual support clearance, collar clearance, nonadjacent separation, folds, caps, aliases and protected/zero-weight boundaries at all declared samples. Add a deliberately failing fuller-shoulder fit case that refuses within unchanged budgets, proving the oracle does not hide failure by increasing reserves.

```csharp
Assert.True(validDomainAccepted);
Assert.False(fullerShoulderOutsideBudgetAccepted);
Assert.False(oldLbsFitCertificateAccepted);
```

- [ ] Red: `build-slot --label dq-coverage-oracle -- dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release --filter "FullyQualifiedName~DqCoverageOracleTests|FullyQualifiedName~CoverageOracleTests"`.
- [ ] Rebuild selected-method oracle inputs and fit certificates, preserving whole-triangle geometric tests and their existing semantics. Keep sample evidence labelled as samples. Do not reinterpret it as a continuous Design B cell or loosen any no-clipping condition.
- [ ] Green: repeat the command, requiring the synthetic valid domain to pass and the deliberate fit failure to refuse with the expected reason.
- [ ] Commit `coverage(skinning): certify DQ oracle samples and attachment fit identity`.

### S4.7: NC2 milestone proof and coverage API documentation

**Files:** Create `KhaozEngine.Render.Tests/Gpu/DqCoverageMilestoneGpuTests.cs`. Modify the DQ amendment, `docs/design/EQUIPMENT-COVERAGE-KERNEL-CONTRACT-2026-10-18.md`, `docs/USING-KHAOZENGINE.md`, `KhaozEngine.Render3D/README.md`, `KhaozEngine.Attachments.Render3D/README.md`, `docs/CROSS-PLATFORM.md` in the integration checkout.

**Interfaces:** The milestone exposes a supported DQ exact coverage reference version and accepted sample domain with complete evidence. Its adoption packet explicitly excludes unproved DQ carry domains and private wardrobe fit.

- [ ] Write native `DQ_coverage_milestone_runs_all_exact_and_final_surface_controls`. Require the reviewed DQ word corpus, precise artifacts, actual clearance, bounds, fallback and all-pass history controls. Assert the 72-case/144-hash LBS controls unchanged. Record actual matched test/case counts and refuse a missing backend or skipped DQ class.

```csharp
Assert.Equal(72, frozenLbsCaseCount);
Assert.Equal(144, frozenLbsManifestHashCount);
Assert.Equal(expectedDqWords, acceptedNativeDqWords);
Assert.Equal(0, skippedDqTests);
```

- [ ] Run the aggregate proof first: use hosted `FILTER=FullyQualifiedName~DqCoverageMilestoneGpuTests` against the test revision before fixing any uncovered integration defect. If already green, record this as verification/documentation work with no invented implementation change. Run local contract smoke with `build-slot --label dq-coverage-smoke -- dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release --filter "FullyQualifiedName~DqCoverageIdentityTests|FullyQualifiedName~DqCoverageReferenceTests"`.
- [ ] Complete only the discovered stage-4 integration gaps, sweep the living API docs and record method/version, precise graph, unsupported domains, fallback meaning and certificate rebuild obligations. If the underlying joint contract must change, stop and return it to joint review rather than alter frozen expectations.
- [ ] Green: run the local smoke, stage-exit build/full headless suite and guards, then hosted `FILTER=FullyQualifiedName~DqCoverage` on all three backends. Record exact `headSha`, cross-CPU preparation words, device/compiler evidence and untouched LBS manifest. No tolerance or fixture reduction is permitted.
- [ ] Commit `docs(coverage): document and gate exact DQ coverage support`.

Stage 4 exit: complete exact DQ coverage and explicit fallback on Metal, Direct3D11 and Vulkan, new sample/fit evidence, no changed LBS words and no unproved bound. This satisfies the engine-side coverage prerequisite of DQ5. The private game still needs its own real-asset comparison, fit validation and owner look review.

## Stage 5: DQ-specific Design B feasibility outline (3 work packages)

This is an outline, not permission to implement carry. Turn these packages into a separately reviewed test-first plan after their entry gate. The active coverage program's accepted decisions supersede its older research draft. Keep that program read only from this planning thread.

### S5.1: Reference, branch witnesses and translation-only cells

**Entry:** Stage 4 accepted, active Design B decisions reconciled, and a DQ feasibility brief agreed with that owner. Read `Carry:KhaozEngine.Render.Tests/Gpu/CoverageCarry/CoverageCarryReference.cs`, `CoverageCarryShaders.cs`, `Certificates/CoverageCarryEnclosure.cs` and `Certificates/CoverageCarryCellBinding.cs`. Their current twelve-coefficient matrix enclosure and bit-identical linear-coefficient restriction are LBS assumptions.

**Work:** Design a new DQ carry reference and certificate identity binding anchor/final-position words, method/version, source/influence order, palette conversion branches, running hemisphere witnesses and a positive outward denominator bound. First prove fixed-rotation translation-only cells over the actual rounded DQ graph, including signed-zero/flush behavior. Use new anchors and rounding enclosures. Crossing a hemisphere/conversion branch requires partitioning or an enclosure of every outcome, never an assumed smooth interpolation. Do not unskin refined vertices or interpolate rest weights.

**Exit:** An independently reviewed continuous-domain proof and positive/negative headless certificate tests for a useful declared translation-only domain, followed by hosted native zero-word comparisons to its new reference. Failure names the unsupported domain and blocks extension. Sample closeness is insufficient.

### S5.2: Rotational domains, actual geometry and refusal sequences

**Entry:** S5.1 proof accepted and the owner has accepted the measured domain being investigated. Existing LBS cells remain controls.

**Work:** Extend the enclosure to admitted rotational domains only where hemisphere/conversion boundaries and denominator bounds are proved. Recheck whole-triangle support/collar clearance, host separation, folds, aliases, caps and protected boundaries. Measure useful lifetimes on synthetic clips first, then private actual idle/walk/run/attack/layers through the consumer's own evidence. Test expiry, pose changes, synchronized equip/attack, Submitted/Abandoned, refused and replaced anchors, origin changes and previously presented final positions. Respect the active program's requirement for certified alternatives throughout any declared hard no-clipping domain. An uncovered fallback is outside that guarantee.

**Exit:** Reviewed continuous proofs and a declared supported motion/attachment domain with certified bootstrap/alternative surfaces and clear refusal policy. No universal animation or no-clipping claim from discrete oracle samples. Any unsupported blend/content goes back to the owner.

### S5.3: Carry plus refresh scheduling and budget feasibility

**Entry:** Useful S5.2 domains, accepted cost protocol and separate explicit owner authorization before any repeated benchmark/load run. Do not run a local stress experiment.

**Work:** On approved hosted GPU hardware measure DQ carry C, validation J, exact refresh E/chunks, palette conversion/bounds, atlas size, cold equip, synchronized attacks and worst-frame scheduling. Preserve the active coverage program's hard per-frame policy and existing approximately 1.2 ms / 32-actor T4 target unless the owner explicitly amends it. Record hardware and workload identity. Do not divide the old LBS exact timing by a refresh period or treat averages as a peak bound. The old draft's 0.65/0.55 ms split is an illustration, not an approved allocation. Retain zero warm managed allocation and the existing CPU encoding budget.

**Exit:** Useful certified DQ domain lifetimes and a demonstrated carry/refresh schedule fitting the unchanged accepted budget, including synchronized difficult events. Otherwise return supported-content, scheduling or budget options with measured costs to the owner. No production carry integration follows a failed feasibility gate.

## Stage 6: Final parity, visual/performance evidence and handoff (3 work packages)

These are gate outlines. No release, tag, version or consumer pin operation is a task here.

### S6.1: Exact-commit native parity and regression gates

**Entry:** Stages 2 to 4 accepted and stage 5 either accepted for the declared supported carry domain or explicitly excluded from the capability being evaluated. Exclusion cannot be presented as completion of DQ carry or of the consumer's full wardrobe requirement.

**Work:** Run the standard unfiltered hosted `cross-platform-gpu.yml` with `legs=all`, `bake=false` and empty `renderTestFilter`, as documented in `docs/CROSS-PLATFORM.md`. Confirm the exact execution SHA, all hosted backend/shard jobs, precise compiler artifacts and unchanged LBS goldens/manifest. Use frozen binary32 poses for arithmetic, and separately report real input-producer differences such as #1530. Any new golden goes through the owning hosted bake and image review without rebaking LBS controls to hide a change.

**Exit:** Complete green standard native matrix and stage-exit solution verification/guards, no missing/skipped DQ cases, no unexplained arithmetic drift. Fixed-palette success is not portable whole-scene determinism.

### S6.2: Visual fidelity and cost acceptance

**Entry:** S6.1 green and owner-approved workload/hardware for any repeated performance measurement. Visual capture follows the headless rule and writes scratch files under `/tmp/khaozengine-dq-skinning/`.

**Work:** Capture public LBS/DQ twist, bend, pivots, moving normals/tangents, normal maps, dissolved surfaces, cascades, point faces, outlines and temporal motion. Embed the evidence for owner review, not only filesystem paths. Record palette preparation/upload, grounding, ordinary main/motion/shadow ALU and memory costs, plus exact coverage and any accepted carry/refresh costs separately on each native backend and approved target GPU. Distinguish hosted software/native validation from hardware performance. The design's 1.2 to 2 times ALU envelope is not a measured pass criterion. The game privately compares actual Preserve Volume/exported shoulders and layers before judging weights, rigid item fit or wardrobe appearance.

**Exit:** Owner accepts visual limitations and measured costs for the declared domain. Missed ordinary parity limits return for investigation, never automatic tolerance increases. Missed coverage budgets, visible clipping or failed private fit return as explicit failed gates with the proper owning repository identified.

### S6.3: Release readiness packet and consumer adoption handoff

**Entry:** Accepted native, visual and cost evidence, exact coverage prerequisite and an explicit supported-domain statement. Carry status is stated independently.

**Work:** Prepare a packet naming the tested commit, public profile/reference versions, run URLs, tool/compiler/Blender provenance, supported rigid domain, numerical limits, bounds proofs, new certificates/corpus, LBS controls, performance results and limitations. Include the consumer checklist for explicit pre-upload asset opt-in, actual layered-pose validation, Preserve Volume/export comparison, attachment fit/coverage certificate rebuild and owner look approval. Link https://github.com/APKiwiOrg/KhaozEngine/issues/1534 and https://github.com/APKiwiOrg/Grimhollow/issues/547. Hand the packet to the owning orchestrator for the separately authorized engine release workflow. A future released capability and independently verified package/tag evidence are prerequisites for a game pin.

**Exit:** The orchestrator has a concrete readiness packet or a named blocker. This package creates no release/tag/version and changes no consumer repository. A released capability and private wardrobe acceptance remain later owner/orchestrator actions.

## Ambiguities and stopping points

1. The approved design fixes extraction branch/tie order and a rational DQ graph but not every scalar parenthesis or exact version-1 API name. This plan specifies those implementation choices above. Independent fixture review must check the row-vector orientation. A mathematically equivalent replacement still changes a versioned numerical graph.
2. "Tiny n" was not numerical in the design, and `1/4` was explicitly an exact-arithmetic lemma. The proposed `1/8` runtime floor is conditional on S2.6's rounded-domain proof. Failed proof blocks admission, it does not authorize narrowing input silently or weakening bounds.
3. DQ4 approves a new exact reference but does not spell out its full NC2 operation/read/flush inventory or JSON/schema compatibility. The joint stage-4 gate is mandatory before its implementation tasks. Ordinary DQ's shader graph is not that precise reference.
4. The design allows an intentionally independently authored garment method with explicit fit evidence, but defines no exception schema or admission evidence. The first binding API requires matching profiles. A cross-method exception needs its own joint contract, not a hidden override.
5. The older Design B draft leaves some budget/fallback questions open that the active program has since ruled on. Reconcile its accepted rulings at stage 4 and stage 5 entry, preserving existing hard-budget and certified-alternative obligations. Do not reopen or invent owner choices from the older draft alone.
6. Ordinary shader parity limits are approved acceptance limits, not measured results or a universal analytic guarantee. Finite but overflowing inputs, unusable envelopes and failed native limits must produce explicit failure evidence. Blender version/exporter order is recorded by S2.8, since the design's source inspection alone does not pin the installed tool.

## Coverage and dependency self-check

- DQ1 is implemented by S2.1, S2.7, S3.1 and S3.7. DQ2 by S2.2 to S2.4, S2.8 and S6.2. DQ3 by S2.1, S2.2, S2.7 and transactional S3.1. DQ4 by the joint gate and S4.1 to S4.7. DQ5 by the stage-3 adoption hold, stage-4 exit and S6.3. DQ6 by S5.1 to S5.3.
- Every consumer in design section 2 has an ordinary task. Picking and authoritative simulation remain outside scope. Rigid socket composition stays unchanged, with new body-fit evidence still required.
- Numerical identities, invalid inputs, influence-order limitations, shape controls and Blender proof are S2.1 to S2.8. Every ordinary current/previous GPU/CPU consumer and posed retention path is S3.1 to S3.11. Exact reference, bounds, final surfaces and geometry are S4.1 to S4.7.
- Sequence stage 2 before stage 3. S2.3 depends on S2.2, S2.4 on S2.3, S2.5 on S2.4, S2.6 on the frozen graph, S2.7 on the profile/preparer and S2.8 on all headless validation. Stage 3 uses S3.1/S3.2 before its shader families, S3.8 before S3.9/S3.10 and all of them before S3.11 activation. Stage 4 needs accepted stage 3 plus joint reconciliation, then identity/reference before GPU source, proved bounds before admission, final-surface integration before the milestone. Stage 5 and 6 retain their explicit entry gates.
- Count: 8 detailed stage-2 tasks, 11 detailed stage-3 tasks and 7 separately gated stage-4 tasks. Stages 5 and 6 each have 3 outline work packages, not implementation-sized tasks. Total 26 detailed tasks and 6 outline packages.
- Self-review checks spec coverage, test-first step granularity, matching type/signature names, all five Review Focus cases and plan proportion. No subagent is needed for this planning review.
