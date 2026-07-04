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

Set `GameAppOptions.PresentMode` (`Vsync` default / `Immediate`) and/or `GameAppOptions.FrameCapHz` (0 = uncapped)
to control presentation (since 9.23.0). `FrameCapHz` paces the loop to a target Hz with a monotonic-clock limiter
independent of the swapchain's vsync - pin it to an integer multiple of a fixed network tick (e.g. 60/120 for 30 Hz)
to keep presentation phase-aligned. It is the deterministic cap where vsync does not throttle (notably Mac/Metal),
and is applied on both the default and a custom `WindowFactory` window; `PresentMode` is honoured on the default
window (a custom factory must forward it).

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
