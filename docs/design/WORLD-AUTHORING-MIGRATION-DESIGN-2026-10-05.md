# Native MapDoc world authoring, option A

Status: **Both specs approved by owner 2026-10-05, T1 to T9 with revised T4, prefab v1/estimate and C4 boundary policy approved. R1 plan approved under OA8, later round plans pending**

This is the engine-wide specification for one native world authoring stack. It replaces the proposal's recommendation with option A. Spec approval settles the contracts below. Each engine round still needs its approved implementation plan and released dependencies before execution. This worker changes engine documents only and stops at its verified commit. The controller reviews and pushes, with no worker merge, tag or pack.

Evidence baseline is engine d4dd8d918b6c6639ea63291ad818aa829c02074a. The proposal commit is 1a90f445a239bfb5c3069ee1d02173ebc5ed6e6a now carried on feature/world-authoring (the proposal branch is retired). Game evidence baseline is 74f57ee22652bd18234b4479faba4f1898a17047, with proposal commit 743029b0c9ddc96a354861f170389e971cdfa269 now carried on feature/world-authoring (the proposal branch is retired). Counts below describe that frozen fixture, not the still-changing grand-world lane [G1, G2, S1].

Bracketed evidence keys resolve to file:line anchors in the register. Existing behavior and measurements are evidence claims. Approved contracts and scope are distinguished from task-level engineering proposals and estimates below. OA and T sources are the supplied 2026-10-05 dispatch, transcribed here as the review record, not claims inferred from source code.

## Owner rulings, binding direction

The owner answered the proposal summary on 2026-10-05: "For the engine - I want one tool to author both yes, I want props and buildings placeable agnostic of tiles in general, but I want specifically grimhollow to move off tile world too."

| Ruling | Direction approved by owner |
| --- | --- |
| OA1 | One tool, MapEditor GUI plus ke-mapedit MCP, authors terrain, props and buildings. No two-format hybrid |
| OA2 | Props and buildings accept free position, yaw and positive uniform scale as an engine-wide capability |
| OA3 | Grimhollow leaves TileWorld completely through the full native MapDoc swap, option A |

B, the MapDoc-led hybrid, was considered and declined by this ruling because it retained TileWorld components. C, TileWorld free transforms with ke-tileedit, was considered and declined because it kept the format and tool. The chosen direction is A [E1 to E5, E9 to E13, E19, E21].

## Approved technical contracts, reconciled 2026-10-05

T1 to T9 began as orchestrator proposals. The controller supplied the owner's exact answer "Approve" on 2026-10-05 for both specs with revised T4, prefab v1 and the 12 to 18 elapsed-week estimate, and C4 exact-boundary policy. The following is the approved contract summary, not a purported verbatim owner statement. Geometry/query details proposed by implementers still require explicit R3 plan refinement and review.

| Ruling | Contract to design against |
| --- | --- |
| T1 Terrain | Native MapDoc layers carry the exact authored 1 m corner-height lattice and per-cell material IDs, overlay shapes, rotations, feathering, blocked and indoor flags. GUI brushes and MCP verbs edit them. Movement floor, render surface and nav capture use one terrain definition. The measured 1.677 m disagreement becomes zero by construction. No procedural regeneration [E9, E10, E12, E15, S1] |
| T2 Water | Native bounded bodies carry their own surface height and medium. The importer materializes the existing bodies exactly. The rim rule is not a native runtime dependency [E11, E18] |
| T3 Buildings | A prefab asset groups free local placements, walls, doors, windows, roofs, furniture, lights, interior floor paint and interior volumes. MapEditor prefab mode offers optional local snapping. Instances have free transforms and overrides. Volumes replace plane-based roof hiding and indoor state [E3, E13, E21] |
| T4 Collision and reach | One engine-defined interaction envelope derived from canonical asset geometry is shared by both heads for picking, reach and walk-up. Retain a minimum 1 m vertical target reach-envelope height (`MinimumObjectReachHeight`) for low objects and define selectable bounds consistently. Physical colliders remain unchanged and never expand to match reach. Share transforms and versioned policy/closure identity. Preserve doorway apertures, physical occlusion and lower-2-m tree eligibility. R3 refinement pins exact geometry/query rules, with no hidden mesh-AABB versus footprint fork. Actual changed targets, distances, occlusion and stances require named import acceptance [E7, E8, E12, E23, G7] |
| T5 Identity | Stable placement IDs. Preserve every Grimhollow numeric object ID at the game boundary. Allocate new IDs without collisions [E1, E6, E22, G12] |
| T6 Headless builders | A GPU-free engine package builds terrain and placement statics, water medium, nav capture input and residency from the same MapDoc for server, client and tests [E8, E18, E25, E27] |
| T7 Tools | ke-mapedit reaches every current ke-tileedit world-editing capability, with the explicit mapping below, plus native paint, water, prefabs, free buildings, markers, foliage density and measured collision heights. GUI covers the same command surface [E4, E5, E16, E33 to E42] |
| T8 Migration | One offline validating lossless importer converts the accepted grand-world Hollowmere with an exhaustive ledger. Grimhollow then deletes its TileWorld code. The engine keeps TileWorld for other consumers until a separate deprecation decision [E3, E21, G1 to G3, S1] |
| T9 Timing | Engine rounds start only after spec and plan approval, on engine task branches. Each release takes the next available engine minor after the concurrent pivot program's releases. Only the owner tags. Game adoption starts after 0.11.0 and accepted grand-world land, on a fresh branch from main [E30, E31, G1, G14, G15] |

## Frozen content and measured constraint

The proposal fixture has 25 regions of 64x64 one-metre cells, four planes at 4.5 m separation, 14 ground materials, 121 archetypes (113 used), seven reusable prefab definitions and nine actual interiors. It contains 5,566 objects, 4,305 None, 842 Solid, 381 Wall and 38 WallCorner, including 578 interactive objects and ten higher-plane roofs [G2, G3, S1].

It has 38 markers, 103,041 distinct ground corners, 102,400 underlays, 2,130 overlays, ten shaped cuts, 660 feather flags, 8,872 blocked, 836 indoor and nine reserved Bridge flags, one 161x161 density raster, and seven clipped water bodies made of 113 rectangles at Y -0.37 m. The bridge deck comes from the asset's walk surface, not its Bridge flag [E9 to E12, G4, S1].

The spike preserved placement records and corner heights but lost operative paint, water, collision, roof, prefab and foliage semantics. Its field differed from the triangle movement floor by up to 1.676952362 m at (4.37,-64.61). This rules out promoting the spike as the importer [E9, E10, S1].

## Baseline capability matrix


This matrix describes the pinned baseline, not the proposed additions.

| Grimhollow feature and source | MapDoc today | MapEditor and ke-mapedit today | Terrain, Terrain.Render3D and runtime builders today | Status |
| --- | --- | --- | --- | --- |
| Archetype catalog, footprints, collision kinds [E3, G2] | Kind and instance tags, no footprint, TileWorld catalog binding, collision kind, Interactive or IsRoof [E1, E2] | Asset-manifest palette and category hook, a different catalog [E4, E7] | AssetEntry has collider, .coll, proxy, surface, heightmap and LOD. Mapping and game semantics required [E7, E8] | Partial |
| Arbitrary X/Z/yaw, uniform scale, explicit Y [E3] | Native float transform, nullable Y and positive scale [E1, E2] | Full placement gizmo and shared move/rotate/scale commands [E4, E5] | Numeric transform survives BuildPlacements, prop/static builders apply yaw/scale [E6, E8] | Supported, measured save/reload [S1] |
| Edge walls and two-edge corners [E12] | No edge/corner topology record [E1, E2] | Can place wall meshes, no grid perimeter contract [E4] | Baked mesh/compound shapes can model walls, no automatic edge conversion [E7, E8, E12] | Partial |
| Doorways and openings [G3] | Mesh placement, no portal/opening semantics [E1, E2] | Prop authoring, no building perimeter validation [E4] | Current doorway omits a blocking edge. A generic building box would close it. Authored compound/mesh colliders can preserve holes [E7, E8, E12] | Partial |
| Roofs, planes, roof Y 4.5 m [G3] | Explicit Y preserves height, not plane or IsRoof membership [E1, E2] | Elevated placement, no plane-aware roof control [E4] | Draw transform works, MapDoc has no runtime roof policy [E6, E13] | Partial position, missing semantics |
| Indoors and plane/interior hiding [G3, E13] | Tagged regions could describe interiors, no built-in Indoors or roof relationship [E1, E14] | Regions exist. Editor visibility controls do not supply gameplay hiding [E4, E13] | TileWorldView owns connected-interior roof hiding and hidden roof shadows [E13] | Missing natively |
| Ground underlay/overlay IDs, shapes, rotations, feathering [G3, E9] | No authored material raster/cut layer [E1, E2] | Sculpt edits heights, not exact painted floor/road layers [E4, E15] | Five-channel splats and pure splatRule are reusable, not a representation of 14 IDs and rotated cuts [E15] | Partial primitives, missing authoring parity |
| Corner heights and upper-plane derivation [E3, E9] | Sculpt over a flat base can preserve plane-0 lattice [E10] | Sculpt and set-height brushes [E4, E16] | Bilinear field matches document HeightAt. Movement uses drawn triangles. No upper-plane derivation [E9, E10, E12] | Partial, floor mismatch measured [S1] |
| Water bodies and flat local surface rule [E11] | Global WaterLevel, analytic lake features and regions, no rim/body rectangles [E1, E11] | Editable global water level, one camera-centred viewport plane [E17] | Scene3D water rendering reusable, bounded bodies/clipping/medium need adapter [E11, E17, E18] | Partial |
| Bridge decks, rails and dry feet above river [G4] | Placement height/yaw/scale, no TileWalkSurface or Bridge flag semantics [E1, E2] | Art placement, no deck/medium contract [E4] | Surface maps and .coll statics reusable. Need same capture floor and water-height test [E8, E12, E18] | Partial |
| Foliage layers, density, underlay/indoor/solid/door exclusions [E19] | Scatter/companion layers, no TileFoliageLayer raster or predicates [E1, E19] | Scatter and bake tools, not a lossless density import [E4, E16] | GroundCoverDistribution accepts sampled density. Reuse TileFoliageSurface's exact rules [E19, E20] | Partial |
| Player, NPC and general named markers [G5] | PlayerSpawns, Spawns and Regions. Typed spawns ground-snap XZ, no marker plane [E1] | Spawn/region tools exist [E4, E16] | Game interprets tags/archetypes. General marker API needs mapping [E1, E14] | Partial, 32 NPCs, one player, five landmarks in probe [S1] |
| Prefabs and actual instance differences [G2, G3] | No multi-layer prefab reference, group instance or override fields [E1, E2] | Duplicate placement/freeze scatter is not a building prefab [E4, E16] | TilePrefab already preserves layers, relative heights, objects and markers [E21] | Missing natively |
| Interactive objects, Examine and stable IDs [G6] | Stable string Id and tags, no Interactive policy [E1] | Stable edit identity [E5] | BuildPlacements uses Kind as PropPlacement.Id and drops placement identity/tags. Editor pairs IDs back separately [E6, E22] | Partial |
| Reach shapes and walk-up stance [G7] | No reach shape/resolver [E1, E2] | Free movement does not update game reach [E5] | Generic ReachGeometry reusable, game resolver uses TileFootprint. Free yaw requires shared oriented geometry [E23, G7] | Partial |
| Physics bridge [G8] | No complete automatic MapDocument equivalent of TileWorldColliders [E6, E12] | Validation is not physics proof [E2, E16] | Shape scaling and terrain statics exist. ChunkStatics is internal to Terrain.Render3D, not a public headless map builder. Extract a GPU-free shared builder and compose ground, medium and grid walls [E8, E12, E18] | Partial |
| Nav capture, profiles and habitat [G9] | Geometry/regions supply inputs, no ready bake [E14] | IsWalkable checks slope/global water, not prop clearance [E24] | PhysicsNavBake captures generic complete physics. Preserve separate analytic movement view and complete capture world [E25, G8, G9] | Partial, generic bake reusable |
| Region residency and streaming [G10] | Monolithic/tiled v3 storage, index, source and hashes [E26] | Whole/windowed load and form-preserving save [E16] | MapTileResidency publishes placements/sculpt snapshots and gates chunk builds. Composite bounds/statics/markers need ownership rules [E27, E8] | Supported core, partial composition |
| Netcode and authoritative state [G11, G12] | MapDocumentHash, no MapDoc game host/state policy [E26] | Authoring has no networking authority [E16] | NetWorld/Locomotion independent of format. Game boot/gates use tile data and IDs today [G8, G11, G12] | Partial integration, keep netcode |
| Snapshot tool and render goldens [G13] | Headless document load [E26] | RenderService uses ViewportWorld/Render3DSnapshot. Missing manifests render terrain only [E28] | Existing TileWorld/Terrain goldens, no Hollowmere cross-format proof [E29, G13] | Partial |

## Native engine contracts

Names below identify proposed API responsibilities, not existing public types. Core MapDoc and its runtime packages acquire no TileWorld or Grimhollow dependency. An optional offline importer is the only new package allowed to read TileWorld. Existing analytic MapDoc consumers and TileWorld consumers retain their current behavior [E1, E2, E6, E26, E31].

### C1 Document, schema, identity and asset closure

**Data and versions.** The evidence baseline is MapDoc format 3. R1 introduces format 4 with separate playable bounds, digest-bearing native asset references, optional placement numeric IDs, a persisted numeric allocation high-water mark and resolver identity metadata. Absent playable bounds migrate from Bounds. Old placements retain their stable string IDs and Kind. An explicit format upgrade changes the format hash and needs matching peers, while legacy analytic execution remains unchanged. The ordinal list position is never identity. Numeric IDs are positive int64, encoded as decimal strings in JSON/MCP so clients cannot round them through a double [E1, E2, E6, E26, E43].

Imported IDs are copied exactly. New allocation reserves above the maximum imported/reserved value, increments monotonically, fails on exhaustion and never reuses deletion tombstones. Prefab child-to-world-ID bindings are serialized, not hashed from names. Generic games may omit numeric IDs. Asset kind, placement ID and game numeric ID are distinct fields in every resolved record, with ordered instance tags retained [E3, E6, E21, E22, G12].

Undoing an accepted allocation removes its placement but does not lower the persisted high-water mark. Redo restores the originally allocated ID. A different edit after undo allocates above that mark. A rejected transaction publishes neither a placement nor an allocation and restores its pre-transaction state. This distinction prevents undo history from reusing durable identities while keeping rejected edits atomic.

Formats advance at the rounds below: 5 native terrain, 6 water, 7 prefabs/interiors, 8 markers, 9 foliage. Every N to N+1 migration is pure and tested. Numbers describe this baseline-relative schema sequence, not reserved engine releases. If concurrent MapDoc work takes a number, rebase the sequence onto the next free format, preserving these semantic transitions. Future-format input refuses rather than dropping unknown fields. All newly structured payloads use closed schemas and explicit payload versions [E2, E26, E43].

The authored-world identity hashes the normalized MapDoc, complete native prefab/material/asset/collider/light/LOD closure and builder algorithm/options versions. Monolithic and storage-chunk forms with identical TileSize resolve equally. Re-tiling can change storage identity as today without changing coordinates. Game catalog and nav/profile hashes are additional game gate inputs, not rules encoded in engine assets [E7, E26, G11].

**Runtime.** A deterministic resolver returns immutable placement records, effective transforms, asset descriptors, semantic surface IDs and ordered tags. A render-free manifest DTO owns collision and surface data. Render3D adapts that DTO for mesh/material loading rather than forcing the headless resolver to reference Render3D. Missing references, duplicate IDs, stale hashes, cycles or unsupported payload versions fail before building any world [E6 to E8, E22, E26].

**Editor/tool.** Load/save/validate/summary and the ID inspector use the resolver. For native game placements, rename changes a display label. Changing the stable placement ID is a separate explicit validated remap that updates every reference and cannot alter numeric state identity. Moves, rotations, scaling, duplication and undo/redo preserve existing IDs. Duplication allocates once and redo restores that allocation. Placement kind and asset variant remain separately inspectable [E4, E5, E16, E22, E35, E38].

**Proof.** Schema migrations, old analytic v3 roundtrip, large int64 exact roundtrip, reordered placement arrays, imported/new-ID collisions, tombstones, redo, closure edits and missing/cyclic/stale references. Two independently built heads return identical resolved identity tables and closure hashes [E1, E6, E26, E43].

### C2 Authored terrain and local floor surfaces

**Data.** Format 5 adds an explicit analytic/authored terrain source discriminator. Authored terrain is an absolute lattice, never analytic noise plus sculpt deltas. Each native surface has stable ID, origin, positive cell size, width/depth, corner heights, per-cell underlay/overlay uint16 IDs, four named overlay cuts, quarter-turn paint rotations and flags Blocked, Indoor, NoDraw, FeatherOverlay and preserved reserved metadata. World terrain uses 1 m cells for Hollowmere. Storage chunks are independent of that resolution [E9, E10, E12, E26, E44].

Imported corner values use exact integer centimetres with an origin/height unit declaration, so copying the old short payload adds no rounding. General authored surfaces may declare a finer unit. Half/quarter-cell triangulation points follow the exact old cut and diagonal rule. Explicit native surface layers replace planes. The importer materializes all four planes' effective heights, including ground-plus-4.5 m derivations and authored overrides, and records how they were derived in its ledger. Void and NoDraw cells stay absent render/capture geometry. No unused upper plane becomes a new walkable floor [E9, E12, E21, E44].

Material entries preserve IDs, texture binding, repeat rate, physical material/medium classification and missing-material diagnostics. They do not squeeze 14 IDs into five splat channels. A local prefab floor uses the same surface DTO and transform pipeline. Moving it does not repaint world cells. Imported building paint can be a non-colliding surface decal constrained to the canonical floor triangles. Elevated floors are explicit support surfaces, with no second plane-derived sampler [E7, E9, E15, E21, G2, G3].

**Runtime.** One GPU-free triangle compiler yields vertices, topology, surface IDs and geometric normals. Rendering, terrain statics, analytic triangle sampling and nav capture consume this output. Feather tessellation subdivides the same triangles without moving the surface. Interior floor paint is an explicit material-override patch, distinct from a rigid support floor. It decorates the canonical terrain triangles without adding a competing floor. A building can author either kind in local space, and the resolver keeps their support roles distinct. There is no bilinear movement fallback on drawn terrain. Void/outside fallback is explicitly tagged as non-capture support, preserves the existing bounded fallback behavior and cannot expand playable bounds [E9, E10, E12, S1].

Indoors flags remain authored native data. Their connected footprint is compiled into the same volume-membership service as C5, with an explicit vertical span. Prefab floors bind to their own interior volume instead. No client separately tests a cell flag while the server tests a volume. Imported indoor mask coverage and authored volume footprints must agree. Blocked flags remain masks, with their medium-aware gameplay filtering supplied by the consumer [E12, E13, G8].

**Editor/tool.** Height set/read/raise/flatten/smooth/import and underlay/overlay/cut/rotation/flag/feather brushes act on named native surfaces. Heights roundtrip without changing units. Brush edits invalidate terrain, physics, nav, material and residency caches as one transaction. Brush preview shows actual triangle floor height/normal, not only the document height [E4, E16, E33, E37].

**Validation and proof.** Reject invalid dimensions, nonfinite heights, inconsistent shared corners, unknown materials/cuts/flags, unsupported units and accidental analytic/authored mixing. Compare every source corner and cell on every plane, canonical triangle topology/normals and frozen render captures. At the spike's failing point and across cuts/seams/slopes, all three consumers use the same triangle sample, giving zero semantic height discrepancy. Physics backend raycasts may use an explicit 0.00001 m numerical tolerance, never the 1.677 m alternate field. No procedural generator is part of the conversion [E9, E10, E29, E44, S1].

### C3 Placement assets, collision, picking, reach and headless build

**Data.** Native asset descriptors have version 1 collision payloads: no-collision, compound boxes with local poses, or a referenced versioned baked collider with digest. Support/deck surfaces and canonical interaction-source geometry, including non-solid scenery, are explicit descriptors. R3 defines the versioned envelope derived from that source, its minimum 1 m vertical target reach-envelope height, and selectable bounds. No head independently chooses a render AABB or catalog footprint. Preserve authored mesh origin, unit scale, materials/parts, LOD and light offsets. Asset descriptors also store digest-verified local render bounds and LOD/light extents, so headless residency needs no GPU mesh. Imported kit loading uses an explicit preserve-source-scale mode, not an unintended HeightMeters renormalization [E7, E8, E12, G2].

A placement/prefab instance applies position XYZ, yaw radians and positive uniform scale exactly once. No quarter-turn or grid requirement exists. Null Y means an explicit terrain-support snap policy, not a second height algorithm. Import uses explicit Y from the canonical source transform, even where old placement anchoring was bilinear, to avoid moving art when the movement floor is unified [E1, E3, E5, E9].

Legacy Wall and WallCorner become thin oriented local boxes or compounds, preserving which edges block. Doorway colliders preserve apertures. A whole-building box is prohibited. Legacy Solid bottoms/top spans can depend on the terrain at that placement. Import materializes a digest-keyed asset collision variant for such spans and for deck components, then records the variant in the placement asset reference. It does not recalculate a footprint at game runtime. Variants share meshes and retain gameplay Kind. New free placements use their selected authored asset collider. Moving an imported variant moves its rigid shape, with editor diagnostics for poor ground seating [E3, E7, E8, E12, E45].

**Runtime package.** Proposed KhaozEngine.MapDoc.Physics depends on MapDoc, the render-free asset descriptor seam, Terrain/Collision/Physics/Movement as needed, with an optional backend adapter. It has no GPU, Render3D, MapEditor or TileWorld dependency. One build produces immutable terrain triangles, resolved placement shapes/support surfaces, blocked masks, medium descriptors, spatial bounds, complete capture input and residency ownership. It exposes these descriptors without requiring a Bepu device just to validate a world [E8, E18, E25, E27].

The consumer registers a complete static world for capture and a movement query view excluding the ground handles, using the same triangle floor sampler. Filtering blocked-water masks is an explicit game option covered by the game identity. The server's complete statics do not depend on client camera/window residency. Client windows include oversized geometry crossing any storage chunk boundary [E12, E18, E25, E27, G8, G9].

**Shared geometry, revised T4.** Physics and occlusion consume the unchanged physical compound/baked collider. Picking, reach and walk-up consume one engine-defined interaction envelope derived from canonical asset geometry, including explicit geometry for non-solid Examine props. Both heads use the same effective transform, selectable bounds, minimum 1 m vertical target reach-envelope height (`MinimumObjectReachHeight`) for low objects, policy version and closure digest. An envelope must preserve doorway openings and must not become a collider or LOS blocker. Broad-phase AABBs are only acceleration, never an unrecorded choice of target. Tree eligibility clips to the lower 2 m. R3 plan refinement explicitly pins source geometry, envelope construction, selectable/ray/distance semantics, band coordinates and versioned identity for every supported shape. Named old/new query outcomes are still reviewed at import, even though this policy is approved [E7, E8, E23, G6, G7].

**Editor/tool.** Gizmos, bounds/collider visualization, object selection, collision queries, walkability and path previews use this build. Measurement reports raw mesh max Y and effective transformed collision bottom/top separately. Writing measured heights changes only the selected asset collision descriptor, with a dry-run diff and invalidation of affected statics, not an accidental mesh resize [E4, E5, E24, E36, E42].

**Validation and proof.** Reject missing collider data for solids, unsupported shape types, zero/negative/nonfinite scale, invalid compounds and stale baked shapes. Headless tests exercise yaw 0.371, offset (0.23,0.17), scale 1.137, slope-seated walls, corner walls, narrow door apertures, decks, a large cross-chunk building, lower tree band and compound distance/stance. Check client/server pick/reach/collider identity and nav clearance against those shapes. Existing generic reach APIs may need oriented/compound support, which belongs in the engine [E8, E12, E23 to E25, E45, S1].

### C4 Bounded water and medium

**Data.** Format 6 adds stable-ID water bodies with disjoint bounded rect/polygon domains, explicit SurfaceY, medium key/parameters and render material reference. Import copies all seven region-clipped bodies, 113 rectangles and their exact heights as independent records. It may associate equal-height records for editing but cannot merge away source identity in the ledger. Terrain height edits no longer silently change a body's level. Analytic maps may retain their old global-water mode, while an authored map selects bounded mode and cannot accidentally render both [E1, E11, E17, E18, S1].

**Runtime.** The shared builder supplies clipped water draw polygons and feet-aware medium samples. Overlap of two bodies with different surfaces/media is invalid unless explicitly split into disjoint domains. Foot height at or above the surface is dry, preserving decks over water. Body edges use one documented half-open domain rule for rendering/query/capture. Neither camera position nor an old region rim recomputes the surface [E11, E17, E18].

Native rectangles include minimum X/Z and exclude maximum X/Z in world coordinates, including negative Z. Simple polygon rings normalize to counter-clockwise XZ winding. An edge owns a boundary point when its direction has negative Z, or has zero Z and positive X. At a vertex, every incident edge must own that point. Shared-edge tessellation uses the same rule. Positive-area domain overlaps are rejected even at equal levels and media, while touching edges are allowed. The importer explicitly normalizes legacy negative-Z domains and records exact-boundary sampling differences in its differential report rather than claiming legacy boundary ownership is unchanged.

**Editor/tool.** Body create/read/list/set/remove, boundary brush and level/medium inspector share undoable commands. Preview water alongside terrain, bridges and medium query readouts. Validate finite levels, nonempty/non-self-intersecting domains, overlaps and valid media/material IDs [E4, E11, E16 to E18].

**Proof.** Exact body domain/height/media ledger, seams, negative Z edges, dry bridge feet, submerged river bed, multiple independent levels and no global ocean outside the bounded bodies. Compare render/capture/medium domain samples before and after import [E11, E18, E25, G4, S1].

### C5 Prefabs, buildings, interior volumes and roofs

**Data.** Format 7 adds version 1 native prefab assets and free-transform instances. A prefab has stable local child keys, placements, native local floor surfaces/paint, lights, markers, interior volumes and optional prefab-local C4 water bodies. The optional local water uses the existing bounded-domain/medium contract and one parent/local transform, resolving J5.1 without a new water algorithm. Instance overrides address those keys for transforms, tags, asset variants, additions/removals, floor paint and volume/roof membership. A persisted child binding map gives resolved children stable world placement/numeric IDs. The approved rigid prefab v1 boundary includes free transforms, stable children, overrides, local floors/volumes and optional snapping. No nested prefab references, generated stairs/foundations or architectural CAD in v1. A building needs no world tile footprint or plane [E1, E3, E13, E21].

Interior volumes are local polygon prisms with explicit lower/upper Y and stable storey/volume keys. Roof child keys attach to the volumes they conceal. Hidden roofs still cast shadows. AlwaysVisible/AlwaysHidden remain view modes. Auto hides only linked roofs above the observer's occupied volume, not all roofs on a global plane. Roof-aware camera blockers and indoor lighting use that same membership result. An open pasture shelter has no indoor volume and stays visible in Auto [E13, G3].

World transforms compose parent and local position/yaw/uniform scale once for child shapes, interaction envelopes, floors, lights, volumes, markers and optional local water. A doorway hole remains open under free rotation/scale. Optional local snapping improves authoring but is not a runtime constraint. Uneven-site seating is a diagnostic, not an automatic procedural foundation. Arbitrary authored storeys fit as local surfaces and volumes, without adding stairs generation, CAD or structural simulation [E3, E7, E8, E21].

**Import rule.** Keep all seven reusable definitions, then reconstruct each actual building using a checked membership manifest over original objects/cells/markers/roofs. A nominal old stamp does not recover actual instance differences. Use explicit overrides or a native variant asset for differences, with every original object represented exactly once. Parent containers are new identities, while original leaf numeric IDs survive. Uncertain grouping refuses and reports the unassigned evidence instead of guessing [E3, E21, G2, G3, S1].

**Editor/tool.** Prefab mode opens one native asset, edits the same placement/floor/water/marker/volume commands in local space, optionally snaps, previews its collider/openings and saves a validated asset. World mode places/transforms an instance and inspects overrides. Extract/save/list/place/edit/override/unpack use one command layer. Asset edits invalidate every referring instance. Undo/redo restores the same child IDs [E4, E16, E21, E38].

**Validation and proof.** Reject dangling overrides/roof links, duplicated child bindings, cycles, unsupported nesting, invalid volume geometry and double-owned floors. Prove seven definitions plus nine actual interiors, cottage/bank door routes, crafting-hall differences, unchanged higher roof transforms, shadow-only hiding, multi-storey observer separation, and free building placement at non-quarter yaw/non-grid position/scales 0.8 and 1.2. Reload must preserve each leaf ID and transform [E13, E21, G3, S1].

### C6 Markers, spawn readers and habitat inputs

**Data.** Format 8 adds the world general-marker registry with stable ID/name, free XYZ/yaw, role, enabled flag, ordered tags and optional prefab-local ownership. Numeric game placement IDs are not required for markers. Prefab-local marker records are part of prefab payload 1 in R5 and join this registry in R6. Player/NPC spawn lists migrate through lossless typed projections, retaining archetype/role and exact legacy computed Y. A general landmark is a point, not a tiny region disc [E1, E14, G5, S1].

**Runtime/editor/tool.** Resolver supports role/name lookup, explicit height or canonical support snap and world/local transforms. GUI and marker set/remove/list plus spawn controls edit this model. Validate globally unique imported names, roles via the consumer registry and dangling prefab ownership. Engine returns generic tagged shapes/points. Cow pen, duck habitat and spawn selection stay game policies [E1, E14, E16, E34, G5, G9].

**Proof.** Preserve all 38 markers, their roles/tags/enabled state/world points, including 32 NPC, one player and five general landmarks. Prove sub-metre/elevated markers, prefab move/scale and old spawn migration without snapping a saved explicit height [E1, E34, G5, S1].

### C7 Foliage density and deterministic dressing

**Data.** Format 9 adds versioned native density layers with ID, origin, row direction, cell spacing, dimensions, byte raster, seed, distribution settings, asset/material references and exclusion rules. Convert the existing 161x161 raster in its positive-world-Z row convention, not the negative-Z legacy tile convention. Do not regenerate the raster or reseed samples [E19, E20, E39, S1].

**Runtime.** The generic CPU distributor reads the C2 canonical surface, material paint, C3 shapes, C5 indoor volumes and explicit doorway/edge exclusions. Imported distributions keep the existing predicate version and parameters so default sample identities, positions, orientations and scales match. Where legacy footprint/roof exclusions differ from physical shapes, the importer materializes explicit native exclusion masks and binds them to their owning placements/prefabs. These are authored distribution masks, not alternate collision/reach shapes. They transform with their owner. New layers can select geometry-derived exclusions. Changing an imported predicate version requires a sample differential rather than silently rerolling. Cache identity includes density, surface, collider/volume and distributor version. Scenery objects produced by worldtrees/worldflora remain explicit placements, not density-layer collision or gameplay targets [E19, E20, G2].

**Editor/tool.** Layer set/get/remove, density set and circular paint share the raster command implementation. GUI paints and previews the same sample set. Validate raster shape/bytes, finite spacing/settings and valid references. Prove every one of the 25,921 source density bytes, all generated sample records and exclusion decisions, seed stability, doorway clearance, rotated-building exclusion and reload with no reroll [E19, E20, E39, S1].

### C8 One native rendering, residency and capture path

**Data/runtime.** No extra schema revision. Authored terrain draws the canonical geometry, with no coarser height-field LOD replacing its triangles. LOD may reduce grouping/material work while preserving that surface. A native resolved-world view composes C2 terrain/local floors, C3 placements/LOD/lights, C4 water, C5 interiors/roofs and C7 foliage. Bounds are derived from effective child shapes/meshes/surfaces, with a single owner and membership in every intersected storage chunk. Partial loads cannot claim whole-world validation without the closure. Server complete build remains independent of visual streaming [E7, E8, E26 to E28, G10].

MapEditor, ke-mapedit RenderService and a game client use this native view. Snapshot APIs accept the same manifests/resolver/options and observer volume. Missing required assets fail capture, rather than silently producing terrain-only images. Hidden roofs enter a shadow-only pass. Camera collision/picking respects current roof visibility while physical world statics stay authoritative [E13, E17, E22, E28, G13].

**Validation and proof.** Complete load versus chunked load, spanning building/tree residency, duplicate-free draw/statics and clean unload. Existing TileWorld and analytic-terrain goldens remain unchanged. Add native terrain paint cuts/feathers, river/bridge, roofs/indoor shadow, transformed props/buildings and foliage goldens through the normal backend CI route. Grimhollow captures use TAA Native. Tests prove scene/capture plumbing as well as isolated mesher output [E27 to E29, G13, G16].

### C9 Editing parity, importer and no-loss gate

**Commands.** GUI and MCP invoke one undoable native command layer. Each mutation reports affected surface/placement IDs, dirty bounds, identity and undo/redo labels. Validation failure writes nothing. Save validates the closure and writes atomically, retaining tiled-writer protection against unindexed overwrites. Deterministic batch transforms/placement operations return every allocated ID, and redo does not allocate again [E4, E5, E16, E32, E33 to E42].

**Importer.** An optional offline TileWorld-to-MapDoc migration package reads a frozen source and its catalogs/prefabs, calculates the actual source runtime semantics, writes only a new destination and emits a versioned ledger. The optional package supplies one offline CLI/library implementation, with source, destination, membership manifest and ledger arguments. It is a one-time converter, not another ongoing world editor. Source and destination hashes, converter/builder versions and parameters are mandatory. All native output loads without TileWorld. Authored integers, bytes, IDs and ordered tags compare exactly. Derived transforms/vertices compare within 0.00001 m and normals within 0.000001 component error, with the tolerance fixed before conversion and every exceeded value reported. Native floor/render/capture agreement still comes from identical triangle descriptors, not separate approximations. The old fixture is an offline comparison input, never a referenced native world component. Re-running identical input produces identical output/digests. Existing unrelated output refuses overwrite [E3, E9 to E13, E19, E21, E26, E32, S1].

The ledger has one record per source object, cell/layer/plane and corner, marker, prefab definition/actual instance membership, roof link, water body/rectangle and foliage density byte/generated sample. Each records source key/value digest, destination native key/owner, preserved effective transform/role/geometry digest, comparison result and an explicit approved semantic exception reference. Storage grouping and new prefab parents have separate counts from original leaf objects. Every source key is accounted once. Duplicate/dangling/unmapped records or unauthorized differences refuse publication. The companion game spec defines the exact Hollowmere gate [G2 to G13, S1].

**Proof.** Exhaustive closure and inventory comparisons plus route/reach/pick/render behavior. Counts and a new MapDocumentHash alone are insufficient. T1 deliberately removes the probe's alternate floor. T4 changes the legacy picking/reach implementation, so the differential report must identify any changed hit target or distance for owner review. It must not call those differences lossless without explicit acceptance. Physics shape preservation and same-head agreement are independently proved [E9, E12, E23, E26, G7 to G13, S1].

## Complete ke-tileedit verb parity map

All 50 baseline MCP verbs are included. Names in the middle column are required native ke-mapedit surfaces, existing where E46 covers them and proposed otherwise. Parity means capability, not accepting a TileWorld input format. Surface cells/storage chunks are authoring addresses only. Native spatial arguments are world metres, yaw radians and explicit surface/volume IDs. Negative world Z and north-first legacy arrays are converted explicitly at import, while foliage retains its positive-Z row convention [E3, E33 to E42, E46].

| Current ke-tileedit verb | Native ke-mapedit verb and contract | GUI equivalent / evidence |
| --- | --- | --- |
| world_open | map_open, native closure only | Open [E35] |
| world_create | map_create, analytic or authored mode | New map [E35] |
| world_save | map_save, validated atomic closure | Save [E35] |
| world_summary | map_summary, layer/instance/leaf/ID counts | Summary [E35] |
| world_validate | map_validate, schema/closure/runtime findings | Validate [E35] |
| catalog_list | asset_list and material_list, all bindings | Palettes/inspector [E35] |
| region_create | storage_chunk_create, no gameplay tile | Storage/terrain extent [E35] |
| region_delete | storage_chunk_delete, refuse content loss unless explicitly selected | Storage delete [E35] |
| region_list | storage_chunk_list, separate from tagged gameplay regions | Storage overview [E35] |
| undo | undo, identical IDs restored | Undo [E35] |
| redo | redo, no reallocation | Redo [E35] |
| tile_get | surface_cell_get, paint/flags/corners/derived physics | Cell inspector [E33] |
| tile_set | surface_cell_set, omitted layers untouched | Single-cell brush [E33] |
| tiles_fill | surface_cells_fill, named surface rect/flags/paint | Fill brush [E33] |
| tiles_get_rect | surface_cells_get_rect, lossless data plus ASCII view | Layer map [E33] |
| object_place | placement_add, asset/XYZ/yaw/scale/tags/ID | Place [E38] |
| object_move | placement_move, full XYZ or explicit support snap | Move gizmo [E38] |
| object_rotate | placement_rotate, radians | Yaw gizmo [E38] |
| object_remove | placement_remove, leaf reference checks | Delete [E38] |
| object_set_tags | placement_set_tags, preserve order | Tags inspector [E38] |
| object_get | placement_get, identity/transform/shape/roles | Placement inspector [E38] |
| objects_in_rect | placements_in_rect, explicit anchor or overlap query mode | Rect select [E38] |
| object_find | placement_find, asset/kind/tag/ID | Search [E38] |
| objects_line | placements_line, metre spacing and free transforms | Line placement [E38] |
| objects_scatter | placements_scatter, seed and shared collision/exclusion tests | Deterministic scatter [E38] |
| marker_set | marker_set, general XYZ/yaw/role and typed spawn projections | Marker tool [E34] |
| marker_remove | marker_remove, stable identity | Marker delete [E34] |
| marker_list | marker_list, all roles including landmarks | Marker list [E34] |
| height_set | surface_height_set, exact corner patch/unit/row declaration | Height brush [E37] |
| height_raise | surface_height_raise, delta/falloff | Raise/lower brush [E37] |
| height_flatten | surface_height_flatten, explicit or rounded mean | Flatten brush [E37] |
| height_smooth | surface_height_smooth, 1 to 64 iterations with legacy 3x3 prior-pass blur | Smooth brush [E37] |
| height_get_rect | surface_height_get_rect, roundtrips set | Height inspector [E37] |
| height_import | surface_height_import, explicit image range/orientation | Heightmap import [E37] |
| prefab_save | prefab_save, extract selected children/surfaces/volumes with stable local keys | Prefab extract/save [E40] |
| prefab_place | prefab_place, free world transform and child ID bindings | Prefab placement [E40] |
| prefab_list | prefab_list, version/digest/closure | Prefab palette [E40] |
| foliage_layer_set | foliage_layer_set, complete versioned native layer | Layer inspector [E39] |
| foliage_get | foliage_get, layer/list plus raster metadata | Foliage list [E39] |
| foliage_density_set | foliage_density_set, exact bytes | Density import [E39] |
| foliage_paint | foliage_paint, shared metre brush/hardness | Density brush [E39] |
| foliage_remove | foliage_remove | Layer delete [E39] |
| collision_at | collision_at, world point/height and oriented shapes/masks | Collision inspector [E36] |
| is_walkable | is_walkable, actual shape clearance and named nav profile, not slope alone | Clearance preview [E36] |
| path | path, continuous route, explicit profile/window and reached status | Route preview [E36] |
| walkable_rect | walkable_rect, sampled clearance with declared resolution/profile | Clearance heatmap [E36] |
| render_topdown | render_topdown, named surfaces/observer volumes and overlays | Orthographic capture [E41] |
| render_view | render_view, canonical world metres and roof observer | Perspective capture [E41] |
| archetype_measure_heights | asset_measure_heights, raw mesh and transformed shape bounds separately | Measured height inspector [E42] |
| archetype_set_collision_heights | asset_set_collision_heights, versioned descriptor/dry-run/closure update | Collision height edit [E42] |

Additional required native verbs are placement_scale, placement_batch_transform, placements_remove, map_translate (moves terrain, placements, markers, water and foliage atomically), surface_layer_add/remove, material_set, water_body_add/set/get/list/remove, prefab_open/edit/override/unpack, interior_volume_add/set/remove, roof_link_set and collision_shape_set. The GUI has matching scale/group transform, layer/paint, water, prefab and volume tools. Existing mapedit procedural/scatter/spawn/region/storage verbs continue to work for old maps. Authored mode never turns analytic regeneration into an implicit edit [E4, E5, E16, E46].

Automated command tests compare GUI command invocation with the MCP operation's resulting native document/hash and dirty bounds, including rejected edits and undo/redo. Route tools must report missing nav profile/capture instead of pretending the existing slope/global-water query proves prop clearance. Height measurement/catalog writing remain explicit asset operations, with their own atomic writes and refresh reporting [E24, E32, E36, E42].

## Gap coverage and bounded engine rounds

This table closes every row of the baseline matrix. A supported free transform still requires proof against every newly resolved shape and prefab child [E1 to E29].

| Matrix concern | Contract and exit round |
| --- | --- |
| Catalog/archetypes/footprints | C1/C3, R1/R3 |
| Free XZ/Y/yaw/scale | C1/C3/C5, R3/R5 |
| Edge/corner walls | C3, R3 |
| Doorways/openings | C3/C5, R3/R5 |
| Roofs/planes/height | C2/C5, R2/R5 |
| Indoor and hiding | C2/C5/C8, R5/R8 |
| Underlay/overlay/cuts/feathers | C2/C8, R2/R8 |
| Corner heights/upper derivation | C2, R2 |
| Bounded water | C4, R4 |
| Decks/rails/dry feet | C3/C4, R3/R4 |
| Foliage density/exclusions | C7, R7 |
| Player/NPC/general markers | C6, R6 |
| Prefabs/instance differences | C5, R5 |
| Interactive/Examine/stable ID | C1/C3, R1/R3 plus game policy |
| Reach/walk-up | C3, R3 plus game policy |
| Physics bridge | C2/C3/C4, R3/R4 |
| Nav/habitat | C3/C6, R3/R6 plus game policy |
| Residency/streaming | C1/C3/C8, R8 |
| Netcode/authority | C1/C3, game adoption, no format-owned networking |
| Snapshots/goldens | C8/C9, R8/R11 |

Each round below is one separately reviewed implementation plan and one independently usable engine minor capability release. Contract skeletons may be additive, but no round may claim a later round's complete authoring workflow. All releases reconcile with the pivot's current main, take the next available minor and wait for the owner's tag. Engine rounds may run before game 0.11.0 without changing pivot pins. No numeric engine releases are reserved [E30, E31, G1, G14, G15].

| Round | Scope and schema | Dependencies | Exit proof |
| --- | --- | --- | --- |
| R1 Native document and identity | C1, format 4 and render-free asset seam | Approved R1 plan and reconciled released CellOrigin main | Old analytic maps unchanged, immutable resolver, int64/redo/closure/hash tests |
| R2 Exact authored terrain | C2, format 5, shared triangle compiler and surface commands | R1 | Every legacy cut/height/flag, all plane derivations, spike-point zero discrepancy and paint parity |
| R3 Shared shape and headless world | C3, asset collision payload 1 and GPU-free MapDoc.Physics | R1/R2 | Identical two-head statics/pick/reach/stance, compound doorway, bridge and transformed-solid nav proof |
| R4 Bounded water | C4, format 6 and medium/level tools | R2/R3 | Exact seven-body fixture, seam/deck/feet behavior and independent water levels |
| R5 Free buildings and interiors | C5, format 7 and prefab payload 1 | R1 to R4 | Actual interiors preserved, free building transform and stable children, volume/roof/shadow/door proof |
| R6 General markers | C6, format 8, typed spawn projections | R1/R2/R5 | 38 exact fixture markers, elevated/local/free markers and no fake region discs |
| R7 Native foliage | C7, format 9 and deterministic density commands | R2/R3/R5/R6 | Every density byte and generated sample, rotated building/door exclusions and reload stability |
| R8 Native view and captures | C8, shared Render3D adapter/residency/capture | R2 to R7 | Native scene across storage seams, shadow-only roofs, backend goldens and complete captures |
| R9 Unified MapEditor workflow | Brushes, prefab mode, local snap, overrides and shape diagnostics | R2 to R8 | Real pointer/key authoring of terrain/prop/building/water/foliage, undo/redo and save/reload use shared commands |
| R10 Complete ke-mapedit surface | All 50 mappings and additional native verbs, command parity | R1 to R9 | Registry inventory plus GUI/MCP equivalence, invalid-write refusal and shape-aware route queries |
| R11 Offline importer and fidelity gate | C9, optional migration package and ledger payload 1 | R1 to R10 | Frozen fixture imports deterministically, exhaustive ledger and differential runtime/render report, native boot with no TileWorld dependency |

Engine exit tests run synchronously in the owning plan's area projects. Rendering follows repository backend CI/golden policy. Build/verification is serialized through the shared slot, no stress loops or parallel local builds. This documents-only revision runs document guards, not production builds or full suites [E29, E31, G15].

R1 to R4 execution plans are [native document and identity](../superpowers/plans/2026-10-05-world-authoring-r1-native-document-identity-assets.md), [authored terrain and paint](https://github.com/APKiwiOrg/KhaozEngine/blob/feature/world-authoring/docs/superpowers/plans/2026-10-05-world-authoring-r2-authored-terrain-paint.md), [shared shapes and headless builders](https://github.com/APKiwiOrg/KhaozEngine/blob/feature/world-authoring/docs/superpowers/plans/2026-10-05-world-authoring-r3-shared-shapes-headless-builders.md) and [bounded water and medium](https://github.com/APKiwiOrg/KhaozEngine/blob/feature/world-authoring/docs/superpowers/plans/2026-10-05-world-authoring-r4-bounded-water-medium.md). R1 to R4 are full plans awaiting owner review. R5 to R11 remain drafts refined at their rounds. Specs and the C4 boundary policy are approved. Implementation/round-plan approval is still pending, and R3 geometry/query detail must be explicit at its refinement.

## Risks, mitigations and estimate

| Risk and evidence | Mitigation and gate |
| --- | --- |
| Bilinear field differs from actual floor by 1.677 m [E9, E10, S1] | C2 shared triangle compiler, exact spike-point and terrain parity gates |
| Legacy slope-dependent wall/solid shapes and reach differ [E12, E45, G7] | Import explicit asset variants, report query differences, require owner acceptance of T4 effects |
| Nominal stamps lose placed interior differences [E3, E21, G3] | Membership manifest, stable child bindings and per-instance override ledger |
| Local floors double draw or diverge under transform [E9, E21] | Surface ownership, paint-only decals on canonical floor, explicit support surfaces and triangle parity |
| Water rim disappears or bridges become wet [E11, E18, G4] | Materialize exact domains/heights, no rim recomputation, feet-aware medium and deck proof |
| Manifest normalization/parts/LOD/lights resize or flatten art [E7, G2] | Preserve-source-scale adapter, asset closure and transform/render tests |
| Foliage rerolls or ignores rotated building masks [E19, E20] | Copy bytes/settings, version predicates and compare each generated sample |
| Hash closure, prefab children or windowed residency lose content [E6, E22, E26, E27] | Deterministic resolution, complete closure validation, cross-chunk/ID/tombstone tests |
| Parallel pivot releases or grand-world edits invalidate a frozen plan [E30, G1, G14] | Re-read baseline before each plan, next available releases, re-freeze accepted source before adoption |
| Grimhollow ships a latent TileWorld adapter [G8, G10, G13] | Native-only dependency/source/tool audits and deleted legacy entry points as adoption exit |

**Approved scope estimate, 12 to 18 elapsed weeks** for one serial implementation lane with the shared build slot and timely owner reviews. Engine R1 to R11 is about 8 to 12 weeks, game importer specialization/adoption about 3 to 4, final parity and owner workflow/look review about 1 to 2. The earlier A estimate was 10 to 16 weeks. The added allowance makes free prefab floors/volumes, collision variants, full tool parity and TileWorld deletion explicit. These are scope-based judgments, not measured throughput or promises [E1 to E29, E33 to E46, G2 to G13, S1].

R1/R2 and R3/R5 are the critical technical path. Independent read-only reviews or authored fixture preparation can overlap. Local builds cannot. Review queues, upstream rendering defects or a newly requested CAD/stair/foundation system extend elapsed time. Re-estimate after R2 floor proof and R5 free-building proof. Grimhollow adoption cannot start before both 0.11.0 and accepted grand-world are on main, regardless of engine progress [E31, G1, G15].

## Approval record and remaining gates

The owner answer supplied by the controller on 2026-10-05 is exactly "Approve". It approves both specs, T1 to T9 with revised T4, the rigid prefab v1 boundary and 12 to 18 elapsed-week estimate, and the C4 min-bound-inclusive/max-exclusive rectangle and polygon-edge policy. This is approval of policy and scope, not a quotation of all the detailed wording above.

Approval IDs are OA4 specs, OA5 revised T4, OA6 prefab/estimate and OA7 water boundary in game DECISIONS. The 1 m allowance preserves `MinimumObjectReachHeight`, a minimum vertical target reach-envelope height. It does not define a 1 m action distance. Action range remains existing game policy.

Implementation approval remains pending. R1 is the next owner-reviewed full plan. R2 to R4 reconcile their released prerequisites before review, with explicit R3 interaction geometry/query refinement. R5 to R11 are drafts refined at their rounds. No next engine version is reserved. Start R1 only from reconciled released CellOrigin main, not by merging the historical docs branch.

Policy approval does not waive actual import differences. Each changed target, distance, occlusion, stance and exact water-boundary sample needs a named old/new case and acceptance reference at R11/game import. Physical collider parity is separate and cannot be traded for green reach tests. No manual playtest is needed for this documentation revision.

## Append-only historical approval evidence

Historical T4 proposal before the 2026-10-05 approval, superseded by revised T4 above. This is the old orchestrator proposal, not current policy or an exact owner quotation.

> Each solid placement, including walls, resolves one oriented compound-box or baked collision shape from its asset. Both heads use that shape for reach, picking and walk-up stance. Mesh AABB and catalog footprint are not competing narrow-phase targets [E7, E8, E12, E23, G7]

Reconciliation record 2026-10-05. The controller reports owner approval of the shared interaction envelope with minimum 1 m vertical target reach-envelope height (`MinimumObjectReachHeight`) for low objects, consistent selectable bounds and unchanged physical collision. This supersedes strict physical-only T4 in operative contracts and affected plans. Prefab v1/estimate and C4 boundary policy are approved. Actual query differentials and round plans remain gated. Earlier source measurements and their evidence keys stay historical, without being relabelled as accepted shipped source. No self-recording SHA, package or execution evidence is asserted.

## Late PROGRAM requirements, reconciled 2026-10-05

- The accepted source grows into negative x regions `r_-1_0` through `r_-1_4`, tile x -64 through -1. PROGRAM describes 6x5 regions, 384x320 m and cow pen x -33 through 7. These are late requirements, not a verified shipped inventory. R6/R11 refreeze actual source commits, paths/digests, coordinates, counts and complete markers after both game-main barriers. Old 25-region/5,566-object/103,041-corner counts remain baseline regression evidence only.
- Independently inspected local engine `main`, `origin/main` and `v20.25.0^{commit}` at `b39fb1a3bde9073d9519357b546b138b2c79d957`. `Directory.Build.props` declares 20.25.0 and `ShardedWorldServerConfig` exposes `public Vector2 CellOrigin { get; init; }`. Independent `git ls-remote` also found annotated tag object `5f4c2dcd2a316b7108dacca8e64bf39fc53bd94f`, peeling to that main commit, and the ancestry check exited 0. The controller reports reviewed/released/packed 20.25.0, suite 25,320 passed/0 failed/1,328 skipped and 200 local package files. This lane did not run that suite or inspect packages. Its own evidence is refs/source only. R1 starts from current reconciled released CellOrigin main. Game adoption retains the one-cell grid with origin at world bounds minimum.
- R3/R11 preserve `river_bridge_grand`'s local x -9 through 9 walk surface over its 16x5 deck footprint, river bed -130 cm, `walkSurface` 2.825 and parapet `collisionHeight` 3.825. Preserve all 32 one-edge 1x1 `river_bridge_parapet` Wall pieces. Never clip support to the footprint or replace an edge Wall by a full box. Confirm exact source semantics/digests at refreeze and report the T4 differential separately.
- Downstream game nav bakes must fit 8 MiB after deterministic `gzip -9` and be committed in plain git. R3 provides complete capture and identity hooks. R11 records actual source/nav/profile hashes for G3 rebake. This game budget is not a universal engine limit or existing package proof.
- Technical consistency ruling J2.1, 2026-10-05. Use one shared bounded smoothing contract of 1 to 64 passes, preserving existing legacy support. Released main's `Tools/HeightTools.cs:60` describes that range and `TileEditOps.Heights.cs:69-74` enforces it. Preserve its double-buffered 3x3 prior-pass average, unchanged outside-patch halo and AwayFromZero integer quantization. R2 and R10 use the same range and parity cases. The old R2 four-neighbour/1-to-16 proposal is superseded.

## Spec self-review

The option B contracts, locked building transforms, grid components, rim recomputation and API-only spike adoption have been removed from the target design. All matrix gaps map to contracts/rounds, all 50 verbs map to native commands, schema transitions and package boundaries are explicit, and migration has a native-only boot/deletion gate. The revised spec approvals are recorded above. Remaining gates are round-plan approval, explicit R3 geometry refinement and named import differential acceptance. Coordinate/count evidence stays frozen until the accepted-world importer plan refreshes it [E1 to E46, G1 to G16, S1, S2].

## Historical THROWAWAY spike and measured fidelity

The following is retained evidence from the proposal, not a conversion contract or a new probe. Native C2 to C9 must replace the missing semantics.


Scratch project/source: /tmp/grand-world/world-authoring-spike. No converter or generated map is committed to either repository. Uses assigned engine source APIs and the ke-mapedit library through MapEditSession, QueryService and MutationService. No GUI, pixels, body simulation, nav capture or suite was run [S1, S2, E16, E28].

The converter uses TileObjectPlacement.AnchorPosition and YawRadians, stable ID strings, unchanged Kind and instance tags, explicit Y and scale 1. It maps the player and NPC markers to typed lists and approximates five general markers as 0.01 m tagged discs. It copies the plane-0 corner lattice into one-metre sculpt cells over a zero-height/no-noise biome, then saves/loads monolithic and tiled forms, verifies hashes and edits through the library [S2, E3, E6, E10, E16].

| Measurement | Result | Scratch file and line |
| --- | --- | --- |
| Input hash | 4acbfdf5c6358f2bc7f82a168f626085646a04742219e901df9ffafd708f4039 | measured-results.json:3 |
| Input regions / objects / archetypes | 25 / 5,566 / 121, 113 used | measured-results.json:4 |
| Exact saved ID/kind/transform/instance-tag records | 5,566 of 5,566 | measured-results.json:36 |
| Runtime placements and tool-open/query counts | 5,566 each | measured-results.json:38 and :62 |
| NPC / player / approximated landmark records | 32 / 1 / 5 | measured-results.json:39 |
| Ground corner / interior samples | 103,041 / 102,400 | measured-results.json:42 |
| Maximum document height error | Corners 0 m, interiors 0.00000190735 m | measured-results.json:44 |
| Difference from current movement triangle sampler | Maximum 1.676952362 m at (4.37,-64.61). 33,121 samples exceed 0.00001 m | measured-results.json:46 |
| Source full TileWorld collider count | 10,197, no converted equivalent built | measured-results.json:52 |
| Sculpt blocks / occupied 64 m storage tiles | 121 / 36 | measured-results.json:59 |
| Padded storage bounds | [0,351] x [-320,31], original play bounds [0,320] x [-320,0] | measured-results.json:53 |
| Pre-edit monolithic/tiled hashes | Equal, 452da39f6deb6c45af6a424a3155516c1e1ea131cc1126ad65d65ca5287c894b | measured-results.json:77 |
| Structure/schema/whole-world hash validation | Valid with no errors | measured-results.json:64 |
| Free-transform save/reload | Passed. None object moved (0.23,0.17) m, yaw 0.371 radians, scale 1.137 | measured-results.json:76, Program.cs:84 |
| Monolithic bytes / in-process duration | 3,119,218 bytes / 2.407 seconds, build and slot wait excluded | measured-results.json:79 |

Boundary lattice samples allocate complete 32-cell sculpt blocks, and the validator requires their full extents inside bounds. Hence extra storage tiles/padding. Never widen playable bounds to match this padding. MapDoc TileSize is storage metres, not the old one-metre gameplay tile size [E10, E26, S1].

Corner equality is not collision parity. Document HeightAt is bilinear. TileGroundSampler follows drawn triangles. The imported field reproduces the former, not the latter. It supplies no bridge physics, blocked cells or water medium [E9, E10, E12, S1].

Operative losses in the stock destination include 14 material bindings, 102,400 underlay cells, 2,130 overlays, ten cuts, 660 feather flags, 8,872 blocked/836 indoor/nine reserved bridge flags, seven water bodies/113 rectangles, one 25,921-cell foliage raster, roof plane membership, prefab authoring and catalog collision/interactive/LOD semantics. Raw document ID/tags survive, but runtime PropPlacement requires pairing to preserve them [S1, E1, E2, E6, E22].

Prefab stamps are expanded tiles/objects. TileObject does not retain the source prefab relationship. Preserve actual placed differences and reconstruct grouping explicitly. The spike converts placed meshes, not the seven reusable definitions [E3, E21, G2, G3].

All builds/runs used the serial wrapper. The first build hit macOS /tmp versus /private/tmp path resolution. Canonical-path build passed. A later measurement run intentionally refused an unindexed write over the existing tiled directory. The final measurement used fresh output-ground and exited 0. That refusal is a writer guard, not a fidelity failure [E32, S1, S2].

Historical reproduction command at the proposal snapshot, retained as evidence only. Its scratch/source paths and old wrapper are not current execution instructions:

~~~bash
/tmp/grand-world/slot-retry.sh authoring-design-probe /tmp/grand-world/world-authoring-spike/reproduce.log -- dotnet run --project /private/tmp/grand-world/world-authoring-spike/THROWAWAY.csproj -c Release -- /Users/antonio/Grimhollow/.worktrees/gw-authoring-design/assets/worlds/hollowmere /private/tmp/grand-world/world-authoring-spike/reproduce-output
~~~

Spike Program.cs SHA-256: 2e0de8010dccea0f9039051b4665e31294d18e360f59063aac21c492c85a7be1.
Measured-results.json SHA-256: 77fb3aec74b6e000e88a02a4d6e40965623e74792978732c421c803a7d5586d4.
These identify the probe code and result report, not the world. Frozen world fixtures use game evidence commit 74f57ee22652bd18234b4479faba4f1898a17047 and their own recorded input digests.
This measured table is the durable evidence. Scratch files may later disappear [S1, S2].

## Evidence register


Engine paths are relative to the pinned engine baseline, game paths to the pinned game baseline. Line numbers identify the contract and nearby implementation. Every bracketed claim above resolves here.

| Key | File and line | Evidence |
| --- | --- | --- |
| E1 | KhaozEngine.MapDoc/MapDocument.cs:19, :32, :167, :182, :209 | Root, terrain, placement, spawn and region DTOs |
| E2 | KhaozEngine.MapDoc/mapdoc.schema.json:7, :171 | Closed root/placement schema |
| E3 | KhaozEngine.TileWorld/TileObject.cs:9, KhaozEngine.TileWorld/TileWorldCatalogs.cs:55, KhaozEngine.TileWorld/TileObjectPlacement.cs:28, KhaozEngine.TileWorld/TileWorldSpace.cs:15 | Identity, archetypes, transform, negative Z |
| E4 | KhaozEngine.MapEditor/EditorTool.cs:19, :38, :44, KhaozEngine.MapEditor/MapEditorOptions.cs:28 | Placement/sculpt/gizmo and host hooks |
| E5 | KhaozEngine.MapEdit.Tool/MutationService.cs:122, :138, :146 | Move/yaw/scale shared commands |
| E6 | KhaozEngine.MapDoc/MapRuntime.cs:175, :216, KhaozEngine.Terrain/PropScatter.cs:11 | Runtime transform and Kind as runtime Id |
| E7 | KhaozEngine.Render3D/Models/AssetManifest.cs:15 | Collider, surfaces, proxy, .coll, LOD, normalization |
| E8 | KhaozEngine.Terrain/PropColliders.cs:22, KhaozEngine.Terrain/PropSurfaces.cs:17, KhaozEngine.Terrain.Render3D/ChunkStatics.cs:23 | Existing collision/surface/static builders |
| E9 | KhaozEngine.TileWorld/TileWorldDocument.Heights.cs:65, KhaozEngine.TileWorld/TileGroundTriangles.cs:102, KhaozEngine.TileWorld.Physics/TileGroundSampler.cs:71 | Bilinear document and triangle runtime floor |
| E10 | KhaozEngine.MapDoc/MapTerrainOverrides.cs:8, KhaozEngine.Terrain/TerrainSculpt.cs:75, KhaozEngine.MapDoc/MapDocumentValidator.cs:133 | Sculpt lattice, interpolation and extents |
| E11 | KhaozEngine.TileWorld/TileWaterBodies.cs:14, :52 | Underlay water and rim-based clipped bodies |
| E12 | KhaozEngine.TileWorld.Physics/TileWorldColliders.cs:52, KhaozEngine.TileWorld.Physics/TileColliderBuilder.Objects.cs:22, KhaozEngine.TileWorld/TileLayers.cs:8 | Ground, wall, blocked, solid and deck semantics |
| E13 | KhaozEngine.TileWorld.Render3D/TileWorldView.Roofs.cs:27 | Connected interior/plane hiding, hidden shadows |
| E14 | KhaozEngine.MapDoc/MapRegionSet.cs:9, KhaozEngine.MapDoc/MapShapeDoc.cs:8 | Resolved disc/rect/polygon tagged areas |
| E15 | KhaozEngine.Terrain.Render3D/TerrainSplatWeights.cs:5, KhaozEngine.Terrain.Render3D/TerrainChunkBuilder.cs:27 | Five channels and presentation-only splat hook |
| E16 | KhaozEngine.MapEdit.Tool/MapEditSession.cs:43, :120, :292, KhaozEngine.MapEdit.Tool/README.md:136 | Load, save, validation and tool families |
| E17 | KhaozEngine.MapEditor/ViewportWorld.cs:312, KhaozEngine.MapDoc/MapDocument.cs:69 | One viewport water plane/global level |
| E18 | KhaozEngine.Terrain.Render3D/TerrainChunkCollision.cs:41, KhaozEngine.TileWorld.Physics/TileMediumSampler.cs:43 | Terrain statics and feet-aware medium |
| E19 | KhaozEngine.TileWorld.Render3D/TileFoliageSurface.cs:31, :97, :258 | Density and exclusion rules |
| E20 | KhaozEngine.Terrain/GroundCoverDistribution.cs:13, :51 | Shared sampled-density ground-cover builder |
| E21 | KhaozEngine.TileWorld/TilePrefab.cs:7, :113 | Full grid prefab payload/extraction |
| E22 | KhaozEngine.MapEditor/ViewportWorld.cs:716 | Stable edit ID paired back to runtime prop |
| E23 | KhaozEngine.Movement/ReachGeometry.cs:11 | Generic reach geometry |
| E24 | KhaozEngine.MapEdit.Tool/QueryService.cs:28 | Slope/global-water-only walkability query |
| E25 | KhaozEngine.Movement/PhysicsNavBake.cs:37 | Complete physics capture and optional water |
| E26 | KhaozEngine.MapDoc/MapDocumentFile.cs:105, :190, :234, KhaozEngine.MapDoc/MapDocument.cs:21, KhaozEngine.MapDoc/MapDocumentHash.cs:11 | Storage forms, storage edge and world hash |
| E27 | KhaozEngine.MapDoc/MapTileResidency.cs:14, :40, KhaozEngine.MapDoc/MapResidencyGate.cs:9 | Immutable residency and chunk gate |
| E28 | KhaozEngine.MapEdit.Tool/RenderService.cs:13, KhaozEngine.TileWorld.Render3D/TileWorldSnapshot.cs:8 | Capture path and missing-manifest limitation |
| E29 | KhaozEngine.Render.Tests/TileWorld/GoldenTileWorldTests.cs:17, KhaozEngine.Render.Tests/Terrain/SplatTerrainGoldenTests.cs:1 | Existing golden families |
| E30 | Directory.Build.props:25 | Staged 20.24.0 |
| E31 | docs/CONTRIBUTOR-RULES.md:109, AGENTS.md:97 | Package/release policy |
| E32 | KhaozEngine.MapDoc/MapTiledFile.Save.cs:43 | Refuse unindexed overwrite of tiled directory |
| G1 | Grimhollow docs/superpowers/specs/2026-10-01-continuous-movement-design.md:31, :39 | O3 substrate and O11 integration boundary |
| G2 | Grimhollow docs/architecture/ARCHITECTURE.md:118 | World files, counts and seven prefabs |
| G3 | Grimhollow Grimhollow.Tests/World/HollowmereInteriorTests.cs:41, :68, :93 | Raised roofs and exact prefab/live interiors |
| G4 | Grimhollow Grimhollow.Tests/World/HollowmereWorldTests.cs:145, assets/catalogs/archetypes.json:114 | Bridge deck and reserved flags |
| G5 | Grimhollow Grimhollow.Shared/HollowmereSpawn.cs:24 | Marker spawn lookup |
| G6 | Grimhollow docs/design/GAMEPLAY-RULINGS.md:21, Grimhollow.Tests/Client/VillagePropExamineTests.cs:23 | Examine/no-examine policy |
| G7 | Grimhollow Grimhollow.Shared/Navigation/GrimhollowReachShapes.cs:40, Grimhollow.Core/Client/Continuous/ContinuousWorldView.WalkUp.cs:238 | Shared reach target and client stance |
| G8 | Grimhollow Grimhollow.Shared/HollowmerePhysics.cs:27, :42 | Shared statics, complete/movement query split |
| G9 | Grimhollow Grimhollow.Shared/Navigation/HollowmereNavigation.cs:19, :125, Grimhollow.Shared/Navigation/HollowmereAreas.cs:32 | Capture windows/profiles and habitats |
| G10 | Grimhollow Grimhollow.Core/World/HollowmereWorld.cs:71, :97, :109 | Source, view and residency |
| G11 | Grimhollow Grimhollow.Shared/GrimhollowContinuousProtocol.cs:71, Grimhollow.Shared/GrimhollowCatalogHash.cs:48 | Content gate and catalog hash |
| G12 | Grimhollow Grimhollow.Shared/Skilling/GrimhollowSkillNodes.cs:32, Grimhollow.Tests/Client/ContinuousNodeVisualTests.cs:17 | Node identity/spent visual |
| G13 | Grimhollow tools/SnapshotTool/Program.cs:63, tools/SnapshotTool/TerrainShots.cs:19 | World render captures |
| G14 | Grimhollow Directory.Build.props:16, :34 | Frozen version/pin |
| G15 | Grimhollow AGENTS.md:91 | Engine adoption and tool/feed ritual |
| G16 | Grimhollow docs/DEVELOPMENT.md:228, :260 | TAA Native and golden procedure |
| R1 | Ruinborne.Editor/Program.cs:145, Ruinborne.Editor/EditorPaths.cs:69 | Thin editor host and real paths/manifests |
| S1 | /tmp/grand-world/world-authoring-spike/output-ground/measured-results.json:3 | Final report, SHA-256 above |
| S2 | /tmp/grand-world/world-authoring-spike/Program.cs:1, :22, :53, :65, :80 | Throwaway conversion, samples and tool mutation |
| E33 | KhaozEngine.TileEdit.Tool/Tools/TileTools.cs:19, :27, :42, :59 | All cell/layer verbs |
| E34 | KhaozEngine.TileEdit.Tool/Tools/MarkerTools.cs:17, :27, :33 | Marker verbs and uniqueness |
| E35 | KhaozEngine.TileEdit.Tool/Tools/WorldTools.cs:25, :31, :42, :47, :52, :57, :63, :70, :77, :82, :88 | Lifecycle, catalogs, storage and history |
| E36 | KhaozEngine.TileEdit.Tool/Tools/CollisionTools.cs:18, :26, :35, :47 | Collision, walkability and route queries |
| E37 | KhaozEngine.TileEdit.Tool/Tools/HeightTools.cs:19, :30, :42, :53, :64, :74 | Six height verbs and array conventions |
| E38 | KhaozEngine.TileEdit.Tool/Tools/ObjectTools.cs:20, :31, :40, :47, :53, :60, :66, :76, :83, :96 | Ten object verbs |
| E39 | KhaozEngine.TileEdit.Tool/Tools/FoliageTools.cs:11, :16, :21, :29, :39 | Five density verbs and positive-Z rows |
| E40 | KhaozEngine.TileEdit.Tool/Tools/PrefabTools.cs:19, :34, :44 | Prefab extraction/place/list and redo allocation |
| E41 | KhaozEngine.TileEdit.Tool/Tools/RenderTools.cs:28, :42 | Topdown/view captures |
| E42 | KhaozEngine.TileEdit.Tool/Tools/ArchetypeHeightTools.cs:18, :24 | Measurement and catalog write/refresh behavior |
| E43 | KhaozEngine.MapDoc/MapDocumentFile.cs:83, :172, :176 | Current format 3 and contiguous migrations |
| E44 | KhaozEngine.TileWorld/TileLayers.cs:8, :25, :39 | Four cut enums, flags, cm height and plane derivation |
| E45 | KhaozEngine.TileWorld.Physics/TileColliderBuilder.Objects.cs:22, :45, :73, :109, :137 | Edge/corner, solid span, deck and terrain-relative collision |
| E46 | KhaozEngine.MapEdit.Tool/Tools/DocumentTools.cs:13, :34, :60, KhaozEngine.MapEdit.Tool/Tools/MutationTools.cs:23, :50, :147, :403, :437, KhaozEngine.MapEdit.Tool/Tools/QueryTools.cs:15, :21, KhaozEngine.MapEdit.Tool/Tools/RenderTools.cs:19 | Existing native command families |

Companion game specification: [Grimhollow option A](https://github.com/APKiwiOrg/Grimhollow/blob/feature/world-authoring/docs/superpowers/specs/2026-10-05-world-authoring-migration-design.md). Integration and production implementation stay with the owning orchestrator after written review.


## R1 execution authority, 2026-10-05

OA8 separately approves the six-task R1 plan and serial Astra medium implementation with fresh
Sol xhigh reviews. Earlier pending-R1 statements are historical. Later rounds and all tags remain gated.
The execution baseline is released b39fb1a3b, with only this spec and the approved R1 plan carried from
planning commit c89f260d2. Other round drafts remain on feature/world-authoring for later refinement.
