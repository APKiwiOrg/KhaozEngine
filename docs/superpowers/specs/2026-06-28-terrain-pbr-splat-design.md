# Terrain PBR splat-textured materials (design)

Date: 2026-06-28. Roadmap item #3 "Visual fidelity", terrain slice only.

## Summary

Replace the terrain's height/slope vertex-colour ramp with real PBR splat-textured materials (grass /
dirt / rock / sand / snow), blended per-fragment by the five splat weights the chunk mesher already bakes
per vertex, with normal maps and triplanar projection so ground and mountains read as textured surfaces.
CC0-asset-friendly (ambientCG-style tileable albedo + normal), no new heavy native dependencies,
MonoGame-free, author-once GLSL. The textured path is opt-in: a consumer that supplies no layer material
gets exactly today's ramp, byte-identical.

## Scope

In scope (terrain only):
- A dedicated Render3D "splat material" pipeline (N tileable layers, weights in vertex colour, triplanar,
  normal maps, per-layer scalar roughness/tint).
- Terrain.Render3D mapping the five named terrain layers onto it, opt-in at chunk upload.
- The small GPU-seam extensions the textured path needs (array-layer + mip texture upload, mip generation,
  anisotropic sampler).
- A procedural placeholder layer material so the in-repo sample and tests run with no binary assets.

Explicitly out of scope (separate roadmap items, do NOT do here):
- Textured props / unflattening the glTF loader (the prop loader keeps flattening to a base colour).
- Water shader.
- Shadow maps.
- Splatmap-texture authoring tools, runtime terrain painting/editing.
- Texture compression (BCn), streaming of detail textures, virtual texturing.

## Current state (verified)

- `KhaozEngine.Terrain.Render3D/TerrainSplatWeights.cs`: `struct TerrainSplatWeights { float Grass, Dirt,
  Rock, Sand, Snow }`, `.From(height, slope01, biome, waterLevel, snowLine)` produces five normalized
  weights (sum == 1).
- `TerrainChunkBuilder.Build()` bakes one `TerrainSplatWeights` per vertex into the parallel
  `TerrainChunkMesh.Splat[]` array, and ALSO collapses it to `ModelVertex.Color` via `TerrainRamp.Of(w)`
  (a five-colour palette blend) for the current render path. Both are kept on the chunk mesh.
- `ModelVertex` (`KhaozEngine.Render3D/Models/GltfMesh.cs`): Position, Normal, Color (Vector4, full
  float), Uv, Tangent (xyzw, ZERO for terrain). 64 bytes. The vertex layout is SHARED by every Render3D
  mesh.
- `ShaderSources.ModelFrag`: samples Albedo/NormalMap/RoughnessMap (bindings 1-3) + a shared sampler
  (binding 4) in binding order (Metal first-sample-order discipline), with 1x1 white / flat-normal /
  zero-rough defaults. `albedo = vColor.rgb * vTint.rgb * texRgb`. Writes three MRT targets (lit colour,
  geometric normal, linear depth).
- Material binding (`ModelRenderer.CreateMaterialSet` + Scene3D `SurfaceMaps`/`TextureHandle`/`LoadTexture`/
  `LoadMesh`): a SINGLE fixed material set of up to three `texture2D` per mesh (layout = UBO + Albedo +
  Normal + Roughness + sampler). No texture-array / multi-layer path exists. `LoadTerrainChunk` calls
  `Scene3D.LoadMesh(mesh)` with no texture, so terrain falls back to the white default and shows the ramp.
- LOD: `TerrainStreamer` + `TerrainLod.PickLod` (tiers 0/1/2 at 64/32/16 segments). `Scene3DChunkSink.ReLod`
  rebuilds the chunk from scratch; weights are re-baked deterministically per `(x,z,seed)` so they survive
  LOD transitions. Vertex FORMAT is unchanged across LOD.
- GPU seam facts: `GpuTextureDescription.ArrayLayers` is plumbed through to Veldrid but unused; the
  `Texture2D` helper hardcodes 1 layer / 1 mip. `UpdateTexture` hardcodes `mipLevel=0, arrayLayer=0`.
  `GpuTextureUsage.GenerateMipmaps` exists and maps to Veldrid, but no `GenerateMipmaps` command is exposed
  at the seam. `GpuSamplerFilter` has only point/linear (no anisotropic); `CreateSampler` passes
  `maxAnisotropy=0`. `GpuSamplerAddress.Wrap` exists.

## Design decisions

The five hard decisions, plus the three tradeoffs the user chose (triplanar configurable; scalar roughness;
mips + anisotropic).

### Overarching: a dedicated terrain pipeline, not an extension of `ModelFrag`

Forced by the binding count: the shared model layout has exactly three `texture2D` bindings and terrain
needs five layers. A separate `SplatVert`/`SplatFrag` pipeline keeps every existing mesh byte-identical and
lets terrain reinterpret the vertex stream. The terrain pass reuses the same per-frame UBO (`U`) and the
same vertex layout (so the same vertex buffer feeds it), duplicates `ModelFrag`'s lighting math (key+fill+
ambient+point-light loop, three MRT outputs, geometric normal to `oNormal`) with a "keep in sync with
ModelFrag/SurfaceShading" comment in the style the repo already uses, and replaces only the albedo/normal/
roughness derivation with the splat blend.

### 1. Five weights to the GPU: pack four into vertex `Color`, derive the fifth as `1 - sum`

`Color` is a full `Vector4` in the vertex layout (lossless), and terrain's only use of `Color` is the ramp
being deleted. The textured path repacks `Color.rgba = (Grass, Dirt, Rock, Sand)` from the existing
`chunk.Splat[]`; `Snow = clamp(1 - Grass - Dirt - Rock - Sand, 0, 1)` is reconstructed in the shader. The
weights are normalized at bake (sum == 1), so the reconstruction is exact. No `ModelVertex` format change,
no blast radius to other meshes, no new vertex attribute. Weights interpolate per-fragment exactly as the
ramp colour does today.

Rejected: a new shared `vec4 splat` vertex attribute (touches every mesh and shader path, grows every
vertex); a per-chunk splatmap texture (heavier, and the weights are already per-vertex so it buys only
LOD-independent blend resolution, not needed now).

### 2. Binding five layers: two `texture2DArray` (albedo + normal), same binding count as today

One `texture2DArray` for albedo (five layers, channel order grass/dirt/rock/sand/snow), one for tangent-
space normals, one shared sampler. Roughness is a per-layer scalar in a params UBO (see decision 3), so no
roughness array. Resource layout:

```
binding 0: U            UBO  (Vertex|Fragment) - the existing per-frame uniforms, reused as-is
binding 1: AlbedoArray  texture2DArray (Fragment)
binding 2: NormalArray  texture2DArray (Fragment)
binding 3: Samp         sampler (Fragment)
binding 4: SplatParams  UBO  (Fragment) - per-layer roughness/tint/tiling + global params
```

Sampled in binding order (Albedo then Normal) for the Metal first-sample-order constraint, same discipline
as `ModelFrag`/`EdgeFrag`. The array texture binds with no seam change: Veldrid wraps a full-array
`TextureView` when an array texture is placed in a resource set, and the shader declares `texture2DArray`.

Rejected: a texture atlas (wrap/mip bleed across sub-rects, fatal for tiling detail); fifteen separate
`texture2D` bindings (layout bloat + Metal ordering hazard across many samplers).

### 3. UV tiling: world-space triplanar (configurable to planar), scalar roughness per layer

Triplanar, default on, with a per-material `ProjectionMode { Triplanar, PlanarXz }` knob (default Triplanar)
as the perf escape hatch. Rationale:
- Terrain tangents are zero and triplanar derives its basis analytically per world plane, so normal maps
  work with no tangent generation added to the mesher.
- World-space UV (`worldPos.xz / yz / xy * tilesPerMetre`) tiles seamlessly across chunk boundaries and LOD
  with no per-chunk UV seams.
- Triplanar is the only projection that does not smear the steep mountain rock that is explicitly in scope.
- Effective cost is far below the 5-layer x 3-plane worst case: flat ground collapses to the XZ plane
  (one dominant blend weight), and cliffs collapse to ~one dominant layer (rock), because the weights
  themselves are slope-derived.

Roughness is a scalar per layer carried in `SplatParams` (grass ~0.9, rock ~0.7, sand ~0.85, snow ~0.4,
dirt ~0.9), `rough = sum(weight_i * roughness_i)`, fed into the same `specStrength/specExp` mapping as
`ModelFrag` (mirrors `SurfaceShading.ApplyRoughness`). This removes a whole texture array and its memory.
Per-texel roughness can be added later behind the same config (an optional roughness texture per layer)
without breaking callers.

`PlanarXz` mode: detail UV = `worldPos.xz * tilesPerMetre`, normal map applied through an analytic XZ
tangent basis (T = +X, B = +Z, N = geometric), no triplanar blend. Cheaper, smears steep faces; offered
only as the knob, not the default.

### 4. Layer configuration: fixed five channels, configurable textures

The channel count stays five to match `TerrainSplatWeights` (changing it would mean changing the baked
weight struct, out of scope). Which texture is "grass" is the consumer's choice. New config types live on
the render side (consumed at chunk upload), separate from the geometry-focused `TerrainConfig`:

- `TerrainMaterialLayer`: one layer. Albedo source + normal source (paths or raw RGBA), `TilesPerMetre`
  (per-layer tiling scale), `Tint` (Color, default white), `Roughness` (scalar 0..1).
- `TerrainLayeredMaterial`: the five layers in channel order + global params (`Projection`,
  `TriplanarSharpness`, a base `TilesPerMetre` default). Built once; uploads the two arrays once and is
  shared by every chunk (the textures do not vary per chunk).

The generic Render3D feature is terrain-agnostic ("splat material": N layers, weights in vertex colour,
triplanar); `TerrainLayeredMaterial` is the terrain-named mapping onto it.

### 5. Backward compatibility: opt-in, default byte-identical

- `TerrainScene3D.LoadTerrainChunk(chunk)` is unchanged (ramp `Color`, shared model pipeline, white
  default texture). Every current consumer (none ship terrain textures) is untouched.
- New `TerrainScene3D.LoadTerrainChunk(chunk, SplatMaterialHandle material)`: repacks `Color` from
  `chunk.Splat[]`, uploads the repacked mesh, and binds the shared splat material + terrain pipeline.
- `Scene3DChunkSink` gains an optional `TerrainLayeredMaterial`/`SplatMaterialHandle`. When present, Load /
  ReLod use the textured overload; absent = current ramp path.

The chunk builder is NOT changed: it keeps baking ramp `Color` and the `Splat[]` array; the textured
uploader derives packed-`Color` vertices from `Splat[]` at upload time.

## Components and data flow

```
Terrain.Render3D                         Render3D (terrain-agnostic "splat material")        Gpu seam
----------------                         --------------------------------------------        --------
TerrainMaterialLayer  ----build----.
TerrainLayeredMaterial             |---> Scene3D.LoadSplatMaterial(albedoLayers,             CreateTexture(arrayLayers, mips)
                                   |        normalLayers, SplatParams) -> SplatMaterialHandle UpdateTexture(.., arrayLayer, mip)
                                   |        (uploads two texture2DArrays ONCE, builds          GenerateMipmaps(tex)
                                   |         the SplatParams UBO + resource set + pipeline)     CreateSampler(Anisotropic, maxAniso)
chunk.Splat[]  --repack Color-->   |
LoadTerrainChunk(chunk, handle) ---'---> Scene3D.LoadMesh(repackedMesh, SplatMaterialHandle)
                                          (mesh tagged splat-material-kind -> terrain pipeline
                                           at draw, binds the shared splat resource set)
Scene3DChunkSink.Draw  ----------------> Scene3D.DrawTerrainChunk(handle)  (identity, white tint, 1 instance)
```

Upload-once: the two arrays + params UBO + resource set + pipeline are created a single time when the
`TerrainLayeredMaterial` is realized into a `SplatMaterialHandle`. Each chunk's `LoadMesh(mesh, handle)`
only references that shared material; per-chunk cost is the `Color` repack + a vertex/index buffer upload.

Per-chunk repack: a new `ModelVertex[]` where `Color = (w.Grass, w.Dirt, w.Rock, w.Sand)` and
Position/Normal/Uv/Tangent are copied from the source vertex; a `GltfMesh` over it + the same indices.
A pure helper (`TerrainSplatPacking.PackColors(vertices, splat)` or a `TerrainChunkMesh` method) so it is
headless-testable. Skirt vertices already copy their edge vertex's weights, so packing is uniform.

Draw routing: the internal `Scene3D` mesh record gains a material-kind discriminator (model vs splat).
At flush, splat-kind meshes bind the splat pipeline + their splat resource set; model-kind meshes are
unchanged. Terrain reuses the instanced draw path (one instance, identity model, white tint, default spec)
so no new draw path is needed; the splat vertex shader reads the instance stream and ignores Uv/Tangent.

## Shader detail (`SplatVert` / `SplatFrag`)

`SplatVert`: identical to `ModelVert` except it forwards `vColor` (the packed weights) untouched and does
not need to emit Uv/Tangent for the fragment stage (it may keep them inert for layout symmetry). Outputs
world position and geometric normal as today.

`SplatFrag` (new), structure:
1. Reconstruct the five weights: `w = vColor.rgba` (grass/dirt/rock/sand), `snow = clamp(1 - dot(w,1), 0, 1)`;
   optionally renormalize `w5 /= sum` to guard interpolation drift.
2. Triplanar blend weights from the geometric normal:
   `bw = pow(abs(N), vec3(TriplanarSharpness)); bw /= (bw.x + bw.y + bw.z);` Planar mode forces `bw = (0,1,0)`
   equivalent (XZ only).
3. For each layer L in 0..4 with weight `wL`:
   - albedo: triplanar sample `AlbedoArray` layer L on the three world planes
     (`uvX = worldPos.yz * tile`, `uvY = worldPos.xz * tile`, `uvZ = worldPos.xy * tile`), blended by `bw`;
     accumulate `albedo += wL * triAlbedo`.
   - normal: triplanar-blend the normal-map samples (whiteout/UDN blend in world space, no vertex tangent);
     accumulate `Nsum += wL * triNormalWorld`.
   - roughness: `rough += wL * Roughness[L]` (scalar from `SplatParams`).
   - tint: `albedo *= Tint[L]` folded per layer (or pre-applied to the accumulation).
4. `N = normalize(Nsum)` (falls back to geometric if degenerate).
5. Lighting: the `ModelFrag` key+fill+ambient term, `specStrength = spec * (1 - rough)` /
   `specExp = mix(...)`, the point-light loop, the cel-band quantization. Same three MRT writes:
   `oColor = lit`, `oNormal = geometricN*0.5+0.5`, `oDepth = vDepth`.

`SplatParams` UBO layout (std140-friendly): `vec4 Roughness` packs roughness[0..3] + a fifth in a second
slot or a `float Roughness[5]` padded; `vec4 Tint[5]`; `vec4 Tiling` (per-layer tiles/metre) or a single
global tile with per-layer multipliers; `vec4 Globals (TriplanarSharpness, projectionMode, layerCount, _)`.
Exact packing finalized in the plan to satisfy std140 and the Metal/D3D11/Vulkan UBO rules.

CPU mirror: a `SplatShading`/`TriplanarMath` helper (pure) mirrors the triplanar blend-weight computation
and the weight reconstruction, so the math is headless-testable the way `SurfaceShading` mirrors the TBN/
roughness math.

## GPU-seam extensions (net-new, all modest)

1. Array + mip texture creation: a `GpuTextureDescription.Texture2DArray(w, h, format, usage, layers, mips)`
   helper (the constructor already takes `arrayLayers`/`mipLevels`), created with
   `Sampled | GenerateMipmaps` usage and a full mip chain.
2. `UpdateTexture` overload taking `arrayLayer` and `mipLevel` (the current one hardcodes both to 0), so
   each layer's base mip can be uploaded. Veldrid's `UpdateTexture` already accepts these subresource
   indices; only the seam signature is missing.
3. `GenerateMipmaps(IGpuTexture)` on the command list (or device) seam, forwarding to Veldrid
   `CommandList.GenerateMipmaps`. Called once after the layer base mips are uploaded.
4. Anisotropic sampler: add `GpuSamplerFilter.Anisotropic` and a `MaximumAnisotropy` field to
   `GpuSamplerDescription`/`CreateSampler` (currently passes 0). A dedicated terrain sampler uses
   `AddressMode = Wrap` (tiling) + anisotropic + the full mip chain. Floor: if a backend reports no
   anisotropy, fall back to `MinLinearMagLinearMipLinear` (trilinear) so the path still runs everywhere.

The array-texture *binding* needs no seam change (Veldrid binds an array texture as `TextureReadOnly`).

## Configuration types (public API)

Render3D (generic):
- `SplatMaterialHandle` (opaque, like `MeshHandle`/`TextureHandle`).
- `Scene3D.LoadSplatMaterial(...)` taking the per-layer albedo + normal pixel sources, the `SplatParams`
  (per-layer roughness/tint/tiling + globals), uploads the arrays once, returns the handle.
- `Scene3D.LoadMesh(GltfMesh, SplatMaterialHandle)` overload (splat-kind mesh).
- `Scene3D.UnloadSplatMaterial(handle)`.

Terrain.Render3D (terrain-named):
- `TerrainMaterialLayer`, `TerrainLayeredMaterial` (above).
- `TerrainScene3D.LoadTerrainChunk(chunk, SplatMaterialHandle)` overload.
- `TerrainMaterialPresets` / a procedural placeholder generator (below).
- `Scene3DChunkSink` optional material parameter.

## Testing

Headless (`KhaozEngine.Tests`):
- Weight pack/unpack round-trip: `PackColors` then reconstruct (4 in Color + `1 - sum`) recovers the
  original five weights within epsilon, including the renormalization and the all-zero -> grass fallback.
- Triplanar blend-weight math: `bw` sums to 1, picks the dominant axis for a flat-up normal (XZ) and a
  vertical normal (side plane), and the planar mode forces XZ.
- `SplatParams` packing: per-layer roughness/tint/tiling round-trip through the UBO struct layout.
- `TerrainLayeredMaterial` config: five layers required, channel order preserved, defaults applied.
- Back-compat: the no-material `LoadTerrainChunk` path still produces ramp `Color` (the builder is
  unchanged) - assert `TerrainChunkBuilder.Build` output is untouched by this work.

GPU-seam tests where a device is available (the existing `GpuFact` pattern): array-layer upload reads back
the right layer; `GenerateMipmaps` populates a lower mip; the anisotropic sampler is created without throwing
on each backend (or falls back).

Visual verification (renderer, run a consumer): `TerrainWalkSample` gains a textured-terrain mode wired to
a `TerrainLayeredMaterial`. Verified by running the Desktop head and eyeballing grass/rock/sand/snow
blending and normal-mapped surface detail on ground and mountains. An optional GPU golden is a follow-up
(a new golden requires the cross-platform-gpu.yml D3D11+Vulkan bake; not gating this work).

## Assets and the sample

To keep the repo light and avoid asset licensing in-tree, the engine ships a small procedural placeholder
layer material (per-layer tinted value-noise albedo + a derived/flat normal, generated at runtime) so
`TerrainWalkSample` and the tests run with no binary textures. Real consumers (Ruinborne) wire ambientCG
CC0 tileable albedo + normal sets per layer. The placeholder proves the full pipeline (arrays, mips,
triplanar, blending, normal maps) end to end without shipping large files.

## Out of scope (restated)

Textured props / glTF un-flattening, water, shadows, texture compression, terrain painting tools,
per-texel roughness textures, splatmap textures, and changing the five-channel weight set. The consumer
adoption (Ruinborne pinning the new version and wiring real textures) is NOT part of this engine work.

## Release and docs (engine ritual)

- One shared `<KhaozEngineVersion>` bump in `Directory.Build.props` (additive feature = minor;
  next is 7.63.0 unless a concurrent release takes it - re-check `origin/main` + tags before bumping).
- `CHANGELOG.md` entry (newest-first, one-line digest first sentence), same commit as the bump.
- Update the three guard-checked version strings (`docs/CONSUMERS.md` "Engine current version",
  `docs/ROADMAP.md` "Current released version", `README.md` `<PackageReference>` example).
- Full doc sweep: `docs/ROADMAP.md` item #3 (delete the terrain-PBR-splat bullet, keep textured-props /
  water / shadows), `docs/RENDER-PIPELINE.md` (note the terrain splat pipeline as a second model-pass
  pipeline), `README.md` package/feature notes and `CLAUDE.md` package map (the new splat-material +
  terrain-material public API), `docs/USING-KHAOZENGINE.md` (a usage section for the splat material +
  textured terrain). No new package is added (the feature extends existing `Render3D` +
  `Terrain.Render3D`), so the package catalog is unchanged.
- `dotnet pack -c Release -o ./local-feed`, commit, `git tag vX.Y.Z`. Push/tag held and batched per the
  engine's standing hold-and-confirm policy.

## Risks and mitigations

- Triplanar sample cost (up to 5 layers x 3 planes x 2 maps). Mitigated by slope-derived weights collapsing
  to ~one layer on cliffs and the XZ plane dominating on flat ground, plus the `PlanarXz` knob. A future
  dominant-2-layer optimization is possible but not in v1.
- Metal sampler-index ordering: sample the two arrays strictly in binding order, same discipline already
  documented in `ModelFrag`/`EdgeFrag`.
- std140 UBO layout for `SplatParams` across Metal/D3D11/Vulkan: finalize a padded, vec4-aligned layout in
  the plan; cover it with the packing round-trip test.
- New GPU golden turning main red if baked only on Metal: defer the golden; verify visually via the sample
  and headless-test the pure logic. If a golden is added, bake D3D11+Vulkan via cross-platform-gpu.yml
  before committing.
- Memory: two RGBA8 arrays of five layers + mips (~tens of MB at 1k textures). Acceptable on desktop;
  compression is a future item.
