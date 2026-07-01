# KhaozEngine.Gpu

The GPU backend seam for the custom MonoGame-free stack: Veldrid contained behind an engine-owned layer.

What it owns today:

- **`GpuBackendKind`** - Metal / Vulkan / Direct3D11 / OpenGL.
- **`GpuBackendSelector`** - `Select()` reads the `KE_GRAPHICS_BACKEND` env override
  (`metal`/`vulkan`/`d3d11`/`gl`, case-insensitive) and otherwise probes the OS (macOS -> Metal,
  Windows -> Direct3D11, Linux -> Vulkan). `Select(string?, OSPlatformKind)` is the pure, headless-testable
  overload.
- **`GpuCapabilities`** - `ClipSpaceYInverted` / `DepthRangeZeroToOne`, read off the live device so the
  renderers can derive clip-Y / depth handling from the active backend instead of a baked Metal assumption.
- **`GpuWindowHandle`** - a native window handle (kind + handle/display) the windowing layer hands over, so
  this package needs no reference to the windowing library.
- **`GpuDeviceContext`** - `CreateForWindow(in GpuWindowHandle, width, height)` (device + swapchain for a
  Silk.NET/GLFW window) and `CreateHeadless()` (offscreen device) on the selected backend. Exposes `Backend`,
  `Capabilities`, and (**transitionally**) the raw Veldrid `GraphicsDevice` so the existing renderers keep
  working unchanged.

This is the ONLY package meant to reference Veldrid. The renderers still use Veldrid internally via the
transitional `GpuDeviceContext.Device` accessor; wrapping the resource/command interface behind a full
`IGraphicsBackend` seam so the Veldrid exposure goes away is still open (engine-audit stage 3).
