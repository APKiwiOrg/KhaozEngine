# KhaozEngine.Windowing

Windowing and input foundation for KhaozEngine.

- `AppWindow` - owns the Silk.NET (GLFW) window, the GPU device + swapchain (via `KhaozEngine.Gpu`), and the
  frame loop. By default `Run(onFrame)` clears and presents around your callback. Each `Frame` gives `Dt`, an engine-native
  `InputState`, framebuffer size, and the GPU command list to draw into. `AppWindow.Scaled(...)` fits a
  design-sized window to the display.
- `AppWindow` wires a GLFW-backed text provider into `KhaozEngine.Platform.Clipboard` and unregisters it
  before window teardown. The clipboard seam remains usable without a windowing dependency.
- `GpuBackends` (18.0.0) - the one call that puts a graphics backend in the process, and the reason a repinned
  game needs no startup line of its own. `KhaozEngine.Gpu` builds no device since 18.0.0 and cannot reference a
  backend package without a dependency cycle, so something above that seam has to close it, and this package is
  the lowest thing every windowed and in-repo headless host already has in its graph. Registering says a
  provider EXISTS (a fact about the app's wiring). WHICH backend runs is `GpuBackendSelector`'s answer, and
  whether the machine can run it is the separate functional probe behind
  `GpuBackendSelector.IsBackendSupported`.
  - `RegisterResolvedIfUnregistered(userPreference = null)` - **what `AppWindow` calls at boot**, and what a
    headless host should call. It registers a provider for the kind the selector RESOLVES to with the stored
    preference in the chain, AND for this platform's own kind, which is where a failed request falls back to.
    Registering only the platform's own is the bug it was written to fix: a Windows player who picked
    `VulkanNative`, or a developer who pinned `vulkan-native` on a Mac, was told to add a package the umbrella
    already ships. It touches nothing already registered, per kind, so a host's own provider (a wrapper that
    counts allocations, a fake in a test) stays authoritative even though `AppWindow` writes late. It returns
    the kind it registered, or null when both were already there. A package that cannot run on this OS is not
    registered at all, so a `d3d11-native` request on a Mac gets the ordinary missing-provider throw rather than
    a seated provider that answers no to everything.
  - `RegisterPlatformDefault()` - this OS's own backend and nothing else: Metal on macOS, Direct3D 11 on
    Windows, Vulkan on Linux and everything else. Idempotent, thread-safe, and never null, since the probe's
    Vulkan arm is a catch-all rather than a Linux special case.
  - `RegisterAll()` - all three whatever the OS, for a process that means to reach every backend it can, which
    in practice is a test harness or a tool comparing two implementations in one process. A GAME does not want
    it: `GpuBackendSelector.SupportedBackends()` offers the player every registered kind that probes true, and a
    foreign kind that somehow probed true would be a settings row that cannot boot.
  - A custom host still calls a package's own `KhaozEngineMetal.Register()` / `KhaozEngineD3D11.Register()` /
    `KhaozEngineVulkan.Register()` when it wants exactly one backend, or one this platform does not default to.
    Registration is idempotent and last-writer-wins per kind, so the two mix freely and in any order.
- **Shared frame completion.** `Frame.Submission` exposes the window's lazily created `GpuFrameSubmission`.
  The loop begins it before preparation, records every scene and diagnostic copy into `Frame.Commands`, and
  submits that list once before Present. Exceptions abandon unsubmitted recording. Completion consumers share
  one retained fence, and submission participants receive their decision even when diagnostic capacity is full.
  Window teardown disposes the owner before the command list and device. Ordinary completion polling never drains.
  Submission teardown forwards the ordered `GpuDiagnosticFailureException` transport and retains its original
  exception references. The [GPU diagnostic contract](../KhaozEngine.Gpu/README.md#optional-compiler-and-memory-diagnostics) owns that carrier.
- **The frame's pre-record phase.** `Run(onFrame, onPrepare)` is the same loop with a second
  callback, invoked each frame after the frame's `Dt` / input / size are latched and **before** the frame's command
  list is opened. `Run(onFrame)` is exactly `Run(onFrame, null)`, so nothing changes for a host that does not want
  it. Use it for scene resource reservation and producers that submit GPU work on their own lists. The engine
  permits one recording per device. Prepare scenes and run producers before opening the host list. The sequence
  is `Begin`, queue draws, `PrepareFrame(width, height)` with the frame's size, then render in `onFrame`. `GameApp` and
  `GameApp3D` already provide this ordering. A repeated
  preparation of an already prepared frame is a no-op. Do not record into `Frame.Commands` from `onPrepare`,
  because that list is not open yet (and on a
  render-suppressed frame never is). Both callbacks run on a render-suppressed frame, so update-side work keeps
  advancing while minimized.
- **Renderer-owned output initialization.** `AppWindow.RendererInitializesSwapchain` defaults to false.
  Setting it true omits both the host's initial swapchain framebuffer bind and its `ClearColor` clear, allowing
  offscreen rendering before the first output bind. The callback must bind and initialize the entire output
  inside this frame's recording before any 2D draws or capture taps, including empty, loading and menu frames.
  Keep the default for 2D, partial or custom renderers that rely on the host clear. A full-output 3D renderer
  still performs its final output bind, clear and fullscreen overwrite. Recording, taps, submission and present
  keep their existing order, and suppressed frames still open and present nothing.
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
    present, displayRefreshHz)` is pure, and since 18.0.0 it resolves to UNCAPPED on every live backend. The only
    backend whose present did not throttle the CPU from vsync alone was the Veldrid Metal incumbent, deleted in
    18.0.0, so what is left is **D3D11Native / VulkanNative**, and the engine's own **`MetalNative`**, measured on
    2026-08-11 as blocking the CPU
    in the drawable acquire for the whole vertical-blank wait. A consumer-set value always wins. `AppWindow.FrameCapHz`
    setter is the explicit int form (positive = fixed, 0 = intentional `Uncapped`), and its getter returns the RESOLVED
    effective cap. The one-time Metal-vsync warning is GONE at 18.0.0, along with the only backend that ever
    tripped it: `AppWindow.WarnIfMetalVsyncUncapped` and its `_warnedMetalVsync` latch were deleted, and nothing
    writes to `Console.Error` from the frame-cap path any more.
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
    `CAMetalLayer.displaySyncEnabled`. The deleted Veldrid Metal backend did not cap the CPU that way and needed a
    `FrameCapHz` paired with vsync. `MetalNative` was measured on 2026-08-11 and its present does throttle from
    vsync alone, so it needs no cap. The one-time `Console.Error` warning and the `AppWindow` members that armed
    it were DELETED at 18.0.0 with the backend that tripped it. What stays is the pure predicate
    `DisplaySettings.RequiresFrameCapWarning(backend, presentMode, frameCapHz)`, now a constant `false`, and it
    stays as the QUESTION rather than as a live warning: it is row 10 of the `GpuBackendKind` append audit and
    it pairs with `FrameCap.Resolve`'s `Auto` arm, so an appended backend whose present free-runs under vsync
    puts an arm back in both in the same commit. Its only in-repo readers are those audits and the
    `DisplaySettings` / `FrameCap` unit tests, which is exactly what a question-shaped member should look like.
    Deleting it would take the audit row with it and let the next backend land with a silent frame-cap gap.
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
- **Launch placement** (since 18.14.0). The half of the above that happens at window CREATION, and it persists
  nothing.
  - The `AppWindow` constructor takes a trailing `focusOnLaunch` (default true). False writes the GLFW `FOCUSED`
    and `FOCUS_ON_SHOW` hints as false before the native window exists, so the window appears without taking the
    keyboard or the foreground. On macOS the keyboard part holds (GLFW's `[NSApp activateIgnoringOtherApps]` is
    on the focus path both hints skip), but the process can still become the active application on the first
    window of a run, which no GLFW 3.4 hint controls. See `AppWindow.Launch.cs` for the full per-platform note.
  - `AppWindow.Scaled(title, designWidth, designHeight, ...)` keeps its existing signature and requests focus.
    To choose launch focus, use the overload with a required first argument:
    `AppWindow.Scaled(focusOnLaunch, title, designWidth, designHeight, ...)`. Both use `screenFraction = 0.9f`,
    `maxScale = 2f`, `PresentMode.Vsync`, `frameCapHz = 0` (explicitly uncapped), and no backend preference by
    default. Positive caps request that Hz, and non-positive caps stay uncapped. `KE_WINDOW_FOCUS` still wins.
    `maxScale` counts logical points. Where GLFW window coordinates are pixels (Win32 and X11) the factory
    multiplies the cap by the primary monitor's content scale, so a 3840x2160 screen at 250% opens a 1280x720
    design at 3456x1944 instead of stopping at 2560x1440. Cocoa and Wayland coordinates are already points, and an
    unknown platform or unreadable scale keeps the plain cap. The pure `FitToScreen` keeps its screen-coordinate
    meaning, and no automatic `GLFW_SCALE_TO_MONITOR` resize is enabled.
    A custom `GameAppOptions.WindowFactory` forwards the nullable option as `o.FocusOnLaunch ?? true`:

    ```csharp
    opts.WindowFactory = o => AppWindow.Scaled(
        focusOnLaunch: o.FocusOnLaunch ?? true, title: o.Title,
        designWidth: o.Width, designHeight: o.Height,
        presentMode: o.PresentMode, frameCapHz: o.FrameCapHz,
        backendPreference: o.GraphicsBackendPreference);
    ```

    `Scaled` uses its integer cap at construction. After a custom factory returns, `GameApp` applies the
    full resolved `FrameCap` intent from `GameAppOptions`, with positive `FrameCapHz` taking precedence.
  - `AppWindow.InitialMonitor` (an `InitialMonitor`: `Saved` by default, else `Primary` / `Rightmost` /
    `Leftmost` / `At(index)`) is applied once by `Run`, after the host's load callback, so an explicit choice
    wins over a position the game restored at boot. `Saved` moves nothing. `ApplyLaunchPlacement()` is the
    idempotent entry point `Run` calls.
  - `KE_WINDOW_MONITOR` (`rightmost` / `leftmost` / `primary` / an index) and `KE_WINDOW_FOCUS` (`0`, `false`,
    `no`, `off`) are the dev overrides, read once at creation beside `KE_MAX_FRAMES`, beating both the options
    and the game's saved position. An unrecognized value is ignored with one WARN line naming it.
  - `AppWindow.PlacementOverridden` is true while `KE_WINDOW_MONITOR` placed the window. **A game that persists
    its window position must skip that write while it is true**, or a harness boot on another monitor becomes
    the player's saved placement.
  - The policy is pure and headless-tested: `WindowLaunch` (parsing, precedence, warning bodies),
    `InitialMonitor.Resolve`, `WindowCreationHints`, `LaunchPlacement`, and
    `WindowPlacement.RightmostIndex` / `LeftmostIndex` (greatest / least monitor x origin).
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
    which is what the Metal and Vulkan backends say, since neither API answers either question. Hand it to
    `TelemetrySessionInfo.WithGpu`'s five-value overload for a windowed game's session header.
  - `Counters` (a `GpuDeviceCounters`, since 17.32.0) - the live soak counters of this window's device, cumulative
    since it was created: time spent waiting for the GPU to go idle, frame boundaries that blocked on a uniform
    ring segment, and device-level buffer writes queued against an in-flight segment. Read LIVE for the same
    reason `Diagnostics` is, since they move every frame. `HasValue` is false on every backend that keeps none of
    them, which is a DIFFERENT fact from counting and finding zero. Feed a telemetry recording with
    `GpuTelemetryChannels.AppendTo(row, window.Counters)`, which appends nothing at all in the absent case.
- `InputState` - per-frame keyboard + mouse + gamepad + touch snapshot (`IsDown`/`WasPressed` for
  `Key`/`MouseButton`, mouse position/delta/scroll, `Gamepad(i)`). Immutable. `WasRepeated(Key)` /
  `WasTyped(Key)` surface OS key auto-repeat (`AppWindow` fills it from GLFW's `REPEAT` action; `WasPressed` stays
  press-edge only) so text fields hold-to-repeat. `TextInput` carries the ordered Unicode text committed by the
  OS this frame. `TextInputAvailable` remains true on an empty GLFW text frame, so dead keys do not type through
  the fallback key map. Hand-built snapshots default to no text source. `IsCommandDown` is true while either Ctrl key or either Super
  (Cmd) key is held, the one cross-platform check for a "command modifier" keyboard chord (Ctrl+Z / Cmd+Z,
  Ctrl+S / Cmd+S, and so on) so a game or editor tests one property instead of OR-ing all four keys itself.
  `WasReleased(MouseButton)` / `MouseReleased` (since 14.25.0) give the mouse the release edge the keyboard
  already had. `WithoutScroll()` clears both `ScrollDelta` and `HorizontalScrollDelta` and removes wheel and
  precise-scroll samples while retaining magnify. It preserves the other input fields and reuses the snapshot
  when both axes are zero and no scroll samples remain. `WithoutPointerGestures()` also removes magnify.
  `PointerCaptured` (since 20.17.0) is true while the window holds the pointer captured. While captured,
  `MouseDelta` is in window points, so the same hand movement reads alike on a 1x and a 2x display, and
  `MousePosition` stays in framebuffer pixels, held where capture started until the frame capture ends. The frames
  capture starts and ends report a zero delta.
  `MouseDeltaPoints` reports window-point motion both before and during capture, with the same first-sample,
  missing-mouse and capture-transition zeros. `FramebufferScale` records framebuffer pixels per point on each axis,
  with nonfinite or nonpositive components read as 1. The original constructor assumes 1x and copies `MouseDelta`
  into `MouseDeltaPoints`. The new overload takes required `mouseDeltaPoints` and `framebufferScale` before the
  original arguments. `WithoutScroll()` preserves both facts. Legacy `MouseDelta` units and framebuffer positions
  remain unchanged.
  `Gestures` owns an immutable ordered `PointerGestureFrame`. Each `PointerGestureSample` carries `Kind`,
  `Source`, `Phase` (`PointerGestureInputPhase`), `PositionPixels`, `ScrollDelta`, `ScaleFactor`, `CommandDown`
  and `IsMomentum`. Anchors are framebuffer pixels, wheel deltas are wheel units, precise-scroll deltas are
  window points, and magnify factors are dimensionless. Unused scroll deltas are zero and unused factors are 1.
  Frames reject nonfinite coordinates, nonfinite deltas, nonpositive or nonfinite factors, default samples
  and magnify products that overflow or underflow. Rejected accumulation leaves the prior samples intact.
  `HasPreciseScroll` includes zero-delta lifecycle samples. `PinchScaleFactor` multiplies only magnify factors
  and defaults to 1. These summaries do not replace the authoritative ordered samples.
  The additive constructor takes `gestures`, `horizontalScrollDelta`, `mouseDeltaPoints` and `framebufferScale`
  before the original arguments. `PointerGestureFrame.IsAvailable` distinguishes an authoritative stream from
  old synthetic snapshots. Public frames and `PointerGestureFrame.Empty` are available even with no samples.
  Both existing constructor signatures use an unavailable empty frame, so legacy-only snapshots retain their
  scalar fallback. Copies and automation preserve availability. An available empty stream never falls back to
  retained legacy scroll after native samples were cleared.
- `PanZoomController` interprets admitted mouse drag, stable-ID touches, ordered wheel/precise-scroll/magnify
  and optional focused keyboard navigation over `IPanZoomTarget`. Targets own anchoring, bounds and wrapping.
  Use the full `InputManager` update, overlays before admission and own-region reservation afterward.
  `Cancel()` discards motion, queued zoom and tap history, inhibiting held contacts until release. New touch IDs
  require `TouchPhase.Began`. Undefined phases, nonfinite positions and duplicate IDs reject the touch sequence
  until release. `Pointer.IsConsumed` rejects the left mouse sequence through release, while fresh touch, wheel
  and native samples use region admission even while that button stays held. Single taps report immediately, and a
  qualifying second tap zooms instead of reporting another single. See the
  [consumer contract](../docs/USING-KHAOZENGINE.md#pan-and-zoom) for units, policy defaults and target examples.
  Native macOS precise pan/magnify and Windows Ctrl+wheel compatibility require device validation through the
  [probe](../tools/GestureInputProbe/README.md). Full native Windows precision pan is outside scope.
- `InputManager.SuppressPointerInput()` gives an owning surface a frame-level pointer arbitration boundary after
  `Update`. It clears mouse buttons, both scroll axes and gesture convenience access for that frame, quarantines
  held buttons until their physical release, and keeps keyboard, gamepad, pointer position, and hover available.
  `PointerInputSuppressed` exposes the boundary for controllers. Raw `State` remains readable and bypasses it.
  `Gestures`, `HorizontalScrollDelta`, `HasPreciseScroll` and `PinchScaleFactor` honor suppression.
  `Pointer.Coordinates` snapshots the framebuffer scale, viewport transform, viewport identity and dimensions.
  `PositionFromPixels` maps event anchors and touches into the pointer's coordinate space. `VectorFromPoints`
  applies framebuffer scale once and then viewport scale without letterbox translation. `VectorFromPixels`
  maps pixel vectors directly. `HasSameMapping` detects changes even when a viewport object is reused.
- `InputAccumulator` (since 14.25.0) - the raw-event to snapshot state machine, split out of `AppWindow` so it is
  headless-testable. It owns the held/pressed/released sets and turns OS callbacks (`OnKeyDown`, `OnMouseUp`,
  `OnScroll`, `OnFocusChanged`, ...) into one immutable `InputState` per `Snapshot(...)` call, with the platform
  reads passed in as arguments. `AppWindow` keeps the Silk/GLFW binding and delegates, so the input hard rule is
  unchanged: `AppWindow` is still the only class touching the Silk input statics. Games do not construct this
  directly, they read `Frame.Input`. It exists so focus-loss release semantics and first-frame cursor priming can
  be tested without a window. `Snapshot(..., pointerCaptured, framebufferScale)` (since 20.17.0) takes the capture
  state and the framebuffer pixels per window point. The points baseline remains stable across scale changes.
  New scale metadata treats nonfinite or nonpositive components as 1. Legacy captured `MouseDelta` keeps its
  existing normalization, where nonpositive or NaN components mean 1.
  `OnPointerGesture(sample)` records an ordered sample independently of legacy totals. `OnScroll(float)` records
  vertical wheel units, and `OnScroll(Vector2)` records both legacy axes without adding a sample. This lets a
  native monitor enrich a callback without counting it twice. Focus and capture transitions discard pending
  gesture samples. Empty accumulation reuses `PointerGestureFrame.Empty`.
- `AppWindow` supplies native macOS precise scroll and magnify through a window-filtered AppKit monitor.
  It preserves event-time anchors, Ctrl/Cmd modifiers, phase and momentum, returns the original event to GLFW,
  and keeps Silk's legacy wheel units independently. Windows wheel messages supply event-time Ctrl and both
  wheel axes through a chained window subclass. Ctrl+wheel is `ModifierWheelCompatibility`, while fractional
  wheel values alone never imply precision. Other platforms and unavailable native registrations use
  unclassified wheel samples with event-time Ctrl/Cmd compatibility. Native registration is removed before
  input and window teardown. Native delivery and device feel still require physical platform validation.
  `AppWindow.NativeGestureFailure` exposes the first failure with `Stage` (`Install`, `Remove`, `Release`
  or `Callback`), a developer `Message` and the original `Exception` when present. Failed native installation
  remains wheel fallback, and failed removal retains callback roots. Both failures stay queryable without
  automatic logging. A platform with no native backend is normal fallback and reports no failure.
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
  / with no parent console / with both output streams redirected, never throwing. Missing native handles can
  attach, while real redirection is preserved per stream. The constructor calls the attach itself (so a
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
  silence the other. `IsPressOriginFresh` is true on the one frame `PressOrigin` was latched (a real press edge
  or a same-frame tap), for a consumer keeping per-GESTURE state of its own: recompute on that frame, never on
  the release, which reads a world that has moved on since the press. `Gui.ScreenStack.PressBeganOverUi` is
  built on it.
- **Pointer occlusion** (since 21.3.0) - opt-in covered areas that hit queries treat as misses. Cleared at the
  start of every `Pointer.Update`. With nothing occluded every query reads exactly as before.
  `Gui.WindowStack` is the normal producer.
  - `Occlude(Rect)` adds an occluded area for the rest of the frame.
  - `OccludeAll()` occludes the whole surface for the rest of the frame.
  - `IsOccluded(Vector2)` reports whether a point is occluded.
  - `ClearOcclusion()` drops every occluder before the next `Update`.
  - `IsTapIn`, `IsTapFromTo`, `IsPressingIn`, `IsRightTapIn` and `IsRightPressingIn` miss when the press origin
    or the cursor is occluded. `IsPointerIn` and `IsHoveringIn` miss when the cursor is. `IsDraggingIn`,
    `GetDragDelta` and `IsDragStartIn` miss when the press origin is.
  - `IsReleasedOutside` counts a release in an occluded area as outside.
  - `IsRightPressOriginFresh` is the right-button twin of `IsPressOriginFresh`.
  - `BlockRegion` and `IsBlocked` keep their meaning. Occlusion is the part the hit queries obey.
  - `PanZoomController` admits no press, touch or gesture at an occluded point and records no tap on an
    occluded release.
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
- **Pointer capture** (since 20.17.0) - the mouse-look output beside rumble. `AppWindow.SetPointerCaptured(bool)`
  records the request and `AppWindow.PointerCaptured` reads the result. `GameApp.SetPointerCaptured` is the
  protected forwarder. Once a frame, before the snapshot, a pure policy captures only while requested and focused,
  releases on focus loss, and waits for a false then true request before capturing again after refocus. It sets
  GLFW's cursor mode through `SetInputMode`, never Silk's `CursorMode` setter, and turns on raw motion only where
  GLFW supports it. See docs/USING-KHAOZENGINE.md.
- `PointerGesture(button, thresholdPixels = 4)` (since 20.17.0) - splits one mouse button into a tap and a drag.
  `Advance(input, uiBlocked)` once a frame, then read `Phase` (`Idle`, `Pending`, `Dragging`), `DragDelta`,
  `TapThisFrame` and `TapPosition`. Travel is path length, crossing replays the pending travel, and above a zero
  threshold a press begun under UI or while unfocused is inert. A threshold of 0 or less drags at once, never taps,
  and drags a held button again on the first unblocked frame. Threshold, replay and continuing `DragDelta` use
  `MouseDeltaPoints`, so they stay in window points before and during capture. The `thresholdPixels` parameter and
  `ThresholdPixels` property names remain for compatibility. `TapPosition` stays in framebuffer pixels.
  `Advance` allocates nothing. Lifted from the reference consumer's
  `RightMouseGesture`. See docs/USING-KHAOZENGINE.md.
- `PointerTapTolerance(DistancePoints, GraceSeconds, GraceDistancePoints)` with `CatchUpLimitPoints` - the opt-in tap
  rule for `PointerGesture(button, tapTolerance)`. It judges straight-line distance from the press point (the
  cursor where the press frame ended, so that frame's own delta never counts) instead of path length, with the wider
  `GraceDistancePoints` limit until `GraceSeconds` after the press, so a click that wobbles and comes back is still a
  tap. A gesture built on one advances with `Advance(input, uiBlocked, elapsedSeconds)`, and its two-argument
  `Advance` throws. `CatchUpLimitPoints` caps the catch-up step when the grace ending decided the press (the distance
  was still within `GraceDistancePoints`), and zero drops it. The strict path rule stays the default. See
  docs/USING-KHAOZENGINE.md.
- `GameClock` (pause/timescale, plus `RealWallGapSeconds`/`LastRealTimestamp` - a UTC wall-clock gap per frame
  that survives OS sleep/suspend, which the frame `dt` does not, so a game can detect a resume), `DesignViewport`
  / `AdaptiveViewport` (letterbox/fill/stretch + responsive). All expose `WindowBounds` (10.38.0) - the whole
  window in design space (`DesignBounds` + the letterbox bars) for full-window scrims/backgrounds; `DesignViewport`
  carries the letterbox formula, the always-edge-to-edge viewports return `DesignBounds`.
  `GameClock.FrameCount` (since 20.14.0) counts `Update` calls, paused or not, and is the per-frame id
  `FollowCamera3D.FrameClock` reads.
- `AdaptiveViewport.WithMinimumCanvas(referenceWidth, referenceHeight, minimumWidth, minimumHeight,
  scaleMultiplier = 1f)` (since 20.28.0) is the opt-in minimum-canvas policy. Its uniform scale is
  `min(framebufferHeight / referenceHeight * ScaleMultiplier, framebufferWidth / minimumWidth,
  framebufferHeight / minimumHeight)` and `Width`/`Height` are the framebuffer divided by that scale, rounded down
  so a control anchored at the far edge stays onscreen. A narrow window gains height instead of overflowing to the
  right, and a larger `ScaleMultiplier` enlarges the UI until the minimum canvas binds. Setting `ScaleMultiplier`
  recomputes the transform from the last framebuffer size at once, so drawing and hit testing agree. Zero,
  negative or non-finite multipliers and non-positive sizes throw `ArgumentOutOfRangeException`. The derived
  transform must also be representable: a finite positive scale, and a visible `Width` and `Height` of at most
  16,777,216 (2^24), the range in which every integer design coordinate is exact as a float. The factory, the
  `ScaleMultiplier` setter and `Update` validate the transform before publishing it and throw
  `ArgumentOutOfRangeException` otherwise, leaving the previous multiplier, cached framebuffer size and transform
  intact. A minimum size above 2^24 is always rejected, a tiny multiplier such as `1e-5f` at a 720 reference
  height fails at creation, and nothing is clamped. The original
  `new AdaptiveViewport(referenceWidth, referenceHeight)` keeps its fixed height and reference-width floor, reports
  `ScaleMultiplier == 1` and throws `InvalidOperationException` when one is set. Neither mode touches the camera.
- `UiViewport` (since 10.12.0) - a point-space viewport for DPI-aware UI, implementing `IDesignViewport`.
  One UI design point maps to `EffectiveScale` device pixels without letterboxing. `DpiScale` remains physical
  OS scale. `ScaleMultiplier`, default 1, requests a user scale between 0.1 and 10 inclusive. Nonfinite and
  out-of-range requests throw before changing the previous request. The next valid `Update` applies it to
  canvas size, projection, pointer mapping and clipping together, so setting it during input handling cannot
  remap that frame. `EffectiveScale` and both axis scales are physical DPI times the applied multiplier.
  `Width`/`Height` are the rounded logical window size divided by that multiplier. `DesignBounds` retains
  fractional coverage and `ContentBounds` remains the exact framebuffer. Bake UI fonts at `EffectiveScale`.
  It returns
  `SnapsToDevicePixels = true`. Drive it with `UiViewport.Update(Frame frame)`, which carries the exact
  `Frame.DpiScale`, or `Update(int framebufferW, int framebufferH, int logicalW, int logicalH)`, which derives the
  scale from the two widths. Because it implements `IDesignViewport`
  it drops straight into `SpriteBatch.Begin`, `Pointer.Update`, `ComputeScissor`, and the Gui screens/layout
  unchanged. Contrast with `DesignViewport` (a fixed design canvas Fit-scaled onto the framebuffer, which magnifies
  fractionally on HiDPI): use `DesignViewport` for the letterboxed game field, `UiViewport` for the crisp UI layer.

## Frame capture taps

An `IFrameCaptureTap` reads the frame the game just rendered, for a screenshot or a rolling clip. Register one
with `AppWindow.AddCaptureTap(tap)` and unregister it with `AppWindow.RemoveCaptureTap(tap)`. Both run on the frame
thread, adding the same tap twice is a no-op, and removing is safe from inside a tap callback. Removal calls the
tap's `Detach(retire)` once, and so does window disposal. The tap hands every GPU resource it owns to `retire`,
which destroys it once the GPU has finished with it.

A rendered frame with a tap registered runs in this order:

1. `onPrepare`, then the frame's list opens. Unless `RendererInitializesSwapchain` is true, it binds `SwapchainFramebuffer` and clears.
2. `onFrame`, the game's draws.
3. Every tap's `Record(context)`, still inside the frame's list.
4. The list closes and is submitted, with the fence a tap handed in or without one.
5. Every tap's `AfterSubmit(context)`, with nothing recording, so a map or a blocking readback is legal.
6. `Present`.

A render-suppressed (minimised) frame runs no tap. A window that never registered a tap always runs the unchanged
frame: one `Submit(commands)` and one `Present()`, nothing else. After the last tap leaves, the registry keeps the
tap path for the few frames its retired resources need to clear the GPU. On a device with completion fences that
includes one empty fenced submit that seals them. Once nothing is pending, frames return to exactly one
`Submit(commands)` and one `Present()`.

Window disposal detaches every tap. A tap whose `Detach` throws does not stop the others from being detached, and
does not stop the registry, the command list, the device or the window from being freed.

`FrameCaptureContext` is shared by every tap in a frame:

| Member | Meaning |
|--------|---------|
| `Frame`, `Device` | the frame being captured and the window's device |
| `Commands` | the frame's own list, open during `Record` only |
| `FrameMilliseconds` | the loop's frame delta in milliseconds, before the 100 ms clamp `Frame.Dt` carries |
| `AcquireSwapchainImage()` | `Record` only. A copy of this frame's swapchain image, taken once per frame however many taps ask. It has the swapchain's size and colour format, one mip and `Sampled` usage, and the registry owns it. Null when the image cannot be copied this frame |
| `SignalOnSubmit(fence)` | `Record` only. Submit the frame with this fence. One fence per frame across every tap, and a second call throws `InvalidOperationException` |
| `Retire(resource)` | destroy a resource once the GPU has finished with it |

The swapchain copy goes through the device's `IGpuSwapchainCapture`. That copy needs a list that bound the
swapchain framebuffer in its current recording. The host does this by default. With `RendererInitializesSwapchain`
enabled, the renderer must bind and initialize it before taps run. A tap records its copies into
`Commands` and never into a list of its own. The registry asks for copy-source usage on the swapchain images with
the first tap and releases it after the last. Metal and Vulkan apply the change at a later present, so the first
frame or two after the first tap may give a null image. A device with no swapchain capture always gives null. A
frame whose `onFrame` draws nothing still captures the host-cleared image under the default policy. The copy texture is remade when the
swapchain size changes and retired when the last tap leaves. A last tap that removes itself in `Record` and then
acquires still gets this frame's image, and the registry retires that image at the end of the frame, after its
submit. Window disposal releases the copy texture whatever the tap count.

## `Frame` DPI members (since 10.12.0)

`Frame` (on `AppWindow`) exposes the logical window size and DPI scale for point-space UI:

| Member | Meaning |
|--------|---------|
| `LogicalWidth` / `LogicalHeight` | logical window size in points, the framebuffer divided by `DpiScale` and rounded to the nearest point, so `LogicalWidth * DpiScale` can differ from the framebuffer edge by at most half a logical point |
| `DpiScale` | the window's OS content scale (`glfwGetWindowContentScale`), e.g. 1 standard, 2 Retina, 1.5 on a 150%-scaled Windows display |

`AppWindow` reads the content scale every frame, so a monitor move or OS scale change reaches the next frame
while an ordinary resize leaves it exact. When the export or backend is unavailable, or it reports a zero,
negative or non-finite scale, the frame keeps the earlier behavior: logical size is the window size and `DpiScale`
is `Width / LogicalWidth`. A manually constructed `Frame` uses that same fallback. `Frame.Width`/`Height` remain
the device-pixel framebuffer size, `AppWindow.WindowWidth`/`WindowHeight` and placement stay in window
coordinates, and pointer positions still convert through `InputState.FramebufferScale` (framebuffer pixels per
window coordinate). `AppWindow.LogicalWidth`/`LogicalHeight` report the same logical size as the frame and query
GLFW on each read, so read them on the window thread (the thread that runs the frame loop). Bake
point-space UI fonts at `frame.DpiScale` and snap UI geometry to whole multiples of it.

The renderers (`Render2D`, `Render3D`) build on this. Silk.NET windowing ships GLFW natives bundled per-RID,
so there is no SDL2/brew step. A mobile-windowing head is not available yet.

**Linux host natives, packed here as well as in `KhaozEngine.Foundation`
([#723](https://github.com/APKiwiOrg/KhaozEngine/issues/723)).** Silk.NET resolves its bindings itself rather
than through `[DllImport]`, and on Linux it probes `runtimes/<distro rid>/native`, which nothing writes, so the
GLFW natives never load until they sit flat beside the assemblies. This package carries a build rule that copies them
there on `build` and on a rid-agnostic `publish`, no-op on Windows, macOS and `publish -r <rid>`, opt out with
`<KhaozEngineFlattenHostNatives>false</KhaozEngineFlattenHostNatives>`. It ships here as well as in `Foundation`
because `Foundation` is not in this package's dependency closure, so a project that references this one and no
umbrella would otherwise get nothing. One physical rule file, so the two copies cannot drift.
