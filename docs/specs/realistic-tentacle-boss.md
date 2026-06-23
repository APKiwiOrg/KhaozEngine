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
| E | PBR-lite on the **skinned** pass (normal/roughness on rigged meshes) | **shipped 7.28.0** |

### The skinned-PBR gap (E) — shipped 7.28.0

B added normal/roughness maps to the **rigid** model pass only; 7.25.0 left skinned meshes albedo-only
(no tangents, so normal maps would be inert). **7.28.0 ships E**: skinned meshes now take normal +
roughness maps too. `SkinnedVertex` carries a tangent (xyz + handedness `w`, defaulting to zero =
geometric normal); `GltfLoader.LoadSkinned` reads glTF `TANGENT` or computes it from UV+position, and
`SkinnedMeshBuilder.BuildTube` computes one from its ring UVs. Bind maps with
`Scene3D.LoadSkinnedMesh(mesh, SurfaceMaps)` (mirrors the rigid overload; 1x1 white / flat-normal /
zero-roughness defaults). The tangent rides the per-frame CPU skin deform into the shared `ModelFrag`,
so the TBN tracks the bent pose and the existing roughness math applies — and the rigid pass's
Metal albedo-first binding-order fix covers the skinned pass for free (it reuses `ModelFrag`). The
no-maps / zero-tangent path is byte-identical to 7.27.0 (committed skinned goldens unchanged). So the
fully-rigged realistic octopus now gets surface detail everywhere; the mesh-split workaround below is no
longer required (still valid if you want it for other reasons).

## Asset-export contract (against 7.28.0)

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
  `LoadMesh(mesh, maps)` (rigid) or `LoadSkinnedMesh(mesh, maps)` (skinned, 7.28.0+). Albedo / normal /
  roughness as separate PNGs.
- Roughness uses the glTF metallic-roughness `.g` convention (metallic ignored).
- Export `TANGENT` for normal-mapped parts, rigid or skinned (the loader falls back to a computed
  tangent; a zero tangent means "use the geometric normal").
- **Skinned parts now take normal/roughness maps too (E shipped 7.28.0)** — bind them via
  `LoadSkinnedMesh(mesh, SurfaceMaps)`, same as rigid.

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
