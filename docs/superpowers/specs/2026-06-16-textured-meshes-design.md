# Textured meshes for KhaozEngine.Render3D (5.35.0)

Add per-mesh albedo texturing to the Render3D model pass. Today meshes are colour-baked only (lit
`vColor * vTint`); this samples a bound texture and multiplies it into the albedo. The groundwork is already
present: `ModelVertex` has a `Uv` field, the model shaders thread `vUv` through both stages (the frag shader
comment literally says "texturing is a later step"), `GltfLoader` reads `TEXCOORD_0`, and every primitive in
`MeshPrimitives` plus `MeshBuilder` already generates / preserves real UVs. So this release wires the sampling
end-to-end, NOT a UV overhaul.

**Safety invariant (the whole design hinges on this):** an untextured mesh samples a 1x1 WHITE texture, so
`white(1,1,1) * vColor * vTint == vColor * vTint` and every existing scene renders PIXEL-IDENTICAL. The committed
`scene3d.*` / `scene2d.*` goldens MUST stay byte/tolerance-identical after this change. A NEW golden scene proves
the texturing path.

## Part A — shader (`KhaozEngine.Render3D/Internal/ShaderSources.cs`, `ModelFrag`)
Add an albedo texture + sampler to the model fragment shader (binding 1 + 2 in set 0; the UBO stays binding 0):
```glsl
layout(set=0, binding=1) uniform texture2D Albedo;
layout(set=0, binding=2) uniform sampler AlbedoSamp;
```
In `main`, sample at `vUv` and fold into albedo:
```glsl
vec3 texRgb = texture(sampler2D(Albedo, AlbedoSamp), vUv).rgb;
vec3 albedo = vColor.rgb * vTint.rgb * texRgb;
```
(Keep `oColor.a = 1.0` - opaque; texture alpha is ignored this release. `ModelVert` is unchanged: it already
outputs `vUv`. The vertex stage does not reference the new bindings, which is fine.)

## Part B — pipeline + per-mesh material set (`KhaozEngine.Render3D/Rendering/ModelRenderer.cs`)
The resource layout currently has ONE element (the UBO) and `BindPass` binds one global set per frame
(`_set` = UBO only, ModelRenderer.cs:51-53, 139-143). Because the texture varies per mesh, move the set binding
to per-mesh.

1. **Layout** (ModelRenderer.cs:51-52) becomes 3 elements:
   - `("U", UniformBuffer, Vertex|Fragment)` (unchanged, binding 0)
   - `("Albedo", TextureReadOnly, Fragment)` (binding 1)
   - `("AlbedoSampler", Sampler, Fragment)` (binding 2)
2. **Renderer-owned defaults** (ctor): a shared linear sampler `_sampler` (`GpuSamplerDescription.Linear`, wrap
   address modes so UVs outside 0..1 tile), a 1x1 white `_white` texture (`R8G8B8A8UNorm`, `Sampled`, updated with
   `{255,255,255,255}`), and a default material set `_defaultSet = CreateResourceSet(layout, _ubo, _white, _sampler)`.
3. **New public method** `IGpuResourceSet CreateMaterialSet(IGpuTexture albedo)` =>
   `factory.CreateResourceSet(new GpuResourceSetDescription(_layout, _ubo, albedo, _sampler))`. (The UBO buffer
   handle is shared and updated each frame, so a set built once still sees fresh frame uniforms.)
4. **`BindPass`** (ModelRenderer.cs:139-143): bind the PIPELINE only; REMOVE the `SetGraphicsResourceSet(0, _set)`
   line (the set is now bound per mesh). Delete the now-unused `_set` field (the white default replaces it).
5. **`DrawMeshInstanced`** gains a material-set parameter:
   `DrawMeshInstanced(cl, vb, ib, indexCount, instanceStart, instanceCount, IGpuResourceSet? materialSet)` and as
   its first action calls `cl.SetGraphicsResourceSet(0, materialSet ?? _defaultSet);` then the existing vertex/
   index binds + `DrawIndexed`.
6. **Dispose**: also dispose `_white`, `_sampler`, `_defaultSet` (drop `_set`).

## Part C — Scene3D mesh texture storage + texture API (`KhaozEngine.Render3D/Scene3D.cs`)
1. **`Mesh` struct** (Scene3D.cs ~267-271) gains two fields: `IGpuResourceSet? MaterialSet` (the per-mesh set, or
   null => renderer's white default) and `IGpuTexture? OwnedTexture` is NOT stored here (textures are owned in a
   separate list, see below, so one texture can be shared by many meshes). Store only `MaterialSet`.
2. **Texture store**: a `List<IGpuTexture> _textures` on Scene3D. New public `readonly struct TextureHandle`
   (wraps an `int Index`; `IsValid`; a `default`/`Invalid`). New methods:
   - `public TextureHandle LoadTexture(string pngPath)` - decode via StbImageSharp
     (`ImageResult.FromMemory(File.ReadAllBytes(path), ColorComponents.RedGreenBlueAlpha)`), create an
     `R8G8B8A8UNorm` `Sampled` texture (`GpuTextureDescription.Texture2D`), `UpdateTexture(... img.Data ...)`,
     add to `_textures`, return the handle. (Add `StbImageSharp` to the Render3D csproj - same decoder Render2D
     uses.)
   - `public TextureHandle LoadTexture(ReadOnlySpan<byte> rgba, int width, int height)` - same but from raw RGBA
     (for procedural textures / tests / the golden checkerboard). (Use a `byte[]` param if `ReadOnlySpan` fights
     the `UpdateTexture(byte[], ...)` signature; match the GPU seam.)
3. **`LoadMesh` overload**: keep `public MeshHandle LoadMesh(GltfMesh mesh)` (untextured => MaterialSet null).
   Add `public MeshHandle LoadMesh(GltfMesh mesh, TextureHandle texture)`: build the GPU buffers as today, then
   `MaterialSet = _model.CreateMaterialSet(_textures[texture.Index])` and store it on the Mesh. (Validate the
   handle; invalid => treat as untextured.)
4. **Render loop**: wherever the per-mesh `DrawMeshInstanced` is called (the instanced draw loop), pass
   `mesh.MaterialSet` through to the new parameter.
5. **`UnloadMesh`**: dispose `mesh.MaterialSet` (if non-null) alongside Vb/Ib. (Do NOT dispose the texture here -
   it may be shared; textures are disposed in `Scene3D.Dispose`.)
6. **`Dispose`**: dispose every texture in `_textures` (and clear it), plus the existing teardown.

Note: ModelRenderer is internal to the package and owned by Scene3D, so `CreateMaterialSet` is internal-callable.
Verify Scene3D actually holds the `ModelRenderer` (`_model`); if the draw loop lives elsewhere, thread the set
the same way.

## Part D — golden coverage (`KhaozEngine.Tests/Gpu/GoldenSnapshotTests.cs`)
1. **Existing `Golden3D_FixedAsymmetricScene` (`scene3d`) MUST stay pixel-identical** - it now exercises the
   white-default path. Do NOT re-bake it; it is the proof the untextured path is unchanged. (If it drifts, the
   white-default wiring is wrong - investigate, don't re-bake.)
2. **New `[GpuFact] Golden3D_TexturedMesh`**: build a small procedural checkerboard texture (e.g. 8x8 or 64x64
   RGBA, two contrasting colours) via `scene.LoadTexture(rgba, w, h)`, load a textured mesh that has clean UVs
   (a `MeshPrimitives.Tile` / a box / a quad - pick one whose UVs span 0..1 so the checker is visible), draw it
   filling the view, capture, and `GoldenCompare.AssertOrUpdate("scene3d_textured", rgba, W, H)`. Keep the scene
   FIXED + deterministic (fixed camera framing, fixed transform) like the existing goldens. Bake the metal golden
   locally (`KE_GPU_TESTS=1 KE_UPDATE_GOLDENS=1`). The D3D11 golden for this new scene is baked on CI in the
   release step (see Verification) so the cross-platform matrix stays green.

## Part E — sample demo (`Render3DSample`)
Add a textured mesh to the sample so it can be eyeballed: generate a checkerboard (or load a PNG if one is handy),
`LoadTexture`, apply it to one primitive/mesh, and draw it alongside the existing models. A key toggle to show
textured vs untextured is a nice-to-have, not required. Honor the existing sample loop / `KE_MAX_FRAMES`.

## Out of scope (note as follow-ups, do NOT build)
- Texture alpha / cutout / transparency (model pass stays opaque).
- Mipmaps (MipLevels=1), anisotropy, per-instance textures (would break instancing; texture is per-mesh by
  design), texture atlases, normal/roughness maps. Per-mesh albedo only.
- Hardpoint adoption (applying a texture to the board/tiles/towers) is a SEPARATE follow-up after the engine ships.

## Tests
- The new `Golden3D_TexturedMesh` golden (gated `[GpuFact]`, needs a device).
- A headless unit test that does NOT need a device: assert `LoadTexture(rgba,w,h)` returns a valid
  `TextureHandle` and `LoadMesh(mesh, handle)` returns a valid `MeshHandle` (construct Scene3D headlessly if the
  existing tests show a pattern; if Scene3D needs a device, gate it `[GpuFact]` and just assert the handles +
  that a textured draw doesn't throw). Prefer reusing whatever headless seam the existing Scene3D tests use; if
  none, keep the texturing verification in the gated golden + the sample smoke.
- `TextureHandle` invalid/`default` handling (LoadMesh with an invalid handle falls back to untextured, no throw)
  - this part is pure and can be unit-tested if Scene3D construction allows.

## Release
- Bump `<KhaozEngine5xVersion>` 5.34.0 -> 5.35.0; CHANGELOG entry; update the Render3D `<Description>` to mention
  per-mesh albedo texturing. Add `StbImageSharp` PackageReference to KhaozEngine.Render3D.csproj (version-match
  what Render2D pins). Pack the 8 5.x packages to local-feed. Merge --no-ff, run the suite on main, pack
  canonical, tag `v5.35.0`, push main + tag.
- (Hardpoint adoption is a separate follow-up.)

## Verification
1. `dotnet build KhaozEngine.slnx` clean.
2. `dotnet test KhaozEngine.Tests` green (report counts).
3. **`KE_GPU_TESTS=1 dotnet test --filter Golden`**: the EXISTING `scene3d` + `scene2d` goldens MUST still pass
   pixel-identical (this is the white-default safety proof), and the new `scene3d_textured` golden passes after
   baking it on metal. Report.
4. Sample smoke: `KE_MAX_FRAMES=120 dotnet run --project Render3DSample` runs the textured path + exits 0. Report.
5. Cross-platform CI (the coordinator drives this after merge, like the prior release): after pushing, bake the
   D3D11 golden for the new `scene3d_textured` scene via `workflow_dispatch bake=true`, commit
   `scene3d_textured.direct3d11.txt`, and confirm the `cross-platform-gpu` workflow goes green (Metal + D3D11
   verify the new scene; Vulkan stays non-blocking). The existing `scene3d.direct3d11.txt` MUST remain valid
   (white default => unchanged).
6. `grep` confirms no Veldrid/StbImageSharp type leaks into Render3D's public API (TextureHandle is an opaque
   int wrapper; IGpuTexture stays internal).
