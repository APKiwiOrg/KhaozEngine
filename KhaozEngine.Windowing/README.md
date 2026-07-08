# KhaozEngine.Windowing

Windowing + input foundation for the custom MonoGame-free stack.

- `AppWindow` - owns the Silk.NET (GLFW) window, the GPU device + swapchain (via `KhaozEngine.Gpu`), and the
  frame loop. `Run(onFrame)` clears + presents around your callback; each `Frame` gives `Dt`, an engine-native
  `InputState`, framebuffer size, and the GPU command list to draw into. `AppWindow.Scaled(...)` fits a
  design-sized window to the display.
- **Present mode + frame cap** (since 9.23.0). The `AppWindow` ctor / `Scaled(...)` take a `PresentMode`
  (`Vsync` default, or `Immediate` = no vertical-blank sync) which selects the swapchain's `SyncToVerticalBlank`
  at creation. `AppWindow.FrameCapHz` (settable any time; 0 = uncapped) paces `Run` to a target Hz with a
  monotonic-clock `FrameLimiter`, independent of the swapchain's vsync - so a game can pin its render rate to an
  integer multiple of its fixed tick (e.g. 60/120 for a 30 Hz network tick) to keep presentation phase-aligned.
  This is the deterministic cap: the Veldrid Metal path does not reliably throttle from vsync alone, so a Mac
  client can free-run well above the refresh unless a `FrameCapHz` is set. `FrameLimiter` is a pure,
  headless-testable scheduler (`WaitBeforeNext(now)`).
- **Runtime display settings** (since 9.24.0). Present mode, frame cap, window mode, and resolution are all
  changeable live mid-session (no crash, no leaked swapchain), via the cohesive `IDisplaySettings` surface that
  `AppWindow` implements (also surfaced on `GameApp.Display`):
  - `AppWindow.PresentMode` is now a get/set property (was construction-only). Setting it reconfigures the live
    swapchain's vsync in place via `IGpuDevice.SyncToVerticalBlank` - no recreate. On Metal it engages
    `CAMetalLayer.displaySyncEnabled` but does not cap the CPU; pair vsync with a `FrameCapHz` (the setter warns
    once, `Console.Error`, if you select vsync with no cap on Metal - see `DisplaySettings.RequiresFrameCapWarning`).
  - `AppWindow.WindowMode` (`WindowMode { Windowed, BorderlessFullscreen, ExclusiveFullscreen }`) switches how the
    window occupies the display; `AppWindow.Resize(w, h)` sets the windowed size in logical points. The swapchain
    follows the new framebuffer via the existing `FramebufferResize` hook, so HiDPI is unchanged (the backbuffer
    tracks the physical drawable). Window-state policy is the pure, headless-tested `WindowModePlanner.Compute`.
  - `DisplaySettings` is an immutable `with`-friendly snapshot (present mode, frame cap, window mode, size, and
    window position `X`/`Y`); `CurrentDisplay` reads it, `ApplyDisplay(in DisplaySettings)` applies a whole snapshot
    at once (window mode, then resolution, then placement, then frame cap, then present mode). Every member is safe
    to call any time after the window exists.
- **Runtime window placement** (since 9.26.0). Window position + monitor are gettable / settable live, so a consumer
  can persist and restore the full placement (which monitor + position + size) across launches. On `IDisplaySettings`
  (implemented by `AppWindow`, surfaced on `GameApp.Display`):
  - `WindowX` / `WindowY` (get) + `MoveTo(x, y)` set the window top-left in virtual-desktop coordinates - symmetric
    with `WindowWidth` / `WindowHeight` + `Resize`. `MoveTo` applies immediately when windowed and is remembered as
    the windowed position to restore from a fullscreen mode.
  - `Monitors` (an `IReadOnlyList<MonitorInfo>` of index / name / bounds, empty on headless), `CurrentMonitorIndex`
    (the monitor holding the window, or -1), and `MoveToMonitor(index)` (centre on that monitor when windowed, cover
    it when borderless) drive a monitor picker.
  - `EnsureVisible()` clamps the window back on-screen when a restored position's monitor is gone. The `DisplaySettings`
    `X`/`Y` default to `PositionUnspecified` (leave the window where it is); when a snapshot carries a position,
    `ApplyDisplay` clamps it on-screen before moving, so a stale saved position self-corrects.
  - All placement math is pure and headless-tested in `WindowPlacement` (`MonitorIndexFor` / `CenterOn` /
    `ClampVisible`) plus the `MonitorInfo` record; only `AppWindow` touches the Silk monitor statics.
- `InputState` - per-frame keyboard + mouse + gamepad + touch snapshot (`IsDown`/`WasPressed` for
  `Key`/`MouseButton`, mouse position/delta/scroll, `Gamepad(i)`). Immutable; no MonoGame. `WasRepeated(Key)` /
  `WasTyped(Key)` surface OS key auto-repeat (`AppWindow` fills it from GLFW's `REPEAT` action; `WasPressed` stays
  press-edge only) so text fields hold-to-repeat.
- `AppWindow.SetIcon(params WindowIcon[])` sets the runtime window/taskbar icon. `WindowIcon` is one already-decoded,
  tightly-packed RGBA8 image (top-left origin); pass several sizes (16/32/48...) and GLFW picks per DPI. On Windows
  it sets the title bar + Alt-Tab (`WM_SETICON`) **and** the taskbar button: GLFW only sets `WM_SETICON`, which does
  not reach the taskbar (the button reads the window *class* icon), so `SetIcon` also copies the icon onto the class
  icon via `Platform.WindowsWindowIcon` - that is the actual taskbar-button fix. Linux/X11 apply it to the title bar
  + taskbar; macOS is a no-op (GLFW ignores window icons there) and never throws. Decode-free on purpose: this
  package takes pixels, not a PNG path, so it pulls in no image-decode dependency (the Game layer decodes via
  `Render2D.ImageRgba`). The Windows `.exe` icon shown when the app is not running is a separate per-game
  `<ApplicationIcon>`, independent of this API.
- `AppWindow.SetMacDockIcon(byte[] pngBytes)` sets the macOS **Dock / Cmd-Tab** icon from PNG bytes (the Cocoa
  counterpart to `SetIcon`, delegating to `Platform.ApplicationIcon`). GLFW cannot set the Dock icon and an
  unbundled `dotnet run` app has no `.app` icns, so without this such a run shows the generic document icon.
  Returns `false` off macOS / on empty input; never throws. `GameApp` calls it automatically from
  `GameAppOptions.WindowIconPath`.
- `AppWindow` is **born hidden** and `AppWindow.Show()` reveals it (idempotent). The Windows taskbar button is
  created when the window is first shown and reads the window *class* icon at that instant, so a host applies the
  runtime icon while hidden (which `SetIcon` syncs onto the class icon) then calls `Show()`, and the button is born
  with the right icon rather than GLFW's generic default. `Run(...)` also calls `Show()`, so a bare `AppWindow` host
  that never calls it still gets a visible window. `GameApp` calls `SetIcon` then `Show` in its constructor.
- `AppWindow.TrySetProcessAppUserModelId(string? appId)` (static) sets the process's Windows **AppUserModelID**
  before the first window is created, so Windows 10/11 groups and pins the running app's taskbar button by it. This
  is taskbar *identity* (grouping/pinning), not the button's icon - the icon is fixed by `SetIcon`'s class-icon sync
  above. Forwards to `Platform.WindowsAppId`; a no-op returning `false` off Windows or on a null/empty id, never
  throwing. Must run before constructing any `AppWindow`. `GameApp` calls it automatically from
  `GameAppOptions.AppUserModelId`.
- `AppWindow.TryAttachParentConsole(bool enable = true)` (static) makes a Windows `WinExe` head keep its console
  output: a Windows-subsystem exe (`OutputType=WinExe`, so no stray console window opens behind the game) has no
  console, so it attaches the process to the launching terminal's console and rewires stdout/stderr, keeping
  `Console.Write*` visible under `dotnet run` / cmd / PowerShell. `AppWindow.ProcessHasConsole` (static) reports
  whether a console is now owned. Both forward to `Platform.WindowsConsole`; no-ops off Windows / for a console exe
  / with no parent console / with redirected output, never throwing. The constructor calls the attach itself (so a
  bare `AppWindow` host is covered - it also un-loses the Metal-vsync `Console.Error` warning above on a WinExe),
  and `GameApp` calls it first (opt out with `GameAppOptions.SuppressParentConsoleAttach`).
- `InputManager` / `Pointer` - the higher-level read: unified pointer, edges, bounds helpers (`IsTapIn` etc.),
  region blocking, keyboard/gamepad/menu navigation.
- **Action maps + rebinding** (`KhaozEngine.Windowing.Actions`) - named actions instead of hardcoded key checks.
  A game declares `InputAction`s (`Button` / `Axis1D` / `Axis2D`) with default `InputBinding`s over `InputSource`s
  (key, mouse button, gamepad button, trigger, a whole stick via `WholeStick` for a 2D move/look, a single stick
  component via `StickAxis`, a two-key 1D axis, or a four-key WASD 2D composite; sticks/triggers take `invert`/`scale`,
  and `WholeStick` invert flips Y only). Multiple bindings per action combine: Button = OR, Axis1D = sum+clamp,
  Axis2D = per-component sum with WASD diagonal normalized to unit length; a whole stick keeps its magnitude and a
  component `StickAxis` is projected onto its own axis. `ActionMap` is BOTH the declaration and
  the pure per-frame runtime (`Update(InputState)` once per frame, then `IsDown`/`WasPressed`/`WasReleased`/`GetAxis`/`GetAxis2D`
  by id); edges are computed against the previous snapshot the same way `InputManager` does. Per-player maps read
  that player's gamepad; keyboard/mouse are global. `RebindOperation` is a pure snapshot-fed capture flow (captures
  the first eligible source on its press edge, sticks/triggers at full tilt, with an exclusion list; Escape cancels
  by default). `ActionMapSerializer` round-trips bindings as a VERSIONED JSON string (plain string in/out; a single
  future/unknown source kind degrades per-binding while the rest of the file survives, only bad JSON syntax discards
  the file, and `Load` returns an `ApplyResult` flagging a `FromFutureVersion` file); the game hands that string to its
  own settings store (no `Windowing -> Persistence` edge). `ActionMapController` is the turn-key wrapper: declare ->
  load persisted -> evaluate per frame -> auto-save on rebind. Action ids are opaque IDENTIFIERS, never localized;
  games localize labels game-side. Fully headless-testable.
- **Gamepad rumble** (`KhaozEngine.Windowing.Rumble`) - the OUTPUT seam mirroring the input rule: only `AppWindow`
  touches the Silk vibration motors, games reach it off `AppWindow.Rumble` / `GameApp.Rumble` (an `IRumble`), and a
  headless `NoopRumble` backs servers/tests. `SetRumble(player, low, high)` is a sustained per-motor level (heavy/low
  + light/high, each `[0,1]`); `Pulse(player, intensity, duration, highScale, shape)` is a fire-and-forget envelope
  (`RumbleDecay` = `Constant`/`Linear`/`EaseOut`) that the frame loop ticks to decay + auto-stop. Stacking policy:
  effective level per motor = MAX of the sustained level and every live pulse (not sum, so it never clips past 1 and
  a weak effect ending never drops a stronger one). The pure `RumbleMixer` (envelope + stacking) drives an
  `IRumbleOutput` sink and is headless-tested against a recording fake. **Reality: the current GLFW input backend
  exposes zero vibration motors (GLFW has no haptics API), so rumble is a graceful no-op today; the wiring is correct
  and a future SDL-backed window lights up through the same seam. On-device feel needs a physical smoke test.**
- `GameClock` (pause/timescale, plus `RealWallGapSeconds`/`LastRealTimestamp` - a UTC wall-clock gap per frame
  that survives OS sleep/suspend, which the frame `dt` does not, so a game can detect a resume), `DesignViewport`
  / `AdaptiveViewport` (letterbox/fill/stretch + responsive).
- `UiViewport` (since 10.12.0) - a point-space viewport for DPI-aware UI, implementing `IDesignViewport`.
  Authoring units are logical points and 1 point maps to `DpiScale` device pixels (no letterbox). `Width`/`Height`
  track the logical window size, so the UI reflows as the window resizes rather than magnifying, and `ScaleX`/`ScaleY`
  equal the DPI scale (stable per display, changing only on a monitor / OS-scale change, not on resize). It returns
  `SnapsToDevicePixels = true`. Drive it with `UiViewport.Update(Frame frame)` (or
  `Update(int framebufferW, int framebufferH, int logicalW, int logicalH)`). Because it implements `IDesignViewport`
  it drops straight into `SpriteBatch.Begin`, `Pointer.Update`, `ComputeScissor`, and the Gui screens/layout
  unchanged. Contrast with `DesignViewport` (a fixed design canvas Fit-scaled onto the framebuffer, which magnifies
  fractionally on HiDPI): use `DesignViewport` for the letterboxed game field, `UiViewport` for the crisp UI layer.

## `Frame` DPI members (since 10.12.0)

`Frame` (on `AppWindow`) exposes the logical window size and DPI scale for point-space UI:

| Member | Meaning |
|--------|---------|
| `LogicalWidth` / `LogicalHeight` | logical window size in points |
| `DpiScale` | device pixels per logical point (`Width / LogicalWidth`), e.g. 1 standard, 2 Retina, 1.5 on a 150%-scaled display |

`Frame.Width`/`Height` remain the device-pixel framebuffer size. Bake point-space UI fonts at `frame.DpiScale`
and snap UI geometry to whole multiples of it.

The 5.x renderers (`Render2D`, `Render3D`) build on this. Silk.NET windowing ships GLFW natives bundled per-RID,
so there is no SDL2/brew step. Touch is mobile-deferred (no 5.x mobile-windowing head yet).
