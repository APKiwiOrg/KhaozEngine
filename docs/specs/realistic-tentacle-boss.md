# Realistic tentacle-boss: roadmap + asset contract

Status doc for the octopus-alien boss work (per-tentacle mechanics, seamless semi-realistic look,
beam weapon). Captures the engine prerequisites, what has shipped, the asset-export contract a model
must meet, and the game-side gameplay plan. Target game: TBD (SpaceGame already runs procedural
tentacles game-side and is the likely host; the engine pieces below are game-agnostic).

## The three layers

1. **Engine renderer + animation** (this repo). The realism ceiling and the motion solver live here.
   Generic, reused by every game.
2. **Asset pipeline** (a rigged octopus model + textures). Needs a human/AI 3D model + rig; not
   something the engine or a coding agent can author alone. See the contract below.
3. **Gameplay** (the game repo). Per-tentacle attack patterns, cadences, damage windows, the
   shockwave/fire/poison on-slam effects, encounter logic. Game-specific.

## Engine prerequisites

| ID | Upgrade | Status |
|----|---------|--------|
| A | 32-bit mesh indices (lifts the 65,536-vertex cap) | **shipped 7.22.0** |
| B | PBR-lite materials on the **rigid** lit pass (normal + roughness maps, `SurfaceMaps`, `UseSmoothPreset`) | **shipped 7.25.0** |
| C | 3D beam/laser primitive (`Scene3D.DrawBeam` + `BeamStyle`) | **shipped 7.26.0** |
| D | `ProceduralChainSolver` (3D writhe + FABRIK reach + slam envelope) | **shipped 7.27.0** |
| E | PBR-lite on the **skinned** pass (normal/roughness on rigged meshes) | **not started** (see gap) |

### The skinned-PBR gap (E)

B added normal/roughness maps to the **rigid** model pass only. The 7.25.0 changelog is explicit:
*"Skinned meshes stay albedo-only this release (no tangents; normal maps would be inert)."* The
tentacles (and a one-piece skinned octopus) are skinned, so under the current engine **they get no
surface detail** — flat albedo + lighting only. Two ways forward:

- **Split the mesh**: body as a separate **rigid** mesh (full normal/roughness via `SurfaceMaps`) +
  **skinned** tentacles (albedo-only). The body looks detailed, the tentacles read smoother. Simplest,
  no new engine work, some visual inconsistency at the join.
- **Ship E**: extend the tangent attribute + `SurfaceMaps` binding + TBN/roughness math to the skinned
  model pass so a fully-rigged realistic octopus gets surface detail everywhere. This is the real
  unlock for "seamless semi-realistic," and it is generic (every game with rigged characters wants it).
  Scope mirrors B but on `SkinnedModelRenderer` + the skinned vertex/shader; watch the same
  Metal texture-binding-order trap B documented.

Recommendation: treat E as the next engine prompt once C lands. Until then, develop against the split
approach or the albedo-only placeholder.

## Asset-export contract (against 7.27.0)

A model handed to the engine for this boss must meet:

**Geometry / format**
- glTF 2.0 binary (`.glb`). Loaded via `GltfLoader.LoadSkinned` (rigged) or `GltfLoader.Load` (rigid parts).
- Vertex budget: effectively uncapped (A shipped 32-bit indices). Keep individual logical meshes sane.
- Triangulated. `+Y` up (the placeholder script exports `export_yup=True`).

**Rig (skinned parts)**
- One root bone `body` near the origin.
- Per tentacle `i` (0-based): a single chain of bones `tentacle.<i>.<j>`, `j = 0..N-1`, child of `body`,
  ordered root→tip. `N` (bones per tentacle) **must equal** the `ProceduralChainSolver` spine length the
  game drives it with (default **8**).
- Total bone count must stay under the **128-bones-per-draw** cap (4×8 + body = 33, fine).
- `JOINTS_0` / `WEIGHTS_0` present; weights normalized. (`GltfLoader.LoadSkinned` validates bone indices
  against the rig and rejects out-of-range ones — see 7.24.0.)

**Textures / materials**
- Engine does **not** auto-read glTF material textures. Bind explicitly: load PNGs with
  `Scene3D.LoadTexture(path)` and pass `new SurfaceMaps(albedo, normal, roughness)` to
  `LoadMesh(mesh, maps)` (rigid). Albedo / normal / roughness as separate PNGs.
- Roughness uses the glTF metallic-roughness `.g` convention (metallic ignored).
- Export `TANGENT` for rigid normal-mapped parts (the loader falls back to a computed tangent; a zero
  tangent means "use the geometric normal").
- **Skinned parts are albedo-only until E ships** (normal/roughness bound to a skinned mesh are inert).

**Look**
- `scene.Post.UseSmoothPreset()` turns off cel bands / palette / dither / edge outline for a smooth
  realistic look. It is **whole-frame**: the entire game renders smooth, not just the boss. A realistic
  boss therefore implies a smooth-rendered game.

## Motion (shipped, D)

`ProceduralChainSolver` (`KhaozEngine.Render3D`) drives each tentacle's bones every frame; no baked
Blender animation and no engine animation-clip player needed.

- `Solve(root, forward, up, clock, cfg, spineOut)` → writhe spine (one point per bone).
- `SolveReach(root, forward, up, clock, target, reachWeight, cfg, spineOut)` → writhe + FABRIK reach
  blended by `reachWeight` (0 = natural writhe, 1 = tip on target).
- `Fabrik(spine, root, target, segmentLength, iterations)` → reusable uniform-length IK.
- `SlamEnvelope(phase, snap)` → `[0,1]` power-stroke to drive `reachWeight`/whip over time.
- Hand the spine to `PolylineFrames.Build(spine, Axis.Z, up)` → `Scene3D.DrawSkinned(handle, bones, model, tint)`.

Per-tentacle independence (different cadences, damage windows, slam types) = a per-tentacle `clock`
offset + `cfg` + `SlamEnvelope` phase. SpaceGame's game-side 2D `SlathTentacleLayout` can later be
retired onto this solver.

## Gameplay plan (game repo, not started)

Per tentacle: a small state driver (idle writhe → telegraph/rear → slam via rising `reachWeight` toward
a target point → impact → recover), its own cadence, its own health/damage window, and an on-impact
hook that fires the distinct effect (shockwave / fire / poison). The laser is a separate attack wired to
C's `DrawBeam` + `AddLight` + a `ParticleSystem` burst at the impact point. This is game-specific and
waits on: the target game decided, C shipped (for the beam), and an asset (or the placeholder).

## Placeholders

- **Blender**: `tools/blender/make_placeholder_octopus.py` emits a rigged stand-in `.glb` matching the
  rig contract above. Validates the import path before a real asset exists.
- **Zero-Blender**: compose `SkinnedMeshBuilder.BuildTube()` ×4 + a body primitive in-engine and drive
  them with `ProceduralChainSolver` for an instant, no-asset stand-in.
