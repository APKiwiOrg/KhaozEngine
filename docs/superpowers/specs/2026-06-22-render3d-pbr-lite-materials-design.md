# Render3D PBR-lite materials: normal + roughness maps

Status: approved design (brainstorm), pre-implementation.
Target version: 7.23.0 (minor, additive).
Worktree/branch: feature/pbr-lite-materials.

## Problem

The lit model pass samples a single Albedo texture and lights with Blinn-Phong + optional cel bands.
`ModelVertex` has no tangent; `GltfLoader` reads only base colour + TEXCOORD_0 and ignores material
textures. Meshes render flat/plastic with no surface detail. Adding tangent-space normal maps plus a
roughness map to the lit pass is the single biggest lever toward a semi-realistic look.

## Goal

Additive PBR-lite (normal + roughness) on the rigid lit model pass, with NO regression for existing
stylized games. A mesh with no maps must render identically to today (the no-map path stays on the
geometric-normal / unmodified-specular code path). Skinned meshes are unchanged this release.

Decisions locked during brainstorming:
- glTF wiring: explicit PNG binding (a new surface-maps bundle), not glTF material auto-read.
- PBR scope: roughness only (sampled `.g`, glTF metallic-roughness convention); metallic ignored.
- Add a Smooth/Realistic post preset.

## Non-goals (this release)

- glTF auto-read of `normalTexture` / `metallicRoughnessTexture` image refs (noted future follow-up).
- Metallic workflow / energy-conserving BRDF (base lighting stays Blinn-Phong).
- Normal mapping on skinned meshes (they carry no tangents; maps would be inert).
- Normal maps on `MeshPrimitives` (they route a zero tangent; geometric-normal fallback). Untouched.

## Architecture

### 1. Geometry: tangents on `ModelVertex` (`Models/GltfMesh.cs`)

- Add `public Vector4 Tangent;` (xyz = model-space tangent, w = +/-1 bitangent handedness). Size 48 -> 64
  bytes; `SizeInBytes = 64`.
- Keep the existing 3-arg and 4-arg ctors (they set `Tangent = Vector4.Zero`). Add a 5-arg ctor
  `(p, n, c, uv, tangent)`. A zero/degenerate tangent is the shader's "no TBN" signal.

### 2. Tangent generation (`Models/MeshAssembler.cs`, `Models/GltfLoader.cs`)

- `MeshCorner` gains an optional source tangent (`Vector4? Tangent`).
- `MeshAssembler`: accumulate a per-welded-vertex tangent across shared faces (Lengyel UV+position
  method), then finalize with Gram-Schmidt orthogonalization against the (finalized) normal and a
  handedness sign. Degenerate UVs (zero area in UV space, e.g. all-zero UVs) leave the tangent zero.
  Source tangents (when a corner supplies one) are honoured like source normals.
- `GltfLoader.Load`: read the glTF `TANGENT` accessor (vec4) when present and pass it into `MeshCorner`;
  otherwise `MeshAssembler` computes it. `LoadSkinned` is unchanged (skinned stays albedo-only).

### 3. Resource bindings (`Rendering/ModelRenderer.cs`, `Internal/ShaderSources.cs`)

This is the Metal/Veldrid mis-bind risk surface (see the BlitFrag depth-texture comment + engine memory).
Mirror the known-good `EdgeFrag` layout (textures grouped, then a single shared sampler):

- Model set 0 layout becomes: `0 = U` (UBO, vtx+frag), `1 = Albedo` (tex, frag), `2 = NormalMap`
  (tex, frag), `3 = RoughnessMap` (tex, frag), `4 = Sampler` (frag). One shared sampler for all three
  textures (the device built-in `LinearSampler`, as today).
- `ModelRenderer` adds two 1x1 defaults next to `_white`:
  - `_flatNormal` = RGBA `(128,128,255,255)` -> tangent-space normal `(0,0,1)`.
  - `_defaultRough` = RGBA `(0,0,0,255)` -> roughness `0`.
- `CreateMaterialSet` extends to `(IGpuTexture albedo, IGpuTexture? normal = null, IGpuTexture? roughness
  = null)`, substituting `_flatNormal` / `_defaultRough` when null. `_defaultSet` binds
  `(UBO, _white, _flatNormal, _defaultRough, _sampler)`.
- Existing `LoadMesh(GltfMesh, TextureHandle)` (albedo only) -> normal/roughness default -> identical
  lighting. This is the byte-identical invariant at the binding layer.

Vertex layout: slot 0 gains a `Tangent` Float4 element (now 5 elements -> shader attribute locations
0..4). The per-instance stream (slot 1) keeps its 7 elements but their shader attribute locations shift
to 5..11. `ModelVert` is renumbered accordingly (add `layout(location=4) in vec4 Tangent;`, instance
inputs 5..11) and outputs `vTangent` at out-location 8.

### 4. `ModelFrag` rewrite (`Internal/ShaderSources.cs`)

Additive; reduces exactly to today's expression at the defaults.

- New frag input `layout(location=8) in vec4 vTangent;` plus the two new texture/sampler bindings.
- Geometric normal `Ngeo = normalize(vNormalW)` (as today).
- Lighting normal:
  - if `length(vTangent.xyz) > 1e-5`: `T = normalize(vTangent.xyz - Ngeo*dot(Ngeo,T))`,
    `B = cross(Ngeo,T) * vTangent.w`, `nTS = texture(NormalMap).xyz*2-1`,
    `N = normalize(mat3(T,B,Ngeo) * nTS)`. With the flat-normal default `nTS = (0,0,1)` ->
    `mat3*nTS = Ngeo`.
  - else `N = Ngeo` (zero/degenerate tangent: primitives, skinned, untangented meshes -> today's path,
    bit-identical).
- Roughness: `rough = texture(RoughnessMap).g` (default texel `.g = 0`). Modulates the existing
  Blinn-Phong, applied to BOTH the key-light and point-light specular:
  - `specStrength = vSpecParams.x * (1.0 - rough)`
  - `specExp = mix(vSpecParams.y, MinExp, rough)` where `MinExp` is a small fixed broad-highlight
    exponent (e.g. 8.0), still clamped `max(specExp, 1.0)`.
  - At `rough = 0`: `specStrength = vSpecParams.x`, `specExp = vSpecParams.y` -> identical to today.
- MRT outputs: `oNormal` keeps writing the GEOMETRIC normal (`Ngeo*0.5+0.5`), NOT the perturbed normal,
  so the depth/normal edge-outline post pass is unaffected and does not spawn spurious outlines from
  normal maps. `oColor` uses the perturbed `N` for lighting. `oDepth` unchanged.
- Cel-banding is already gated by `Params.x` (CelBands); the perturbed normal flows through it
  unchanged, so the stylized path is preserved.

### 5. Skinned path (`Internal/ShaderSources.cs` SkinnedModelVert)

`SkinnedModelRenderer` (the GPU-skinned pipeline) is currently dormant (never instantiated; only its
static helpers + the `MaxBonesPerDraw` constant are used). It still binds `ModelFrag`, so to avoid a
latent link break, `SkinnedModelVert` gains a `layout(location=8) out vec4 vTangent;` set to
`vec4(0.0)`. No skinned vertex-attribute changes, no `SkinnedVertex`/`SkinningMath` changes. The active
CPU-skinned path already produces `ModelVertex` via the 4-arg ctor (zero tangent) -> geometric-N
fallback, unchanged.

### 6. glTF wiring: explicit binding (`Scene3D.cs`)

- New public `readonly struct SurfaceMaps` carrying `TextureHandle Albedo, Normal, Roughness` (an invalid
  handle in any slot -> that slot's renderer default). Add `LoadMesh(GltfMesh, SurfaceMaps)`. Existing
  `LoadMesh(GltfMesh)` and `LoadMesh(GltfMesh, TextureHandle)` untouched.
- Textures are loaded with the existing `LoadTexture(string)` / `LoadTexture(byte[],int,int)`; normal and
  roughness PNGs are just textures.
- Skinned `LoadSkinnedMesh` overloads stay albedo-only this release.

### 7. Smooth/Realistic post preset (`PixelPostProcessSettings.cs`)

`Post` is a get-only property holding a mutable settings object, so the preset is an INSTANCE method
that mutates in place (not a static factory): e.g. `void UseSmoothPreset()` setting `CelBands = 0`,
`Quantize = false`, `Dither = false`, `Outline = false`, `Starfield = false` (lighting/colours left
alone). Document that a realistic material is otherwise still quantized/outlined by the post chain.

## Tests

### Headless (CPU), always run

- `ModelVertex.SizeInBytes == 64` and `Marshal.SizeOf<ModelVertex>() == 64`.
- Tangent ctors: 3-arg and 4-arg leave `Tangent == Vector4.Zero` (back-compat); 5-arg sets it.
- `MeshAssembler`: a known UV-mapped quad yields a tangent orthogonal to the normal, unit-length, with
  correct handedness sign; a degenerate-UV (all-zero UV) input yields zero tangent.
- Default-texture byte helpers: flat normal `(128,128,255,255)`, roughness `(0,0,0,255)`.
- A pure CPU mirror of the new shading math (a small `Render3D` helper, the way `SkinningMath` mirrors
  the skinned shader, commented as "mirrors ModelFrag"):
  - flat normal map (`(0,0,1)`) + any tangent reproduces the geometric normal (within FP epsilon);
  - a non-flat normal map tilts N in the expected direction;
  - higher roughness lowers `specStrength` and broadens `specExp` (lower exponent), and `rough = 0`
    reproduces `(vSpecParams.x, vSpecParams.y)` exactly.

### GPU golden (`KE_GPU_TESTS=1`, gated)

- New scene `scene3d_normalmap`: a tangent-bearing quad (built via `MeshAssembler` from explicit UV'd
  corners, so it carries a real tangent), bound with a bump normal map + a roughness gradient, cel bands
  off (smooth) so the perturbation reads cleanly, fixed key light + framing. Baked per-backend on
  **D3D11 + Vulkan** (CI runners) and **Metal** (local), via `KE_GPU_TESTS=1 KE_UPDATE_GOLDENS=1`.
- The existing `scene3d`, `scene3d_textured`, `scene3d_fill`, `scene3d_texbillboard` goldens MUST pass
  with NO re-bake = the byte-identical regression proof for the no-map path AND the Metal mis-bind
  tripwire (a mis-bound albedo/normal would shift the textured golden grossly).

### Manual (user)

- A windowed Metal run is the real cross-backend confidence check for the new bindings (the unit tests
  cannot run the GLSL shader; the CPU mirror + goldens are proxies). Boot command handed off at the end.

## Risks / mitigations

- **Metal/Veldrid multi-texture mis-bind** (the documented hazard): mitigated by mirroring the proven
  `EdgeFrag` grouped-textures-then-sampler layout, and by the existing textured golden as the tripwire.
  Still REQUIRES the manual Metal device run before release.
- **Bit-identity of the no-map path**: guaranteed by (a) zero-tangent meshes never building a TBN, and
  (b) `rough = 0` collapsing the spec terms to today's exact expressions (`mix(a,b,0)=a`, `x*(1-0)=x`).
  Validated by the existing goldens not needing a re-bake.
- **CPU-mirror drift** from the GLSL: the mirror is documented as authoritative-by-convention and is
  backed by the GPU golden, same arrangement as `SkinningMath`.

## Release ritual (per CLAUDE.md)

Bump `<KhaozEngine5xVersion>` 7.22.0 -> 7.23.0; `CHANGELOG.md` (detailed, newest-first) +
`CHANGENOTES.md` (one-line digest) in the same commit; update the three guarded declarations
(`docs/CONSUMERS.md` engine current version, `docs/ROADMAP.md` current released version, `README.md`
`<PackageReference>` example); `dotnet pack -c Release -o ./local-feed`; commit; tag `v7.23.0`; push
`main` + tag. Docs note in USING-KHAOZENGINE: normal/roughness maps + the Smooth preset; a realistic
material is still quantized/outlined unless the post chain is dialled down.
