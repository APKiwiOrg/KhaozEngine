# P0 Stage 3 Phase 3c — migrate Render3D + Windowing off Veldrid GPU (5.27.0-experimental)

Migrates **Render3D** fully onto the `KhaozEngine.Gpu` interface (drop its Veldrid GPU refs), retypes
`Frame.Commands` to `IGpuCommandList`, and stops `AppWindow` exposing raw Veldrid GPU types. After this,
**Render2D + Render3D are Veldrid-free**, and the only Veldrid usage in the renderer/windowing stack is
KhaozEngine.Gpu (all GPU) + Windowing's SDL2 window/input platform (see scope note). Verified by BOTH goldens
staying pixel-identical (`KE_GPU_TESTS=1`).

Builds on 3b: the full `KhaozEngine.Gpu` interface already exists (`IGpuDevice`/`IGpuResourceFactory`/
`IGpuCommandList` + resource handles + Gpu* enums/descriptions). Render2D already migrated. Use that API.

## Scope note (what stays Veldrid in Windowing)
The GPU backend seam is about GPU types, not the windowing/input platform. Windowing creates the SDL2 window +
pumps input via `Veldrid.Sdl2`/`Veldrid.StartupUtilities` (`Sdl2Window`, `InputSnapshot`, `SdlGamepadPoller`).
3c does NOT abstract SDL2 — that's a separate future item (Silk.NET windowing). So after 3c, **Windowing keeps a
`Veldrid.Sdl2`/`Veldrid.StartupUtilities` reference for the window/input ONLY, and exposes NO Veldrid GPU types**
(`AppWindow.Device`/`MainSwapchain` go away / internal). The window-creation path already routes through
`GpuDeviceContext.CreateWindow` (3a), which returns the `Sdl2Window`. That's fine — it's the platform layer.

## Render3D migration (must become fully Veldrid-free: csproj + source)
Rewrite every Veldrid GPU usage to `IGpu*`:
- `Rendering/ModelRenderer.cs`, `Rendering/PixelPostProcess.cs`, `Internal/RenderResources.cs`,
  `Rendering/LineRenderer.cs`, `Rendering/BillboardRenderer.cs`, `Scene3D.cs`: device/factory/commandlist/
  buffers/textures/framebuffers(MRT)/pipelines(blend+depth+raster+multi-vertex-layout incl. instance step
  rate)/resource-layouts/sets/samplers/shaders(from SPIR-V) → the engine types + descriptions. Preserve the
  exact pipeline state (the instancing + MRT + post chain + depth-less-equal + FaceCullMode.None + the additive/
  alpha overlay blends) — a wrong mapping shows as a golden failure.
- `Render3DSurface.cs`: consume `frame.Commands` as `IGpuCommandList` + the window's `IGpuDevice`.
- `Render3DSnapshot.cs`: build the offscreen device + targets via `GpuDeviceContext.CreateHeadless(...).GpuDevice`
  + factory (it disposes via `using` today — keep that; the 3D snapshot path already disposes fine, unlike 2D).
- `Internal/ShaderSources.cs`: if it only has a stray `using Veldrid;`, remove it (it's GLSL string consts).
- `Render3DHost.cs` (POC standalone host) + `Input/Key.cs` + `Input/FrameInfo.cs`: these must also lose Veldrid.
  `Render3DHost` already gets its device via `GpuDeviceContext` (3a) but its command/render loop + input pump use
  Veldrid — migrate the GPU parts to `IGpu*`. If `Input/Key.cs`/`FrameInfo.cs` map from `Veldrid.Sdl2` input, EITHER
  migrate them to consume Windowing's input OR (cleaner, per the audit's "demote the POC") mark `Render3DHost`/
  `Render3D.Input.*` `internal`/`[Obsolete]` and exclude them from the public Veldrid-free requirement IF they
  can't be cleanly de-Veldrided without scope creep — but they must still COMPILE without Render3D referencing
  Veldrid. Pragmatic: make `Render3DHost` + `Input/Key.cs` + `FrameInfo.cs` `internal` and migrate their GPU bits;
  flag if you instead delete them (don't delete without noting it).
- `KhaozEngine.Render3D.csproj`: remove `Veldrid`, `Veldrid.SPIRV`, `Veldrid.StartupUtilities` PackageReferences.
  Keep `SharpGLTF.Core`, `Newtonsoft.Json` pin (needed? Newtonsoft came via Veldrid.SPIRV — if nothing else
  needs it, drop it; verify build), `KhaozEngine.Ecs`, `KhaozEngine.Windowing`, `KhaozEngine.Gpu`.

## Frame.Commands retype + AppWindow (Windowing)
- `Frame.Commands` becomes `IGpuCommandList` (was Veldrid `CommandList`). Remove the transitional
  `Frame.GpuCommands` + the `GpuCommandLists.Wrap` bridge (no longer needed). Update `Render2DSurface` to use
  `frame.Commands` (now `IGpuCommandList`) instead of `frame.GpuCommands`.
- `AppWindow`: create the frame command list via `IGpuDevice.Factory.CreateCommandList()`; drive the loop via
  `IGpuCommandList` (Begin/SetFramebuffer(device.SwapchainFramebuffer)/ClearColorTarget/End) + `IGpuDevice`
  (Submit/Present) + on resize `device.ResizeSwapchain`. REMOVE the public `Device` (Veldrid GraphicsDevice) and
  `MainSwapchain` (Veldrid Swapchain) — keep `GpuDevice` (IGpuDevice) + `Backend` + `Capabilities`. The Sdl2Window
  + input pump stay (platform layer). `GpuCommandLists.Wrap` can be removed from KhaozEngine.Gpu if nothing else
  uses it after the retype.
- Clip-Y/depth: the `GpuCapabilities` are available; the actual GL/Vulkan clip-Y branching stays a documented
  follow-up for real-hardware bring-up (do NOT change the Metal math — keep the goldens pixel-identical). You may
  remove the `// TODO Phase 3c` markers and replace with `// TODO cross-platform bring-up: derive from
  Capabilities` if you prefer, but behaviour on Metal must be identical.

## Files / Release
- Modify the Render3D files above + `KhaozEngine.Windowing/AppWindow.cs` (+ `Frame`) + `KhaozEngine.Render2D/
  Render2DSurface.cs` (frame.Commands retype). Possibly remove `KhaozEngine.Gpu/GpuCommandLists.cs` if unused.
- Bump 5.26.0 → 5.27.0-experimental, CHANGELOG, pack 7 pkgs.

## Verification
- `dotnet build KhaozEngine.slnx` clean.
- **`grep -rn "Veldrid" KhaozEngine.Render3D --include=*.cs` (excl bin/obj) returns NOTHING** and Render3D.csproj
  has no Veldrid PackageReference. Report it.
- `grep` Windowing: `AppWindow` exposes no Veldrid GPU type publicly (Device/MainSwapchain gone); Veldrid.Sdl2/
  StartupUtilities usage remains ONLY for the window/input (acceptable per scope note).
- `dotnet test` green (default; report count).
- **`KE_GPU_TESTS=1 dotnet test --filter FullyQualifiedName~Golden`** — BOTH 3D + 2D goldens pass pixel-identical.
  Do NOT re-bake. If the 3D golden fails, a pipeline/format/blend/MRT mapping is wrong — fix it.
- Controller eyeballs a 3D scene.
