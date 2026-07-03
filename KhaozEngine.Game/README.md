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

Override `OnResume(TimeSpan wallGap)` to react to an OS sleep/suspend/hibernate (or a long hang): it fires once,
before `OnUpdate`, on the first frame whose wall-clock gap (`Clock.RealWallGapSeconds`, which survives a suspend
where the frame `dt` does not) exceeds `GameAppOptions.ResumeGapThresholdSeconds` (default 30s; 0 or negative
disables). Use it for offline/AFK catch-up, timer re-sync, or an auto-pause. The 0.1s sim-delta clamp and `Dt`
are unaffected.

Set `GameAppOptions.WindowIconPath` (a PNG) or `WindowIcons` (explicit decoded `ImageRgba`, multi-res,
wins over the path) for the runtime window/taskbar icon; `GameApp` decodes via `Render2D.ImageRgba`
and applies it through `AppWindow.SetIcon`. Windows/Linux get the live title-bar/taskbar icon. On macOS
`SetIcon` is a no-op (GLFW can't set the Dock icon), so when `WindowIconPath` is set `GameApp` also feeds that
PNG to `AppWindow.SetMacDockIcon` to set the Cocoa Dock / Cmd-Tab icon at runtime (fixes the generic document
icon on an unbundled `dotnet run`). The Windows `.exe` icon stays a per-game `<ApplicationIcon>`.

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
