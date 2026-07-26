# KhaozEngine.Gpu

The GPU backend seam for the custom MonoGame-free stack: Veldrid contained behind an engine-owned layer.

What it owns today:

- **`GpuBackendKind`** - Metal / Vulkan / Direct3D11 / OpenGL.
- **`GpuBackendSelector`** - `Select()` reads the `KE_GRAPHICS_BACKEND` env override
  (`metal`/`vulkan`/`d3d11`/`gl`, case-insensitive) and otherwise probes the OS (macOS -> Metal,
  Windows -> Direct3D11, Linux -> Vulkan). `Select(string?, OSPlatformKind)` is the pure, headless-testable
  overload.
- **`GpuCapabilities`** - `ClipSpaceYInverted` / `DepthRangeZeroToOne` (so renderers derive clip-Y / depth
  handling from the active backend instead of a baked Metal assumption), plus diagnostics: `DeviceName` (the GPU
  adapter/driver), `SamplerAnisotropy`, `SamplerLodBias` (whether those sampler levers are supported),
  `MaxMsaaSampleCount` (the largest MSAA sample count the engine's MRT formats support, for building/clamping an AA
  menu), and `SupportsShadowMaps` (whether the device can render + sample an R32_Float depth target, gating the
  `ShadowMode.ShadowMap` tier in Render3D). Read off the live device.
- **MSAA plumbing** - `GpuTextureDescription.SampleCount` (and `IGpuTexture.SampleCount`) make a multisampled render
  target; `GpuOutputDescription.SampleCount` / `WithSampleCount` carry the count so a pipeline matches its
  framebuffer (read a live multisampled framebuffer's count off `IGpuFramebuffer.Outputs`); and
  `IGpuCommandList.ResolveTexture(src, dst)` resolves a multisampled target into a single-sample texture. Default
  sample count 1 (single-sample) everywhere, so existing render paths are unchanged.
- **`GpuWindowHandle`** - a native window handle (kind + handle/display) the windowing layer hands over, so
  this package needs no reference to the windowing library.
- **`GpuDeviceContext`** - `CreateForWindow(in GpuWindowHandle, width, height, syncToVerticalBlank = true)` (device
  + swapchain for a Silk.NET/GLFW window; the vsync flag feeds both the device options and the swapchain, since
  9.23.0) and `CreateHeadless()` (offscreen device) on the selected backend. Exposes `Backend`,
  `Capabilities`, and the engine-owned `IGpuDevice`; the raw Veldrid device is private to the context (the
  transitional accessor that used to be here is gone, and `Capabilities` is read once so the context's copy and
  the device's cannot drift). Device creation and disposal are serialized process-wide behind a single static gate, on
  every backend: concurrent device creation races the Vulkan loader's dispatch setup (observed as
  `vkGetDeviceQueue` aborts on Mesa lavapipe under full test-suite parallelism), and creation/disposal are rare
  enough that the serialization costs nothing measurable. Callers see no API change, it only affects the
  interleaving of concurrent create/dispose calls across threads.
- **`IGpuDevice.SyncToVerticalBlank`** (settable, since 9.24.0) - flip vsync on a live windowed device: it
  reconfigures the main swapchain in place (no recreate, no leaked swapchain, size + depth preserved; on Metal it
  reaches `CAMetalLayer.displaySyncEnabled`). A no-op mirrored value on a headless device (Veldrid throws setting
  it with no main swapchain). `AppWindow.PresentMode` routes through it for runtime present-mode switching.
- **Compute** (since 15.2.0) - `IGpuResourceFactory.CreateComputeShaderFromSpirv(computeGlsl)` compiles a GLSL 450
  compute source into an `IGpuComputeShader`, and `CreateComputePipeline(in GpuComputePipelineDescription)` builds an
  `IGpuComputePipeline` over it plus its resource layouts. Both handle types are separate from the graphics
  `IGpuShaderSet` / `IGpuPipeline`, so a compute pipeline bound for a draw is a compile error. `IGpuCommandList`
  gains `SetComputePipeline`, `SetComputeResourceSet` (plain and dynamic-offset), `Dispatch(x, y, z)`, and
  `CopyBuffer`. Storage resources: `GpuTextureUsage.Storage` for a read-write image (bound via
  `GpuResourceKind.TextureReadWrite`), and the existing `GpuBufferUsage.StructuredBufferReadOnly`/`ReadWrite` for
  storage buffers. Readback: `IGpuDevice.Map`/`Unmap` take a buffer as well as a texture, and
  `GpuReadback.ReadBuffer<T>` wraps the staging-copy-map-unmap sequence. Gate on `GpuCapabilities.SupportsCompute`;
  creating a compute shader or pipeline on a device without it throws.
  - **The workgroup size comes from the shader, not from you.** `IGpuComputeShader.ThreadGroupSizeX/Y/Z` is read
    out of the compiled SPIR-V module's `LocalSize` execution mode and is what the pipeline is built with, so there
    is no second copy of `layout(local_size_x = ...)` to drift (which would be invisible on Vulkan/D3D11 and
    silently wrong on Metal, the only backend that reads it). A source with no literal workgroup declaration throws.
  - **Ordering rules, since there is no barrier call.** Compute writes a storage texture that a graphics pass then
    samples: record BOTH in the SAME command list and create the texture `Storage | Sampled`. A dispatch that reads
    what an earlier dispatch wrote: separate them with `End` + `Submit` + `WaitForIdle`. Full reasoning per backend
    is on `IGpuCommandList`, in `docs/USING-KHAOZENGINE.md`, and in `docs/design/GPU-COMPUTE-DESIGN-2026-07-26.md`.
- **`ShaderValidation`** - `ValidatePair(vertexGlsl, fragmentGlsl, label?)` compiles a GLSL 450 vertex/fragment
  pair to SPIR-V and cross-compiles it to every backend target (HLSL, MSL, GLSL, ESSL) with NO `GraphicsDevice`,
  so a shader syntax error or a backend miscompile is caught in a fast GPU-free test loop instead of at first run
  on a real device of that backend. `ValidateCompute(computeGlsl, label?)` is the single-stage sibling for a
  compute shader. A compile failure throws `ShaderValidationException` naming the label and the failing
  stage/target. The engine's own shader-source tests use this to validate every embedded production shader; games
  can validate their custom shaders the same way in their own fast test suites.

This is the ONLY package meant to reference Veldrid, and the containment is complete: the resource, command and
device surface is the engine-owned `IGpuDevice` / `IGpuResourceFactory` / `IGpuCommandList` interface set, the
backend implementation lives in `Internal/`, and no Veldrid type reaches the public API (asserted by reflection in
`GpuPublicApiTests` here and `VeldridLockdownTests` for the consumer packages). Swapping the backend is a new
`IGpuDevice` implementation, not a consumer-visible change.
