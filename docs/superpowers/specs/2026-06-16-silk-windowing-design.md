# Replace Veldrid.Sdl2 with Silk.NET windowing (5.33.0)

Swap the desktop window + input platform from `Veldrid.Sdl2`/`Veldrid.StartupUtilities` (unmaintained, ships
only `osx-x64` natives → the `brew install sdl2` problem) to **Silk.NET windowing + input** (maintained, bundles
its GLFW natives per-RID across desktop, AOT-friendly, and is the foundation the mobile project needs). **The GPU
stays Veldrid** behind the `KhaozEngine.Gpu` seam — Silk.NET only replaces the window/input/loop.

The risky interop (Silk window → Veldrid Metal device on its native handle) was SPIKED and works:
`SwapchainSource.CreateNSWindow(window.Native.Cocoa.Value)` → `GraphicsDevice.CreateMetal(opts, scDesc)` rendered
+ presented cleanly, and Silk pulled the GLFW native automatically (no brew). This release productizes that.

## Clean split of responsibility
- **`KhaozEngine.Windowing`** owns the **Silk.NET window + input + loop** (refs `Silk.NET.Windowing` +
  `Silk.NET.Windowing.Glfw` + `Silk.NET.Input` + `Silk.NET.Input.Glfw` + `KhaozEngine.Gpu`; DROPS `Veldrid`/
  `Veldrid.StartupUtilities`).
- **`KhaozEngine.Gpu`** keeps owning the **Veldrid device**, but creates the windowed device **from a native
  handle** instead of creating its own SDL2 window (DROPS `Veldrid.StartupUtilities`/`Veldrid.Sdl2`; keeps
  `Veldrid` core; does NOT take a Silk dependency — it just receives an `IntPtr` handle + a kind).

## Part A — `KhaozEngine.Gpu`: device-from-handle
- New `public enum GpuWindowKind { Cocoa, Win32, X11, Wayland }` and
  `public readonly struct GpuWindowHandle { GpuWindowKind Kind; IntPtr Handle; IntPtr Display; ctor(...); }`
  (Display used only for X11; Wayland uses Handle=surface + Display=display).
- New `public static GpuDeviceContext GpuDeviceContext.CreateForWindow(in GpuWindowHandle window, uint width, uint height)`:
  build the Veldrid `SwapchainSource` per kind — `CreateNSWindow(Handle)` / `CreateWin32(Handle, hInstance=IntPtr.Zero)`
  / `CreateXlib(Display, Handle)` / `CreateWayland(Display, Handle)` — then `var scDesc = new SwapchainDescription(source, width, height, null, true, false);`
  and `GraphicsDevice.Create<Backend>(opts, scDesc)` where backend = `GpuBackendSelector.Select()` mapped (Metal→
  `CreateMetal(opts, scDesc)`, Vulkan→`CreateVulkan`, Direct3D11→`CreateD3D11`, OpenGL→throw not-supported here).
  Wrap in a `GpuDeviceContext` (ownsDevice:true). `SwapchainFramebuffer` is non-null.
- REMOVE the old `CreateWindow(string,int,int)` that returned `(Sdl2Window, ctx)` (AppWindow was its only caller).
  Keep `CreateHeadless()` UNCHANGED (snapshots/goldens use it — they must stay pixel-identical; verify).
- Drop `Veldrid.StartupUtilities`/`Veldrid.Sdl2` from the Gpu csproj.

## Part B — `KhaozEngine.Windowing`: Silk window + input + loop
Rewrite `AppWindow` on Silk.NET. PRESERVE the public API EXACTLY: `AppWindow(title,w,h)` ctor, `Run(Action<Frame>)`,
`Exists`, `Close()`, `GpuDevice`, `Backend`, `Capabilities`, `ClearColor`, `Dispose`, and `Frame` (Dt/Input/
Width/Height/Commands as `IGpuCommandList`). Consumers (surfaces, GameApp, Hardpoint) must not change.
- **Window creation in the ctor** (so `GpuDevice` is available immediately, as today): `Silk.NET.Windowing.Glfw.GlfwWindowing.RegisterPlatform();`
  then `Window.Create(WindowOptions.Default with { Size=new Vector2D<int>(w,h), Title=title, API=GraphicsAPI.None })`;
  `window.Initialize();` (creates the native window WITHOUT starting the loop, so the handle is valid). Read the
  native handle (`window.Native!.Cocoa`/`.Win32`/`.X11`/`.Wayland`) → build a `GpuWindowHandle` (the right kind for
  the platform) → `GpuDeviceContext.CreateForWindow(handle, (uint)w, (uint)h)` → device + command list. Wire
  `window.Resize += s => _device.ResizeSwapchain((uint)s.X,(uint)s.Y)` and `window.FramebufferResize`.
- **`Run(Action<Frame>)`**: wire `window.Render += dt => { build Frame; cl.Begin; SetFramebuffer(SwapchainFramebuffer);
  ClearColorTarget(0, ClearColor); onFrame(_frame); cl.End; Submit; Present; }` then `window.Run();`. Compute `Dt`
  from the `Render` callback's delta (clamp to 0.1). `Exists` ← `!window.IsClosing`; `Close()` ← `window.Close()`.
  Honor an optional **`KE_MAX_FRAMES`** env: after N rendered frames, `window.Close()` (lets a windowed smoke test
  run a few frames + exit cleanly).
- **Input**: `IInputContext _input = window.CreateInput();` Build `InputState` each frame from the context:
  - keyboards: use `IKeyboard.KeyDown`/`KeyUp` events to maintain a down-set + per-frame pressed/released (mirror
    today's edge logic), mapping `Silk.NET.Input.Key` → engine `Key` (port the existing `MapKey` switch to Silk's
    enum — Silk's `Key` names are close: `Key.A`..`Key.Z`, `Key.Number0`.., `Key.F1`.., arrows, `Space`, `Enter`,
    `Escape`, `ShiftLeft`, etc.).
  - mouse: `IMouse.Position` (Vector2), buttons via `IMouse.MouseDown`/`MouseUp` → engine `MouseButton`, scroll via
    `IMouse.Scroll` event → wheel delta. Track delta from last position.
  - gamepad: replace `SdlGamepadPoller` — read `_input.Gamepads` (Silk `IGamepad`: `Buttons`/`Thumbsticks`/
    `Triggers`) → engine `GamepadState` (map to the existing `GamepadButton`/deadzone model). If the gamepad
    mapping is sizable, a `SilkGamepadReader` helper analogous to the old poller is fine. (Touch is desktop-N/A;
    leave `InputState.Touches` empty as today.)
  - Build `InputState(downKeys, pressed, released, mouseDown, mousePressed, pos, delta, wheel, width, height, gamepads)`
    exactly as today.
- Delete `SdlGamepadPoller.cs` (replaced). Drop `Veldrid`/`Veldrid.StartupUtilities` from the Windowing csproj; add
  the Silk packages. Update the class doc (no longer "SDL2 / Veldrid.Sdl2"; now Silk.NET; remove "Metal only" if
  appropriate — backend is selected).
- Delete the per-sample `CopySdl2` MSBuild targets in `GuiSample`/`Render3DSample`/`MiniGame`/`Render2DSample`/
  `WindowingSample` (natives now come from Silk.NET.Windowing.Glfw). Hardpoint's `CopySdl2` is in its own repo —
  out of scope here (note it).

## Files / Release
- `KhaozEngine.Gpu`: new `GpuWindowHandle.cs` (+ enum), `GpuDeviceContext.CreateForWindow`, csproj ref changes.
- `KhaozEngine.Windowing`: rewrite `AppWindow.cs`, delete `SdlGamepadPoller.cs`, csproj ref changes; possible
  `SilkGamepadReader.cs`.
- Sample csprojs: drop `CopySdl2`.
- Tests: the windowed loop needs a display so it's smoke-verified (KE_MAX_FRAMES), not unit-tested. Add/keep any
  headless input-mapping unit tests that don't need a window. Headless goldens (CreateHeadless) are unaffected.
- Bump 5.32.0 → 5.33.0, CHANGELOG, pack 8 pkgs.

## Verification
- `dotnet build KhaozEngine.slnx` clean.
- `dotnet test` green (default; goldens skipped) — report count. **`KE_GPU_TESTS=1 dotnet test --filter Golden`**
  still passes (CreateHeadless path unchanged by the windowing swap) — report.
- **Windowed smoke on this Mac**: `KE_MAX_FRAMES=5 dotnet run --project GuiSample` (and `Render3DSample` without
  `--smoke`) → confirm it creates the Silk window + Veldrid Metal device, renders ~5 frames, exits cleanly (0).
  Report the exit + any console output. (The window appears briefly; we can't capture it, but a clean
  multi-frame run through the real Silk+Veldrid path is the proof, matching the spike.)
- Confirm `grep -rn Sdl2 KhaozEngine.Windowing KhaozEngine.Gpu --include=*.cs` is empty and the csprojs have no
  Veldrid.Sdl2/StartupUtilities refs.
