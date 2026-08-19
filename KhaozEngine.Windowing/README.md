# KhaozEngine.Windowing

Windowing + input foundation for the custom MonoGame-free stack.

- `AppWindow` - owns the Silk.NET (GLFW) window, the GPU device + swapchain (via `KhaozEngine.Gpu`), and the
  frame loop. `Run(onFrame)` clears + presents around your callback; each `Frame` gives `Dt`, an engine-native
  `InputState`, framebuffer size, and the GPU command list to draw into. `AppWindow.Scaled(...)` fits a
  design-sized window to the display.
- **The frame's pre-record phase.** `Run(onFrame, onPrepare)` is the same loop with a second
  callback, invoked each frame after the frame's `Dt` / input / size are latched and **before** the frame's command
  list is opened. `Run(onFrame)` is exactly `Run(onFrame, null)`, so nothing changes for a host that does not want
  it. Reach for it when something in your frame has to submit GPU work on a command list of its **own**: with
  Direct3D11 in immediate-context mode a command list IS the device's immediate context, so opening one while the
  frame's list is recording wipes the frame's bindings and faults the device a few draws later
  ([#423](https://github.com/APKiwiOrg/KhaozEngine/issues/423),
  [#429](https://github.com/APKiwiOrg/KhaozEngine/issues/429)). That is where a `Scene3D`'s `Begin` + draws +
  `PrepareFrame` belong on a windowed host. `GameApp` / `GameApp3D` already run there, so a game on those needs no
  change. Do NOT record into `Frame.Commands` from `onPrepare` - the list is not open yet (and on a
  render-suppressed frame never is). Both callbacks run on a render-suppressed frame, so update-side work keeps
  advancing while minimized.
- **Present mode + frame cap** (since 9.23.0). The `AppWindow` ctor / `Scaled(...)` take a `PresentMode`
  (`Vsync` default, or `Immediate` = no vertical-blank sync) which selects the swapchain's `SyncToVerticalBlank`
  at creation. `AppWindow.FrameCapHz` (settable any time) paces `Run` to a target Hz with a monotonic-clock
  `FrameLimiter`, independent of the swapchain's vsync - so a game can pin its render rate to an integer multiple
  of its fixed tick (e.g. 60/120 for a 30 Hz network tick) to keep presentation phase-aligned. `FrameLimiter` is a
  pure, headless-testable scheduler (`WaitBeforeNext(now)`).
- **Backend-aware auto cap + background throttle** (since 10.96.0). Sensible pacing by default, so a client no longer
  free-runs a whole core plus the GPU out of the box.
  - `FrameCap` is the frame-cap intent: `Auto` (the default - and `default(FrameCap)`), `Uncapped`, or `Hz(n)`.
    `AppWindow.FrameCap` (and the plain `new AppWindow(title, w, h)` ctor) default to `Auto`. `FrameCap.Resolve(backend,
    present, displayRefreshHz)` is pure: on the incumbent **`GpuBackendKind.Metal` + vsync** (where the Veldrid present
    does not throttle the CPU) it resolves to the display refresh, else `FrameCap.DefaultMetalAutoCapHz` (120).
    Everywhere else it stays uncapped, because vsync throttles there or `Immediate` asked for a free-run: **D3D11 /
    Vulkan**, their native backends, and the engine's own **`MetalNative`**, measured on 2026-08-11 as blocking the CPU
    in the drawable acquire for the whole vertical-blank wait. A consumer-set value always wins. `AppWindow.FrameCapHz`
    setter is the explicit int form (positive = fixed, 0 = intentional `Uncapped`), and its getter returns the RESOLVED
    effective cap. The one-time Metal-vsync warning (`Console.Error`, via the pure
    `DisplaySettings.RequiresFrameCapWarning` fed the resolved cap) fires ONLY for an explicit uncapped + vsync choice
    on the incumbent Metal backend, never for the resolved `Auto` default and never on `MetalNative`.
  - `AppWindow.BackgroundThrottle` (a `BackgroundThrottlePolicy`, default `Default` = ON) throttles a backgrounded
    window. **Minimized** (detected via Silk's `WindowState.Minimized`): skip render + present and idle at `MinimizedHz`
    (default 10). `Run` sets `Frame.RenderSuppressed` and still calls the callback so update-side simulation keeps
    advancing. **Unfocused but visible:** render capped to `UnfocusedHz` (default 15, or lower). `Disabled` opts out
    (render full-rate in the background). `BackgroundThrottlePolicy.Plan(activity, baseCapHz)` is the pure,
    headless-tested per-frame decision (render gate + effective cap).
- **Runtime display settings** (since 9.24.0). Present mode, frame cap, window mode, and resolution are all
  changeable live mid-session (no crash, no leaked swapchain), via the cohesive `IDisplaySettings` surface that
  `AppWindow` implements (also surfaced on `GameApp.Display`):
  - `AppWindow.PresentMode` is now a get/set property (was construction-only). Setting it reconfigures the live
    swapchain's vsync in place via `IGpuDevice.SyncToVerticalBlank` - no recreate. On Metal it engages
    `CAMetalLayer.displaySyncEnabled`. On the VELDRID Metal backend that does not cap the CPU, so pair vsync with a
    `FrameCapHz` there (the setter warns once, `Console.Error`, if you select vsync with no cap on that backend, see
    `DisplaySettings.RequiresFrameCapWarning`). The engine's own `MetalNative` was measured on 2026-08-11 and its
    present does throttle from vsync alone, so it needs no cap and never warns.
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
- **GPU diagnostics accessors on `AppWindow`** - read-only facts about the device the window created, for a
  game's own debug overlay or bug-report dump. Every one of them is the value `KhaozEngine.Gpu` already logs at
  device creation, so an overlay row and the log cannot disagree. `BackendSelection` (since 17.21.0) says which
  backend ran and who chose it. The four below live on the `AppWindow.Diagnostics.cs` partial.
  - `ThreadingCaps` (a `GpuThreadingCaps?`, since 17.22.0) - the Direct3D11 driver's multi-threading
    capabilities. Null on every other backend, off Windows, and when the query failed, so render it with
    `GpuThreadingDiagnostics.Describe`. A false `DriverCommandLists` is the case worth putting on screen.
  - `AdapterDescription` (a `string`, since 17.24.0) - the adapter the device runs on, or empty when the backend
    reports none. On Direct3D11 it is exactly the DXGI adapter description, so it is the line a bug report needs
    to say which physical card rendered. The same value as `Capabilities.DeviceName`.
  - `InjectedModules` (an `IReadOnlyList<string>?`, since 17.24.0) - known third-party overlay / capture software
    found hooked into this process when the device was created. **Null (nothing was scanned, off Windows or the
    scan failed) and empty (the scan ran and found none) are opposite facts**, so render it with
    `GpuInjectedModules.Describe` rather than testing the count. Worth a row: this software injects itself into
    Direct3D and is a known cause of stutter, corrupted frames, and driver crashes that look like engine bugs.
  - `Diagnostics` (a `GpuDeviceDiagnostics`, since 17.32.0) - whether this session is on a software rasterizer
    (`SoftwareAdapter`) and why the device was lost if it has been (`DeviceLossReason`). Unlike the three above
    it is read LIVE through to the device on every access rather than captured at creation, because a device loss
    happens at an arbitrary moment long afterwards. Both members are nullable and null means "nobody answered",
    which is what every backend on the Veldrid path says. Hand it to
    `TelemetrySessionInfo.WithGpu`'s five-value overload for a windowed game's session header.
  - `Counters` (a `GpuDeviceCounters`, since 17.32.0) - the live soak counters of this window's device, cumulative
    since it was created: time spent waiting for the GPU to go idle, frame boundaries that blocked on a uniform
    ring segment, and device-level buffer writes queued against an in-flight segment. Read LIVE for the same
    reason `Diagnostics` is, since they move every frame. `HasValue` is false on every backend that keeps none of
    them, which is a DIFFERENT fact from counting and finding zero. Feed a telemetry recording with
    `GpuTelemetryChannels.AppendTo(row, window.Counters)`, which appends nothing at all in the absent case.
- `InputState` - per-frame keyboard + mouse + gamepad + touch snapshot (`IsDown`/`WasPressed` for
  `Key`/`MouseButton`, mouse position/delta/scroll, `Gamepad(i)`). Immutable; no MonoGame. `WasRepeated(Key)` /
  `WasTyped(Key)` surface OS key auto-repeat (`AppWindow` fills it from GLFW's `REPEAT` action; `WasPressed` stays
  press-edge only) so text fields hold-to-repeat. `IsCommandDown` is true while either Ctrl key or either Super
  (Cmd) key is held, the one cross-platform check for a "command modifier" keyboard chord (Ctrl+Z / Cmd+Z,
  Ctrl+S / Cmd+S, and so on) so a game or editor tests one property instead of OR-ing all four keys itself.
  `WasReleased(MouseButton)` / `MouseReleased` (since 14.25.0) give the mouse the release edge the keyboard
  already had.
- `InputAccumulator` (since 14.25.0) - the raw-event to snapshot state machine, split out of `AppWindow` so it is
  headless-testable. It owns the held/pressed/released sets and turns OS callbacks (`OnKeyDown`, `OnMouseUp`,
  `OnScroll`, `OnFocusChanged`, ...) into one immutable `InputState` per `Snapshot(...)` call, with the platform
  reads passed in as arguments. `AppWindow` keeps the Silk/GLFW binding and delegates, so the input hard rule is
  unchanged: `AppWindow` is still the only class touching the Silk input statics. Games do not construct this
  directly, they read `Frame.Input`. It exists so focus-loss release semantics and first-frame cursor priming can
  be tested without a window.
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
- `AppWindow.RequestForeground()` brings the window to the foreground and gives it input focus (restoring it
  first if minimized). This is the OS-touching seam `KhaozEngine.App.SingleInstanceGuard` drives so a losing
  second launch attempt can hand control back to this already-running instance instead of opening a second
  window - see `KhaozEngine.Game`'s `GameApp` (`GameAppOptions.SingleInstance`). MUST be called from the
  main/window thread (GLFW is not thread-safe for this call); best-effort and never throws otherwise.
- `AppWindow.TryAttachParentConsole(bool enable = true)` (static) makes a Windows `WinExe` head keep its console
  output: a Windows-subsystem exe (`OutputType=WinExe`, so no stray console window opens behind the game) has no
  console, so it attaches the process to the launching terminal's console and rewires stdout/stderr, keeping
  `Console.Write*` visible under `dotnet run` / cmd / PowerShell. `AppWindow.ProcessHasConsole` (static) reports
  whether a console is now owned. Both forward to `Platform.WindowsConsole`; no-ops off Windows / for a console exe
  / with no parent console / with redirected output, never throwing. The constructor calls the attach itself (so a
  bare `AppWindow` host is covered - it also un-loses the Metal-vsync `Console.Error` warning above on a WinExe),
  and `GameApp` calls it first (opt out with `GameAppOptions.SuppressParentConsoleAttach`).
- `InputManager` / `Pointer` - the higher-level read: unified pointer, edges, bounds helpers (`IsTapIn` etc.),
  region blocking, keyboard/gamepad/menu navigation. A tap whose press AND release land inside one frame still
  registers: the button is already up when `Update` runs, so the down transition sees nothing and the pointer
  completes the gesture off the snapshot's `MousePressed` edge, reporting the frame as a release with the
  press-origin at the cursor. It matters on any frame hitch and at the background-throttle rates. Reading that
  edge is additive, so a producer that only ever fills `MouseDown` behaves exactly as it always did. The right button has the same bounds helpers as the left
  (`IsRightTapIn` / `IsRightPressingIn`, forwarded on `InputManager`), carrying the same press-origin invariant
  off their own `RightPressOrigin` - what a right-click context menu hangs off, since hit-testing by raw
  position plus a button read is against the rule above. `ConsumeRightGesture()` / `IsRightConsumed` are the
  right-button twin of `ConsumeGesture()` / `IsConsumed` and are tracked separately, so neither button can
  silence the other.
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
  / `AdaptiveViewport` (letterbox/fill/stretch + responsive). All expose `WindowBounds` (10.38.0) - the whole
  window in design space (`DesignBounds` + the letterbox bars) for full-window scrims/backgrounds; `DesignViewport`
  carries the letterbox formula, the always-edge-to-edge viewports return `DesignBounds`.
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
