# R2 sculpted caves and sparse-world scale

Status: **DRAFT FOR OWNER REVIEW. Candidate refinement only. R2 implementation remains unapproved.**

## Outcome and review boundary

Author a continuous outdoor slope into a spacious underground passage in the same native
MapDoc/MapEditor/ke-mapedit workflow. Floors, ceilings, entrance rims and walls describe the same
geometry to rendering, support and later collision/navigation. Preserve the shipped world's exact
imported terrain and free prop/building placement. This document proposes the contracts needed to
reconcile R2's six tasks. It does not replace their executable plan or authorize implementation.

OA13 already selects continuous sculpting, seamless entrances, cavernous spaces, rough depths and
peaks of 500 m, and giant MMO horizontal scope. Full option A persists. Native prefabs remain useful
for reusable pieces and buildings. C5 includes native local floors, paint and volumes, so prefab
caves do not require all their shape work in Blender. Primary cave sculpting should not depend on
R5 module assembly. No TileWorld extension or hybrid, swimming, nested prefabs, generated stairs or
foundations, procedural world generation, or new art production is proposed.

R2-D1's **64 km by 64 km, approximately +/-32,000 m horizontally**, is a provisional engineering
verification envelope. It is neither an owner-specified exact size, a measured WoW area, a hard cap,
nor a promise to author that area. Proposed datum is the shipped world-coordinate Y=0, with no
translation of existing heights or water levels. The rough +/-500 m terrain targets are relative to
that datum. Object bounds, camera offsets and capture probes extend independently beyond them.
Review the envelope, datum and contracts together.

API evidence is released **v20.27.0**, commit `a87038f5a0441d76f1ca71c8ba8acf6ec7b1b6af`.
The source checkout `wa-r1-document-identity` has that HEAD. Origin advertises annotated tag object
`2da36f154a6fca514b9a4595da52afe42019fd18` with that peeled commit. Local package identity was
controller-verified. Publication attempt 2 acquired a runner and built successfully, then failed
the existing D3D11 threading resize/present test. Publication is blocked by
[#1309](https://github.com/APKiwiOrg/KhaozEngine/issues/1309), with a test-only repair active in a
separate worktree. No MapDoc runtime/API regression is claimed. The planning checkout is historical
and is not the source of the APIs below.

The controlling intent/corrections are Grimhollow's
[DECISIONS OA9/OA13](https://github.com/APKiwiOrg/Grimhollow/blob/ae2c0db0/docs/superpowers/programs/world-authoring/DECISIONS.md#oa13-seamless-sculpted-caves-and-mmo-scale-world-intent),
[R2 owner gate](https://github.com/APKiwiOrg/Grimhollow/blob/ae2c0db0/docs/superpowers/programs/world-authoring/R2-OWNER-GATE.md)
and [PROGRAM](https://github.com/APKiwiOrg/Grimhollow/blob/ae2c0db0/docs/superpowers/programs/world-authoring/PROGRAM.md).
The scratch research memo is fallible background. Its constant-height layer prisms, blanket
non-overlap rule, prefab/Blender claim, tolerance conclusion and calendar ranges are not adopted.
The approved base remains [C2/C3/C4/C5/C8 and DG9.1 to DG9.6](WORLD-AUTHORING-MIGRATION-DESIGN-2026-10-05.md).
All interfaces named as proposals below are **new**, not shipped public APIs.

## Geometry ownership and continuous entrances

Use a logical surface ID with bounded lattice patches. A patch has an explicit row direction,
cell size, height unit, integer corner values, material/paint bytes and physical presence mask.
Roles distinguish support floor, downward-facing ceiling, non-support wall and paint override.
Authoring layers group/select these records. A layer ID is not an occupied-space volume, water
container, navigation layer or server cell.

In this candidate, the native presence mask removes complete cells. Smaller cell sizes and explicit
boundary subdivisions refine a curved mouth. Overlay half/quarter cuts remain paint topology, not
an aperture tool. Non-cell-aligned arbitrary holes require explicit placed geometry rather than a
hidden reinterpretation of paint. This first representation's limitation is part of the review.

A height field describes one Y for each local XZ. Paired fields describe a variable floor and roof
over a footprint, including a broad chamber or descending tunnel. They cannot by themselves express
a closed cave, vertical wall, self-overhang, arch or two floors at the same XZ. Multiple patches and
spaces permit stacks. Explicit boundary strips provide vertical walls. More complex rock/arch
geometry can use the existing placement/asset path. This is not a general volumetric modeller.

The proposed minimal boundary records have stable IDs and reference canonical boundary vertices,
rather than storing another independently rounded copy of their coordinates:

| New record | Geometry contract |
| --- | --- |
| `MapSurfaceSeam` | Two patch edges identify the same ordered vertex keys. Compatible units, positions, heights and edge subdivisions agree exactly. The seam changes ownership, not shape |
| `MapCaveBoundary` | A closed footprint edge joins the referenced floor and ceiling chains. Each corresponding segment becomes a ruled quad, split lower-start to upper-end. Winding points into the occupied air space. Additional authored chain vertices shape the boundary |
| `MapCavePortal` | An explicitly open boundary interval has no wall faces. It records its floor chain, optional ceiling/lintel chain, side boundaries and the two connected space IDs, one of which may be declared exterior |
| `MapVerticalLink` | Names the connected spaces, portal/support endpoints and intervening geometry owners. A ramp uses continuous floor patches. A shaft uses an explicit floor aperture and side boundaries. The record does not fabricate traversal or a nav edge |

Floor and ceiling boundaries use corresponding XZ chains in this first representation. Their heights
vary independently. Vertical wall faces and nonrectangular footprints need those explicit strips,
not infinitely steep height samples. Free assets can supply shapes outside this representation.
Boundary subdivisions must occur on the shared lattice or declared half/quarter lattice vertices.
Joins with incompatible tessellation refuse until the author explicitly subdivides both edges.

A surface-to-cave entrance is one transaction over the outside floor, descending floor, roof-start
edge and side boundaries. Where outdoor ground covers the proposed entrance, remove its physical
faces explicitly. Bind the resulting rim to the descending floor/side chains. Start the downward
ceiling behind the open mouth, with explicit rim/return faces where necessary to join the ground
above. Keep positive roof thickness where top ground and underside coexist. An opening has no
invisible floor or closing wall. Terrain outside the edited footprint remains unchanged.

A portal's optional top applies only to an explicitly open-top exterior side. A cave-side roof chain
must be present, even though the portal has no wall face. The exposed slope before the roof starts
belongs to the exterior space. It does not become a cave column with missing ceiling geometry.

For a simple sloping entrance, the outdoor and descending floor can be the same logical surface.
They can also meet at a declared seam. In either case their boundary positions agree by vertex key.
The space label changes at the authored portal, not at an arbitrary depth or storage boundary.
Spaciousness comes from the authored floor/ceiling separation and footprint. No historical 4.5 m
spacing or four-layer limit applies to new caves.

Each face has one geometry owner and stable `(owner, patch, primitiveKey, triangle)` identity.
Floor/ceiling primitive keys address cells. Wall keys address boundary segments. Storage or
query memberships may reference that owner several times. They do not create additional faces.
Shared edge/corner vertices have one authoritative key. Interior lattice ownership uses the
half-open cell partition. At a patch junction, choose the lowest ordinal incident patch key as
the persisted vertex owner and include that dependency in every touching patch. Missing owners
make the seam unresolved, never edge-extended implicitly. Changing a shared corner updates every
dependent patch atomically or refuses the edit.

Compile positions, topology, face normals, support roles and material subdivisions once. Imported
topology follows the released diagonal/cut arithmetic [E10/E11]. Ceiling winding reverses floor
winding. Wall normals come from their explicit strip faces. Use the same descriptors for floor
sampling, draw input and R3 capture. Feathering subdivides those faces without changing the physical
surface. Distinct faces may meet with different normals at a real crease. Smooth shading, if used,
must not replace the geometric normal used for support or collision.

This is **canonical same-geometry equality**, not a numerical tolerance between independently
rebuilt meshes. Transforms are applied once per published frame-local descriptor. Backends may have
numerical raycast error, but cannot substitute a bilinear floor or a different diagonal. R3 owns
physical face-sidedness, headroom and movement clearance. A canonical opening does not yet prove a
walkable route.

## Occupied spaces and gameplay domains

Keep three identities separate: geometry owner, occupied space and gameplay domain. A floor may
bound an outdoor space and a cave. An interior tag may apply inside a cave. Water covers only its
explicit contained subset. Layer grouping, XZ coincidence and a global ocean height decide none of
these memberships.

Proposed `MapSpaceDoc` references a footprint, lower floor patches, upper ceiling patches and its
wall/portal boundary records. A cave column contains a point when its XZ belongs to that footprint
and `floorY(X,Z) <= point.Y < ceilingY(X,Z)`. Both heights come from the canonical referenced
triangles. The vertical interval varies with geometry. Validate positive separation over the common
floor/ceiling triangulation refinement, including its vertices, not just the original cell corners.
Explicit wall boundary coverage and declared portals complete the enclosure.

An exterior space explicitly declares an open top and its support/footprint. The absence of a cave
match alone does not certify exterior membership. A cave boundary cannot omit its ceiling and
silently become exterior. At a portal, persist a normal from FromSpace to ToSpace. ToSpace owns
exact-plane points, while negative signed distance is on the FromSpace side. The containing-space
rule applies beyond that boundary. At a shared portal vertex, the lowest ordinal incident boundary
key supplies the tie rule on both heads. No epsilon-sized overlapping doorway volume is introduced.

Proposed `MapSpaceMembership.Query(FramePoint)` returns `Resolved`, `Outside`, `MissingGeometry`,
`Ambiguous` or `CapacityExceeded`, plus the selected space and containing semantic-domain keys.
An explicit point below a floor is outside that space. Stacked spaces with overlapping XZ are valid
when their actual variable intervals distinguish the point. Missing/unloaded floor, ceiling or seam
dependencies return `MissingGeometry`. They do not fall back to the surface world.

Overlap validation is about competing interpretations, not all overlapping volumes. Allow nested
interior domains with an explicit parent space and independent gameplay tags. Multiple tags may
apply to one point. Two peer spaces describing the same occupied air must declare one shared-space
alias or an explicit partition/portal. Otherwise membership is ambiguous and publication refuses.
Coincident support faces similarly require one geometry owner or an explicit paint role. Distinct
floors separated in Y are valid. Do not impose a blanket 3D or XZ non-overlap rule on every domain.

Imported Indoor masks retain their bytes and connected footprint. Their agreed explicit vertical
span remains an import-domain recipe, compiled through the shared membership service. It does not
turn all cave layers into fixed prisms. R5's authored rigid polygon-prism interiors remain valid
semantic domains inside a parent space. That contract does not require every cave enclosure to be
a prism. Roof/light consumers receive all relevant typed membership keys, not a guessed single tag.

## Physical holes, paint and imported fallback

| Condition | Required result |
| --- | --- |
| Native physical aperture | Presence mask removes the physical triangles. Support is absent, draw/capture omit them, and wall/portal references explain any occupied-space opening |
| Overlay cut or feather | Changes material coverage/tessellation on the canonical surface. It never creates an air hole or removes collision/support |
| Imported void or NoDraw | Preserve integers/bytes and absence of rendered/captured terrain. Preserve the existing explicitly bounded non-capture fallback recipe. Do not reinterpret it as a cave entrance |
| Indexed patch not loaded, missing digest/asset or unresolved seam | Return missing/incomplete data and refuse a complete result. Never synthesize empty terrain or use the legacy fallback to hide it |

Released TileWorld draw geometry requires a nonzero underlay and no NoDraw flag [E10]. Its bilinear
height API also exists [E12]. The accepted C2 contract confines such fallback to the legacy bounded
void/outside cases and labels it non-capture support. Keep that compatibility path separate from
new physical apertures. An imported legacy fallback cannot close a newly authored entrance. An
author must explicitly change the physical policy to make that edit, with the affected identity
and geometry diff visible. Import does not infer cave spaces or convert unused upper planes into
walkable floors. Reserved Bridge metadata is preserved and never becomes support geometry.

## Bounded payloads, storage and query identity

R2 must replace the old plan's root-wide corner/cell arrays with bounded patch payloads. Proposed
world-patch maximum is **64 by 64 cells**, independent of document tile, nav tile and server cell
sizes. Smaller patches and multiple patches form any logical surface. An int32 height lattice has
at most 4,225 values per patch. Eight-byte cell records need at most 32,768 bytes, height values
16,900 bytes, and a one-bit physical presence mask 512 bytes. These are representation calculations,
not benchmarks. Set a **1 MiB decoded patch limit**, including boundary/reference metadata, and
reject dimensions/count arithmetic before allocating. Variable compiler output has a separate
proposed **65,536-face limit per patch**. Excessive feather/boundary subdivision refuses explicitly.

Use new `MapSurfaceRef` metadata for stable logical surface ID, units, role and digest-bearing patch
index reference. Sparse index entries name `(surfaceId, patchKey)`, bounds, payload digest, storage
owner, corner/seam dependencies and domain links. Index payload pages contain at most **256 entries**
and **1 MiB decoded data**. A directory references occupied pages only. An unlisted key is empty
only under a verified index page/range. A listed unloaded key is unavailable. No 64 km rectangle is
enumerated to create empty patches. Material/support/geometry identity ignores physical file grouping.

Keep byte-integrity digests separate from semantic geometry digests. Index-page/payload SHA-256
checks validate the bytes read. Authored geometry identity flattens stable patch keys and canonical
values/dependencies, excluding filenames, page grouping and storage-owner bookkeeping. A changed
triangle, unit, portal or support policy changes semantic identity. Merely repacking identical
records does not. Declared document parameters that affect behavior still enter build identity.

R2 pins patch/page encoding, digest normalization and a bounded provider contract. Proposed
`IMapSurfaceSource.ReadPatch(key)` and `FindPatches(scope)` distinguish `Present`, `KnownEmpty`,
`Unloaded`, `Missing` and `Corrupt`. Scope declares XZ, Y, required roles/spaces and limits. Candidate
queries must not enumerate the whole world. Initial proposed query limits are **256 candidate patches,
4,096 inspected faces and 64 support intersections**. Overflow returns `CapacityExceeded`, never a
truncated winning floor. These are reviewable operational budgets, not world or layer-count caps.

Support lookup addresses the intersected cells/triangles through the patch index. It does not scan
every face in each 64-cell patch to answer a point query. Bound index-page reads and dependency
expansion in the provider, with the same explicit overflow result.

The existing MapTile grid is XZ and half-open [E7]. Keep its distinct coordinate type and negative
flooring rule. Surface addresses, storage tiles, nav tiles, cells and space IDs must have explicit
maps. A deep ceiling/floor can share a storage tile without sharing support or gameplay membership.
R2 implements bounded payload reading/compilation and deterministic provider tests. It does not
implement runtime eviction, a production streaming scheduler or HLOD.

R1 hashes a complete in-memory serialized document and verified complete closure [E2/E4]. It refuses
partial documents [E4]. Those APIs remain legitimate complete-view operations for small fixtures.
They are not bounded large-world validation. R2's proposed authored identity scheme 2 hashes sorted
semantic surface/patch/domain records and dependency digests, builder/resolver versions and policies.
The complete-view reference calculation must match monolithic and tiled persistence. It must not
trust a stale persisted tile hash as proof that bytes were checked.

Pin a new scope identity envelope now: complete document root digest, exact required patch/asset
digests, coordinate frame, query/build policy versions and a coverage witness for the declared
scope. A scoped result has its own type and cannot masquerade as `MapResolvedDocument`. R8 owns
runtime directory-page residency, bounded closure acquisition, scope validation/publication,
generation fencing and eviction. Complete offline verification can stream all declared resources
and accumulate digests without simultaneous residency. A loaded window alone certifies only its
checked scope. Large-world operation is gated on those R8 proofs, not declared finished by R2.

The game nav budget remains **8 MiB aggregate after deterministic gzip -9, in plain git**. Tiling
does not waive it. R3/R11 supply geometry/profile/tile identity and size accounting. G3 checks the
actual shipped-world output. Bigger distribution/storage is a separate owner choice before exceeding
the budget. Sparse far-coordinate capability fixtures do not prove a fully authored 64 km map fits.

## Coordinate ownership and precision proposal

Reuse existing frames and local geometry. `Scene3D.RenderOrigin` reduces GPU-bound translations,
while CPU submissions/culling remain absolute [E5]. `IPhysicsWorld.Origin/Rebase` makes a physics
world's queries and poses local to its origin [E6]. Terrain collision already stores local mesh
vertices and registers a pose reduced by that origin [E6]. `CellOrigin` changes grid keying,
ghosting, handoff and per-cell frames, not stored authoring coordinates [E8]. These are real seams,
but none recovers precision already lost by constructing an absolute float vertex first.

`WorldFrame` has **128 m XZ anchors**, a **512 m maximum local planar radius**, and an anchor Y of
zero [E9]. It does not currently rebase vertical simulation coordinates. SamplerSpace distinguishes
absolute and frame-local sampler delegates [E9]. Existing ground probes are XZ-only and may return
fallback after a miss [E13]. MapDoc placements/transforms and resolved XZ bounds use floats [E3].
Finiteness validation checks validity, not precision [E4].

The significant coordinate choice has the following weighted scores. Scores are engineering
judgments from 1 to 10, not measurements. Weights favor preserving canonical local terrain while
keeping the released consumer seams.

| Criterion | Weight | A. Absolute float data plus existing frames | B. Integer-addressed terrain patches plus existing float placements/frames | C. Whole-document/runtime double-coordinate rewrite |
| --- | ---: | ---: | ---: | ---: |
| Useful near/far geometry precision | 3 | 6 | 9 | 10 |
| Released consumer compatibility | 3 | 9 | 8 | 3 |
| Canonical geometry and import control | 2 | 6 | 9 | 7 |
| Focused delivery and verifiable scope | 2 | 8 | 7 | 3 |
| Weighted total, maximum 100 | | 73 | **83** | 59 |

A has the smallest migration but loses local offsets when absolute vertices/samples are materialized.
B adds a terrain-address/compiler contract while retaining current placement and frame interfaces.
It controls lattice/seam arithmetic and allows frame-local queries without a large-magnitude round
trip. It still has float placement precision and requires R3/R8 adapters. C improves stored global
precision, but expands protocol, transform, backend and consumer migration scope. Doubles alone do
not solve bounded residency or ensure equal triangles. **Recommend B**, subject to owner review and
the proofs below. Do not start a sweeping double rewrite.

Proposed new `MapLatticeFrame` stores signed integer X/Z lattice addresses, explicit positive
cell-unit numerator/denominator, row direction and datum binding. Patch corner Y uses int32 height
units. Unit declarations are rational metre scales, with centimetres as the import/default sculpt
unit and explicit finer units allowed. Serialize int64 addresses losslessly as decimal strings.
Keep float `MapTransform` for rigid local floors and free assets. Changing units is an explicit
conversion transaction with a value diff, never a serializer side effect.

For imported corner geometry, convert the preserved effective integer using the source-compatible
unit factor and operation order, including `heightCm * 0.01f` and legacy mid-edge construction
[E10/E12]. Materialize upper-plane derivations as effective integer values with ledger provenance.
Do not reinterpret an old short as a native depth cap. Native int32 centimetres can represent the
new depth targets. The importer must prove its arithmetic against the selected source oracle.

Proposed `MapFramePoint(WorldFrame Frame, Vector3 Local)` keeps local XZ and world-datum Y. Sampling
subtracts patch/frame integer anchors before converting small offsets to floats. Do not evaluate
`float(frameAnchor + local)` and then subtract it again. A new `CompileInFrame`/transform adapter
applies rigid yaw/scale in local space, then adds the **already reduced** placement translation.
Keep R1 `TransformPoint/Compose` unchanged for old callers. R5 uses the new local composition path
when resolving prefab floors in a frame. Existing explicit imported world transforms are preserved.
R8 submits patch-local meshes with an absolute patch translation through RenderOrigin. It must not
rebake their vertices into absolute floats and claim that subtracting RenderOrigin repairs them.

| Quantity | Proposed contract and future proof |
| --- | --- |
| Stored authoring precision | Integer terrain values/addresses and units roundtrip exactly. Existing float placement values roundtrip unchanged. No implicit centimetre snap is applied to free transforms |
| New far-world placement use | At the proposed +/-32,000 m envelope, float XZ spacing is 0.001953125 m. New authoring must preserve a useful 0.01 m position edit. This is an interaction/representation target, not an import tolerance or a claim of submillimetre global positions |
| Runtime local terrain query | Proposed height/position error target at most 0.0001 m against the canonical descriptor in the **same frame**, tested near/far and on slopes. Render/support/capture triangle identity remains exact. Imported differential/backend gates remain tighter wherever already required |
| Frame/cell budget | Keep the released planar-radius sizing rule, including overlap and anchor-grid allowance [E9]. R3/R8 reject an oversized simulation/capture working set before claiming frame precision |
| Vertical proof | Terrain samples at datum +/-500 m. Fixture objects may extend another 64 m vertically. Probes reach +/-640 m. Y remains absolute under current WorldFrame. Float spacing through +/-640 m is at most 0.00006103515625 m, a representation fact that still needs arithmetic/backend proof |
| Horizontal proof padding | Include authored geometry/camera/probe bounds up to 128 m beyond the provisional terrain envelope in the sparse fixture. Runtime residency expands from actual effective bounds. An asset exceeding fixture padding requires a wider named proof, not clamping |
| Import differential | Exact integers/bytes/IDs, at most **0.00001 m** derived position/vertex error and **0.000001** normal component error remain binding. Compare actual old/native computations on identical frozen inputs and declare comparison space |
| Canonical agreement | Same vertex keys, triangle IDs, winding and geometric normals after the same transform. It is not certified by passing an approximate positional comparison |

The vertical/horizontal padding numbers are proposed acceptance-fixture bounds, not limits on every
world object. Preserve existing bounds even if they exceed them. Capture probe extents derive from
the required geometry and adapter space, not terrain extrema alone. Current nav options declare
absolute probe heights and convert against physics origin [E14]. Keep that distinction explicit.

Float spacing alone does **not** prove failure of the 0.00001 m import fixture. Grid-aligned XZ,
equal operation sequences and local source meshes can compare exactly. Conversely, successful
centimetre roundtrips do not prove arbitrary transformed/query precision. Any demonstrated import
exceedance is a reported blocker with the offending inputs/operations. Changing the approved
tolerance requires evidence and an owner ruling, not an automatic ULP-based relaxation.

## Height-aware support, migration and terrain transactions

R1 `Resolve` accepts `Func<float,float,float>` and calls it only when placement Y is absent [E1].
It cannot choose a stacked support from actor height. Authored identity and bound validation accept
resolver version 1 only [E2/E4]. Preserve that overload, its missing-Y behavior and analytic callers.
Do not send caves through an XZ adapter that guesses the highest floor.

Proposed new `MapSupportRequest` carries frame point, optional explicit surface/space key, current
support key, bounded step-up/drop-down, slope policy and query limits. `MapSupportResult` returns
status, geometry owner/triangle, height/normal, capture-support flag and space context. Query order:

1. Acquire the required geometry scope and verify dependencies. Missing or ambiguous data refuses.
2. Restrict to upward support faces and the selected/occupied space. Ceilings, walls and paint are
   not support. Include another space only through an explicit geometric portal/link transition.
3. Filter to the authored/requested height interval and slope policy. A floor above the actor cannot
   win solely from XZ. R3 subsequently proves clearance and collision, rather than R2 inventing it.
4. Select the highest eligible support within the interval. Preserve the current owner's continuity
   through its declared seam when identifying that face, without favoring a lower floor over a legal
   step. Equal-height independent owners refuse unless explicitly aliased to one canonical owner.
   Report `NoSupport` when a fully known physical hole has no eligible lower support.

Explicit placement Y stays exactly authored and does not invoke support. For **new authored**
missing-Y placements, require `MapSupportBinding`: exact surface ID and XZ sampling, or space ID
plus an authored reference Y and bounded search interval. The latter may still be ambiguous and
must refuse. Imported placements use explicit Y. Format-4 native missing-Y placements retain a
tagged version-1 support recipe when migrated. They do not silently acquire a topmost-floor policy.

Propose a separate resolver-version-2 entry point for authored surfaces/support bindings, with
document `ResolverVersion=2` and options checked together. The existing two-field resolver metadata
retains `PayloadVersion=1`, with validation extended explicitly for supported resolver meanings.
The new entry point returns support identity alongside
the resolved placement. Unsupported combinations refuse before callbacks. Version 1 continues to
compute its existing identity for untouched format-4 inputs. Authored migration changes format and
identity explicitly. Analytic migration adds no native surfaces/bindings and preserves execution.
MapDoc's released format is 4 and its current native migration handles 3 to 4 [E15]. Use the next
free format for R2's semantic transition, nominally 5, with pure sequential migrations and closed
payload schemas. Do not reserve a release number.

The pure format migration retains the old resolver meaning and adds no invented terrain. Explicit
adoption of authored surfaces selects resolver 2. A migrated legacy support recipe can call the
old callback only with its declared policy identity included in build options. It cannot invent a
surface/space binding. The version-1 overload refuses a version-2 document before executing a
callback, instead of silently changing the old call's meaning.

Extend the **existing** native command preparation/validation/publication seam. It currently
accepts only `INativePlacementCommand` and publishes Placements/high-water state [E16]. Proposed
new `INativeDocumentCommand` preparation produces a detached candidate plus a typed write set and
deferred history state. Placement commands participate through an adapter. A terrain command's
write set contains affected patches, shared corner owners, seams/portals, space bindings and
material references. Validate local geometry and the explicitly bound closure before publishing
that set atomically. Do not publish only Placements or add a separate direct terrain mutation path.

GUI history and MCP use this one transaction. Rejection leaves document, history, dirty state,
identity, events and command retry state unchanged. Undo/redo restores the same IDs and exact values.
Effects include old/new 3D bounds, patch/space/dependency IDs, geometry/paint/domain digest changes
and terrain/physics/nav/material/residency invalidations. R3 maps them to affected nav tiles/seam
links. R8 maps them to loaded ownership. R9 consumes them for bounded gestures and history.

R2 needs correct atomicity on bounded complete fixtures. Its implementation must not claim the R1
whole-document cloning path is a scalable partial editor. R9 owns scoped transactions, loaded
dependency acquisition and reduced-copy history under [#1302](https://github.com/APKiwiOrg/KhaozEngine/issues/1302).
Until then, terrain commands on unsupported partial documents refuse. The pending full R2 plan must
replace its obsolete direct replacement wiring with this seam. Preserve the approved J2.1 smoothing
semantics, including 1 to 64 passes, 3x3 prior-pass neighborhood, unchanged halo and AwayFromZero
quantization. A missing/ambiguous shared halo refuses before mutation.

## Round ownership and required dependencies

| Owner | Contract/exit responsibility after this candidate is approved |
| --- | --- |
| R2 / C2 | Bounded terrain encoding, canonical floor/ceiling/wall/aperture geometry, space membership, height-aware query/resolver contract, migrations/identity and atomic terrain mutations. Proves geometry inputs and compatibility, not complete cave runtime |
| [R3 / C3](../superpowers/plans/2026-10-05-world-authoring-r3-shared-shapes-headless-builders.md) | Physics sidedness, floor support and ceiling/wall headroom, picking/LOS, placement supports and movement integration. Owns tiled/incremental nav capture/profile/link identities under [#1301](https://github.com/APKiwiOrg/KhaozEngine/issues/1301). Must allocate that implementation explicitly within R3 or a named prerequisite before plan approval |
| [R4 / C4](../superpowers/plans/2026-10-05-world-authoring-r4-bounded-water-medium.md) | Explicit flooded/dry containment and bed/surface semantics using space geometry, OA7 XZ edges and variable vertical coverage. Water overlap is checked within actual wet domains, not rejected from XZ alone. Owns data/geometry dependencies for [#1300](https://github.com/APKiwiOrg/KhaozEngine/issues/1300), [#1297](https://github.com/APKiwiOrg/KhaozEngine/issues/1297) and walker policy [#1299](https://github.com/APKiwiOrg/KhaozEngine/issues/1299) with R3 |
| [R5 / C5](../superpowers/plans/2026-10-05-world-authoring-r5-free-buildings-prefabs-interiors.md) | Free prefab transforms, native local support/paint, parent-space/interior domains and roof links. Uses the R2 compiler/query in local space. No duplicate floor owner, new cave model, nesting or foundation generation |
| [R8 / C8](../superpowers/plans/2026-10-05-world-authoring-r8-native-rendering-residency-captures.md) | Bounded surface/index/asset residency, scope validation, lighting, camera occlusion, HLOD and RenderOrigin adapters. Physical ceilings remain when view rules hide geometry. Sky/lighting follows occupied space. Owns backend water captures and scoped capture completeness |
| R3/R8 and game G3 | Per-cell geometry coverage independent of camera windows, correct frame/sampler mapping, multi-cell ghost/handoff continuity and on-demand nav. Use released PhysicsWorldFactory coverage/pose rules [E8]. Required OA9 work, not an optional extension |
| [R9](../superpowers/plans/2026-10-05-world-authoring-r9-unified-mapeditor-workflow.md) / [R10](../superpowers/plans/2026-10-05-world-authoring-r10-complete-ke-mapedit-parity.md) | One native editing experience with floor/ceiling/space selection, portal/hole diagnostics, bounded scopes, dependency halo acquisition, gestures/history and matching wire schemas. Input/wire tests cannot silently select another level. Report affected nav tiles and incomplete scopes |
| [R11](../superpowers/plans/2026-10-05-world-authoring-r11-offline-importer-no-loss-ledger.md) / game G2/G3 | Actual import refreeze, exhaustive ledger, scoped closure/capture coverage, named semantic acceptance, native runtime adoption and aggregate nav storage gate |

R6 marker support and R7 foliage/exclusions consume the same selected support/space keys. Their
gameplay/distribution policies remain in their owning rounds.

An ocean cannot wet a dry cave just because feet are below its SurfaceY. R4 must expose a contained
feet sample and a separately defined optical-bed query. Each result names its water body and space
coverage. Water can be limited by variable floor/ceiling geometry and explicit boundaries, with
dry interiors as deliberate exclusions. Missing containment geometry refuses a complete medium
result. R2 supplies geometric membership, not a new water simulation or approved optical algorithm.

Preserve the option of an explicitly sea-connected cave: a body may later cover a declared connected
set of spaces/portals under an approved C4 rule. It must not be hard-coded to exactly one layer.
Whether/how water crosses that connection remains the C4 approval gate. No swimming is introduced.
The mandatory water captures remain the submerged 0.3 m ledge, 2 m river, 30 m shelf to 100 m ocean,
enclosed cave lake and cross-region body, with no pale band, terrace band or seam step. Their depth
examples are not universal ocean depth limits.

Storage patch/page size does not pick nav tile size or cell size. R3 exposes explicit mappings and
cross-boundary links. The released nav capture defaults to four surfaces per column, while bake
serialization caps counts at 255 [E14]. R3 selects bounded layer/probe budgets and detects overflow.
Increasing a world extent or a ceiling height does not make those limits disappear. R8 supplies
on-demand residency, not authority derived from what the client currently sees.

## Bounded acceptance and fixture provenance

These are future acceptance contracts, not results of this documentation task. Synthetic geometry
is deterministic test data only and never replaces or enlarges Hollowmere. Each synthetic case uses
at most 32 patches, normally no more than 8 by 8 cells per patch. Use a fixed set of near-origin and
separated far-region cases, not an allocation proportional to the 64 km rectangle. No random/stress
loops, large local benchmark or live client is needed for R2.

| Proof and owner | Required assertion |
| --- | --- |
| `NativeCaveRepresentationContract`, R2 | Ramp enters a broad chamber with variable floor/ceiling, explicit side walls and open portal. A deeper chamber and shaft/link show the representation's ownership, without generated stairs |
| `CanonicalEntranceSeamAndAperture_Agree`, R2 | Shared vertex/triangle keys, winding/normals and rendered/support/capture input agree across patch edges. Physical hole has no faces/fallback. Changing an overlay cut never substitutes a physical entrance |
| `StackedCaveFloorsAndCeilings_PreserveGeometryAndSupportSelection`, R2/R3 | At least eight vertically stacked supports distinguish height/space, explicit Y, ceiling exclusion, equal-owner ambiguity, links and missing ceiling refusal. R2 proves query descriptors, R3 proves real clearance/collision and nav |
| `SparseFarAndDeepGeometry_PreservesLocalCoordinates`, R2 | Equivalent cases around origin and around both signs of 32,000 m, including signed storage boundaries, +/-500 m terrain, object bounds to +/-564 m and probes to +/-640 m. Compare canonical frame-local positions/normals and declared precision targets. Include a non-quarter yaw and scales 0.8/1.2 |
| `SparseIndex_UnloadedIsNotEmpty`, R2/R8 | Known-empty page/range, indexed-unloaded patch, corrupt payload, missing corner owner and exceeded query budget have distinct results. Bounded query reads only required records. Complete-view and scoped identities cannot be exchanged |
| `TerrainTransaction_RestoresSeamSpaceAndIdentity`, R2 | Multi-patch entrance edit, invalid ceiling crossing and missing halo demonstrate atomic reject, retry, undo/redo and GUI/service equivalence, including every effect/digest |
| `TiledNav_SeamsLinksAndVerticalLayersAreDeterministic`, R3 | Four small adjacent nav tiles cross a terrain-patch and portal boundary. Capture/profile identity includes local transforms and space/link dependencies. A changed seam invalidates its two sides, while unrelated tile digests stay unchanged. This is an actual tiled bake proof in R3, not a claim that R2 baked nav |
| `MultiCellGhostHandoff_PreservesLayerAndWorldCoordinates`, R3/R8/G3 | Two neighboring cells cover the same required border geometry in their own frames, preserving support/space and world position across ghosting/handoff. Storage/nav/cell boundaries deliberately differ |
| `DryCaveUnderOcean_RemainsDry`, R4 | Outdoor ocean, dry cave and lower enclosed lake share XZ but have correct separate containment. Missing geometry and conflicting wet domains refuse. Explicit approved sea connections have their own later cases |
| `FarOriginHlodRenderAndPick_MeetPrecisionContract`, R8 | Native triangle fidelity, local mesh submissions, lights/occlusion, scope coverage and camera/selection agree near/far. Actual pixels and required water looks use normal backend CI and later owner review |

R2 compatibility fixture preparation should select shipped **Grimhollow v0.11.0**, commit
`6b016b2caf5196889080a85b28e1f1142adac78f`, verified from the local tag for this draft. Its engine
pin is **20.25.0**. Freeze source bytes/catalogs/prefabs and the source runtime-oracle version before
fixture extraction. Do not assume running the later engine 20.27.0 decoder proves the old oracle
unchanged. Record any checked decoder equivalence, or extract expectations with the pinned source
runtime semantics. An explicitly selected later shipped source can replace this proposed snapshot
at plan reconciliation, with its own immutable provenance.

Store exact source commit/tag, per-path SHA-256, sorted aggregate digest, source engine pin, extraction
tool/version/options, units/row orientation and comparison policy. Derive expected counts and key
sets from that inventory. Keep test data self-contained, not tied to a mutable external worktree.
Account for every effective plane/corner/cell, cut/rotation/feather/flag/reserved byte, signed region
edge and source triangle. Check the historical spike coordinates when present, plus every operative
topology in the selected source. Old `74f57ee2`, 25-region, 103,041-corner and other fixed counts are
historical regression evidence, not the new acceptance inventory.

`LegacyTerrainImport_RemainsExactWithCaveModel` compares all selected source keys and derived geometry
at the approved tolerances. It proves no extra cave or floor was introduced by import. If the selected
source no longer exercises an old edge case, keep a separately labelled small regression fixture.
R2 creates no production importer and changes no current content. R11/G2 must refreeze **again at
actual import time**, including source/runtime/catalog/nav/profile changes since R2. R2's fixture
is not authorization to import an obsolete snapshot later.

## Impact on the existing six-task R2 plan

This is proposed allocation for subsequent writing-plans work, not replacement executable steps.
Retain the six concerns but reconcile their types, source references, tests and ordering:

| Existing task | Required refinement | Indicative implementation/verification labor |
| --- | --- | ---: |
| 1. Absolute surfaces/materials/validation | Patch/page encoding, integer lattice addresses and units, presence roles, seam/space reference schema, pure migration and versioned identity. Remove eager whole-surface payload assumptions | 4 to 7 engineer-days |
| 2. Exact topology/paint | Bounded canonical compiler with ceiling winding, wall strips, portals/rims, shared vertex ownership and aperture-versus-paint distinction. Preserve all legacy arithmetic | 4 to 7 engineer-days |
| 3. Sampler/local supports/paint | Frame-local, height-aware request/result and resolver-v2 entry point with explicit support binding, typed absence/overflow and unchanged v1/analytic callers | 3 to 5 engineer-days |
| 4. Masks/membership | Variable floor/ceiling spaces and nested semantic domains, portal boundary ownership and ambiguity/missing-geometry refusal. Keep import Indoor spans separate | 2 to 4 engineer-days |
| 5. Height/paint mutations | Extend native transaction write sets, atomic seam/space edits, exact undo/redo, retry safety, unified effects and unchanged J2.1 smoothing | 4 to 7 engineer-days |
| 6. Compatibility oracle | Freeze selected shipped source/runtime, derive inventory, compare exact data/geometry, add bounded near/far/deep/scoped fixtures and living API documentation | 3 to 5 engineer-days |

Task 1 declares reference validity. Task 2 produces geometry that Task 4 validates into occupied
spaces. Task 3's space-filtered query therefore depends on the membership result from Task 4.
The reconciled executable order should be **1, 2, 4, 3, 5, 6** or explicitly stage Task 3's query
types before its membership-dependent tests. Do not conceal that dependency in the old numbering.
Version-2 identity/migration must be tested before publishing resolver-v2 results.

The labor ranges assume one experienced implementer, existing headless/schema/storage test seams,
small fixed fixtures, ordinary review fixes within each task, and the proposed patch/frame strategy.
They include the bounded schema/compiler/transaction work above. They exclude R3 physics/nav, R4
water, R5 prefab runtime, R8 streaming/rendering and R9/R10 complete frontend delivery because those
are assigned to their rounds, **not because they are optional**. An unexpected required public
coordinate/protocol change or failed import-arithmetic gate requires re-estimation before execution.

The sum is **20 to 35 implementation/verification engineer-days**, plus **2 to 4 engineer-days of
independent design/code review and reconciliation**, giving **22 to 39 engineer-days of R2 labor**.
These are decomposed planning judgments, not measured throughput or an elapsed calendar promise.
With one implementer, review is partly serial. Owner response time, shared-machine verification
slots, release/publication availability and external infrastructure waits are separate elapsed
delays and are not silently converted into productive labor. R1's hosted-runner wait illustrates
that distinction. The subsequent #1309 failure adds separate test-repair work and a publication gate,
without changing this draft's immutable MapDoc evidence or R2 labor scope.

The old **12 to 18 elapsed weeks** predates OA9. The raw memo's **26 to 50 weeks** has no defensible
task allocation and is not reused. A whole-program calendar cannot yet be defended. Missing
decomposition is the exact #1301 tiled/incremental nav work and R3/prerequisite split, multi-cell
engine/game integration, R4 containment plus #1299/#1297/#1300 data/optical work, R8 scope/residency/
lighting/HLOD/capture work, R9/R10 scoped authoring, and actual R11/game adoption/storage acceptance.
Each needs task effort, dependencies, review/backend gates and available serial execution capacity.
Allocate all mandatory OA9 work before forecasting the program. Refresh at R2 plan approval/exit
and R5 refinement/exit. Do not reserve future release versions or hide required work in an optional
follow-up estimate.

## Remaining approval items and next gate

The material choices remaining for **this design review** are:

1. Accept or revise the provisional 64 km envelope, retained Y=0 datum and named padding/precision
   targets. Existing giant-world/500 m intent and continuous-sculpting choice are already settled.
2. Accept or revise paired patch geometry with explicit wall/portal/link ownership, variable occupied
   spaces, nested semantic domains and height-aware refusal rules. These are candidate contracts.
3. Accept or revise integer-addressed terrain plus existing float placements/frames, bounded payload/
   query budgets and the explicit format/resolver/identity migration. No coordinate rewrite is approved.
4. Accept the R2 allocation/effort assumptions as a basis for a reconciled full plan, while requiring
   the mandatory downstream decomposition before a whole-program calendar.

C4 sea-connected-water details and any larger nav storage/distribution remain their later explicit
owner gates. They are not prerequisites for writing the R2 plan and are not silently decided here.
After this draft is approved, reconcile the base C2/DG contracts and affected plans under the
controller, then write/review the full executable R2 plan against the released APIs. **Draft approval
permits planning. Execution still requires that plan's approval and released prerequisite checks.**

## Released evidence register

All engine links below use the immutable released commit. Each label is an exact file:line anchor.
Proposed types in the preceding sections have no released source citation.

| Key | Decisive released evidence |
| --- | --- |
| E1 | [KhaozEngine.MapDoc/MapResolver.cs:12](https://github.com/APKiwiOrg/KhaozEngine/blob/a87038f5a0441d76f1ca71c8ba8acf6ec7b1b6af/KhaozEngine.MapDoc/MapResolver.cs#L12) has the XZ-only callback. [MapResolver.cs:23](https://github.com/APKiwiOrg/KhaozEngine/blob/a87038f5a0441d76f1ca71c8ba8acf6ec7b1b6af/KhaozEngine.MapDoc/MapResolver.cs#L23) preserves explicit Y |
| E2 | [KhaozEngine.MapDoc/MapAuthoredIdentity.cs:20](https://github.com/APKiwiOrg/KhaozEngine/blob/a87038f5a0441d76f1ca71c8ba8acf6ec7b1b6af/KhaozEngine.MapDoc/MapAuthoredIdentity.cs#L20) serializes the complete document. [MapAuthoredIdentity.cs:41](https://github.com/APKiwiOrg/KhaozEngine/blob/a87038f5a0441d76f1ca71c8ba8acf6ec7b1b6af/KhaozEngine.MapDoc/MapAuthoredIdentity.cs#L41) hashes resolver version. [MapAuthoredIdentity.cs:48](https://github.com/APKiwiOrg/KhaozEngine/blob/a87038f5a0441d76f1ca71c8ba8acf6ec7b1b6af/KhaozEngine.MapDoc/MapAuthoredIdentity.cs#L48) accepts version 1 only |
| E3 | [KhaozEngine.MapDoc/MapResolvedDocument.cs:10](https://github.com/APKiwiOrg/KhaozEngine/blob/a87038f5a0441d76f1ca71c8ba8acf6ec7b1b6af/KhaozEngine.MapDoc/MapResolvedDocument.cs#L10) defines float MapTransform. Lines 12/18 implement point/parent composition. [MapResolvedDocument.cs:45](https://github.com/APKiwiOrg/KhaozEngine/blob/a87038f5a0441d76f1ca71c8ba8acf6ec7b1b6af/KhaozEngine.MapDoc/MapResolvedDocument.cs#L45) defines XZ resolved bounds. [MapDocument.cs:174](https://github.com/APKiwiOrg/KhaozEngine/blob/a87038f5a0441d76f1ca71c8ba8acf6ec7b1b6af/KhaozEngine.MapDoc/MapDocument.cs#L174) starts float placement coordinates |
| E4 | [KhaozEngine.MapDoc/MapBoundDocumentValidation.cs:26](https://github.com/APKiwiOrg/KhaozEngine/blob/a87038f5a0441d76f1ca71c8ba8acf6ec7b1b6af/KhaozEngine.MapDoc/MapBoundDocumentValidation.cs#L26) rejects partial native documents, line 27 requires resolver 1, lines 38/39 validate float finiteness. [Assets/MapAssetClosure.cs:39](https://github.com/APKiwiOrg/KhaozEngine/blob/a87038f5a0441d76f1ca71c8ba8acf6ec7b1b6af/KhaozEngine.MapDoc/Assets/MapAssetClosure.cs#L39) loads all roots/resources |
| E5 | [KhaozEngine.Render3D/Scene3D.RenderOrigin.cs:17](https://github.com/APKiwiOrg/KhaozEngine/blob/a87038f5a0441d76f1ca71c8ba8acf6ec7b1b6af/KhaozEngine.Render3D/Scene3D.RenderOrigin.cs#L17) keeps CPU inputs absolute. [Scene3D.RenderOrigin.cs:78](https://github.com/APKiwiOrg/KhaozEngine/blob/a87038f5a0441d76f1ca71c8ba8acf6ec7b1b6af/KhaozEngine.Render3D/Scene3D.RenderOrigin.cs#L78) exposes RenderOrigin. [Scene3D.RenderOrigin.cs:194](https://github.com/APKiwiOrg/KhaozEngine/blob/a87038f5a0441d76f1ca71c8ba8acf6ec7b1b6af/KhaozEngine.Render3D/Scene3D.RenderOrigin.cs#L194) reduces the matrix translation |
| E6 | [KhaozEngine.Physics/IPhysicsWorld.cs:99](https://github.com/APKiwiOrg/KhaozEngine/blob/a87038f5a0441d76f1ca71c8ba8acf6ec7b1b6af/KhaozEngine.Physics/IPhysicsWorld.cs#L99) exposes Origin, line 104 CanRebase and line 115 Rebase. [Physics.Bepu/BepuPhysicsWorld.Rebase.cs:48](https://github.com/APKiwiOrg/KhaozEngine/blob/a87038f5a0441d76f1ca71c8ba8acf6ec7b1b6af/KhaozEngine.Physics.Bepu/BepuPhysicsWorld.Rebase.cs#L48) translates bodies/statics. [Terrain.Render3D/ChunkTerrainCollision.cs:34](https://github.com/APKiwiOrg/KhaozEngine/blob/a87038f5a0441d76f1ca71c8ba8acf6ec7b1b6af/KhaozEngine.Terrain.Render3D/ChunkTerrainCollision.cs#L34) registers local terrain with an origin-reduced pose |
| E7 | [KhaozEngine.MapDoc/MapTileGrid.cs:12](https://github.com/APKiwiOrg/KhaozEngine/blob/a87038f5a0441d76f1ca71c8ba8acf6ec7b1b6af/KhaozEngine.MapDoc/MapTileGrid.cs#L12) is a distinct XZ storage coordinate. Lines 39/48 implement floor/half-open grid behavior. [MapTileIndex.cs:67](https://github.com/APKiwiOrg/KhaozEngine/blob/a87038f5a0441d76f1ca71c8ba8acf6ec7b1b6af/KhaozEngine.MapDoc/MapTileIndex.cs#L67) distinguishes partial indexed data |
| E8 | [KhaozEngine.NetWorld/ShardedWorldServerConfig.cs:23](https://github.com/APKiwiOrg/KhaozEngine/blob/a87038f5a0441d76f1ca71c8ba8acf6ec7b1b6af/KhaozEngine.NetWorld/ShardedWorldServerConfig.cs#L23) exposes CellOrigin with grid/handoff semantics at lines 17 to 22. [ShardedWorldServer.Frame.cs:51](https://github.com/APKiwiOrg/KhaozEngine/blob/a87038f5a0441d76f1ca71c8ba8acf6ec7b1b6af/KhaozEngine.NetWorld/ShardedWorldServer.Frame.cs#L51) defines required per-cell geometry extent, local pose/origin and legitimate border duplication. Line 67 exposes PhysicsWorldFactory |
| E9 | [KhaozEngine.Primitives/WorldFrame.cs:36](https://github.com/APKiwiOrg/KhaozEngine/blob/a87038f5a0441d76f1ca71c8ba8acf6ec7b1b6af/KhaozEngine.Primitives/WorldFrame.cs#L36) is the 128 m grid. Line 47 is MaxLocalRadius. [WorldFrame.cs:64](https://github.com/APKiwiOrg/KhaozEngine/blob/a87038f5a0441d76f1ca71c8ba8acf6ec7b1b6af/KhaozEngine.Primitives/WorldFrame.cs#L64) gives anchor Y=0. Lines 85/91 convert local/world. [NetWorld/SamplerSpace.cs:8](https://github.com/APKiwiOrg/KhaozEngine/blob/a87038f5a0441d76f1ca71c8ba8acf6ec7b1b6af/KhaozEngine.NetWorld/SamplerSpace.cs#L8) distinguishes World and Frame queries |
| E10 | [KhaozEngine.TileWorld/TileGroundTriangles.cs:31](https://github.com/APKiwiOrg/KhaozEngine/blob/a87038f5a0441d76f1ca71c8ba8acf6ec7b1b6af/KhaozEngine.TileWorld/TileGroundTriangles.cs#L31) defines drawable presence. [TileGroundTriangles.cs:140](https://github.com/APKiwiOrg/KhaozEngine/blob/a87038f5a0441d76f1ca71c8ba8acf6ec7b1b6af/KhaozEngine.TileWorld/TileGroundTriangles.cs#L140) compiles corner/mid-edge positions. Line 162 converts corner height with `heightCm * 0.01f` and region-local XZ |
| E11 | [KhaozEngine.TileWorld/TileTriangulation.cs:49](https://github.com/APKiwiOrg/KhaozEngine/blob/a87038f5a0441d76f1ca71c8ba8acf6ec7b1b6af/KhaozEngine.TileWorld/TileTriangulation.cs#L49) chooses the diagonal, including forced diagonal-half rotation. Line 60 produces two/four triangles with shared winding |
| E12 | [KhaozEngine.TileWorld/TileWorldDocument.Heights.cs:13](https://github.com/APKiwiOrg/KhaozEngine/blob/a87038f5a0441d76f1ca71c8ba8acf6ec7b1b6af/KhaozEngine.TileWorld/TileWorldDocument.Heights.cs#L13) resolves effective centimetre corners, including derived planes. Line 22 converts centimetres, and [TileWorldDocument.Heights.cs:65](https://github.com/APKiwiOrg/KhaozEngine/blob/a87038f5a0441d76f1ca71c8ba8acf6ec7b1b6af/KhaozEngine.TileWorld/TileWorldDocument.Heights.cs#L65) is the separate bilinear API |
| E13 | [KhaozEngine.Physics/PhysicsGroundProbe.cs:19](https://github.com/APKiwiOrg/KhaozEngine/blob/a87038f5a0441d76f1ca71c8ba8acf6ec7b1b6af/KhaozEngine.Physics/PhysicsGroundProbe.cs#L19) documents query space and fallback. Line 56 starts XZ-only Height. [PhysicsColumnProbe.cs:86](https://github.com/APKiwiOrg/KhaozEngine/blob/a87038f5a0441d76f1ca71c8ba8acf6ec7b1b6af/KhaozEngine.Physics/PhysicsColumnProbe.cs#L86) samples multiple hits with bounded output. Its lines 78 to 84 describe truncation, so a native complete-query adapter needs explicit overflow handling |
| E14 | [KhaozEngine.Movement/PhysicsNavBakeOptions.cs:7](https://github.com/APKiwiOrg/KhaozEngine/blob/a87038f5a0441d76f1ca71c8ba8acf6ec7b1b6af/KhaozEngine.Movement/PhysicsNavBakeOptions.cs#L7) documents absolute probe Y. Line 12 defaults MaxSurfacesPerColumn to four, lines 63 to 89 validate layout budgets. [GroundNavigationBake.cs:18](https://github.com/APKiwiOrg/KhaozEngine/blob/a87038f5a0441d76f1ca71c8ba8acf6ec7b1b6af/KhaozEngine.Movement/GroundNavigationBake.cs#L18) caps serialized surface counts at 255 |
| E15 | [KhaozEngine.MapDoc/MapDocumentFile.cs:84](https://github.com/APKiwiOrg/KhaozEngine/blob/a87038f5a0441d76f1ca71c8ba8acf6ec7b1b6af/KhaozEngine.MapDoc/MapDocumentFile.cs#L84) is format 4. Lines 174 to 185 refuse future versions and run sequential registered migrations. [MapNativeMigration.cs:10](https://github.com/APKiwiOrg/KhaozEngine/blob/a87038f5a0441d76f1ca71c8ba8acf6ec7b1b6af/KhaozEngine.MapDoc/MapNativeMigration.cs#L10) is the pure 3-to-4 native upgrade |
| E16 | [KhaozEngine.MapEditor/NativePlacementTransaction.cs:9](https://github.com/APKiwiOrg/KhaozEngine/blob/a87038f5a0441d76f1ca71c8ba8acf6ec7b1b6af/KhaozEngine.MapEditor/NativePlacementTransaction.cs#L9) defines preparation. Lines 21 to 31 reject other commands, clone/validate and publish only placement/high-water state. [MapEdit.Tool/MutationService.cs:40](https://github.com/APKiwiOrg/KhaozEngine/blob/a87038f5a0441d76f1ca71c8ba8acf6ec7b1b6af/KhaozEngine.MapEdit.Tool/MutationService.cs#L40) routes native service mutations through that seam |

## Draft self-review

Checked against OA13, C2/C3/C4/C5/C8 and DG9.1 to DG9.6. Continuous sculpting and free native
placement persist. Geometry, spaces and water are distinct. Walls/openings and stacked support have
explicit ownership/refusal contracts. Physical apertures never borrow legacy fallback. Sparse
payloads, integer terrain addresses and frame-local geometry avoid an eager world array, without
claiming R1 closure or R2 fixtures finish R8 scale. Datum/padding preserve shipped heights. Approved
import tolerances remain unchanged. Source provenance and later refreeze are separate. Required
downstream navigation, water, multi-cell and authoring work remain in scope and in estimate gates.
All new interfaces/budgets are proposals. No implementation, world/content edit or release is claimed.
