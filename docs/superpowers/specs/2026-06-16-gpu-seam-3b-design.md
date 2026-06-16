# P0 Stage 3 Phase 3b — full GPU interface + migrate Render2D (5.26.0-experimental)

Builds the COMPLETE engine-owned GPU abstraction in `KhaozEngine.Gpu` (covering the Veldrid surface BOTH
renderers use, so phase 3c just migrates Render3D against it), and migrates **Render2D** onto it — dropping
Render2D's direct Veldrid reference. Verified by the 2D golden (`KE_GPU_TESTS=1`) staying pixel-identical.

Design context: `docs/superpowers/specs/2026-06-16-gpu-backend-seam-design.md`. Phase 3a already shipped
`GpuBackendKind`/`GpuCapabilities`/`GpuBackendSelector`/`GpuDeviceContext` (the latter currently exposes the raw
Veldrid `GraphicsDevice` transitionally).

## Surface to cover (enumerated from Render2D + Render3D + Windowing usage)
Device/factory: device submit/wait/updateBuffer/map-unmap/main-swapchain-framebuffer; factory creates
buffer/texture/framebuffer/sampler/resource-layout/resource-set/graphics-pipeline/command-list and shaders from
SPIR-V. CommandList: begin/end, setFramebuffer, clearColorTarget/clearDepthStencil, setPipeline,
setGraphicsResourceSet, setVertexBuffer, setIndexBuffer, setScissorRect/clearScissor, draw, drawIndexed,
updateBuffer, copyTexture. Resources: buffer, texture, sampler, framebuffer, pipeline, resourceLayout,
resourceSet, shaderSet. Descriptions: buffer/texture/sampler/framebuffer/resourceLayout/resourceSet/pipeline
(blend+depth+rasterizer+shaderSet[multiple vertex layouts incl. instance-step-rate]+outputs+layouts)/shader.
Enums: pixelFormat, textureUsage, bufferUsage, indexFormat, primitiveTopology, shaderStages, resourceKind,
faceCull, polygonFill, frontFace, comparisonKind, blendFactor, blendFunction, samplerFilter, samplerAddressMode,
mapMode. Misc: clear colour as `Vector4` (not RgbaFloat), `MappedData` (for staging readback), output
description.

## `KhaozEngine.Gpu` interface to build (Veldrid hidden inside the impl)
Mirror the above as engine-owned types. Sketch (the implementer fills the exact members from the usage):
```
public interface IGpuDevice : IDisposable {
    GpuBackendKind Backend { get; } GpuCapabilities Capabilities { get; }
    IGpuResourceFactory Factory { get; }
    IGpuFramebuffer SwapchainFramebuffer { get; }
    void Submit(IGpuCommandList cl); void WaitForIdle();
    void UpdateBuffer<T>(IGpuBuffer b, uint offsetBytes, ReadOnlySpan<T> data) where T: unmanaged;
    void UpdateBuffer<T>(IGpuBuffer b, uint offsetBytes, T[] data) where T: unmanaged;   // convenience
    void UpdateBuffer<T>(IGpuBuffer b, uint offsetBytes, in T data) where T: unmanaged;   // single struct
    MappedData Map(IGpuTexture staging, MapMode mode); void Unmap(IGpuTexture staging);
    void ResizeSwapchain(uint w, uint h); void Present();
}
public interface IGpuResourceFactory {
    IGpuBuffer CreateBuffer(in GpuBufferDescription d);
    IGpuTexture CreateTexture(in GpuTextureDescription d);
    IGpuFramebuffer CreateFramebuffer(IGpuTexture? depth, params IGpuTexture[] colour);
    IGpuSampler CreateSampler(in GpuSamplerDescription d);   // + a PointSampler/LinearSampler convenience if useful
    IGpuResourceLayout CreateResourceLayout(in GpuResourceLayoutDescription d);
    IGpuResourceSet CreateResourceSet(in GpuResourceSetDescription d);
    IGpuShaderSet CreateShadersFromSpirv(string vertGlsl, string fragGlsl);
    IGpuPipeline CreateGraphicsPipeline(in GpuPipelineDescription d);
    IGpuCommandList CreateCommandList();
}
public interface IGpuCommandList : IDisposable {
    void Begin(); void End();
    void SetFramebuffer(IGpuFramebuffer fb);
    void ClearColorTarget(uint index, Vector4 rgba); void ClearDepthStencil(float depth);
    void SetPipeline(IGpuPipeline p); void SetGraphicsResourceSet(uint slot, IGpuResourceSet set);
    void SetVertexBuffer(uint slot, IGpuBuffer b); void SetIndexBuffer(IGpuBuffer b, GpuIndexFormat fmt);
    void SetScissorRect(uint index, uint x, uint y, uint w, uint h); void SetFullScissorRects();
    void Draw(uint vertexCount, uint instanceCount, uint vertexStart, uint instanceStart);
    void DrawIndexed(uint indexCount, uint instanceCount, uint indexStart, int vertexOffset, uint instanceStart);
    void UpdateBuffer<T>(IGpuBuffer b, uint offsetBytes, in T data) where T: unmanaged;
    void CopyTexture(IGpuTexture src, IGpuTexture dst);
}
// IGpuBuffer/Texture/Sampler/Framebuffer/Pipeline/ResourceLayout/ResourceSet/ShaderSet : IDisposable.
//   IGpuTexture exposes Width/Height/Format; IGpuFramebuffer exposes its OutputDescription-equivalent
//   (a GpuOutputDescription { GpuPixelFormat? Depth; GpuPixelFormat[] Colour; }) for pipeline creation.
// Descriptions + enums (GpuPixelFormat/GpuTextureUsage/GpuBufferUsage/GpuIndexFormat/GpuPrimitiveTopology/
//   GpuShaderStages[Flags]/GpuResourceKind/GpuFaceCull/GpuPolygonFill/GpuFrontFace/GpuComparison/GpuBlendFactor/
//   GpuBlendFunction/GpuSamplerFilter/GpuSamplerAddress/MapMode) re-declared engine-side, mapped to Veldrid in
//   the impl. GpuPipelineDescription carries blend (per-attachment, incl. additive + alpha presets),
//   depth-stencil (test/write/comparison or Disabled), rasterizer (cull/fill/frontface/scissor), topology,
//   vertex layouts (LIST — incl. per-instance step rate), the shader set, resource layouts, and outputs.
```
Implementation `KhaozEngine.Gpu/Internal/Veldrid*`: each wrapper holds the Veldrid object (internal accessor),
enums/descriptions map 1:1 to Veldrid. `GpuDeviceContext` gains `IGpuDevice GpuDevice { get; }` (the wrapped
device) ALONGSIDE the transitional raw `GraphicsDevice Device` (Render3D still uses raw until 3c). The headless
`Render2DSnapshot` path keeps its no-dispose workaround (phase 3a note).

## Migrate Render2D
Rewrite to use `KhaozEngine.Gpu` types instead of Veldrid, dropping Render2D's `Veldrid`/`Veldrid.SPIRV`
`<PackageReference>`s (it references `KhaozEngine.Gpu`):
- `Render2DCore` (Internal): takes an `IGpuDevice` (+ its `IGpuResourceFactory`) instead of `GraphicsDevice`.
- `SpriteBatch`: all Veldrid fields (`DeviceBuffer`/`Pipeline`/`ResourceLayout`/`ResourceSet`/`Sampler`/
  `CommandList`) become the `IGpu*` types; pipeline/blend/scissor via the engine descriptions; the persistent
  vertex buffer (phase-2 work) stays, retyped. Keep submission order + scissor behaviour identical.
- `Texture2D`/`SpriteFont`: wrap `IGpuTexture` instead of Veldrid `Texture`.
- `Render2DSurface`/`Render2DContext`: consume `AppWindow`'s `IGpuDevice` + an `IGpuCommandList` frame command
  list. `Frame.Commands` is still a Veldrid `CommandList` until 3c — so Render2DSurface bridges: get the
  `IGpuCommandList` from the device/context. (If `Frame.Commands` can't yet be an `IGpuCommandList` without
  touching Windowing/Render3D, have `AppWindow` ALSO expose the per-frame command list as an `IGpuCommandList`
  for 2D consumers this phase; full `Frame.Commands` retype is 3c.)
- `Render2DSnapshot`: build its offscreen device + target via `IGpuDevice`/factory; keep the no-dispose
  workaround.

## Files
- `KhaozEngine.Gpu/`: the full interface + `Internal/Veldrid*` impl + enums/descriptions (many small files).
- `KhaozEngine.Render2D/`: migrate all `.cs` off Veldrid; csproj drops Veldrid/Veldrid.SPIRV, keeps Stb* +
  KhaozEngine.Gpu + Windowing refs. (Veldrid.SPIRV moves entirely into KhaozEngine.Gpu via
  `CreateShadersFromSpirv`.)
- Tests: a few headless `KhaozEngine.Gpu` description/enum-mapping unit tests (no GPU); the 2D golden is the
  integration guard.
- Release: bump 5.25.0 → 5.26.0-experimental, CHANGELOG, pack 7 pkgs.

## Verification
- Default `dotnet test` green (goldens skipped).
- `KE_GPU_TESTS=1 dotnet test --filter ~Golden` — **2D golden MUST pass pixel-identical** (proves the Render2D
  migration is behaviour-equivalent); the 3D golden also still passes (Render3D untouched this phase). Do NOT
  re-bake.
- Confirm Render2D's csproj has NO `Veldrid` reference (grep). Controller eyeballs a 2D scene.
- CRITICAL: the engine enum/description → Veldrid mapping must be exact (a wrong blend/format/topology mapping
  shows as a golden failure). The persistent-buffer + scissor + submission-order behaviour from earlier phases
  must be preserved through the retype.
