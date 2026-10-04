# World authoring migration for Grimhollow

Status: **Proposal for owner review. No production implementation approved.**

Engine baseline d4dd8d918b6c6639ea63291ad818aa829c02074a, branch feature/world-authoring-design.
Game baseline 74f57ee22652bd18234b4479faba4f1898a17047, branch feature/gw-authoring-design, forked from grand-world.
Ruinborne was inspected read-only as a consumer example.

## Recommendation and intent

Choose **B, a MapDoc-led hybrid, 44/60**. Make MapEditor and ke-mapedit the primary composition tools. Retain exact grid terrain and building/interior content as referenced components behind an engine-owned adapter. Move loose scenery to native free placements. Add transformed Solid placements only after collider, picking and reach parity. Native free transforms already exist. The compatibility layer does not [E1, E2, E4, E5, E12, E13].

This is a selective format migration. TileWorld remains a component format. If the owner's requirement is complete removal of every TileWorld file and API, choose A instead. C is the cheapest route to visual variation, but keeps ke-tileedit. These are recommendations, not new owner rulings [E3, E4, G1].

The dispatch requests the tool swap because cliffs and trees repeat on whole-tile placements. Current TileObject coordinates are integers, rotation is quarter turns and LocalToWorld uses scale 1. Archetype yaw offsets affect every instance, not individual variation [E3].

The task is investigation, these documents and a throwaway probe. No source world, game rule, engine pin or production API changes. The movement pivot's O3 retained TileWorld. O11 stages its integration as 0.11.0. This proposal must be separately approved, not silently amend that program [G1].

The probe preserved all **5,566 placement records** and the ground corner lattice. It did **not** preserve a playable world. The imported field differs from the current movement floor by up to **1.676952 m**. Matching parse counts and document heights cannot replace physics, rendering and interaction parity [S1, E9, E10].

## Evidence and current content

Evidence keys resolve to exact file and line references in the register below. Supported means a current API directly represents the capability. Partial means reusable primitives exist but Hollowmere parity needs integration or new semantics. Missing means the current closed document/editor model has no corresponding contract. Proposed contracts, scores and estimates are explicitly design judgments.

The source world has 25 regions, each 64x64 one-metre tiles, covering 320x320 m. It has four planes with 4.5 m plane height, 14 ground materials, 121 archetypes, 113 used kinds and 5,566 objects. Seven reusable prefab definitions and nine actual building interiors exist [G2, G3, S1].

Measured placements comprise 4,305 None, 842 Solid, 381 Wall and 38 WallCorner objects. There are 578 interactive objects and ten roofs on nonzero planes. The world has 38 markers, one 161x161 foliage density layer, 836 Indoors cells, 8,872 Blocked cells, 2,130 overlay cells, ten shaped overlays and 660 FeatherOverlay cells [S1].

Seven region-clipped water bodies share surface Y -0.37 m and contain 113 rectangles. Clipping is part of the existing rim-based flat-surface rule. The river_bridge archetype supplies its walk surface. The nine Bridge flags alone do not implement its deck collision [S1, E11, E12, G4].

## Capability matrix

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

## Options, costs and trade-offs

Estimates are judgments for a serial implementation lane with one local build/test slot, released engine adoption and owner reviews. They are not measured throughput or delivery promises. The matrix, especially E2, E9, E12, E13, E19, E21 and G7 to G13, defines their scope.

### A. Full native MapDoc swap

**Engine.** Add versioned authored ground paint/topology/holes, bounded water, interior/roof relationships, prefab references/overrides, resolved stable IDs, shared collision and interaction geometry, residency and matching GUI/MCP tools. Extend engine abstractions rather than encode game rules in magic tags [E1, E2, E9, E12, E13, E19, E21].

**Game and tests.** Replace world load/view, physics/medium/nav inputs, spawn/habitat readers, picking, reach, node positions, hashes, asset publication and world snapshots. Broad content/fixture migration and new cross-format physics/render tests. Preserve doorway, river and coordinate assertions rather than weaken them because types changed [G3 to G13].

**Content.** Offline validating importer accounts for every ID, cell, marker, prefab, roof and foliage sample. Exact initial geometry. Re-authoring requires a separate owner decision [S1, G2, G3].

**Risk/payoff.** Best format consistency and unrestricted future authoring. Highest risk of floor, wall, roof, water and reach changes. The probe disproves an API-only conversion [S1].

**Estimate.** 10 to 16 weeks. Engine 6 to 9, game/importer 2 to 4, integration/review 2 to 3. An unrestricted multi-storey building editor could exceed this scope.

### B. MapDoc-led hybrid, recommended

**Engine.** Typed component references, stable resolved identities and an optional bridge package depending on MapDoc and TileWorld. Core MapDoc does not acquire a TileWorld dependency. Compose exact grid terrain and editable grid prefab assets with native placements. Share bounds, picking, statics, medium and residency across game/editor, with component preview/edit extension points in MapEditor/ke-mapedit [E1, E2, E4, E6, E12, E21, E27].

**Game and tests.** One shared resolved-world facade. Keep game rules, localization and archetype IDs. Start with an exact copied grid component, then extract loose placements by stable ID with no duplicated ownership. Retain grid component tests and adapt fixtures/consumers at the facade. Add composition/transform/parity tests and targeted pictures [G3 to G13].

**Content.** Preserve every terrain layer and all nine actual building interiors. Keep the seven editable prefab definitions. Do not replace actual buildings with their nominal stamps and lose differences. Root references hash the component closure [G2, G3, E21, E26].

**Risk/payoff.** Two component formats and a real composition seam. Double draws/statics, stale referenced hashes, dangling IDs and editor/runtime picking disagreement are the main risks. Initially constrain buildings to scale 1 and grid-compatible transforms. Free solids require shared physical/reach geometry [E3, E8, G7].

**Estimate.** 6 to 9 weeks. Engine schema/bridge/editor 3 to 5, game facade/importer 2 to 3, parity/review 1. A loose-scenery demonstration is about 1 to 2 weeks after approval, not a complete swap.

### C. Extend TileWorld free transforms, keeping ke-tileedit

**Engine.** Backward-compatible per-instance yaw, uniform scale and sub-tile offset. Defaults preserve current transforms and hashes. Add validation, undo and tool verbs. Centralize effective transform across renderer, raycast, shadows, camera blockers, LOD, foliage exclusions and physics [E3, E12, E19].

**Stages.** None scenery first, still respecting Examine and roofs. Solid next, with oriented footprint-derived colliders and matching reach. Keep Wall, WallCorner, Diagonal, door topology and grid prefab stamping constrained until explicitly designed. Rotated solids must not become whole-tile raster blockers [E3, E12, G6, G7].

**Game, content and tests.** Released engine adoption, transformed bounds/reach/stance and scenery pass tools. Content does not move at defaults. Add transforms only to selected objects, preserving IDs/footprints. Concentrated schema/default/hash/physics/pick/reach tests, deliberate world hash and picture updates. Existing terrain, water and building proofs remain [G3 to G13].

**Risk/payoff.** Cheapest visual variation, but dual grid/physical geometry in a grid-oriented format. A visual-only transform on Solid is wrong. Primary editor remains ke-tileedit, only partly meeting the tool goal [E3, E12, G1].

**Estimate.** 2 to 4 weeks. None support 0.5 to 1, Solid/reach 1.5 to 3. Free walls/buildings excluded.

### D. Status quo

No migration. Keep content, tests and tools. Mesh variants and hand dressing can reduce repetition but cannot supply free per-instance transforms. Lowest immediate cost and risk, little progress on the requested workflow. Migration estimate 0 weeks, dressing work excluded [E3, G1, G2].

### Scores

Higher is always better. Risk means safety, cost means affordability, test churn means stability. Equal weights, maximum 60. Scores are scope-based design judgments, not measured performance.

| Option | Goal fit | Risk safety | Cost affordability | Test stability | Engine reuse | Authoring ergonomics | Total |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| A. Full native swap | 10 | 3 | 2 | 2 | 9 | 9 | **35** |
| B. MapDoc-led hybrid | 9 | 6 | 5 | 6 | 9 | 9 | **44** |
| C. TileWorld transforms | 5 | 8 | 8 | 8 | 6 | 6 | **41** |
| D. Status quo | 1 | 9 | 10 | 10 | 2 | 2 | **34** |

B changes the main workflow while preserving the grid features that dominate A's cost. If tool replacement is optional, C wins on affordability. If TileWorld retirement is mandatory, A wins on fit [E1 to E5, E9 to E13, E19, E21, G1].

## Proposed hybrid contracts

These are proposed contracts, not existing APIs.

1. **Composition root.** Versioned MapDoc contains native placements and typed content references. Grid component documents remain canonical assets, not generated duplicate authoring copies. References carry digests. Verify/save refuses missing, cyclic, stale or unsupported components. The optional bridge keeps core MapDoc independent [E2, E26].
2. **Single ownership.** Begin with the exact current world as a grid component. Extract loose objects by stable ID, removing their grid copies in the same validated transaction. A ledger maps original region/plane/archetype/transform to the new placement. No double draws/colliders. Publish the complete reference closure [E3, E6, E26, G11].
3. **Shared resolution.** Both heads consume stable identity, archetype, instance/catalog tags, transform, bounds, collision/reach geometry and roof/interior relationships. Preserve numeric authored IDs at game boundaries. Allocate new IDs collision-free. Do not hash strings to longs and assume uniqueness [E1, E6, E22, G7, G12].
4. **Exact terrain first.** Preserve all grid layers, flags, four-plane height derivation, topology, water and foliage in the grid component. MapEditor previews it through the adapter. Its analytic field must not create a second visible/collidable floor. Ground edits route through the existing grid command layer until a separately approved lossless native surface format exists [E9 to E13, E19, E21, S1].
5. **Physics and reach.** Reuse existing grid triangles, walls/corners, blocked flags and deck surfaces. Keep the game's wading filter. Native solids use a shared transformed footprint/height definition for both collider and reach. Never mesh-AABB reach on one head and catalog reach on the other [E8, E12, G7, G8].
6. **Building instances.** Preserve the seven definitions and nine actual interiors. Keep initial local geometry, roof height and hiding. Lock imported buildings to scale 1 and grid-compatible translation/quarter turns. Free building transforms are additional scope [E3, E13, E21, G3].
7. **Storage versus simulation.** Start with 64 m document tiles, using world-space negative Z. Residency accounts for geometry crossing tile boundaries, oversized props and referenced prefab bounds. Server complete physics cannot depend on client view distance. Keep storage padding distinct from playable bounds [E3, E25 to E27, G8, G9, S1].

Ruinborne demonstrates a thin game-owned stock editor head with working-tree paths, manifests and game spawn choices. It does not demonstrate Hollowmere's painted floors, interiors, roof policy or game reach. Borrow its hosting pattern, not its gameplay/content [R1].

## Phases, engine releases and the 0.11.0 barrier

The engine baseline stages 20.24.0. This frozen game lane pins 20.23.0 [E30, G14]. Do not reserve release numbers while the pivot runs. Each approved package batch takes the next available release after concurrent pivot work. Docs and this spike do not bump versions. Later releases/adoption follow repository rules [E31, G15].

| Phase / release batch | Engine work and exit proof | Game work | Timing |
| --- | --- | --- | --- |
| 0. Morning decision | Approve format boundary/preservation, then write a separate implementation plan | Record revised authoring ruling | Proposal now, no production work here |
| R1. Composition foundations, next available minor | Typed refs, schema migration/hash closure, stable resolved IDs, headless grid adapter. Tests for stale/missing/cyclic refs, collisions and exact/default roundtrips | Facade/asset mapping design, copied fixture import and no-loss ledger | After approval, may start before 0.11.0. Optional APIs, no pivot repin |
| R2. Primary map authoring, following capability release | Component preview/select/save/extract, free None placements, shared picking/residency, exact ground/water/roof draw. Headless parity and captures | Thin Grimhollow.Editor host, copied-world scenery workflow, Examine/LOD/bounds proof | Independent tooling can start before 0.11.0. Shipped world and runtime held |
| R3. Solid geometry and grid component tools, following capability release | Shared transformed Solid collider/reach, embedded grid/prefab editing, MCP parity, mixed snapshots and exact sampler/nav proofs | Released-pin adoption, shared boot/view/reach adapter replacement, actual accepted world import, ID state preservation, nav/cache/hash and SnapshotTool updates | Only after 0.11.0 lands and current main is reconciled into a new migration branch |
| G1. Game adoption batch | Parity fixes released normally, then repin | Build/test/format, no-loss ledger, targeted graphics and owner playtest. Land after workflow/look acceptance | Separate post-0.11.0 batch, grand-world look approval also required |

R2 is an authoring preview milestone, not a production boot proof. New fields absent/default must keep other consumers working. Split R3 into releases if Solid and embedded grid editing cannot be proved together. Do not make this program a new 0.11.0 dependency [G1, E2, E31].

## Acceptance requirements

- Every original object ID appears once with the same archetype, initial transform, tags, state key, roof membership and interaction role. All 38 marker roles/world points survive. General landmarks need an actual marker adapter, not tiny probe discs [E1, E3, E6, G5 to G7, G12, S1].
- No-edit import preserves ground layers/flags/topology, four-plane derivation, foliage density/settings, body-based water and bridge deck. Verify the grid payload before changing its storage [E9 to E13, E19, E21, S1].
- Compare triangles/normals, movement sampler, complete statics and filtered movement view. Capture identical nav windows/profiles. Prove spawn-to-bank, adjacent wall refusal, cottage door, bridge deck, wading, hill, cow pen and duck habitat [E12, E18, E25, G8, G9].
- Hash component closure and game catalogs. Changed referenced geometry/colliders/nav sources must refuse stale peers/caches. MapDocumentHash alone cannot cover omitted files or catalogs [E26, G11].
- Keep existing engine tile/terrain goldens. Add mixed composition images through the renderer shared by game and snapshots, including interiors, water/bridge, feathers, ridge and trees at TAA Native. Omitted manifests producing no props is a failure [E28, E29, G13, G16].
- New transforms agree across mesh, pick, shadow, camera block, collider, reach, action stance and spent form. None scenery still has Examine unless an explicit no-examine tag applies [E3, E8, E19, G6, G7, G12].

## THROWAWAY spike and measured fidelity

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

Reproduce once into a fresh directory while scratch exists:

~~~bash
/tmp/grand-world/slot-retry.sh authoring-design-probe /tmp/grand-world/world-authoring-spike/reproduce.log -- dotnet run --project /private/tmp/grand-world/world-authoring-spike/THROWAWAY.csproj -c Release -- /Users/antonio/Grimhollow/.worktrees/gw-authoring-design/assets/worlds/hollowmere /private/tmp/grand-world/world-authoring-spike/reproduce-output
~~~

Source SHA-256: 2e0de8010dccea0f9039051b4665e31294d18e360f59063aac21c492c85a7be1.
Result SHA-256: 77fb3aec74b6e000e88a02a4d6e40965623e74792978732c421c803a7d5586d4.
This measured table is the durable evidence. Scratch files may later disappear [S1, S2].

## Owner decisions

1. Approve B's retained grid components, choose A for total TileWorld retirement, or C if transform variation is the whole goal.
2. Accept exact grid terrain behind the main editor adapter, or include native terrain/paint replacement now.
3. Keep editable grid prefab definitions and initially locked building transforms, or fund free buildings/interior topology.
4. Approve None first, then shared Solid geometry. Interactive transforms wait for reach, stance and spent-form parity.
5. Approve opt-in engine work before 0.11.0 with game adoption after pivot/grand-world acceptance.
6. Accept exact initial fidelity as the default. Re-authoring, Examine removal, terrain feel and collision changes require explicit decisions.

Silence is not approval. Review both proposals before approving a separate implementation plan.

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

Companion game specification: APKiwiOrg/Grimhollow branch feature/gw-authoring-design, docs/superpowers/specs/2026-10-05-world-authoring-migration-design.md. Integration, releases and production implementation stay with the owning orchestrator after review.
