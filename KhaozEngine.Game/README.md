# KhaozEngine.Game (5.x)

A 2D game-loop facade over the MonoGame-free 5.x stack. `GameApp` is an abstract base that owns the
per-frame composition + ordering so a game can't get it wrong:

```
OnLoad();
each frame:
  Clock.Update(dt)
  Viewport.Update(window size)   -> OnResize on change
  Pointer.Update(input, Viewport)
  OnResume(wallGap)              // only when the wall-clock gap exceeds the threshold (OS sleep/suspend/hang)
  OnUpdate(Dt)
  OnRenderWorld(frame)           // empty by default; a subclass renders a world here (e.g. a 3D scene)
  Surface2D.NewFrame(frame); Batch.Begin(Viewport); OnDraw2D(Batch); Batch.End()
```

Subclass it and override `OnLoad` / `OnUpdate(dt)` / `OnDraw2D(batch)` / `OnResize(w, h)`; call
`Quit()` to close. Construct with `GameAppOptions.For(title, w, h)` (set `DesignWidth/Height`,
`ScaleMode`, `ClearColor` as needed).

Set `GameAppOptions.PresentMode` (`Vsync` default / `Immediate`), `GameAppOptions.FrameCapHz` (0 = uncapped), and
`GameAppOptions.WindowMode` (`Windowed` default) for the initial presentation (present mode since 9.23.0, window
mode since 9.24.0). `FrameCapHz` paces the loop to a target Hz with a monotonic-clock limiter independent of the
swapchain's vsync - pin it to an integer multiple of a fixed network tick (e.g. 60/120 for 30 Hz) to keep
presentation phase-aligned. It is the deterministic cap where vsync does not throttle (notably Mac/Metal). Frame
cap and window mode are applied on both the default and a custom `WindowFactory` window; `PresentMode` selects the
swapchain vsync at creation on the default window (a custom factory must forward it, though it can be flipped live).

**Runtime display settings** (since 9.24.0): change present mode, frame cap, window mode, and resolution live
mid-session (no crash, no leaked swapchain) via `GameApp.Display` (the cohesive `IDisplaySettings` surface) or the
`GameApp.PresentMode` / `FrameCapHz` / `WindowMode` pass-throughs. Read a `DisplaySettings` snapshot from
`Display.CurrentDisplay`, tweak it (`with`), and `Display.ApplyDisplay(...)` it back from a settings screen.
Since 9.26.0 `Display` also carries window placement - position + monitor (`WindowX` / `WindowY` / `MoveTo`,
`Monitors` / `CurrentMonitorIndex` / `MoveToMonitor`, `EnsureVisible`) and `X`/`Y` on the `DisplaySettings` snapshot -
so a game can persist + restore its full window placement across launches (the restore self-clamps on-screen).
`GameApp.Backend` exposes the active graphics backend so display defaults can branch per platform (e.g. force a
`FrameCapHz` on Metal, where vsync alone does not cap - the engine warns once if you select vsync with no cap there).

Override `OnResume(TimeSpan wallGap)` to react to an OS sleep/suspend/hibernate (or a long hang): it fires once,
before `OnUpdate`, on the first frame whose wall-clock gap (`Clock.RealWallGapSeconds`, which survives a suspend
where the frame `dt` does not) exceeds `GameAppOptions.ResumeGapThresholdSeconds` (default 30s; 0 or negative
disables). Use it for offline/AFK catch-up, timer re-sync, or an auto-pause. The 0.1s sim-delta clamp and `Dt`
are unaffected.

Set `GameAppOptions.WindowIconPath` (a PNG) or `WindowIcons` (explicit decoded `ImageRgba`, multi-res,
wins over the path) for the runtime window/taskbar icon; `GameApp` decodes via `Render2D.ImageRgba`
and applies it through `AppWindow.SetIcon` while the window is still hidden, then shows it, so the Windows
taskbar button is born with the icon. Windows/Linux get the live title-bar/taskbar icon. On macOS
`SetIcon` is a no-op (GLFW can't set the Dock icon), so when `WindowIconPath` is set `GameApp` also feeds that
PNG to `AppWindow.SetMacDockIcon` to set the Cocoa Dock / Cmd-Tab icon at runtime (fixes the generic document
icon on an unbundled `dotnet run`). The Windows `.exe` icon stays a per-game `<ApplicationIcon>`.

Set `GameAppOptions.AppUserModelId` (e.g. `"APKiwi.Nullwake"`, a dotted `CompanyName.ProductName`) so the
running app's **Windows taskbar button shows the app icon** instead of the generic `.exe` placeholder. `GameApp`
sets the process's explicit Windows AppUserModelID (via `AppWindow.TrySetProcessAppUserModelId`) before creating
the window, which also stabilises taskbar grouping/pinning. Null (the default) keeps the current behaviour;
no-op off Windows.

**Point-space UI pass** (since 10.12.0, for DPI-aware crisp UI): `GameApp` exposes a per-frame
point-space `Ui` (a `UiViewport`) and `UiPointer` (a `Pointer` mapped through it), plus a new virtual
`OnDrawUi(SpriteBatch batch)` that runs in a SECOND draw pass after `OnDraw2D` each frame, with the batch
already in `Begin(Ui)`. `OnDraw2D` stays the design-space (letterboxed) game field, `OnDrawUi` is the
DPI-aware UI layer: author text via `DpiFont.For(Ui.DpiScale)` and hit-test with `UiPointer`. `OnDrawUi`
is empty by default, so a game that only overrides `OnDraw2D` is completely unaffected.

`SceneManager` gains `UiViewport`, `UiPointer`, and `DrawUi(SpriteBatch)`, and `GameScene` gains a virtual
`OnDrawUi(SpriteBatch)`. A scene draws its DPI-aware UI in `OnDrawUi` and hit-tests via `Manager.UiPointer`.

It sits above `KhaozEngine.Windowing` + `Render2D` + `Gui` - **no 3D renderer dependency**, so a 2D
game pulls no Render3D. For a 3D world pass, use `KhaozEngine.Game.Render3D` (`GameApp3D`, plus the
`IGameScene3D` scene hook and the `SceneManager.Draw3D` extension). It is the optional convenience
layer: a game with special needs can still drive `AppWindow.Run` directly.

```csharp
sealed class Demo : GameApp
{
    public Demo() : base(GameAppOptions.For("Demo", 960, 540)) { }
    protected override void OnUpdate(float dt) { if (Input.WasPressed(Key.Escape)) Quit(); }
    protected override void OnDraw2D(SpriteBatch batch) { /* draw */ }
}

using var app = new Demo();
app.Run();
```
