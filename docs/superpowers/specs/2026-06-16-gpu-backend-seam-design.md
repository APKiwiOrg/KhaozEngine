# P0 Stage 3 — the `IGraphicsBackend` seam (full abstraction)

P0#1 from `docs/ENGINE-AUDIT-5x-2026-06-16.md`, the full-abstraction variant the user chose: contain Veldrid
entirely inside one engine-owned `KhaozEngine.Gpu` layer, rewrite every renderer against the engine types, so
(a) Veldrid never appears on any public API, and (b) Veldrid is swappable (a Silk.NET backend later is a new
`IGpuDevice` impl, not a renderer rewrite). Veldrid already abstracts Metal/Vulkan/D3D11/GL — this seam wraps
*that* so the engine owns the contract.

This is a MULTI-RELEASE initiative. Each phase is independently shippable and GOLDEN-VERIFIED
(`KE_GPU_TESTS=1 dotnet test` — the 3D + 2D goldens must stay pixel-equivalent throughout; a backend wrap that
changes the image means a wrapping bug).

## Target architecture
- **New package `KhaozEngine.Gpu`** — the ONLY package that references `Veldrid`/`Veldrid.SPIRV`/`Veldrid.Sdl2`.
  Owns: engine GPU interfaces + the Veldrid implementation + backend selection + capabilities + headless device.
- Windowing, Render2D, Render3D reference `KhaozEngine.Gpu` (NOT Veldrid). After migration, remove their direct
  Veldrid `<PackageReference>`s.
- No public API exposes a Veldrid type. `AppWindow` stops exposing `GraphicsDevice`/`Swapchain`; `Frame.Commands`
  becomes an engine `IGpuCommandList`.

### The engine GPU interface (designed to be backend-neutral, mirroring the subset the renderers actually use)
```
namespace KhaozEngine.Gpu;
public enum GpuBackendKind { Metal, Vulkan, Direct3D11, OpenGL }
public readonly struct GpuCapabilities { public bool ClipSpaceYInverted {get;} public bool DepthRangeZeroToOne {get;} }

public interface IGpuDevice : IDisposable {
    GpuBackendKind Backend { get; }
    GpuCapabilities Capabilities { get; }
    IGpuResourceFactory Factory { get; }
    void Submit(IGpuCommandList cl);
    void WaitForIdle();
    IGpuFramebuffer SwapchainFramebuffer { get; }   // for windowed; headless devices return their offscreen target
    void ResizeSwapchain(uint w, uint h);
    void Present();
    // buffer upload + staging readback (used by snapshots):
    void UpdateBuffer<T>(IGpuBuffer buf, uint offsetBytes, ReadOnlySpan<T> data) where T : unmanaged;
    MappedData Map(IGpuTexture staging); void Unmap(IGpuTexture staging);
}
public interface IGpuResourceFactory {
    IGpuBuffer CreateBuffer(in GpuBufferDescription d);
    IGpuTexture CreateTexture(in GpuTextureDescription d);
    IGpuFramebuffer CreateFramebuffer(IGpuTexture? depth, params IGpuTexture[] color);
    IGpuSampler CreateSampler(in GpuSamplerDescription d);
    IGpuResourceLayout CreateResourceLayout(in GpuResourceLayoutDescription d);
    IGpuResourceSet CreateResourceSet(in GpuResourceSetDescription d);
    IGpuShaderSet CreateShadersFromSpirv(byte[] vertGlsl, byte[] fragGlsl); // wraps Veldrid.SPIRV CreateFromSpirv
    IGpuPipeline CreateGraphicsPipeline(in GpuPipelineDescription d);
    IGpuCommandList CreateCommandList();
}
public interface IGpuCommandList : IDisposable {
    void Begin(); void End();
    void SetFramebuffer(IGpuFramebuffer fb);
    void ClearColorTarget(uint index, Vector4 rgba); void ClearDepthStencil(float depth);
    void SetPipeline(IGpuPipeline p);
    void SetGraphicsResourceSet(uint slot, IGpuResourceSet set);
    void SetVertexBuffer(uint slot, IGpuBuffer b); void SetIndexBuffer(IGpuBuffer b, GpuIndexFormat fmt);
    void SetScissor(uint x, uint y, uint w, uint h); void ClearScissor();   // (Render2D needs scissor)
    void Draw(uint vertexCount, uint instanceCount, uint vertexStart, uint instanceStart);
    void DrawIndexed(uint indexCount, uint instanceCount, uint indexStart, int vertexOffset, uint instanceStart);
    void UpdateBuffer<T>(IGpuBuffer b, uint offsetBytes, ref T data) where T : unmanaged;
    void CopyTexture(IGpuTexture src, IGpuTexture dst);
}
// IGpuBuffer/Texture/Framebuffer/Sampler/ResourceLayout/ResourceSet/ShaderSet/Pipeline : IDisposable opaque handles.
// GpuBufferDescription/... mirror the Veldrid description fields the renderers use; enums (GpuPixelFormat,
// GpuTextureUsage, GpuBlend*, GpuDepthState, GpuFaceCull, GpuPrimitiveTopology, GpuVertexElementFormat/Semantic,
// GpuShaderStages, GpuResourceKind) re-declared engine-side, mapped to Veldrid in the impl.
```
The Veldrid impl (`Internal/Veldrid*`) holds the real Veldrid object inside each wrapper (internal accessor),
and maps the engine descriptions/enums to Veldrid ones. This is mechanical but must be exhaustive over the
subset the renderers use (enumerate it from ModelRenderer/PixelPostProcess/RenderResources/SpriteBatch/
Line/Billboard renderers + the snapshots).

## Phasing (each a release, golden-verified)
- **Phase 3a (this release, 5.25.0) — foundation, no renderer rewrite.** Create `KhaozEngine.Gpu` with
  `GpuBackendKind`, `GpuCapabilities`, and **`GpuBackendSelector.Select()`** (RuntimeInformation probe →
  Metal/Vulkan/D3D11/GL + `KE_GRAPHICS_BACKEND` env override) + a `GpuDevice.CreateForWindow(...)` /
  `CreateHeadless(...)` that wraps `VeldridStartup`/`GraphicsDevice.CreateMetal` behind the selector and exposes
  `GpuCapabilities`. Route `AppWindow` + `Render2DSnapshot` + `Render3DSnapshot` + `Render3DHost`'s device
  creation through it (kills the 4 hard-coded `GraphicsBackend.Metal` / `CreateMetal` sites). Derive the
  clip-Y/depth handling that `Camera2D`/`ModelRenderer` need from `GpuCapabilities` instead of the baked Metal
  assumption (centralized). This phase does NOT yet wrap the resource types — it lands the package, the backend
  selection, and the capability seam. Golden 3D+2D must still pass.
- **Phase 3b (next) — wrap the GPU resource/command interface + migrate Render2D.** Build the full
  `IGpuDevice`/`IGpuCommandList`/resource wrappers + Veldrid impl; rewrite `SpriteBatch`/`Render2DCore`/
  `Render2DSurface`/snapshot against them; drop Render2D's Veldrid ref. Golden 2D verifies.
- **Phase 3c — migrate Render3D + Windowing.** Rewrite `Scene3D`/`ModelRenderer`/`PixelPostProcess`/
  `RenderResources`/`Line`/`Billboard`/surfaces + `AppWindow`/`Frame` against the engine GPU types; `Frame.Commands`
  becomes `IGpuCommandList`; `AppWindow` stops exposing Veldrid. Drop Render3D/Windowing Veldrid refs. Golden 3D
  verifies.
- **Phase 3d — consumer migration + lockdown.** Update Hardpoint + samples to the new API; confirm NO public
  Veldrid type remains (a grep/test); document.

## Phase 3a scope (THIS release)
- Create `KhaozEngine.Gpu/KhaozEngine.Gpu.csproj` (5.x line, refs Veldrid + Veldrid.SPIRV + Veldrid.Sdl2 +
  Newtonsoft pin), README, add to slnx + Tests.
- `GpuBackendKind`, `GpuCapabilities`, `GpuBackendSelector.Select()` (probe + `KE_GRAPHICS_BACKEND` override:
  values metal/vulkan/d3d11/gl), and a thin `GpuDeviceContext` returned from `CreateForWindow`/`CreateHeadless`
  exposing the Veldrid `GraphicsDevice` (internal, transitional) + `GpuBackendKind` + `GpuCapabilities`. (3b/3c
  replace the internal Veldrid exposure with the full wrappers.)
- `AppWindow`, `Render2DSnapshot`, `Render3DSnapshot`, `Render3DHost`: obtain the device via `KhaozEngine.Gpu`
  (selector + factory) instead of the literal `GraphicsBackend.Metal`/`CreateMetal`. Windowing/Render2D/Render3D
  reference `KhaozEngine.Gpu`. On THIS dev box the selector resolves to Metal (unchanged behaviour).
- Centralize clip-Y/depth: add the capability to the device context; have `Camera2D` + the model/snapshot paths
  read it (Metal values match today, so the golden stays pixel-equivalent).
- Headless tests: `GpuBackendSelector` (env override parsing + default probe per OS — mockable via an injected
  OS/env seam, headless), `GpuCapabilities` plumbing. Golden 3D+2D pass with `KE_GPU_TESTS=1`.
- Release: bump 5.24.0 → 5.25.0-experimental, CHANGELOG, pack (now 7 pkgs incl. KhaozEngine.Gpu).

NOTE: Phase 3a deliberately keeps the renderers using Veldrid internally (via the transitional internal device
accessor); it lands the package, backend selection, and capability seam without the big resource-wrapper
rewrite (3b/3c). This sequences the risk and keeps each release golden-verifiable.
