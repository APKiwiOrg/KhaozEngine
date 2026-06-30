# Collision-proxy bake pipeline (design)

Date: 2026-06-30
Status: approved, ready for an implementation plan
Engine target: 8.11.0 (additive minor)
Roadmap entry retired by this work: "Collision-proxy bake pipeline (structural, unscheduled)" (Physics, item 2)

## Problem

Buildings collide as their FULL one-sided render mesh. Every wall, eave, dormer AND every substantial interior
object (anvil, forge, workbench, beams, stairs) is a collision triangle. A 0.4 m-radius capsule navigating that
cluttered detail wedges in tight concave pockets and balances/slips off small details. Measured 2026-06-30:
~13/125 detail stand-spots inside the blacksmith trap the capsule so it can neither walk nor jump out.

The 8.8.1 swept resolver guarantees the capsule never FREEZES (gravity-authoritative wall slide + downward
ray-fan support gate), but it cannot make a cluttered detailed interior cleanly navigable. That needs simpler
collision GEOMETRY, not more resolver logic.

### Why the obvious shortcuts were ruled out (2026-06-30 investigation, do not redo)

- **Two-sided building meshes** depenetrate the WRONG way (eject the capsule THROUGH the wall at the mid-plane)
  and do nothing for the `t=0` zero-normal sweep degeneracy. Probe-verified.
- **Convex decomposition / voxelization** structurally removes the pin but introduces WORSE failures: interior
  wall-tunnel traps in a hollow building with no doorway, stair aliasing, dormer fall-through, a ~1800-static
  broadphase that multiplies exact-tie contacts, and a hard `ScaleShape` crash on the new shape kind.
- **Naive convex hull per building** (an earlier playtest) smoothed away the stairs and ledges.

## Core insight

Every ruled-out failure shares one root: the collision geometry is non-convex, thin, or one-sided. That IS the
pin/wedge/freeze problem class:

- one-sided thin faces -> `ComputePenetration` reports nothing, sweeps return `t=0` zero-normal degeneracies
- concave pockets (wall+eave, or a capsule wedged between an anvil's body and base) -> conflicting contacts trap
- two-sided meshes -> eject through the wall at the mid-plane
- decomposition -> many statics, interior tunnel traps, stair aliasing

The clean structural fix is to make every collision solid CONVEX. Each convex solid has a unique shortest-exit
MTV, so `ComputePenetration` always ejects OUTWARD, sweeps return clean normals, and there are no concave pockets.
That is precisely the property that already makes rocks and tree-trunk hulls trap-proof.

A building proxy therefore becomes a `CompoundShape` of convex children (boxes / wedges / hulls), each at a local
pose. A capsule STANDS ON a box top (flat -> walkable support found by the existing downward sweep) and BUMPS INTO
a side (clean tangential slide). Both work by construction, with no wedge.

This composes with the 8.8.1 invariant rather than replacing it: the never-freeze gravity gate is untouched. And a
compound is ONE static with N children (a building is ~10-25 children), so the decomposition approach's
~1800-static broadphase explosion does not arise.

## Goal metric

Scan jump / walk / stand spots around AND inside the real building proxies and find ~0 wedges (a spot where the
capsule can neither walk nor jump out), while stairs climb and floors / ledges / furniture-tops remain standable.

## Decisions (from brainstorming, all confirmed)

1. **Production = authored proxy GLB.** One simplified proxy mesh per building, modelled in Blender as separate
   convex blocks. Auto-simplification and decomposition are ruled out (see above). The convex pieces are authored,
   not auto-derived.
2. **Interiors stay fully navigable.** Floor, stairs, and standable furniture are preserved. Substantial objects
   (anvil, forge, workbench, counters, crates, pillars) are KEPT as convex pieces the player can stand on and bump
   into. Only thin one-sided decoration with no standable volume is dropped: roof eaves, awnings, dormers, soffits,
   trim. Those are the exact source of the angled-normal pockets and the balance-on-detail slips.
3. **Rollout = all 7 Ruinborne buildings now.** Author all 7 proxies, re-bake their `.coll`, adopt the 8.11.0 pin,
   run the scan metric on all 7, hand back for the alpha deploy.
4. **Box kind included** in the format (near-free, the type + Bepu factory already exist, opens a future
   manifest-primitive path) even though the V1 auto-bake emits compound-of-hulls.
5. **Public engine scale helper** promoted (retires the `RuinbornePhysics.ScaleShape` / `ChunkStatics.ScaleShape`
   mirror the code already flagged as wanting).

## Architecture

### A. Representation: `CompoundShape` of convex children

A building's `.coll` is a `CompoundShape` whose children are convex solids (`ConvexHullShape`, optionally
`BoxShape`), each at a local `Pose`. The Bepu backend already builds this: `ShapeFactory.AddCompound` recurses and
`AddConvexHull` / `AddBox` exist and work today. No backend change is needed.

- Wall segments are solid boxes (or hulls). Door / window openings are GAPS BETWEEN boxes, never holes in a box.
- Stairs are a single explicit ramp WEDGE object (convex + standable).
- The floor is a thin slab.
- Standable furniture (anvil, forge, workbench, ...) is one convex block each (1-2 boxes / hulls per item).
- Thin decoration (eaves, dormers, awnings, trim) is not authored, so it generates no collider.

### B. Authoring workflow (per building, via the Blender MCP)

1. Import the render `.glb` into Blender in its raw frame.
2. Model the proxy as separate convex blocks ON TOP of it, one Blender OBJECT per collision piece. Stairs become a
   single wedge object.
3. Delete the render mesh and export `<id>_collision.glb` containing the proxy objects only, still in the render
   glb's RAW coordinate frame.

Authoring in the render mesh's raw frame is deliberate. The bake applies the RENDER MESH's own normalization
transform (scale-to-`heightMeters`, drop-base-to-0, recenter-XZ) to the proxy, so the proxy overlays the visual
building exactly, independent of the proxy's own bounding box. No alignment guesswork and no per-building offset
tuning.

### C. Bake path

- **`GltfLoader.LoadGroups(path) -> IReadOnlyList<GltfMesh>`** (new): one group per logical glTF node-with-mesh,
  world-baked, in logical-node order (deterministic). Object boundaries are preserved; the existing `Load`
  flattens them into one triangle soup, which is why a separate grouping load is needed.
- **`PropCollisionBake.BakeProxy(GltfMesh renderRaw, IReadOnlyList<GltfMesh> proxyGroups) -> CompoundShape`**
  (new): derive the normalization transform (scale, baseY, cx, cz) from the RAW render mesh, apply that SAME
  transform to every proxy group, then `HullFromPoints` each normalized group (already deterministic) into one
  `ConvexHullShape` child at `Pose.Identity` (each hull carries its own position in its points, mirroring how
  `BakeConvexHull` already works). Child order = node order, so the bake is byte-reproducible.
- **`Bake(mesh)` is unchanged** for trees, rocks, and buildings WITHOUT a proxy. The proxy path is opt-in: a
  building with no authored proxy bakes exactly as today. This whole feature is therefore additive, with no
  behaviour change for anything that does not adopt a proxy.

The `.surf` walkable-top heightmap is still baked from the RENDER mesh, unchanged (it is not in the movement
stack; only the `.coll` drives collision). Out of scope to touch.

### D. Format extension (`PropCollisionFormat`)

Append two stable wire kinds (never renumber existing values 1/2/3):

- `KindBox = 4`: half-extents (3 floats).
- `KindCompound = 5`: child count (int32), then per child a local `Pose` (position 3 floats + orientation
  quaternion 4 floats = 7 floats) followed by a nested shape (recursively: kind byte + payload).

Refactor `Write` / `Read` to recurse over a shape via a private `WriteShape` / `ReadShape` (magic + version are
written / read ONCE at the top level). Existing kinds 1/2/3 stay byte-identical. The format `Version` stays `1`:
the new kinds are additive, old `.coll` files never contain them, and Ruinborne re-pins + re-bakes together so no
mixed build ever reads a new kind from an old writer. `PropCollisionBake.Write` keeps delegating to
`PropCollisionFormat.Write` so the bake tool and the headless server share one encoder. `PropCollisionLoader`
(client manifest path) and `PropCollisionFormat.LoadDirectory` / `Load` (headless server) keep working unchanged
because they delegate to `PropCollisionFormat.Read`.

### E. Scaling: promote a public engine helper

`RuinbornePhysics.ScaleShape` and the engine-internal `KhaozEngine.Terrain.Render3D.ChunkStatics.ScaleShape` both
currently mirror per-placement uniform shape scaling, and both only handle Cylinder / ConvexHull / TriangleMesh.
Both need Box + Compound now. Rather than patch both mirrors:

- Add a PUBLIC scale helper in `KhaozEngine.Physics` (the dependency-free leaf, reachable from both the
  Render3D-side `ChunkStatics` and the headless Foundation-side `RuinbornePhysics`). Proposed shape:
  `PhysicsShapeScale.Uniform(PhysicsShape shape, float scale) -> PhysicsShape`.
- Handle every kind, including Compound (scale each child's shape AND its local-pose POSITION by the scale, leave
  the orientation unchanged) and Box (scale the half-extents). A scale of 1 returns the original instance.
- `ChunkStatics.ScaleShape` and `RuinbornePhysics.ScaleShape` both delegate to it, retiring the duplication that
  the `RuinbornePhysics` comment already flagged as the intended end state.

### F. ke-propbake tool (`KhaozEngine.PropSurface.Tool`) + manifest

- New optional manifest field `collisionProxy: "<id>_collision.glb"` (resolved against the manifest directory like
  `file` / `heightmap` / `collisionShape`). Added to `AssetEntry` + the manifest DTO + parser.
- The tool, per entry: if `collisionProxy` is set, load the RAW render mesh once (`GltfLoader.Load`), use it both
  to normalize for the `.surf` (`PropLoader.Normalize`, unchanged) AND, with the proxy groups
  (`GltfLoader.LoadGroups`), to bake the `.coll` via `PropCollisionBake.BakeProxy(rawRender, proxyGroups)` (which
  derives the normalization transform from that raw render mesh). Otherwise the current `PropBakePlan.For(mesh)`
  path is unchanged. The `.surf` bake (from the render mesh, walkable solids only) is unchanged either way. Stamp
  `collisionShape` and report the kind (`compound`).
- `PropBakePlan` gains an overload that carries the proxy decision so the tree-gets-coll-not-surf rule stays
  unit-testable without a glTF fixture.

## Testing (headless, real geometry)

New behaviour ships with headless tests. The full suite (~2496) stays green.

1. **Format round-trip** (`PropCollisionFormatTests` or equivalent): a `BoxShape` and a nested `CompoundShape`
   (hull + box children at non-identity poses) write -> read byte-identical; an unknown kind still throws.
2. **Bake determinism** (`PropCollisionBakeTests`): a synthetic multi-object proxy bakes to a `CompoundShape` with
   the expected child count, and a re-bake of the same input is byte-identical.
3. **Scale helper** (engine `ChunkStaticsTests` + a new helper test): Box and Compound scale correctly (child
   geometry AND child-pose position scaled, orientation preserved; scale 1 returns the original instance). Confirm
   both call sites delegate.
4. **Goal-metric test against a REAL proxy** (`RealBuildingCollisionTests`, extended): author the blacksmith proxy
   (the proven-bad building) DURING engine dev, bake its compound `.coll`, commit it as a new engine fixture
   (`KhaozEngine.Tests/Physics/Fixtures/blacksmith_proxy.coll`). The same authored proxy is then reused in the
   Ruinborne rollout (so the blacksmith is authored once, in this phase, not twice). Add a scan harness over a grid
   of stand / jump / walk spots inside + around the proxy asserting:
   - ~0 WEDGES: no spot where a settled capsule can neither walk nor jump out (mirrors the existing `PinnedStarts`
     harness, extended from jump-only to walk + stand).
   - STAIRS CLIMB: walking the stair region gains height and ends standing higher.
   - STANDABLE: the interior floor and at least one furniture top hold the capsule (grounded, not sunk).

   Synthetic flat-quad fixtures are NOT acceptable (they give zero-normal contacts and do not reproduce real
   building wedges; this cost three iterations historically). The fixture is a real baked proxy.

The captured `building_with_eaves.coll` + the existing `RealBuildingCollisionTests` never-pin invariant stay green
(the resolver is unchanged). Determinism is CPU-only here (no GPU golden, so no cross-platform bake concern); the
resolver's existing bit-identity tests continue to lock client/server reconciliation.

## Determinism and byte-identity

- Both heads read the SAME committed `.coll` bytes (`PropCollisionFormat.LoadDirectory` on the server,
  `PropCollisionLoader` on the client), so client-vs-server byte-identity is automatic; the shared
  `CharacterMovement.Step` then resolves identically.
- The bake itself is reproducible: logical-node child order is stable and `HullFromPoints` already sorts
  deterministically, so a re-bake of the same proxy glb yields the same `.coll` (locked by the fixture).
- No wall-clock, no randomness, stable iteration order throughout.

## Rollout (engine release ritual, see CLAUDE.md)

1. Bump `<KhaozEngineVersion>` 8.10.0 -> 8.11.0 (additive minor: new format kinds, `BakeProxy`, `LoadGroups`, public
   scale helper, manifest field; no behaviour change without a proxy). Re-check `origin/main` + `git tag` for a
   concurrent bump first and take the next FREE version if 8.11.0 is claimed.
2. `CHANGELOG.md` entry, newest-first, tight first sentence, noting "consumers re-bake building `.coll`".
3. Update the 3 guard-checked doc-version strings (`docs/CONSUMERS.md` "Engine current version", `docs/ROADMAP.md`
   "Current released version", `README.md` `<PackageReference>` example) so `scripts/check-doc-versions.sh` passes.
4. Full doc sweep: `docs/PHYSICS-PIPELINE.md` (the bake side-flow now emits a building proxy compound),
   `KhaozEngine.Physics/README.md` (new format kinds + scale helper), `KhaozEngine.Render3D/README.md` (`BakeProxy`
   + `LoadGroups`), `docs/USING-KHAOZENGINE.md` (new public API + authoring workflow), `docs/DEPENDENCY-SEAMS.md`
   only if an edge changed. Grep the new type / field names across all `*.md` + `CLAUDE.md`. DELETE the ROADMAP
   "Collision-proxy bake pipeline" entry (it moves to the changelog).
5. `dotnet pack -c Release -o ./local-feed`.
6. CONFIRM WITH THE USER before `git tag v8.11.0` + push (publishes to GitHub Packages for all 4 games).
7. Ruinborne: author all 7 building proxies, add `collisionProxy` to `buildings.manifest.json`, re-bake via
   `ke-propbake`, pin 8.11.0 in `Directory.Build.props`, run the scan metric on all 7 buildings, hand back for the
   alpha deploy. (`RuinbornePhysics.ScaleShape` delegates to the new public helper, so the new compound `.coll`
   scales correctly.)

## Out of scope

- The interim "unstuck / return-to-spawn" client mitigation (already handled separately in Ruinborne over the
  shipped `WorldClient.RequestSelfRescue` 8.6.0 seam).
- Removing or reworking the `.surf` walkable-top heightmap (vestigial for movement but out of scope).
- Auto-simplification / decomposition (ruled out).
- Textured / PBR building rendering (separate roadmap item).

## File-level change map

Engine:
- `KhaozEngine.Physics/PropCollisionFormat.cs`: add `KindBox`/`KindCompound`, recursive `Write`/`Read`.
- `KhaozEngine.Physics/PhysicsShapeScale.cs` (new): public `PhysicsShapeScale.Uniform`.
- `KhaozEngine.Render3D/Models/GltfLoader.cs`: add `LoadGroups`.
- `KhaozEngine.Render3D/Models/PropCollisionBake.cs`: add `BakeProxy`.
- `KhaozEngine.Render3D/Models/PropBakePlan.cs`: proxy-aware overload.
- `KhaozEngine.Render3D/Models/AssetManifest.cs`: `collisionProxy` field.
- `KhaozEngine.Terrain.Render3D/ChunkStatics.cs`: delegate `ScaleShape` to the public helper.
- `KhaozEngine.PropSurface.Tool/Program.cs`: proxy-aware bake.
- Tests: format round-trip, bake determinism, scale helper, real-proxy scan harness + `blacksmith_proxy.coll`
  fixture.
- Docs: PHYSICS-PIPELINE, Physics + Render3D READMEs, USING, ROADMAP delete, CHANGELOG, version strings.

Consumer (Ruinborne, after the engine ships):
- `Ruinborne.Client/assets/buildings/*_collision.glb` (new, 7 authored proxies).
- `Ruinborne.Client/assets/buildings/buildings.manifest.json`: `collisionProxy` per building.
- Re-baked `*.coll`.
- `Ruinborne.Core/RuinbornePhysics.cs`: `ScaleShape` delegates to the new public helper (handles compound).
- `Directory.Build.props`: pin 8.11.0.
