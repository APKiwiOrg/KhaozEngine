# Dual quaternion skinning: design

- **Date:** 2026-10-22
- **Status:** design approved on 2026-10-22. Implementation awaits plan review and an execution choice.
- **Issue:** https://github.com/APKiwiOrg/KhaozEngine/issues/1534
- **Consumer:** https://github.com/APKiwiOrg/Grimhollow/issues/547
- **Baseline:** `27fd915731b362305cc2d029f62557a89b2a4d71`, branch `feature/dual-quaternion-skinning`.

## 1. Goal and recommendation

Add an opt-in dual quaternion skinning method for articulated skins. Shoulders, elbows and hips should
avoid the rotational collapse of linear matrix blending. Existing content retains linear blend
skinning (LBS). One selected deformation must reach colour, depth, shadows, outlines, motion,
grounding, attachment fitting and equipment coverage.

Recommend **option B, an immutable per-skin method with rigid DQ first and a versioned coverage
extension**. All material parts of a skin use one profile. Existing APIs retain LBS defaults. DQ
supports rigid composed joint transforms, four nonnegative influences and the current 128-joint cap.
Unsupported scale or shear produces a clear opt-in validation failure. An asset can explicitly select
LBS instead. Do not silently switch methods as animation crosses a scale threshold or equips armour.

Coverage-compatible DQ is part of the intended outcome. It requires a new exact source-skinning
reference, native bit-equality evidence, DQ-aware bounds and new oracle/carry certificates. A DQ body
cannot retain an LBS coverage host. The current LBS coverage path and its frozen expectations remain
unchanged controls. Publishing ordinary DQ first is a development milestone, not completion of the
consumer's wardrobe requirement.

The motivating private diagnosis reports mine-left shoulder volume ratios of 0.639 under LBS and
0.984 under a same-input CPU DQ substitution. Those figures are reproduced in issue #1534. They are
evidence for the method, not engine GPU proof or a no-clipping certificate. DQ also increased crest
travel in that pose. Weight ownership and authored motion remain separate game work. DQ does not
guarantee constant mesh volume, anatomical shoulders or equipment fit.

## 2. Evidence and scope

Unqualified source references below are relative to the baseline engine checkout. `EC:` means the
read-only `feature/equipment-coverage` snapshot at `8be362eecff2a28c6798e6f59bbb116fe1eb8268`.
`Carry:` means read-only `feature/equipment-coverage-b-carry` at
`575472e3a57145fcac3409befd61d20922144ffb`. Carry code cited here is research/test infrastructure,
not a shipped runtime capability. The Design B draft is a separate research proposal, not permission
to change NC2. No other checkout is changed by this design.

References starting `Models/`, `Animation/`, `Rendering/` or `ShaderSources` are within
`KhaozEngine.Render3D`. The Design B draft cited by line below is
`EC:.superpowers/sdd/2026-10-20-equipment-coverage-performance-harness/design-b-amortized-correction-draft.md`.

### Existing consumers and required changes

| Consumer | File and line evidence | Required DQ behavior |
| --- | --- | --- |
| Joint composition and CPU evaluator | `KhaozEngine.Render3D/Models/SkinningMath.cs:20`, `:27`, `:49` | Preserve `inverseBind * jointWorld` order. Add a separate DQ evaluator and profile-aware dispatch. Keep `BlendSkinMatrix` an LBS operation. |
| Draw submission and GPU palette | `KhaozEngine.Render3D/Scene3D.DrawDescriptors.cs:76`, `:90`, `KhaozEngine.Render3D/Scene3D.GpuSkinning.cs:26`, `:41` | Resolve and validate one profile before queue mutation. Prepare one palette per submitted pose and share its values across passes. |
| Main material, normal maps and dissolve | `KhaozEngine.Render3D/Internal/ShaderSources.Model.cs:357`, `:409`, `:428`, `:534` | DQ position, normal and tangent in both ordinary and dissolve variants. Retain material, alpha and dissolve semantics. |
| CPU fallback and uploaded surface | `KhaozEngine.Render3D/Scene3D.SkinnedSubmission.cs:61`, `:73` | `UseGpuSkinning` selects execution location, not deformation method. CPU DQ must remain DQ. |
| Directional shadows | `KhaozEngine.Render3D/Internal/ShaderSources.Shadow.cs:374`, `:422`, `KhaozEngine.Render3D/Scene3D.ShadowCasters.cs:393`, `:421` | All cascades, plain and both dissolve senses use the selected current pose. CPU shadows reuse the CPU-deformed surface. |
| Point shadows | `KhaozEngine.Render3D/Internal/ShaderSources.PointShadow.cs:250`, `:267`, `:274`, `:285`, `KhaozEngine.Render3D/Rendering/PointShadowRenderer.Skinned.cs:44` | Replace the independent four-matrix helper for DQ variants. Cover every selected face and static transient/dynamic row, including shadow-only actors. |
| Current and previous GPU positions | `KhaozEngine.Render3D/Internal/ShaderSources.MotionModel.cs:38`, `:48`, `KhaozEngine.Render3D/Scene3D.MotionTarget.cs:124` | Evaluate current and previous palettes with their matching profile/version. Never feed previous LBS positions to a current DQ draw. |
| Previous CPU positions | `KhaozEngine.Render3D/Internal/CpuSkinnedMotion.cs:20`, `KhaozEngine.Render3D/Scene3D.MotionTarget.cs:162` | Replace unconditional previous matrix blending with the profile-aware position evaluator. |
| Target outlines, including independent outline submission | `KhaozEngine.Render3D/Scene3D.SkinnedTargetOutlines.cs:52`, `:64`, `KhaozEngine.Render3D/Rendering/TargetOutlineSkinningStore.cs:64`, `KhaozEngine.Render3D/Rendering/TargetOutlineRenderer.Skinned.cs:19`, `:42`, `KhaozEngine.Render3D/Internal/ShaderSources.TargetOutline.cs:27` | Carry the profile through the separate outline pose store. Both mask/occlusion modes and the outline CPU path need DQ, even without a colour draw. |
| Ground contact for presentation | `KhaozEngine.Render3D/Animation/SkinnedGrounding.cs:8`, `:26`, `:40`, `:53` | Its weighted height-column shortcut depends on linearity. DQ must evaluate each source position with the same prepared DQ palette, then take world minimum Y. Retain the LBS fast path. |
| Base-pose geometry used for fitting | `KhaozEngine.Render3D/Animation/ContractJointMap.cs:154`, `:164` | `SkinAtBase` must accept the selected profile so authored-stance measurements describe the displayed body. |
| Rest bounds and ordinary culling | `KhaozEngine.Render3D/Scene3D.SkinnedMeshLoading.cs:63`, `KhaozEngine.Render3D/Scene3D.SkinnedSubmission.cs:36`, `KhaozEngine.Render3D/Scene3D.cs:2147` | Today's 1.5x rest sphere is a documented heuristic. It is not a DQ enclosure or coverage certificate. Add a DQ envelope for the new method. |
| Skinned attachments | `KhaozEngine.Attachments.Render3D/SkinnedAttachmentBinding.cs:16`, `:34`, `:88`, `KhaozEngine.Attachments.Render3D/AttachmentDraw.cs:22` | Joint-name and inverse-bind matching alone do not establish deformation compatibility. Bind and propagate the skin profile to garment draws and outlines. |
| Rigid attachments and sockets | `KhaozEngine.Render3D/Animation/BoneSocket.cs:11`, `:14`, `KhaozEngine.Attachments.Render3D/SkinnedAttachmentBinding.cs:88` | Keep joint/socket composition unchanged. A rigid item follows one joint, not a weighted body DQ. Revalidate its fit against the new body surface. |

The renderer does not currently expose a posed-skinned triangle picking service. The Render3D search
for picking/raycast entry points finds the camera ray type, not a skin-query path
(`KhaozEngine.Render3D/Camera/Ray.cs`). This is a bounded negative survey, not a claim about every
game's collision code. Do not add picking to this feature. A consumer that skins vertices for a
presentation query must call the profile-aware evaluator. Simulation and authoritative collision
remain independent of rendering, as `Models/SkinningMath.cs:6` already requires.

### Input and skeleton evidence

`Models/SkinnedGltfMesh.cs:14` stores four indices and four weights in the existing 96-byte vertex.
`Models/GltfLoader.Skinned.cs:63` reads only `JOINTS_0` and `WEIGHTS_0`, `:99` normalizes them, `:80`
documents its single-skin assumption, and `:125` enforces the bone cap. DQ neither introduces more
influences nor changes that import limitation.

The engine supports scale. `Animation/JointPose.cs:14` stores it, `:25` composes scale/rotation/
translation, `:37` interpolates it, and `Models/GltfLoader.cs:375` imports scale tracks. Nonuniform
parent scales combined with child rotations can yield shear in composed matrices. A quaternion-only
replacement cannot preserve this existing capability. The procedural tube fixture uses translation
rest matrices (`Models/SkinnedMeshBuilder.cs:27`), and the basic skeleton composition fixture uses unit
scales (`KhaozEngine.Render.Tests/Render3D/Animation/SkeletonComposeTests.cs:16`). Those fixtures do not
make all supported skins rigid.

A read-only JSON scan of the motivating consumer asset, matched to the diagnosis's SHA-256, found no
nonunit node scales, matrix-form nodes or animation scale channels. This supports a rigid first
release for that asset. It does not certify the composed palette at every pose or any future asset.
Keep the private asset and its numerical corpus out of public engine fixtures. Consumer adoption
must validate the actual composed transforms, including attachment placement and animation layers.

## 3. Options

Scores are design judgments from 1 to 10, higher is better. Cost scores reward lower runtime and
engineering cost. Risk scores reward safer adoption. Criteria have equal weight. Coverage scores
describe compatibility after the stated work, not existing proof. A failed requirement blocks an
option regardless of its total.

| Option | Correctness | Look fidelity | Coverage compatibility | Cost | Risk | Total / 50 |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| A. Global DQ switch, with whole-draw matrix fallback for nonrigid poses | 7 | 8 | 4 | 8 | 4 | 31 |
| B. Immutable per-skin DQ profile, rigid-only opt-in, versioned coverage extension | 9 | 9 | 8 | 7 | 8 | 41 |
| C. Per-vertex LBS/DQ mix, with separately blended scale | 8 | 9 | 5 | 4 | 4 | 30 |

**A** has the smallest selection API and uniform palette preparation. Its disadvantages are a global
appearance migration, incompatible old certificates and pose-dependent method changes when scale
appears. It cannot safely infer that every asset should change. Coverage still needs all of B's
numerical and geometric work, so the small API does not remove the difficult part.

**B, recommended,** provides an explicit migration unit, stable temporal identity and a preserved LBS
control. Rigid DQ addresses the demonstrated collapse without implementing a new scale-decomposition
system. Its disadvantages are an additional palette/pipeline family, validation requirements and a
substantial coverage amendment. A rigid DQ garment should normally inherit its host profile. An
intentional independently authored garment method needs explicit fit evidence.

**C** can art-direct DQ bulging and accommodate scaled joints. It requires a new per-vertex blend
attribute, export policy, combined normal rule, larger deformation identity and new bounds and
certificate proofs. It also pays for both skinning methods on mixed vertices. Blender's Preserve
Volume checkbox does not supply this custom blend map through ordinary glTF. No current requirement
justifies adding it now.

For scale specifically, two-phase scale/shear separation can retain nonrigid animation, but needs an
agreed decomposition, reflection handling, bone-space convention and shading rule. Blender has its
own scale/pivot machinery. A generic scale-vector lerp is not equivalent. Keep this as a separately
designed extension. The immediate alternative is explicit LBS for such content, preserving its
current behavior without pretending the DQ preview matches it.

## 4. Selection and public contract

Introduce a small immutable `SkinningProfile` with method `LinearBlend` or `DualQuaternionRigid` and
an algorithm version. Names are proposed API names, not existing symbols. Attach the profile to the
CPU skin asset and retain it in uploaded mesh state. One source skin's material parts and attachment
bindings must agree. A consumer creates/selects the profile before upload, then uses ordinary draw
APIs. Existing constructors and overloads select LBS. `UseGpuSkinning` remains an execution switch.

The profile also travels through grounding, base-pose measurement, the independent outline queue,
motion history, coverage refinement/oracle construction and attachment binding. Do not keep a method
only on the colour draw descriptor. No per-frame mutable global or per-vertex selector is proposed.
Changing the profile means creating a distinct asset/handle identity and invalidating its dependent
fit, coverage and motion data. It is not an in-place toggle on a live certified actor.

Core [glTF 2.0 skinning](https://registry.khronos.org/glTF/specs/2.0/glTF-2.0.html#skins) specifies
linear blend skinning through joint attributes and inverse binds. It carries no standard Preserve
Volume selector. The first version therefore uses an explicit engine-side load/profile choice in
the game's asset configuration. Do not infer DQ from the presence of a skin or silently interpret
arbitrary `extras`. A future named extension/sidecar can transport this same profile if needed.

The opt-in validator requires finite positions and attributes, four valid integer indices, finite
nonnegative weights and either a total below `1e-8` or `abs(total - 1) <= 2^-20`. Pin the scalar
three-add total order. Retain the existing `1e-8` unweighted threshold. Validate unused indices too,
preserving the current palette-access contract. Reject more
than four positive source influences during the authoring/import validation used for DQ adoption.
Do not silently truncate and call the result Blender-equivalent. Existing LBS input behavior is
unchanged.

## 5. Rigid DQ mathematics and Blender parity

### Palette and coordinate convention

Compose each bone's skin matrix in the existing row-vector convention, `inverseBind * jointWorld`.
Convert that composed transform, not the raw joint-world transform, to a unit real quaternion `r`
and translation `t`. Using Hamilton quaternion multiplication, active rotation
`r * (p,0) * conjugate(r)` and `(x,y,z,w)` storage, define the dual part
`d = 0.5 * (t,0) * r`. This must reproduce System.Numerics point transforms, not accidentally apply
the inverse rotation because uploaded matrices are read column-major by shaders.

Conversion runs once per bone on the CPU. Pin the largest-component matrix-to-quaternion branch,
with tie order W, X, Y, Z, its scalar expression order, square root and division. Normalize the
result and choose the sign by the first nonzero component in W, X, Y, Z order being positive.
Negate both real and dual halves together. Do not extract or decompose matrices per GPU vertex.
Ordinary and coverage DQ use the same prepared binary32 palette values.

### Four-influence blend

Process positive influences in stored X, Y, Z, W order. Start real and dual sums at zero. Before
each addition, if `dot(r_i, accumulatedReal) < 0`, negate both halves of that influence. A zero dot
does not flip. Add the weighted real and dual parts in that fixed order. A zero-weight slot has no
effect. Never sort, merge or reorder influences at runtime.

Let the sums be `R` and `D`, and `n = dot(R,R)`. The mathematical unit dual quaternion is obtained by
dividing both sums by `sqrt(n)` and removing the dual component parallel to the real component.
For point and direction transformation, the equivalent rational formulation below avoids that square
root and the unobservable parallel component:

```text
rotation(p) = vector(R * (p,0) * conjugate(R)) / n
translation = 2 * vector(D * conjugate(R)) / n
position = rotation(sourcePosition) + translation
```

Implement this as one pinned scalar graph with an unnormalized 3x3 quaternion rotation numerator,
a translation numerator, one reciprocal `1/n` and multiplication. It is one chosen numerical graph,
not permission to interchange algebraically equivalent formulas between consumers. Treat nonfinite
or tiny `n` as an invalid DQ input, before presentation. With unit quaternions, normalized
nonnegative weights and running-sum hemisphere alignment, exact arithmetic gives
`n >= sum(w_i*w_i) >= 1/4` for four influences. The implementation must account for rounding when
using this bound, including the admitted weight-total interval. Tiny normalization is not an
expected valid-pose case.

This is normalized rigid DQ blending, not spherical dual quaternion interpolation. The original
[DQ skinning paper and reference shaders](https://users.cs.utah.edu/~ladislav/dq/index.html) provide
the mathematical basis. A common-weight rigid patch has a rigid local mapping and avoids the LBS
rotation-average contraction. Varying weights across a mesh can still stretch or bulge it.

### What matches Blender and what can differ

[Blender's Armature modifier](https://docs.blender.org/manual/en/latest/modeling/modifiers/deform/armature.html)
uses DQ for Preserve Volume. The inspected
[Blender 4.5 rigid DQ source](https://github.com/blender/blender/blob/v4.5.0/source/blender/blenlib/intern/math_rotation_c.cc#L1944)
aligns against the running quaternion sum and transforms through a squared-length denominator.
This design follows those rigid mathematical choices. The same source also has scale/pivot handling,
which this first method excludes.

The [Armature evaluation loop](https://github.com/blender/blender/blob/v4.5.0/source/blender/blenkernel/intern/armature_deform.cc#L310)
uses vertex-group order. glTF export may reorder or reduce those influences. Rigid DQ agrees
mathematically when the influence set, weights, order, transforms and coordinate conversion agree.
With ambiguous hemisphere choices, a different order can change the result. Two influences with
nonzero quaternion dot, or a set whose chosen representatives have positive pairwise dots, avoid
that ordering ambiguity. Exact orthogonal-quaternion ties still require the specified sign rule.
Floating-point evaluation need not match Blender bits.

Other explicit differences are the engine's four-influence limit, `1e-8` zero-weight threshold
versus Blender's contribution threshold, and exclusion of envelopes, segmented B-Bones, modifier
group masks, additional modifiers, scale and shear. Exporter resampling and weight normalization
also change inputs. Therefore the claim is parity for a validated rigid subset, not unrestricted
Blender modifier equivalence. Before judging the new shoulder weights, the game must compare actual
Preserve Volume results with the exported, engine-evaluated poses. Include a deliberately reordered
ambiguous four-influence fixture so this limitation cannot be hidden by easy poses.

### Normals, tangents and equality

Apply the blended rigid rotation to source normals and tangent XYZ. Re-normalize with the current
zero-length fallback rules (`Models/SkinningMath.cs:53`, `:61`). Preserve tangent W and keep a zero source
tangent exactly zero. Retain the existing model/world transform and fragment TBN conventions.
The selected skin method does not change rigid-model normal behavior under nonuniform world scale.

All-zero weights produce the existing identity path. Add an exact identity-palette path that
preserves source positions exactly and retains existing normal/tangent normalization semantics.
Do not snap near-identity poses to rest. Real inverse-bind products can have rounding residuals.

A single influence of weight one has the same mathematical transform as current LBS for a rigid
joint. Matrix-to-quaternion conversion and the rational graph can change binary32 results. Require
the numerical parity tolerance in section 8, not a false universal bit-equality promise. Exact
identity and representable translation fixtures additionally require exact equality where their
graphs make it attainable. DQ does not reproduce the current raw-weight scaling of arbitrary
unnormalized LBS inputs, which is why DQ validates its weight domain.

## 6. Scale, shear and invalid input policy

DQ eligibility is about the composed skin transform. Bind-space scale can cancel, so a nonunit raw
joint matrix alone is not enough to reject it. Conversely, unit local scales are not proof about an
arbitrary supplied palette. Check finite affine matrices, a positive determinant and a Gram matrix
near identity. Proposed rounding allowance is `max(abs(A*A_transpose - I)) <= 2^-16`, with exact
affine last-column form. It permits floating-point drift of intended rigid transforms, not authored
stretch animation. Quaternion extraction projects that small residual to a rotation, a bounded
preview difference that the parity test must include.

Uniform animated joint scale, nonuniform scale, reflection, shear, collapsed bases and projective
palettes are unsupported by `DualQuaternionRigid`. Do not discard their scale and continue. Static
validation reports the joint, asset and clip/sample. Dynamic/procedural validation fails before
queue mutation, with a diagnostic naming the joint and effective profile. The consumer can retain
LBS for that skin. No automatic per-vertex or per-pose LBS fallback is proposed. This avoids method
flicker and prevents an LBS sample from entering a DQ-only certificate domain.

The separate model transform remains the existing placement transform. Scale there is not a reason
to reinterpret the joint skin. Coverage still applies its existing affine/frame conditioning rules
to placement. No scale feature is removed from the default LBS method.

## 7. Equipment coverage and Design B

### Existing contract that cannot silently change

`EC:docs/design/EQUIPMENT-COVERAGE-KERNEL-CONTRACT-2026-10-18.md:13` requires every accepted device
output word to equal the C# reference, with zero tolerance. `:38` pins scalar palette composition,
and `:335` transcribes the four-matrix source-skinning graph.
`EC:KhaozEngine.Render3D/Coverage/CoverageReferenceSkinning.cs:39` and `:61` implement it.
`EC:KhaozEngine.Render3D/Coverage/Gpu/CoverageGpuSkinRefineGlsl.cs:82` implements the corresponding
device skinning before refinement. DQ changes those input positions and directions, all subsequent
geometric decisions and potentially the selected outcome. It is not an optimization preserving the
old graph.

The coverage host must describe the same selected body deformation. Covered actors draw the
conformed final surface, so that final surface may intentionally differ from the ordinary body.
Its source body, protected originals and uncovered fallback must nevertheless derive from DQ when
the actor selects DQ. LBS coverage under DQ armour/body rendering is not a no-clip proof.

Amendment R's guarantee is exact at the declared oracle samples, not every unsampled pose
(`EC:docs/design/EQUIPMENT-COVERAGE-AMENDMENT-R-2026-10-18.md:56`). Design B's proposed continuous
reuse guarantee is additional work. Neither becomes a universal DQ guarantee automatically.

### Required compatible path

1. **Version the method, retain NC2.** Add a DQ source-skinning graph/reference alongside the frozen
   LBS graph. Bind method, algorithm version, ordered influences and prepared palette identity into
   coverage host/refinement, oracle, attachment fit and cache/certificate identities. Old LBS
   certificates cannot admit DQ. Preserve the 72 frozen cases and 144-hash manifest described by the
   Design B draft at `:205`. Add a reviewed DQ corpus rather than regenerating LBS expectations.
2. **Use the exact arithmetic seam.** DQ coverage uses pinned scalar operations, NC2's inclusive
   flush rule, signed-zero policy, precise compilation and corrected division/root primitives.
   Hemisphere comparisons and the reciprocal denominator belong in the graph. Ordinary visual
   tolerances are not accepted coverage tolerances. The new palette conversion is a pinned CPU
   input producer with cross-CPU evidence, extending the existing composition obligation.
3. **Skin source vertices, then refine.** Replace only the method-specific source stage and its
   explicit fallback. Preserve the frozen recipe order, aliases, protected boundaries and ordered
   gathers. DQ of interpolated rest weights is not refinement of DQ-skinned source vertices.
   Update the reference normals' initial basis as well as positions. Conformation still rebuilds
   shading from the actual resulting surface.
4. **Publish one current and previous final surface.** Colour, motion, cascade, point faces and
   outlines continue reading the selected GPU surface. They do not DQ-skin it again.
   Preserve Submitted/Abandoned behavior, origin rebasing and local transition rejection
   (`EC:docs/design/EQUIPMENT-COVERAGE-AMENDMENT-R-2026-10-18.md:193`). Coverage guard refusal selects
   the current DQ uncovered pose. No-compute fallback must also retain DQ. Such fallback is explicit
   loss of coverage, not successful no-clipping presentation.
5. **Reprove bounds and geometry.** The existing convex union of bone-transformed influence boxes
   depends on matrix blending (`EC:KhaozEngine.Render3D/Coverage/CoverageActorBounds.cs:78`, `:91`,
   amendment R `:215`). DQ can leave that union. Replace its envelope before using it for culling,
   shadow retention, origin admission or temporal rejection. Rerun the oracle over every intended
   armour/clip/layer sample and the unchanged clearance/recession budgets. A more voluminous shoulder
   may cause a real fit failure. Do not enlarge caps or clearance reserves to conceal it.

An implementable conservative DQ envelope starts with a sphere about model origin of radius `r`
containing the source vertices. Rotation preserves `r`. Bound translation by
`2 * norm(Dsum) / sqrt(nLower)`, using palette norm maxima, nonnegative weight bounds and a proved
positive lower bound on the actual rounded real-sum norm. Add outward rounding error, refinement
rounding and the allowed conformation recession, then transform conservatively to the required
frames. This is O(joints) CPU work but can be loose. The exact-arithmetic `1/4` observation in
section 5 is a starting lemma, not a binary32 proof. Initial verification can bound actual
CPU-evaluated source positions as an independent O(vertices) control. If a valid conservative
envelope cannot be produced, coverage admission must fail. Never substitute the old LBS bound.

### Carry proofs that reopen

The inspected CPU carry reference explicitly calls the current matrix reference skinning before
interpolation (`Carry:KhaozEngine.Render.Tests/Gpu/CoverageCarry/CoverageCarryReference.cs:18`). Its
GPU exact skin stage reuses the same coverage skin shader
(`Carry:KhaozEngine.Render.Tests/Gpu/CoverageCarry/CoverageCarryShaders.cs:32`).
Its certificate enclosure literally blends twelve matrix coefficients
(`Carry:KhaozEngine.Render.Tests/Gpu/CoverageCarry/Certificates/CoverageCarryEnclosure.cs:58`).
The current cell admission holds the nine
linear coefficients bit-identical and varies only translations
(`Carry:KhaozEngine.Render.Tests/Gpu/CoverageCarry/Certificates/CoverageCarryCellBinding.cs:86`).
These are concrete LBS assumptions,
not a generic certificate for any deformation.

DQ requires new anchors, an amended carry reference and new enclosures over the actual DQ graph.
Certificates must bind hemisphere branch choices, palette-conversion branches, a positive
normalization denominator bound, operation version and final-position identity. Cells crossing a
hemisphere decision cannot assume one continuous branch. Partition them or establish a bound
covering every possible output. A positive normalization denominator alone does not prove
continuity at a sign boundary. Translation-only cells with fixed rotations are a useful first
feasibility case, but even those require new rounding enclosures and geometry checks.

The proposed recession transport can remain command-relative, with a newly DQ-skinned current base.
Whole-triangle support clearance, collar clearance, host separation, folds, caps, aliases and
protected boundaries must all be rechecked. Do not unskin refined vertices through an invented
single DQ or interpolate rest influences. Preserve the Design B draft's separation between oracle
point samples and continuous domains, particularly `:97`, `:118`, `:128`, `:176` and `:192`.

**Cost and sequencing:** source DQ compute adds arithmetic and a precise reciprocal, while the
conformation and ordered gather chain remains. CPU palette preparation and new bounds add work.
Oracle sampling, certificate construction and useful domain lifetimes must be measured again.
DQ's fuller shape can reduce geometric slack and increase refresh frequency. Existing LBS carry
timings, certified cells and performance claims do not transfer. This is a coordinated coverage
amendment with its own feasibility gate, not a shader-only skinning patch. Retain the existing
coverage GPU budget until the owner explicitly changes it.

## 8. Determinism, history and numerical agreement

Separate three obligations:

- **Presentation repeatability:** the same method, source and palette use stable influence order,
  tie rules and quaternion signs. Ordinary DQ CPU/GPU agreement is measured within a stated bound.
- **NC2 exactness:** coverage outputs match their selected versioned C# reference bit for bit on
  Metal, Direct3D11 and Vulkan for identical uploaded input words. No numerical tolerance is added.
- **Cross-platform input determinism:** identical animation/camera recipes must actually produce
  identical inputs before bitwise scene identities can be compared.

[Issue #1530](https://github.com/APKiwiOrg/KhaozEngine/issues/1530) demonstrates that `MathF.Sin`
already changes camera input bits across ARM64 and x64. DQ cannot fix that by rounding hashes or
excluding palettes. Avoid new per-vertex trigonometry. Pin matrix conversion and exact CPU palette
preparation, use frozen binary32 poses for cross-backend arithmetic tests, and separately report
differences from real animation/renderer input production. Do not claim portable whole-scene
determinism merely because a fixed-palette DQ test passes.

For ordinary DQ, propose a componentwise position bound `abs(cpu - gpu) <= 2e-5 * L`, where
`L = max(1, maximum source-position norm, maximum prepared joint-translation norm)` in model units.
Compare before model/projection, with identical uploaded palette words and within the validated
weight/rigid domain. Require normalized direction vector error at most `2e-5` and angular error at
most `1e-4` radians for nonzero normals/tangents. Use both component/vector and angle checks so one
cannot hide the other. These are proposed acceptance limits, not measured results. If native
backends miss them, investigate rather than raising the limits without owner review. Blender rigid
subset comparisons use the same position bound after coordinate conversion, with explicit input
differences reported separately.

History records method/version and source identity with both current and previous palette payloads.
Store prepared DQ values rather than independently re-extracting last frame's matrices. Retain
current-data seeding for absent history. A method change or invalid correspondence invalidates only
the affected actor's history and projected rejection region. Preserve render-origin handling.
Coverage motion continues using actually presented final surfaces, including carried frames,
instead of reconstructing the previous ordinary DQ body.

## 9. Cost estimate and resources

These estimates are arithmetic counts, not benchmark results. The current main shader expresses
four weighted 4x4 matrices, then point and direction transforms (`ShaderSources.Model.cs:409`). A
compiler can remove unused rows. The new path blends two four-component values, performs three
hemisphere dot/branch decisions, constructs a rational rotation/translation and transforms the
same attributes. Normals and nonzero tangents still require normalization.

| Work per four-influence vertex | Current LBS | Proposed rigid DQ |
| --- | --- | --- |
| Palette payload read before cache effects | Four 64-byte matrices, 256 bytes | Four 32-byte real/dual pairs, 128 bytes |
| Weighted sums | 12 to 16 coefficients, about 84 to 112 scalar multiply/add operations | Eight coefficients, about 56 scalar multiply/add operations |
| Additional blend work | None | About 21 dot-product operations, comparisons, sign choices, rotation/translation construction and one reciprocal |
| Point plus two directions, excluding common normalizations and world/projection | Roughly 140 to 170 scalar operations including blend | Roughly 180 to 240 including blend and hemisphere work |
| CPU pose preparation | Matrix composition | Matrix composition, rigid validation and quaternion/dual conversion once per joint |

Expect approximately 1.2 to 2 times the skinning ALU cost as a planning envelope, potentially offset
by half the palette traffic. Instruction contraction, matrix dead-code elimination, branch lowering,
register pressure and attribute presence can change that ratio. It is not a prediction of frame
time. Position-only shadows and outlines need their own measurement. Motion evaluates a previous
position as well, so it pays an additional blend. Covered actors skin once in compute and reuse the
final surface in every pass, so their cost model is different from repeated vertex-shader skinning.

Keep separate LBS and DQ pipeline variants and share one generated DQ helper between material,
shadow, point, outline and motion sources. Do not introduce a per-vertex method branch. A 128-bone
DQ palette is 4096 bytes versus LBS's 8192 bytes before slot alignment. Current palette packing is
64 bytes per bone (`Rendering/SkinnedBonePalette.cs:86`), so stores, dynamic offsets, resource
ranges and previous-pose stores need explicit DQ layouts. Do not reinterpret an existing matrix
buffer or promise upload savings until all stores and stats use the new stride.

The precise coverage DQ graph is not priced by the ordinary shader estimate. Its reciprocal uses
the established correction machinery and its compiler/artifact constraints. Keep the existing
precise arithmetic seam reusable without adding a coverage dependency to ordinary skinning.
Benchmark both routes with synthetic public rigs before making a budget claim. No benchmark,
build, GPU bake or stress workload was run for this document.

## 10. Verification design

Tests must distinguish the selected algorithm from a path that accidentally still uses LBS.
Preserve all existing LBS expectations. Add public synthetic DQ fixtures with explicit binary32
inputs and independently derived expected results. Use existing headless and GpuFact projects,
including `SkinningMathTests`, `SkinnedGroundingTests`, `CpuSkinnedMotionTests`,
`Render3DGpuSkinningGpuTests`, `SkinnedMotionGpuTests`, `SkinnedPointShadowGoldenTests` and
`SkinnedTargetOutlinePoseGoldenTests`.

1. **Core identities.** Exact identity, zero weights, one weight of one, representable translations,
   arbitrary rigid pivots and oblique rotations. Cover all four slots, duplicate indices, reordered
   inputs, `q`/`-q`, exact zero dots, both sides of a hemisphere decision and 180-degree rotations.
   Assert normalization, finite output and the specified equality/tolerance contract. Reject negative,
   nonfinite, nonintegral/out-of-range or excess influences and invalid rigid transforms.
2. **Independent known poses.** A 50/50 identity and 90-degree Z rotation maps `(1,0,0)` to
   `(sqrt(1/2),sqrt(1/2),0)` under DQ, versus `(0.5,0.5,0)` under LBS. Add a nonzero pivot/translation
   case, four influences and bind/inverse-bind composition. Lock expected words for the exact
   reference graph separately from real-arithmetic analytic expectations.
3. **Volume and limitations.** A synthetic tube with constant 50/50 weights and opposite twist
   endpoints demonstrates LBS cross-section collapse and DQ radius preservation. A closed constant-
   weight patch retains rigid volume within the numerical bound. Include varying-weight bulge and
   bend cases to prevent a test name from implying universal volume preservation. Public fixtures
   must not contain private consumer meshes or extracted numerical datasets.
4. **Blender compatibility.** Compare synthetic rigid exported poses to Preserve Volume using a
   recorded Blender version, source order and exported order. Exercise ambiguous ordering and scale
   exclusions deliberately. The game separately compares its reweighted shoulders and real layered
   clips before owner look approval. No Blender authoring/render run is required for this design.
5. **CPU and GPU parity.** Read back actual deformed positions/normals/tangents on Metal, D3D11 and
   Vulkan. Test both execution settings, zero/nonzero tangents, normal maps, material splits and
   dissolves. Fixed inputs isolate arithmetic from #1530. Exact coverage tests compare words with
   zero tolerance, including signed zeros and flush boundaries.
6. **Every render consumer.** Use a visibly collapsed LBS witness so colour, key shadows, point faces,
   outline masks and both dissolve senses fail if one retains LBS. Exercise shadow-only and
   camera-culled casters, outline-only submission and multi-view/origin changes. For motion, check
   moving joints with stationary placement, moving placement, previous-pose DQ, absent history,
   profile/mesh replacement and coverage's selected final-position history.
7. **CPU users and attachment identity.** Grounding must equal the minimum of independently DQ-posed
   vertices. Base-pose fitting must use the profile. Test matching and mismatched garment/host
   profiles, rigid socket equality and revalidated fit identity. New DQ envelopes must contain
   read-back surfaces, fallback surfaces and maximum allowed recession under admitted transforms.
8. **Coverage and carry.** Retain the LBS frozen corpus. Add DQ exact-reference/native parity,
   actual support clearance and protected/alias/zero-work checks. Mutate method/version and require
   certificate rejection. DQ carry cells need independent outward bounds, denominator and branch
   witnesses, translation-only and rotational-domain cases, whole-triangle geometry checks and
   expiry/refusal/history sequences. Native closeness alone is not a continuous-domain proof.
9. **Performance and adoption.** Record fixed synthetic LBS/DQ GPU timings, uploads, CPU palette and
   grounding cost, ordinary motion/shadows and coverage/carry cost on each backend. Run the normal
   hosted cross-platform gate described in `docs/CROSS-PLATFORM.md:260`. Any repeated stress or load
   test needs separate owner authorization. All local builds, tests and bakes use `build-slot`.

## 11. Staged implementation outline

This is the delivery order for a later approved plan, not an instruction to implement now.

1. Approve the owner decisions below and pin the DQ scalar graph, public profile and input domain.
   Design the coverage amendment jointly with its active program, preserving its LBS control.
2. Add headless DQ math, palette preparation, validation, analytic fixtures, bounds proof and the
   profile-aware grounding/base measurement seams. Prove Blender rigid-subset behavior.
3. Thread immutable profiles through upload, material parts, attachments, outlines and history.
   Implement ordinary CPU/GPU consumers together, including every shadow and previous-pose path.
   A main-pass-only demo does not satisfy this stage.
4. Add versioned exact DQ coverage source skinning, matching fallback, new bounds, identity and
   oracle tests. Keep final-surface consumers unified and prove all three native backends under NC2.
5. Extend Design B only after DQ-specific continuous-domain and cost feasibility. Rebuild DQ
   anchors/certificates. Report unsupported domains explicitly rather than spending driver reserve.
6. Run native parity, visual and performance gates, then hand off a released engine capability to
   the game for explicit asset opt-in, weight/clip judgement and wardrobe re-certification. Engine
   release, game pin and owner art approval remain separate authorized actions.

## 12. Owner decisions approved

The owner approved this design and every decision DQ1 to DQ6 as written on 2026-10-22
("yes to all"). Every row below is approved. The approval preserves each decision's substance.

| Id | Decision | Recommendation and implication |
| --- | --- | --- |
| DQ1 | Selection and migration unit | Choose B. Per-skin immutable profile, LBS default, coherent material parts and attachment binding. No global migration or per-vertex mix. |
| DQ2 | Rigid subset and Blender promise | Approve the defined rigid subset, running-sum hemisphere rule, numerical limits and explicit influence-order differences. Require the game's actual Preserve Volume/export comparison before judging its revised weights. |
| DQ3 | Scale/shear handling | Reject unsupported DQ opt-ins and keep those assets explicitly on LBS. Defer Blender-compatible two-phase scale/shear work rather than silently stripping scale or switching per frame. |
| DQ4 | Coverage contract change | Approve a new DQ reference/version and corpus with NC2 zero tolerance retained. Rebuild oracle and attachment/carry evidence. Existing LBS certificates remain valid only for LBS. |
| DQ5 | Delivery gate for the consumer | Require coverage-compatible DQ before the covered Grimhollow body opts in. Ordinary DQ can land as an earlier engine milestone, with no wardrobe-completion claim. |
| DQ6 | Carry scope and budget | Approve a separate DQ carry feasibility gate with new continuous-domain proofs and unchanged budget. If useful domains or cost fail, return supported content, scheduling or budget choices to the owner. Do not weaken no-clipping claims or numerical comparisons. |

This records approval of the written method, coverage and supported-domain choices, beyond issue
#1534's feature direction. The implementation plan and execution choice remain separate review steps.
