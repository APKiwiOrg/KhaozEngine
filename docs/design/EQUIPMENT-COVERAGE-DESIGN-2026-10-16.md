# Equipment coverage design

Historical construction and display APIs in this document are superseded by the GPU path in
[Amendment R](EQUIPMENT-COVERAGE-AMENDMENT-R-2026-10-18.md). The exact geometry certifier and pinned
reference remain offline evidence. The CPU cost authorization is retired and cannot start a workload.


Status: **Owner approved the written design and visible collar implication on 2026-10-16.**

Date: 2026-10-16. Source audit: `0bd787b5ccf25ea5a79afadc1bcfe315a2886016`.
Engine program: [#1418](https://github.com/APKiwiOrg/KhaozEngine/issues/1418).
Execution: [approved implementation plan](../superpowers/plans/2026-10-16-equipment-coverage.md).
Consumer handoff: [Grimhollow #547](https://github.com/APKiwiOrg/Grimhollow/issues/547).
This is a design specification, not an implementation plan. All proposed API names below are
unavailable features. Engine-first ownership, this algorithm and its host-deformation implication
are approved. The implementation plan and subagent-driven execution are also approved. Load measurements and any release
retain separate authorization.

## 1. Intended outcome and recommendation

Keep a rigid close-fitting equipment exterior while preventing the covered host surface from
breaking through it during posing. The result must be independent of camera and light direction.
Colour, depth, shadows and outlines must describe the same surface. Exposed limbs remain intact,
and the host must not acquire visible open cuts. The authored rig, animations, base host asset and
rigid equipment geometry remain unchanged.

**Recommend bounded, region-authored render-time host conformation to the actual rigid inner
surface.** Preserve the host topology. Recess an eligible patch within an authored displacement
budget, with a continuous collar returning to unchanged skin. This changes the posed render
surface and can alter the visible contour in that collar. It does not promise identical exposed
appearance. The owner must approve that implication before implementation.

Pure removal was a hypothesis, not a requirement. A bounded private probe erased the front
breakthrough but opened visible gaps beside a floating plate in normal front-oblique and grazing
rear views. A closed-surface probe retained skin instead. Correcting its target from a global
conservative plane to the actual approved inner sheet removed the pronounced cavity of that
plane approximation and cleared the observed breakthrough without saturating the same budget.
A smaller collar contour change remained. This is useful one-pose feasibility evidence, not
accepted art, an engine GPU proof or acceptance of the full pose domain.

The owner reviewed this written design, the private opaque A/B packet and the implementation plan.
Execution is in progress. Geometry, native rendering and cost acceptance remain open.
Do not implement a removal-only API that refuses every real case and call that a fix. If every
exposed contour must remain exact while the required cut has a visible gap around the rigid
plate, removal and conformation cannot both satisfy that restriction. Authored fit, a visible
sealing surface or poses must then change through a separate owner decision.

This is not cloth simulation, a general collision solver, a rig change, a mesh Boolean or armour
deformation. Arms crossing the plate and equipment against equipment remain separate consumer
art and pose clearance work. No widening of host eligibility to erase those intersections is
permitted.

## 2. Current public seams

The following facts are pinned to the audited commit. Later sections are proposals.

| Fact | Source |
| --- | --- |
| A skinned draw carries mesh, absolute model, material, dissolve, shadow flags and motion identity. It has no coverage or surface replacement field. | [SkinnedInstanceDraw](../../KhaozEngine.Render3D/SkinnedInstanceDraw.cs#L15-L75) |
| The host has indexed geometry, inverse binds, rest pose and an optional skeleton. Its 96-byte vertex carries UVs but no anatomical identity. | [SkinnedGltfMesh and SkinnedVertex](../../KhaozEngine.Render3D/Models/SkinnedGltfMesh.cs#L8-L98) |
| Both memory and file importers group skin by source material in stable first-use order. Material grouping is not anatomical segmentation. | [LoadSkinnedPartsWithMaterials](../../KhaozEngine.Render3D/Models/GltfLoader.SkinnedMaterialParts.cs#L10-L34) |
| Skinned upload retains colour/outline material sets and alpha cutoff, with provisional resources disposed on failure. | [Skinned upload](../../KhaozEngine.Render3D/Scene3D.SkinnedMeshLoading.cs#L39-L88) |
| The CPU route produces `ModelVertex` geometry, while GPU draws use a rest mesh and palette. Both feed colour and shadows. | [PrepareSkinnedDraws](../../KhaozEngine.Render3D/Scene3D.SkinnedSubmission.cs#L13-L94) |
| Colour alpha-tests UV material samples. Skinned cascaded shadows select opaque/dissolve variants without skinned alpha cutout. | [Model fragments](../../KhaozEngine.Render3D/Internal/ShaderSources.Model.cs#L480-L587), [shadow selection](../../KhaozEngine.Render3D/ShadowDepthSelection.cs#L62-L83) |
| Point shadows have dedicated CPU/GPU caster routes. Current-pose skin reaches both static-light transient rows and dynamic rows. | [PointShadowRenderer](../../KhaozEngine.Render3D/Rendering/PointShadowRenderer.Skinned.cs#L13-L164), [point scheduling](../../KhaozEngine.Render3D/Scene3D.SkinnedPointShadows.cs#L62-L129) |
| Full and visible outline masks alpha-test. Visible masks apply dissolve. Outline submission separately snapshots skin palettes. | [Outline shaders](../../KhaozEngine.Render3D/Internal/ShaderSources.TargetOutline.cs#L5-L111), [outline submission](../../KhaozEngine.Render3D/Scene3D.SkinnedTargetOutlines.cs#L12-L86) |
| Parts distinguish rigid joint/local placement from skinned geometry, without host coverage. | [AttachmentPartDefinition](../../KhaozEngine.Attachments/AttachmentPartDefinition.cs#L5-L9) |
| Attachment JSON v1 is rigid-only, v2 is mixed, and the canonical writer chooses v1 for rigid-only sets. Unknown members refuse. | [AttachmentSetJson](../../KhaozEngine.Attachments/AttachmentSetJson.cs#L12-L47), [writer](../../KhaozEngine.Attachments/AttachmentSetJson.cs#L86-L133) |
| Bound sets borrow cache resources, preallocate scratch and validate a whole pose before queuing. Borrowed draw memory lasts until the next preparation. | [BoundAttachmentSet](../../KhaozEngine.Attachments.Render3D/BoundAttachmentSet.cs#L9-L46), [borrowed draws](../../KhaozEngine.Attachments.Render3D/BoundAttachmentSet.cs#L60-L88), [queue](../../KhaozEngine.Attachments.Render3D/BoundAttachmentSet.cs#L146-L173) |
| Binding resolves contract alignment and requires its exact skeleton instance. The rigid placement seam already composes the part, current joint and actor model. | [AttachmentSetBinding](../../KhaozEngine.Attachments.Render3D/AttachmentSetBinding.cs#L61-L69), [frame validation](../../KhaozEngine.Attachments.Render3D/AttachmentSetBinding.cs#L94-L153) |

A host may have one material spanning head, body and limbs and no authored UVs. Eligibility cannot
be inferred from material, UVs, dominant bone, joint name or camera-facing triangles. Such
shortcuts can diagnose a private case but are not the engine authoring contract. Noise dissolve
does not provide geometric coverage.

Preserve the existing [attachment dependency seam](../DEPENDENCY-SEAMS.md#attachment-definitions-a-gpu-free-leaf).
Attachments remains a BCL-only leaf. Attachments.Render3D references only Attachments and Render3D.
Render3D gains no Attachments, ECS or physics dependency. No new package or backend API is proposed.

The private **equipment coverage feasibility packet, 2026-10-16** is held by the consumer owner.
It contains the removal and conformation probes, source-integrity checks and inspected opaque
A/B stills. No private mesh, image, code, numerical dataset or personal filesystem path belongs
in this public document or its eventual fixtures. Section 12 defines public synthetic equivalents.

## 3. Approaches considered

Scores from 1 to 10 are design judgments, higher is better, not measured proof. Criteria have
equal weight. No total overrides a failed requirement.

| Approach | Closed result | Keep rigid exterior | Protect exposed limbs | Posed adaptability | Cost and complexity | Author control | Total / 60 |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| A. Closure-producing coverage with a replacement patch joined to retained host boundaries | 8 | 9 | 9 | 7 | 3 | 7 | 43 |
| B. Bounded host conformation to the actual inner sheet through a weighted collar | 9 | 10 | 9 | 8 | 6 | 8 | 50 |
| C. Required authored fit or pose changes, without a runtime host change | 10 | 4 | 10 | 5 | 8 | 10 | 47 |

**B is recommended.** It retains a surface behind a static gap, has an explicit recession budget
and shares one final geometry stream across passes. Its costs are CPU work, optional render
refinement and a changed collar contour. The actual-inner-sheet probe supports this direction
without proving its production fit, performance or whole pose domain.

A would clip a host patch, then weld a cap or replacement to a moving cut boundary. An
equipment-space cap alone does not establish a crack-free join. Robust boundary triangulation,
new topology and attribute/motion correspondence add complexity beyond B. The replacement is
still visible through a static gap. It does not preserve exposed appearance automatically.

C is the fallback if the conformed collar is unacceptable. Hidden inner fit, supporting garments,
the rigid exterior or motions can change by owner art decisions. A previous anchor trial is not
proof that changing a joint solves the fit. Do not silently alter approved assets to help an
engine feature pass.

Static index-region omission is cheap when a replacement encloses that region with durable
overlap, but an exposed cut can move beyond its silhouette. Solid-only clipping misses skin
already beyond the outer face. Forward extrusion removes that skin but can erase unrelated
geometry or open a hole. Camera stencil and depth bias disagree with other views and shadows.
These are insufficient for the motivating floating-panel case.

## 4. Selected geometry contract

An authored host region has a **core**, a weighted **collar** and a zero-weight boundary. Core
strength is exactly 1. Collar strength is finite in `[0,1]`. Every point outside the region and
on its boundary has strength 0. Welded surface points agree across material and shading seams.
The core identifies skin that must be kept behind equipment. The collar explicitly permits
bounded visible host deformation. Everything else, including crossing limbs, stays protected.

A conformation command targets exactly one region of one bound host map and one rigid part of
one equipped set instance. It supplies a local coverage frame with positive Z outward, actual
inward support triangles, positive outer collar width, finite Z caps, clearance and a maximum
world-space recession. The engine contains no special body, bone, game or item names.

The support is an authored face selection of the actual equipment mesh, not a proxy hull or
global minimum plane. In coverage XY it must be a single-valued triangulated height field with
a simple convex boundary and no holes. Barycentric interpolation gives its exact inward height
`h(x,y)`. On shared edges both triangles must agree. Outside the sheet, continue the height of
the nearest point on its actual projected boundary through the finite collar. Do not fill empty
convex-hull cells using an invented plate depth. A projected-triangle BVH and boundary-edge BVH
are built once at load. First scope excludes support overhangs, multiple depths at one XY, holes
and an inward sheet that cannot be expressed in this one direction.

For posed host point `p` in current coverage coordinates:

```text
spatialWeight = 1 inside the actual support-sheet domain
spatialWeight = smoothstep(1 - distanceToSheet / collarWidth) in the outer collar
spatialWeight = 0 farther away
weight = authoredRegionStrength * spatialWeight
targetZ = h(p.x, p.y) - clearanceInCoverageUnits
requestedRecession = max(0, p.z - targetZ)
recession = weight * requestedRecession
pNew = (p.x, p.y, p.z - recession)
```

`smoothstep(t)` is `u*u*(3-2*u)` for `u` clamped to `[0,1]`. Distance is exact Euclidean distance
to the actual convex sheet domain, including its corners. At zero weight or zero requested
recession, copy the original posed vertex without arithmetic change. The finite caps must clear
all eligible vertices by the authored margin. Caps never slice or switch off an eligible patch.

There is no clamping that leaves a core uncleared. If the weighted displacement exceeds its world
budget, refuse the command. Core vertices and entire core triangles must stay within the actual
support domain with clearance. Core escape, cap contact and a support gap refuse the frame.
Do not enlarge the budget, change eligibility or move protected geometry to rescue it.

An exact support at vertices alone does not prove that a large deformed triangle clears a curved
sheet in its interior. Render refinement and full triangle checks below address that limitation.
No blanket no-clipping claim is made outside accepted regions and validated poses.

## 5. Authoring and provenance

**Proposed `HostCoverageMap`**, owned by Render3D, is immutable optional metadata beside a host
GLB. The importer owns glTF decoding and retains source provenance through material grouping.
Strict JSON format 1 contains:

| Field | Contract |
| --- | --- |
| `formatVersion` | Integer 1, other versions refuse. |
| `hostSha256` | Lowercase SHA-256 of the exact self-contained host GLB bytes. |
| `topology` | Source mesh/primitive vertex to welded source surface-vertex IDs. |
| `regions` | At most 32 unique ordinal keys, source triangle ranges, per-surface-vertex strengths and explicit core/collar roles. |
| `refinement` | Fixed subdivision level 1 to 4 for selected source triangles, plus conforming shared-edge splits. |
| `authoring` | Exporter identity and source revision label for diagnostics, not identity proof. |

An author selects core/collar faces and strengths in the editable model. A repeatable exporter
records stable source surface IDs and mesh/primitive/triangle ordinals. Regenerate and review the
map after any GLB change. Material and joint names do not establish compatibility.

Import refuses invalid ranges, overlaps, role/strength disagreement, nonfinite scalars, duplicate
keys, nonmanifold affected topology and nonzero boundary strength. A triangle core requires all
its strengths to be 1. Positive-strength surface points belong to only one region. Surface IDs
may join duplicate render vertices only when rest positions and normalized skin influences
match, allowing influence-slot permutation. Do not stitch coincident independently deforming
surfaces. Preserve identity across original material and normal seams.

The proposed optional overload is
`GltfLoader.LoadSkinnedPartsWithMaterials(glb, identity, hostCoverageMap)`. Existing overloads and
constructors still mean no coverage. Programmatic synthetic meshes supply the same explicit
topology and strengths. Upload snapshots metadata, so later caller mutation cannot alter a live
handle. Runtime anatomical heuristics are excluded.

**Proposed `EquipmentCoverageDefinition`**, in existing GPU-free Attachments, is a separate strict
JSON format 1 companion. It identifies exact attachment-definition byte hash, host-map semantic
hash and commands ordered by unique positive ID. Each command names a rigid part ID and host
region, a row-major coverage-local matrix, actual inward source mesh/primitive/triangle ranges,
collar width, caps, clearance and world recession limit. The part's existing mesh hash identifies
the actual displayed support geometry. The renderer adapter maps support selections through the
same rigid importer, including material grouping and canonical node transforms.

Canonical writing fixes member order, orders IDs and ordinal keys, normalizes negative zero and
rejects duplicate/unknown fields. Distinct hash-domain prefixes separate host maps and equipment
companions. Exact byte hashes verify fetched content. Semantic hashes name canonical coverage
identity. The existing asset source owns path and byte integrity policy. Replacing either mesh
requires revalidation. Shared region names never select unrelated hosts.

## 6. Render refinement and final normals

Refinement is an immutable render topology created at load. It changes no base asset or rig.
Each refined vertex records source triangle and barycentric coordinates. Skin original source
vertices first, then interpolate the already posed triangle. Subdividing rest positions and
interpolating bone weights before skinning is not equivalent and is prohibited.

Refine selected triangles and the neighboring edge splits needed for a conforming mesh. A source
surface edge has the same ordered rational split parameters on both sides, including material
and normal seams. Protected neighbors receive triangulation of their unchanged original posed
surface. Do not leave T-junctions or independently round shared positions. Materials, colours,
UVs and shading seams follow source barycentric provenance. The no-displacement surface matches
the original piecewise-linear surface, rather than inventing a smoother base body.

Compute final normals and tangents from the deformed surface. For each refined triangle, form its
before/after tangent-plane deformation gradient, transform the interpolated authored normal by
its inverse transpose and tangent by the forward gradient, and accumulate only within original
shading groups. Preserve tangent handedness. Degenerate gradients refuse. No displacement copies
existing skinned normals and tangents exactly. A new curved collar must not light using normals
from its unrecessed geometry. Normal maps remain optional and UV-based where UVs exist.

Refinement is finite and never grows adaptively during a frame. If level 4 and the permitted
recession still fail clearance, return to design review. Repeated subdivision until one screenshot
looks acceptable is not an acceptance argument.

## 7. Offline and every-pose geometric validation

Offline validation establishes hashes, topology, scalar continuity, refinement bounds and the
actual inward sheet. Analytic projected triangle overlays verify its single-valued complete
domain and clearance against the displayed mesh. A transparent or alpha-cut sealing material,
an open missing inward sheet or a proxy claiming coverage over a displayed hole is unsupported.
The outer boundary can float above the host because a conformed host remains present beneath it.
No removal-only seal certificate is used to reject the actual gap by construction.

A finite offline pose suite can find failures but cannot certify every blended pose. Runtime
checks apply to **every pose actually submitted**, after interpolation, blends, additive layers
and actor placement. Endpoint checks do not whitelist an intermediate pose.

Before any host or equipment draw is submitted:

1. Skin source vertices, construct the fixed refined surface and evaluate conformation. Validate
   transforms, core domain, caps and exact world displacement budgets.
2. Require the eligible patch to be an injective height field in coverage XY in this pose. Check
   nondegenerate projected triangles and nonadjacent projected interior overlap. A folded,
   vertical or overhanging patch refuses this directional mechanism.
3. Verify identical welded positions, zero-displacement region boundaries, manifold connectivity
   and nonzero face areas. Retaining connectivity alone is not proof against self-intersection.
4. Check final core and collar triangles against actual equipment triangles for intersection and
   minimum clearance. Analytic triangle overlays verify the required core stays behind the inner
   sheet throughout its surface, not just at vertices. Use immutable equipment BVHs.
5. Check changed triangles against nonadjacent retained host triangles and other changed regions,
   with a preallocated refitted host BVH. Do not push a front patch through the rear or a protected
   part. Shared adjacent edges are allowed. New nonadjacent intersections refuse.
6. In the positive feasibility fixture require positive recession over positive core area and
   exact protected witnesses. Empty regions, no-ops and refusal of every difficult pose fail.

CPU certification uses independent double-precision predicates over actual final float positions
destined for rendering. Scale-derived position, area and clearance tolerances must fit inside
the authored safety margin. Ambiguous cases refuse. Do not relax tolerances to accept a real
intersection. Runtime checks require CPU work even when ordinary actors use GPU skinning.

First scope permits 4 commands on disjoint positive-strength regions. They may share zero-weight
boundaries. Two commands claiming the same positive-strength point, or incompatible overlapping
deformations, refuse as `OverlappingConformers`. There is no order-dependent accumulation.
Multiple independent covered pieces and actors sharing assets are supported. A multi-constraint
surface solver is outside this design.

Stable refusal reasons include `UnknownRegion`, `IdentityMismatch`, `UnsupportedSupport`,
`UnsupportedTransform`, `CoreEscaped`, `CapContact`, `RecessionExceeded`, `FoldedPatch`,
`EquipmentIntersection`, `HostIntersection`, `OverlappingConformers`, `CapacityExceeded` and
`StaleFrame`. Name the host, equipment instance and command. Failure queues no partial surface.
The caller explicitly selects an uncovered presentation of the current actor pose after refusal.
That fallback does not count as a successful fix. This capability never freezes an actor pose or
changes simulation state. Never silently hide a body, skip malformed requested coverage, reuse
old deformation under a new pose or truncate commands.

## 8. Compatibility and minimal API shape

Keep attachment JSON v1/v2 unchanged, including rigid-only v1 canonical selection, mixed v2
semantics, bytes, mesh hashes and identities when coverage is absent. No v3 is needed for the
separate companion. An old reader can still load unchanged equipment. A consumer requesting
coverage explicitly loads its companion, so an unsupported reader cannot claim it enabled it.
Absent optional coverage means no effect. Requested but missing/malformed coverage is a failure.

Minimal **proposed** API additions are:

- Optional host metadata in Render3D import/upload, and empty-by-default
  `SkinnedInstanceDraw.Coverage` of proposed type `SkinnedCoverageFrame`.
- Actor-owned `HostCoverageBinding` in Attachments.Render3D, binding exact uploaded host handles
  and map, exact skeleton instance, explicit bound equipment instances and companions.
  `TryPrepare` consumes current palette/model and returns a validated final-surface frame token
  or structured refusal.
- A Scene3D skinned-outline descriptor overload consuming that same token. Existing outline
  overloads mean no coverage. Engine-owned adapters forward it. Custom adapters must support
  active coverage or refuse, rather than drop the field.

Reuse existing attachment binding and resource leases, not a parallel socket implementation.
Preserve source and binary compatibility for existing `IAttachmentScene` implementations. Add an
opt-in coverage capability or overload rather than required interface members. An uncovered
custom adapter continues unchanged. A coverage request against an adapter without that capability
returns an explicit unsupported result before submitting any actor part.
Existing bound equipment draws do not secretly change a host. Callers stage complete host and
equipment descriptors before submitting any actor part.

Tokens bind scene/frame generation, actor incarnation, exact host handles and generations, map,
skeleton, palette/model snapshot, equipment instance IDs and rigid frames. They cannot target
another mesh, actor, pose or frame. A multi-material host gets corresponding slices of the same
final surface. Underwear or another mesh is untouched unless explicitly mapped and bound.

## 9. Matrix and scale conventions

Use System.Numerics row vectors. Existing skinning composes
`skin[j] = inverseBind[j] * jointModel[j]`, as defined by
[SkinningMath](../../KhaozEngine.Render3D/Models/SkinningMath.cs#L18-L52).
GPU palette bytes are read column-major as their transpose without an extra CPU transpose, as
documented by [SkinnedBonePalette](../../KhaozEngine.Render3D/Rendering/SkinnedBonePalette.cs#L84-L97).

```text
equipmentWorld = partLocal * contractBodyAlignment * currentJointModel * actorModel
coverageWorld = coverageLocal * equipmentWorld
hostToCoverage = actorModel * inverse(coverageWorld)
coveragePoint = posedHostPoint * hostToCoverage
finalHostPoint = conformedCoveragePoint * coverageWorld * inverse(actorModel)
```

Use the exact composed equipment frame used by its draw. Store coverageLocal as 16 finite
row-major numbers. Host and equipment use one palette/model snapshot. Render-origin translation
cancels in relative coordinates. Jitter and shadow cameras never affect conformation.

Positive nonuniform scale and affine shear use the full inverse. World recession is local
recession times the length of coverageWorld's transformed unit-Z vector. Clearance uses
conservative singular-value bounds, not average scale. Refuse projective, singular, nonfinite
or ill-conditioned transforms. First scope also refuses negative determinant at actor, effective
rigid part or coverage frame, including animated mirrors. Mirrored authored geometry needs its
own correctly wound asset and positive frame. Uncovered attachment behavior stays compatible.

## 10. One final surface across all passes

The first capability deliberately prepares covered hosts on the CPU and draws **one canonical
ModelVertex stream** everywhere, even when global `UseGpuSkinning` is enabled for other actors.
Expose a covered-surface CPU-preparation counter. This is not native GPU conformation. An
independent GPU evaluator is deferred until a separate correctness and performance design.

Reuse the existing CPU-skinned geometry routes with the final slice, without another deformation.
Outlines borrow this same slice instead of skinning the original mesh again. Uncovered hosts
continue on their selected CPU/GPU route. Both settings must give identical covered surfaces.

| Path | Required behavior |
| --- | --- |
| Colour, cel/PBR, normals, depth and motion | Final positions, normals and tangents only, without the original skin underneath. |
| Directional cascades | Same final geometry for every cascade, including camera-culled and shadow-only casters and dissolve variants. |
| Point-shadow six faces | Same final geometry in static-light transient and dynamic base atlases. No cached original silhouette. |
| Full and visible outline masks | Same geometry before existing alpha/dissolve rules. Through-geometry outlines cannot reveal original protrusions. |
| Snapshot, capture and other depth routes | Consume the token and final slice or refuse explicitly. |
| Global CPU/GPU skinning setting | Covered hosts use canonical preparation in both. Uncovered controls still exercise both native skinning routes. |

No clip-plane UBO, fragment region varying, alpha texture or stencil pass is needed. Region
encoding is immutable region index plus float strength per refined surface vertex, backed by
source barycentrics. Zero means protected. Support-triangle and query-stack capacities are CPU
bounded. Reuse the current CPU-skinned vertex layout and its existing previous-position motion
stream, so deformation needs no additional shader attributes or uniforms. Existing skinned UV
shadow-cutout limitations are not declared solved. The mechanism requires no UVs or textures.

### Memory, lifecycle and allocation

Caches share immutable maps, refinement topology, support/rigid geometry, BVHs and digests.
Actor-owned `HostCoverageBinding` stores source/posed/final vertices, normal/tangent scratch,
host-BVH refit storage, query stacks, command records and previous final positions. It borrows
cache resources on the owning render thread. Calls cannot overlap.

Scene3D copies successful final slices and material ranges into its frame arena before returning
the token. Caller scratch and borrowed attachment descriptors can then be reused for another
actor without rewriting earlier draws. Passes reference immutable frame offsets. Upload the
frame image once before recording, never repeatedly rewrite one UBO or vertex window between
recorded draws. Capacity growth and resource retirement occur before recording.

Warm valid preparation, validation and submission allocate zero managed bytes. Creation, load,
equip and explicit growth may allocate. Arrays, edge tables and query stacks have explicit
capacities. No per-frame polygon lists, LINQ, unbounded dictionaries, retained caller spans or
readback. Allocation proofs include changing poses and multiple actors, not one repeated no-op.

Equip changes are transactional by stable instance ID. New resources/companions bind completely
before replacing the active list. Unequip restores the host on the next prepared frame. Reset,
incarnation change and host replacement clear current and previous state. Failed loads release
all provisional maps, topology buffers, BVHs and leases. Binding, cache or scene disposal
invalidates tokens and follows existing deferred GPU retirement. Partial preparation is unusable.

### Dissolve and temporal history

Conformation precedes material alpha and world-space dissolve. Colour, shadows and outlines
keep their ordinary, complementary and inverted-shadow semantics over the same final geometry.
No coverage cut or glow is introduced. For independent equipment fade/hide, callers disable its
conformation or explicitly supply a transition weight in `[0,1]`. Below 1 cannot claim full core
clearance. Whole-actor dissolve can keep full conformation while fading the composite.

Motion uses previous **final** positions, not previous bones alone. Fixed refinement topology
preserves vertex correspondence. The existing
[CPU-skinned motion upload](../../KhaozEngine.Render3D/Scene3D.MotionTarget.cs#L142-L182)
already uploads a parallel previous-position stream, but reconstructs those positions from the
original vertices and previous palette. Covered draws must instead append their retained previous
final vertices in the expected render space, with the same refined count and frame-origin rebase.
Do not send a refined covered draw through that original-mesh reconstruction branch. On invalid
history, append the current final vertices using the existing current-position fallback.
Keep motion keys stable during normal poses and combine map,
equipment and refinement revision into identity on changes. Unequip, reset, refusal and topology
replacement seed previous positions from the new current surface and reject temporal history
over current/previous projected changed bounds. A conservative transition-only R8 rejection mask
can carry those rectangles into resolve. A near-plane crossing rejects the viewport for that
transition. Steady motion uses final-position history. Do not reset global history every frame.
Prove no ghost skin, stale shadow or outline remains after each transition.

## 11. Bounded cost and capacity

Initial limits are 32 regions, 4 disjoint active commands, 1,024 inward support triangles and
256 boundary edges per command, refinement level at most 4, and 65,536 final vertices and triangles
per host. Use existing 32-bit indices where required. Exceeding a limit refuses with counts.
Raising limits requires reviewed geometry, memory and performance evidence.

There is no per-region draw split. Material grouping remains, so two materials still need two
draws in each pass. Costs are CPU skinning, refinement, per-refined-point height/BVH queries,
surface clearance/self-intersection validation, dynamic upload and previous-position storage.
Level 4 multiplies selected triangles by 16 plus conforming boundary splits. It does not multiply
the whole body unless explicitly selected, which accidental whole-body tests must catch.

A typical synthetic budget fixture has 2,000 original triangles, 200 eligible triangles, level 2
and two disjoint commands/materials. It adds about 600 eligible triangles plus boundary splits.
The limit fixture uses all four commands and level 4 within the cap. Report actual generated
vertices. At 8,000 final vertices and a 64-byte ModelVertex, current geometry is 500 KiB per actor
and previous positions add about 94 KiB. For 32 actors current geometry alone is about 16 MiB per
frame, roughly 1 GiB/s at 60 frames/s before in-flight copies. At the vertex cap one actor's
current geometry is 4 MiB. These estimates expose a material upload risk, not a measured promise.

Measure isolated off/on workloads at 1, 32 and 128 actors, with colour-only, four cascades, dynamic
and static-transient point lights, outlines and temporal accumulation. Report skin, preparation
and validation CPU time separately, allocations, uploaded bytes, peak arenas, draw calls and
completed-frame GPU time. Exercise both global skinning settings and disclose the canonical
covered-host CPU path. WARP/lavapipe correctness is not native GPU performance evidence.

Load and throughput measurements require separate explicit owner permission naming workload,
runner, iterations and duration before execution. Written-spec approval does not authorize them.
Run approved load work through the hosted stress workflow, never on the development Mac or the
self-hosted fleet. If an eligible hosted native GPU runner is unavailable, report that performance
gate as unproved and agree a permitted route before running it. Ordinary bounded correctness
checks remain separate from load testing.

Proposed owner-review budgets are zero warm managed allocations, at most 2 ms added CPU
preparation/validation for 32 typical actors on a documented desktop CPU, and at most 15 percent
completed-frame GPU overhead for that typical workload on each native reference GPU. The limit
fixture establishes bounded behavior and reports cost without pretending to meet typical
budgets. A miss returns this design for revision. Do not remove validation, over-recess skin or
silently add a native GPU implementation to meet a timing gate.

## 12. Synthetic GPU-independent geometry oracle

Use public synthetic fixture constructors only. The oracle accepts explicit topology, regions,
strengths, barycentric refinement, inverse binds, weights, posed palettes, actual rigid/support
triangles and commands. It emits final float vertices plus independent double-precision geometry
assertions. It has no window, GPU, texture or importer dependency. Do not validate a helper by
calling it through a second name.

Fixtures include a closed segmented ellipsoid, a curved thick rigid panel with a static gap,
a lower-edge bulge, a crossing independent segment, a second actor sharing assets and a disjoint
second covered region. Joint/region tokens are generic. All host anatomy shares one material
and lacks UVs. Repeat with shading/material seams and 32-bit indices.

| Falsification case | Required result |
| --- | --- |
| Bulge through actual curved equipment | Positive baseline breakthrough, positive core recession, full triangle clearance and exact protected positions in the accepted result. |
| Floating panel with unchecked removal-only control | Control visibly opens a gap. Accepted conformation retains triangles over that area. |
| Global minimum-plane target compared with exact inner sheet | Expose unnecessary plane recession. The selected mechanism evaluates actual triangles, not the global plane. |
| Budget smaller than required displacement | `RecessionExceeded`, never a saturated uncleared core. |
| Core outside support or touching a cap | Refuse without infinite extrusion, slicing or eligibility expansion. |
| Large triangle with clear vertices but intersecting interior | Full surface check fails until a fixed permitted refinement genuinely clears it. |
| Nonzero boundary strength or disagreeing seam values | Refuse. No lighting-hidden crack. |
| Protected segment crossing the part | Positions remain exact and the crossing remains a separate fit defect. |
| Recession reaches the rear host surface or folds the patch | Refuse `HostIntersection` or `FoldedPatch`, regardless of front-camera appearance. |
| Safe endpoint poses and an invalid intermediate blend | The submitted intermediate pose refuses. Endpoints are not a whitelist. |
| Disjoint equipment and shared assets on two actors | Independent surfaces, unchanged by reverse submission order. Overlapping positive regions refuse. |
| Nonuniform scale/shear, then mirror/singular/nonfinite transforms | Valid positive cases match direct world geometry. Unsupported cases refuse before partial drawing. |
| Missing, stale, unknown, malformed, empty or over-capacity data | Structured refusal, no leak and no whole-host hiding or targeting. |
| Unequip, reset, replacement and partial-load failure | Exact restoration and final-position history, with another actor unchanged. |

Check manifold edge incidence, welded positions, face areas, patch injectivity, exact clearance
and new nonadjacent intersections. Mutate region identity, support frame, budget and a seam weight
so the positive fixture fails. Require positive core work and protected witnesses so no-op,
refuse-everything and whole-body movement implementations cannot pass. A synthetic success
cannot establish the consumer's full supported pose domain.

## 13. Inspected native-backend evidence

Run the same synthetic fixtures on **MetalNative, Direct3D11Native and VulkanNative**. The middle
name is the actual `GpuBackendKind` value in [the desktop matrix](../CROSS-PLATFORM.md#platform--backend-desktop-scope).
No engine GPU proof has been run for this draft.

Inspect labelled normal opaque off/on full-resolution PNGs at identical poses, cameras, materials
and lights. Include front, rear, both grazing sides, high/low views, contrasting background and
backlighting. The removal control must visibly fail at a grazing view, while conformation
retains a closed visible surface. Transparent overlays and coarse golden grids cannot replace
opaque inspection. Report collar contour and shading changes even if geometry gates pass.

Read back colour, depth, normals, motion, full/visible outline masks, every cascade and every
point face. Assert baseline breakthrough, its removal in the accepted conformed result, protected
retention and no new background holes. Compare geometry with the oracle in both global skinning
settings. Include asymmetric frames, camera-culled and shadow-only casters and two different poses
sharing a mesh to expose slot aliasing. Follow [shader and pixel-readback rules](../CROSS-PLATFORM.md#authoring-shaders-that-pass-on-all-three-backends).

Capture sequences for steady/blended motion, equip, unequip, replacement, refusal, reset and
partial load, with temporal accumulation on/off and ordinary/complementary/inverted-shadow
dissolve. Independent equipment fading explicitly changes conformation state. No original-skin
history, shadow or outline may remain. Record actual backend/device, shader-cache state and
failure artifacts under the [golden process](../CROSS-PLATFORM.md#backend-aware-goldens). Do not
rebake a golden or relax a threshold to forgive a hole, fold or excessive collar change.

## 14. Owner decisions and adoption

The owner must approve a bounded visible collar change while the rig, base assets and rigid
exterior remain exact. Review the actual-inner-sheet A/B packet, not the earlier over-conservative
plane approximation. The corrected one-pose result is promising, but the private diagnostic
eligibility/refinement is not authored production metadata or a passing whole-pose proof.

Other explicit decisions are whether this directional height-field scope is useful, whether the
4 disjoint-command/refinement limits cover the first equipment set, whether canonical CPU
preparation is acceptable initially, and whether performance budgets and unsupported mirrors,
support holes and overhangs are acceptable. These are review decisions, not placeholders granting
implementation freedom. If appearance or geometry cannot meet them, return to authored fit/pose
work. Do not keep probing parameters indefinitely.

After written-spec approval, write a separate implementation plan for review. Implementation then
proves compatibility, geometry, native backends, performance and ordinary engine checks. An
owner-authorized engine release is separate work. Consumer adoption uses that actual released
package and its authored maps/companions. Do not invent a future version or use a branch package
as the released adoption pin.

Consumer acceptance reviews the collar, proves its representative torso failure and supported
motion domain without erasing arms or opening holes, and verifies its production render paths.
Arm crossings and equipment against equipment remain separate art/pose clearance tasks. Neither
a refusal-only API nor one successful synthetic panel completes that consumer outcome.
