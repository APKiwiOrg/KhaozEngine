# KhaozEngine.Game (experimental, 5.x)

A game-loop facade over the MonoGame-free 5.x stack. `GameApp` is an abstract base that owns the
per-frame composition + ordering so a game can't get it wrong:

```
OnLoad();
each frame:
  Clock.Update(dt)
  Viewport.Update(window size)   -> OnResize on change
  Pointer.Update(input, Viewport)
  OnUpdate(Dt)
  if 3D: Scene.Begin(); OnDraw3D(Scene); Surface3D.Render(frame)
  Surface2D.NewFrame(frame); Batch.Begin(Viewport); OnDraw2D(Batch); Batch.End()
```

Subclass it and override `OnLoad` / `OnUpdate(dt)` / `OnDraw3D(scene)` / `OnDraw2D(batch)` /
`OnResize(w, h)`; call `Quit()` to close. Construct with `GameAppOptions.For(title, w, h)` (set
`Enable3D = true`, `DesignWidth/Height`, `ScaleMode`, `ClearColor` as needed).

It sits above the renderers (`KhaozEngine.Windowing` + `Render2D` + `Render3D` + `Gui`). It is the
optional convenience layer: a game with special needs can still drive `AppWindow.Run` directly.

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
