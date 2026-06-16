# KhaozEngine.Gpu (experimental, 5.x)

The GPU backend seam for the custom MonoGame-free stack. This is the foundation phase (Phase 3a) of
containing Veldrid behind an engine-owned layer.

What it owns today:

- **`GpuBackendKind`** — Metal / Vulkan / Direct3D11 / OpenGL.
- **`GpuBackendSelector`** — `Select()` reads the `KE_GRAPHICS_BACKEND` env override
  (`metal`/`vulkan`/`d3d11`/`gl`, case-insensitive) and otherwise probes the OS (macOS -> Metal,
  Windows -> Direct3D11, Linux -> Vulkan). `Select(string?, OSPlatformKind)` is the pure, headless-testable
  overload.
- **`GpuCapabilities`** — `ClipSpaceYInverted` / `DepthRangeZeroToOne`, read off the live device so the
  renderers can derive clip-Y / depth handling from the active backend instead of a baked Metal assumption.
- **`GpuDeviceContext`** — `CreateWindow(...)` (SDL2 window + device via `VeldridStartup`) and
  `CreateHeadless(options)` (offscreen device, Metal today) on the selected backend. Exposes `Backend`,
  `Capabilities`, and — **transitionally** — the raw Veldrid `GraphicsDevice` so the existing renderers keep
  working unchanged.

This is the ONLY package (post-migration) meant to reference Veldrid. Phase 3a deliberately keeps the
renderers using Veldrid internally via the transitional `GpuDeviceContext.Device` accessor; Phase 3b/3c wrap
the resource/command interface and the Veldrid exposure goes away.
