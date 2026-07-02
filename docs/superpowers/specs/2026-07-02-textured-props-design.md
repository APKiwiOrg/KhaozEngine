# Textured props (visual fidelity #3a)

Status: approved design, pre-plan.
Roadmap: `docs/ROADMAP.md` near-term item #3 (visual fidelity), first of three slices
(textured props -> water -> shadows). This spec covers **textured props only**.

## Problem

Props (trees, rocks, buildings) render flat base-colour: no surface detail. The roadmap frames
this as "the prop loader flattens each material's texture to a single factor during ingest." The
real situation, verified against the code and assets:

- The prop/model shader (`ShaderSources.ModelFrag`) already samples albedo + normal + roughness and
  does full tangent-space normal mapping + roughness-modulated specular. The renderer
  (`ModelRenderer`) already has albedo/normal/roughness texture slots + `CreateMaterialSet`.
  `Scene3D` already has `LoadMesh(mesh, SurfaceMaps)`, `LoadMesh(mesh, GltfMaterialMaps)`,
  `LoadSurfaceMaps`, `LoadTexture(byte[],w,h)`. The **character mesh already uses this path** via
  `GltfLoader.LoadSkinnedWithMaterial` + `Scene3D.LoadSkinnedMesh(mesh, maps)`.
- Props never opted in: `PropLoader.LoadProp` returns geometry only (`GltfLoader.Load`), and the
  sample uploads via the untextured `Scene3D.LoadMesh(mesh)`.
- The in-repo CC0 assets carry **no textures** anyway. The Quaternius Stylized Nature props were
  ingested with textures + `TEXCOORD_0` deliberately stripped (WebP, which SharpGLTF can't decode),
  leaving `POSITION`/`NORMAL`/`COLOR_0` only. So they cannot be textured in place: no UVs, no
  tangents. Multi-material colour already works (per-primitive `baseColorFactor` baked to per-vertex
  colour), so a tree is already brown-trunk/green-leaf; the gap is genuinely *surface detail*.

Conclusion: the capability is 90% built. What's missing is (a) letting props opt into the existing
material path, and (b) a way to demonstrate + test it, since no in-repo asset ships textures.

## Goals

1. A prop glTF that ships baseColor/normal/roughness textures renders with full PBR (real
   textured-prop capability, matching the character path).
2. A fully in-repo, deterministic, **asset-free** textured prop for demonstration + headless tests,
   mirroring how terrain already does `TerrainMaterialPresets.Procedural()`.
3. Immediate visible payoff (a textured prop you can walk up to) + a PNG dump proving albedo +
   normal relief actually reach the GPU.

Non-goals (explicitly deferred): the showcase consolidation + clearing + CC0 houses (sub-project B),
re-ingesting a real Quaternius textured asset, water, shadows, per-primitive multi-material
*textures* within one mesh (single material set per mesh is fine for this slice).

## Design

All engine changes live in `KhaozEngine.Render3D` (no new package, one shared version bump).

### 1. Real-glTF prop texture path

- `PropLoader.LoadPropWithMaterial(AssetEntry entry, PropValidation? = null)` ->
  `(GltfMesh Mesh, GltfMaterialMaps Maps)`. Internally routes the raw load through
  `GltfLoader.LoadWithMaterial(entry.File)` instead of `Load`, then runs the same `Normalize`
  (scale-to-heightMeters, drop origin) on the mesh. Returns all-absent maps
  (`GltfMaterialMaps.IsEmpty`) when the glTF has no textures, degrading to today's render. Never
  throws on a missing/undecodable texture (the loader already guarantees this).
- `AssetManifest` / `AssetEntry`: add an opt-in `bool Textured` (JSON `"textured": true`, default
  false). When set, the sample/consumer calls `LoadPropWithMaterial` and uploads via
  `Scene3D.LoadMesh(mesh, maps)`; otherwise the existing untextured path is unchanged. (Auto-detect
  was considered; an explicit flag keeps ingest intent visible and avoids surprising per-asset
  decode cost. The loader still degrades gracefully if a flagged asset turns out untextured.)
- Existing `PropLoader.LoadProp` and the untextured `Scene3D.LoadMesh(mesh)` path are untouched, so
  every current consumer keeps working byte-identically.

### 2. Tangents for primitive meshes

`MeshPrimitives.Box` emits per-face UVs but zero tangents (`ModelVertex.Tangent == Vector4.Zero`),
which the shader reads as "no TBN, light with the geometric normal" - so normal maps have no effect
on primitive meshes today.

- Add `MeshOps.WithTangents(GltfMesh mesh)` -> `GltfMesh`: derives a per-vertex tangent (xyz + `w`
  bitangent sign) from UV + position gradients (Lengyel accumulate + Gram-Schmidt orthogonalize
  against the normal) - the exact algorithm `GltfLoader`/`MeshAssembler` already use for glTF meshes
  that lack `TANGENT`. Returns a new mesh; a degenerate/zero-UV face leaves a zero tangent
  (graceful, shader falls back). Reusable by any procedural mesh, not just props.

### 3. Procedural prop material preset

- Add `PropMaterialPresets.Procedural(...)` (in `KhaozEngine.Render3D`), mirroring
  `TerrainMaterialPresets.Procedural()`: deterministically generates a tileable **albedo + normal**
  (raw RGBA8) and returns a `GltfMaterialMaps` (built from `DecodedImage`s - no PNG encoder needed,
  no binary asset). Default look: a **mossy stone block** (grey stone albedo with green moss
  mottling; a matching bumpy normal map). Parameters (size, seed, tint) kept minimal.
- Combined with `MeshOps.WithTangents(MeshPrimitives.Box(...))` this is a complete textured prop
  built entirely in code.

### Consumption / interim visible home

- `TerrainWalkSample` places one procedural mossy-stone-block prop near spawn: build the box, add
  tangents, `Scene3D.LoadMesh(box, PropMaterialPresets.Procedural())`, draw it at a fixed spot. This
  is the immediate walk-up payoff + manual playtest. Sub-project B relocates this into the
  `KhaozEngine.Showcase` 3D World room (TerrainWalkSample is retired by B).

## Verification

- **Headless unit tests** (`KhaozEngine.Tests`, no GPU):
  - `PropLoader.LoadPropWithMaterial` returns the normalized mesh + the material's decoded maps for a
    textured glTF fixture, and `IsEmpty` maps for an untextured one; the returned mesh is identical
    to `LoadProp`'s (material read is additive).
  - `AssetManifest` parses `"textured": true`/absent correctly.
  - `MeshOps.WithTangents` produces, for each vertex, a finite tangent roughly orthogonal to the
    normal with `w == +/-1`; a box's +X face tangent lies in its face plane.
  - `PropMaterialPresets.Procedural` returns albedo + normal of the expected dimensions, deterministic
    for a fixed seed, with normal-map texels centred near (128,128,255).
- **Visual confirmation (throwaway):** a `GpuFact` renders the textured stone block and dumps raw
  RGBA -> PNG via stdlib Python (engine has no PNG encoder). I eyeball albedo + normal relief before
  claiming success; the GpuFact is removed before merge, not committed.
- **No new committed GPU golden** in this slice: a new golden needs a 3-backend CI bake
  (Metal + D3D11 + Vulkan) which is real cost. Left as an optional follow-up if regression coverage
  is wanted.

## Docs sweep (per engine governance)

- `CHANGELOG.md` newest-first entry (first sentence = digest) in the same commit as the version bump.
- Bump `<KhaozEngineVersion>` in `Directory.Build.props` to the next FREE version at release time
  (9.1.0 is released; likely 9.2.0, but re-check `git tag` + `origin/main` because concurrent dev is
  in flight and may claim it). Update the 3 guard-checked declarations
  (`docs/CONSUMERS.md`, `docs/ROADMAP.md`, `README.md` example).
- New public API -> `KhaozEngine.Render3D/README.md` (its own `PackageReadmeFile`) +
  `docs/USING-KHAOZENGINE.md` (a section on textured props: `LoadPropWithMaterial`, the manifest
  `textured` flag, `MeshOps.WithTangents`, `PropMaterialPresets.Procedural`).
- Root package catalog table unchanged (no package added/removed).
- `docs/ROADMAP.md`: trim item #3 to note textured props landed (water + shadows remain).
- Grep the new type/member names across all `*.md` + `CLAUDE.md` before committing.

## Concurrent-dev note

Heavy parallel engine dev is expected during this work. Before merging back: `git fetch`, and if
`main`/`origin/main` advanced past this branch's base, merge `main` INTO this branch first, resolve
conflicts (the `<KhaozEngineVersion>` line collides constantly - take the next free version and
rebase the CHANGELOG entry onto it), re-run build + tests on the merged result HERE, then merge back
clean. Hold + batch the push/tag per the engine release policy; confirm before publishing.

## Follow-on

Sub-project B: consolidate the windowed demos into a new `KhaozEngine.Showcase` app (menu hub of
rooms), add a `FlattenFeature` clearing + hand-placed CC0 Quaternius houses with box-compound
collision (Ruinborne pattern), relocate the textured prop into it, retire the folded sample projects,
prune `.vscode` + README. Own spec + plan after A ships.
