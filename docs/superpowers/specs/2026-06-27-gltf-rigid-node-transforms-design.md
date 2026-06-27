# GltfLoader rigid node-transform baking (glTF conformance)

Date: 2026-06-27
Status: approved design, ready for implementation plan
Area: engine (Render3D) — a glTF-conformance fix in the rigid loader

## Context

`GltfLoader.BuildRigid` iterates `root.LogicalMeshes` and copies raw `POSITION` accessor values — it
never reads the scene graph, so **node world transforms are ignored**. Any glTF that positions geometry
via nodes (Blender exports, multi-piece kits, meshes instanced by several nodes) loads mis-placed /
mis-oriented / mis-scaled, and a mesh instanced by N nodes loads once at local origin. `BuildSkinned`
already bakes `node.WorldMatrix` (via the skin joints), so the rigid path is inconsistent with the
skinned path and non-conformant with glTF semantics.

This was worked around for the prop kits by baking node transforms into vertex positions at ingest
(Blender `transform_apply`), which is fine and good practice — but it shouldn't be *required*. Fixing
the loader makes future kits "just work" and removes manual ingest friction (engine-first).

## The fix — `BuildRigid` walks nodes

Instead of iterating `LogicalMeshes`, iterate the scene's **nodes** (`root.DefaultScene` /
`LogicalNodes`); for each node whose `Mesh` is set, transform that mesh's primitives into model space by
the node's world matrix:

- `POSITION` → `Vector3.Transform(p, node.WorldMatrix)`.
- `NORMAL` and `TANGENT.xyz` → by the **normal matrix** (transpose of the inverse of the upper-3x3 of
  the world matrix), then renormalize — correct under non-uniform scale; keep `TANGENT.w` (bitangent
  sign).
- A mesh referenced by multiple nodes emits **one transformed copy per node** (fixes instancing).
- Identity-node assets (single mesh at origin, or pre-baked) must produce **byte-identical** output to
  today (world matrix = identity → no-op).
- `LoadWithMaterial` keeps its per-primitive material mapping aligned with the transformed corners.

(SharpGLTF exposes `node.WorldMatrix`; a manual node walk is the clearest implementation. Match what
`BuildSkinned` does.)

## Testing (headless)

- A mesh on a node with a non-identity translation/rotation/scale loads at the **transformed** position,
  not the origin (vs the current behaviour).
- A mesh instanced by multiple nodes loads **N placed copies**.
- An **identity-node** asset is byte-identical to the pre-fix output (pre-baked props + single-mesh
  assets unaffected).
- Normals are correct under non-uniform scale (the normal matrix, not the raw world matrix).

## Regression (the real risk — a loader behaviour change)

Models with non-identity mesh-node transforms currently load wrong and will now load **correctly** — a
visual change that shifts any GPU golden using them.

- **Sweep** the consumer repos' committed `.glb` (`~/SpaceGame`, `~/Hardpoint`) and the engine's
  test/sample assets for non-identity **mesh-node** transforms.
- Any affected GPU golden is **re-baked on all three backends** (Metal locally + D3D11/Vulkan via
  `cross-platform-gpu.yml` `workflow_dispatch bake=true`, commit artifacts) — per the engine's
  golden rule, a Metal-only bake turns `main` red.
- If **no** consumer/test asset is affected (likely — they have worked under the current loader), state
  that explicitly. Never silently move a golden.

## Scope

### In scope

- `BuildRigid` (+ `LoadWithMaterial`) node-world-transform baking in `KhaozEngine.Render3D`.
- Headless tests (transformed / instanced / identity-byte-identical / normal-matrix).
- Regression sweep + cross-platform golden re-bake for anything affected.
- **Patch** version bump (a conformance bug fix); docs: CHANGELOG/CHANGENOTES, the 3 guard
  declarations, a `USING` note (rigid glTF now honours node transforms; ingest `transform_apply` is no
  longer required for placement, still harmless).

### Out of scope (named)

- Morph targets, rigid-mesh animation (it stays a static mesh), camera/light nodes.
- The skinned path (already node-aware).
- Any change to the asset pipeline / scatter (this is purely the loader).

## Engine-first

`Render3D` loader fix; every consumer + future kit benefits. Independent of the static-collision and
Ruinborne work, but it shares `Render3D` with anything touching the renderer — if another engine
release is in flight, check tags and bump past (the concurrent-release rule).
