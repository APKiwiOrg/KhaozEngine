# P0 Stage 2 — submission performance (5.24.0-experimental)

Second of three P0 hardening releases (see `docs/ENGINE-AUDIT-5x-2026-06-16.md`). Removes the two submission
ceilings: per-instance 3D draw/UBO upload, and per-frame GPU-buffer allocation in SpriteBatch. Guarded by the
golden-snapshot net from stage 1 (`KE_GPU_TESTS=1 dotnet test`) + a controller visual check. Public API of
`Scene3D.Draw`/`SpriteBatch.Draw` is UNCHANGED (this is an internal submission rewrite).

## Part A — 3D GPU instancing (KhaozEngine.Render3D)

Today `ModelRenderer.DrawInstance` does `UpdateBuffer(_ubo)` + draw PER instance, with per-instance data (Model,
Tint, Emissive, SpecParams) living in the shared UBO. Move per-instance data to an **instanced vertex buffer**
and draw each unique mesh ONCE with `instanceCount`. Result: 1 UBO upload/frame (frame uniforms only) + 1
instance-buffer upload/frame + one draw per UNIQUE mesh.

### Split the model UBO into per-frame vs per-instance
- **UBO (per frame, binding 0)** keeps ONLY: `mat4 ViewProj; vec4 LightDir; vec4 LightColor; vec4 Ambient;
  vec4 Params; vec4 FillDir; vec4 FillColor; vec4 CameraPos;` (drop `Model`, `Tint`, `Emissive`, `SpecParams`).
  New size = 1 mat4 + 7 vec4 = 64 + 112 = **176 bytes**.
- **Per-instance vertex stream (buffer slot 1, `InstanceStepRate = 1`)**: a struct
  `InstanceData { Matrix4x4 Model; Vector4 Tint; Vector4 Emissive; Vector4 SpecParams; }` = 64 + 48 = **112
  bytes**. Vertex-layout elements (with `instanceStepRate: 1`): four `Float4` for the Model rows + `Float4`
  Tint + `Float4` Emissive + `Float4` SpecParams.

### Shaders (`Internal/ShaderSources.cs`)
- `ModelVert`: UBO loses Model/Tint/Emissive/SpecParams. Add instance inputs at NEW locations after the vertex
  attributes (Position=0, Normal=1, Color=2, TexCoord=3): `layout(location=4..7) in vec4 IModel0..IModel3;`
  (rows of the model matrix), `layout(location=8) in vec4 ITint; layout(location=9) in vec4 IEmissive;
  layout(location=10) in vec4 ISpecParams;`. Reconstruct `mat4 Model = mat4(IModel0,IModel1,IModel2,IModel3);`
  (column-major — match how `InstanceData.Model` rows are written; verify the multiply order gives the same
  world transform as today `Model * vec4(Position,1)`; if a transpose is needed, do it). Pass `vTint`,
  `vEmissive`, `vSpecParams` as new `out`s (locations 5,6,7 after `vUv`=4) to the fragment stage.
- `ModelFrag`: UBO loses the four per-instance fields. Read `vTint`/`vEmissive`/`vSpecParams` from the new
  `in`s instead of the UBO. Lighting math otherwise identical (albedo = vColor.rgb * vTint.rgb, etc.).

### `ModelRenderer`
- UBO buffer 176 bytes; struct `FrameUbo { Matrix4x4 ViewProj; Vector4 Dir, Color, Ambient, Params, FillDir,
  FillColor, CameraPos; }`.
- `SetFrameUniforms(CommandList cl, Matrix4x4 viewProj, Vector3 cameraPos, PixelPostProcessSettings s)` — upload
  the FrameUbo once per frame.
- Add a growable instance `DeviceBuffer` (geometric growth like LineRenderer) + a method
  `DrawMeshInstanced(CommandList cl, DeviceBuffer vb, DeviceBuffer ib, int indexCount, uint instanceStart,
  uint instanceCount)` that sets vertex buffer slot 0 = mesh vb, slot 1 = the instance buffer (offset 0; use
  `instanceStart` as the `DrawIndexed` instanceStart so all instances share one buffer), index buffer, and
  `cl.DrawIndexed((uint)indexCount, instanceCount, 0, 0, instanceStart)`.
- The pipeline's `ShaderSetDescription` now has TWO vertex layouts (the existing per-vertex layout + the new
  per-instance layout with `instanceStepRate:1`). `BindPass` still binds pipeline + resource set 0 (the UBO).
- Keep `BeginModelPass` (clear) as-is.

### `Scene3D.RenderInternal`
- After `BeginModelPass`: `SetFrameUniforms(...)` once, `BindPass(...)` once.
- Group `_instances.Items` by `MeshHandle.Index`: build a single flat `InstanceData[]` ordered by mesh, plus a
  list of `(meshIndex, instanceStart, instanceCount)` runs. Upload the flat array to the instance buffer once.
  Then for each run, `DrawMeshInstanced(cl, mesh.Vb, mesh.Ib, mesh.IndexCount, start, count)`.
- Build `InstanceData.Model = inst.World`, `Tint = inst.Tint`, `Emissive = inst.Material.Emissive`,
  `SpecParams = new Vector4(inst.Material.Specular, inst.Material.Shininess, 0, 0)`.
- Allocation discipline: reuse a member `List<InstanceData>`/array + a member grouping buffer across frames
  (clear, not realloc), matching the engine's existing per-frame-allocation-free pattern. Grouping can be a
  stable sort by mesh index or a bucket pass — keep it allocation-light.

## Part B — persistent SpriteBatch vertex buffer (KhaozEngine.Render2D)

`SpriteBatch.Flush` currently `f.CreateBuffer(...)` + `verts.ToArray()` per run EVERY frame and disposes them
next frame (`_frameBuffers`). Replace with ONE persistent growable vertex buffer:
- Hold a `DeviceBuffer? _vb` + `uint _vbCapacityBytes`, grown geometrically (2x) when a frame's total vertex
  bytes exceed capacity (like LineRenderer.EnsureCapacity). Never disposed per frame; disposed in `Dispose()`.
- In `Flush`: compute the total vertex count across all runs, ensure capacity, then upload each run's vertices
  into the buffer at increasing byte offsets (`_gd.UpdateBuffer(_vb, offset, span)`), and
  `SetVertexBuffer(0, _vb)` + `Draw(count, 1, 0, vertexStart)` per run using the run's vertex start. Upload
  from the run's backing list via `CollectionsMarshal.AsSpan(list)` — NO `.ToArray()`.
- Remove the `_frameBuffers` per-frame create/dispose machinery. Keep the `_sets` texture→ResourceSet cache as
  is (that's a separate concern). Keep submission order + scissor behaviour identical.

## Files
- Modify `KhaozEngine.Render3D/Internal/ShaderSources.cs`, `Rendering/ModelRenderer.cs`, `Scene3D.cs`.
- Modify `KhaozEngine.Render2D/SpriteBatch.cs`.
- Tests: extend `KhaozEngine.Tests` where headless-checkable (e.g. a `Scene3D` instance-grouping unit if the
  grouping is factored into a pure helper; the rest is GPU-verified). Keep `SceneInstancesTests` green.
- Release: bump `<KhaozEngineVersion>` 5.23.0 -> 5.24.0-experimental, CHANGELOG, pack the 6 5.x packages.

## Testing + verification
- `dotnet test` ALL green (default; goldens skipped).
- **`KE_GPU_TESTS=1 dotnet test` — the stage-1 golden 3D + 2D snapshots MUST still PASS** (this is the primary
  regression guard for both rewrites; if the instanced render or the SpriteBatch buffer changed the image, the
  goldens fail). Do NOT re-bake the goldens unless the controller confirms an intended change — they should pass
  unchanged because the output must be pixel-equivalent.
- Controller additionally renders a many-instance 3D scene + a multi-texture 2D scene and eyeballs them.
- CRITICAL risks: (1) the instance-attribute layout/stride must match `InstanceData` and the shader instance
  inputs (a mismatch garbles all 3D); (2) the Model matrix row/column order from the 4 instance vec4s must
  reproduce today's `Model * pos` world transform (verify against the golden — an upside-down/scrambled result
  means the matrix reconstruction is transposed).
