# P1 batch 2 — GameApp loop facade + demote Render3DHost (5.30.0-experimental)

Audit P1#4 (no game-loop framework) + P1#5 (POC debt: Render3DHost + a second Key enum). A subclass `GameApp`
base owns the per-frame composition + ordering so games can't get it wrong; migrating the samples onto it frees
the standalone `Render3DHost` (and its private `Key`/`FrameInfo`) to be demoted.

## Part A — new `KhaozEngine.Game` package + `GameApp`

New 5.x package `KhaozEngine.Game` (PackageId `KhaozEngine.Game`, `<Version>$(KhaozEngine5xVersion)</Version>`,
README, `InternalsVisibleTo KhaozEngine.Tests`) referencing `KhaozEngine.Windowing` + `KhaozEngine.Render2D` +
`KhaozEngine.Render3D` + `KhaozEngine.Gui`. Add to `KhaozEngine.slnx` + `KhaozEngine.Tests.csproj`. (It sits
ABOVE the renderers — that's why it's a new top package, not in Windowing which is the floor.)

```csharp
namespace KhaozEngine.Game;

public struct GameAppOptions
{
    public string Title;
    public int Width, Height;
    public int DesignWidth, DesignHeight;   // 0 => use Width/Height (1:1 design space)
    public ScaleMode ScaleMode;             // default Fit
    public Vector4 ClearColor;              // default dark
    public bool Enable3D;                   // create a Render3DSurface + Scene
    public static GameAppOptions For(string title, int width, int height); // sensible defaults
}

public abstract class GameApp : IDisposable
{
    protected GameApp(in GameAppOptions options);

    protected AppWindow Window { get; }
    protected GameClock Clock { get; }
    protected DesignViewport Viewport { get; }
    protected Pointer Pointer { get; }            // design-space; updated each frame BEFORE OnUpdate
    protected InputState Input { get; }           // current frame's raw input (for custom/3D-pick needs)
    protected Render2DSurface Surface2D { get; }
    protected Render3DSurface? Surface3D { get; } // null unless Enable3D
    protected Scene3D? Scene { get; }             // Surface3D?.Scene
    protected SpriteBatch Batch { get; }          // Surface2D.Batch
    protected int FrameWidth { get; }
    protected int FrameHeight { get; }
    protected float Dt { get; }                   // Clock.ScaledDeltaSeconds this frame
    public Vector4 ClearColor { get; set; }       // forwards to Window.ClearColor

    protected virtual void OnLoad() {}
    protected virtual void OnUpdate(float dt) {}
    protected virtual void OnDraw3D(Scene3D scene) {}     // only if Enable3D; scene.Begin() already called
    protected virtual void OnDraw2D(SpriteBatch batch) {} // batch.Begin(Viewport) already called
    protected virtual void OnResize(int width, int height) {}
    protected void Quit();                        // Window.Close()

    public void Run();      // the fixed, correct ordering (below)
    public void Dispose();  // disposes surfaces + window
}
```

`Run()` ordering (the whole point — games never write this):
```
OnLoad();
Window.Run(frame =>
{
    Clock.Update(frame.Dt);
    // expose Input/FrameWidth/FrameHeight/Dt for this frame
    Viewport.Update(frame.Width, frame.Height);
    if (frame.Width != lastW || frame.Height != lastH) { OnResize(frame.Width, frame.Height); lastW=..; lastH=..; }
    Pointer.Update(frame.Input, Viewport);
    OnUpdate(Clock.ScaledDeltaSeconds);
    if (Surface3D is not null) { Surface3D.Scene.Begin(); OnDraw3D(Surface3D.Scene); Surface3D.Render(frame); }
    Surface2D.NewFrame(frame);
    Surface2D.Batch.Begin(Viewport);
    OnDraw2D(Surface2D.Batch);
    Surface2D.Batch.End();
});
```
Notes: ctor sets `Window.ClearColor = options.ClearColor`, builds the `DesignViewport` (design = Width/Height if
0), the `Render2DSurface`, and (if Enable3D) the `Render3DSurface`. `GameApp` is the optional convenience layer
— a game with special needs can still use `AppWindow.Run` directly (keep that path public/unchanged).

Tests (`KhaozEngine.Tests`, headless — `GameApp.Run` needs a real window so the loop itself is sample/golden-
verified, NOT unit-tested): cover `GameAppOptions.For` defaults + the design-size fallback (0 → Width/Height).
Keep it light; the integration proof is the migrated samples.

## Part B — migrate the samples (validate GameApp)

- **Render3DSample**: rewrite onto `GameApp` (Enable3D = true): the host setup → ctor options; the per-frame
  scene building/controls → `OnLoad`/`OnUpdate`/`OnDraw3D` (read `Input` for the camera controls it has;
  Esc → `Quit()`). Preserve the demo's content/behaviour. This REMOVES its use of `Render3DHost`.
- **GuiSample**: rewrite onto `GameApp` (2D, DesignWidth/Height 960/540): `ScreenStack` lives in the subclass;
  `OnUpdate` → `stack.Update(dt, Input, Viewport)`; `OnDraw2D(batch)` → `stack.Draw(batch)`; Esc → `Quit()`.
  Validates the 2D + design-viewport path. (MiniGame/Render2DSample/WindowingSample may stay as-is — migrate
  only if trivial; don't expand scope.)

## Part C — demote the POC debt (P1#5)

With Render3DSample off `Render3DHost`, nothing public consumes it. Make `Render3DHost`, `Render3D.Input.Key`,
and `Render3D.Input.FrameInfo` **`internal`** (or delete `Render3DHost` if nothing references it — prefer
internal + `[Obsolete("use KhaozEngine.Game.GameApp")]` if kept, or delete cleanly and note it). The single
public `Key` enum is then `KhaozEngine.Windowing.Key`. Confirm `dotnet build KhaozEngine.slnx` stays clean.

## Files / Release
- New `KhaozEngine.Game/` (csproj + README + `GameApp.cs` + `GameAppOptions.cs`); slnx + Tests refs.
- Rewrite `Render3DSample/Program.cs`, `GuiSample/Program.cs`.
- `KhaozEngine.Render3D`: demote `Render3DHost.cs` + `Input/Key.cs` + `Input/FrameInfo.cs`.
- Bump 5.29.0 → 5.30.0-experimental, CHANGELOG, pack 8 pkgs (incl. new KhaozEngine.Game).

## Verification
- `dotnet build KhaozEngine.slnx` clean (samples compile on GameApp).
- `dotnet test` green (default; goldens skipped) — report count.
- `KE_GPU_TESTS=1 dotnet test --filter FullyQualifiedName~Golden` — both goldens still pass (GameApp doesn't
  touch the snapshot path; this just confirms nothing regressed). Do NOT re-bake.
- Controller runs `Render3DSample --smoke` (it has a smoke mode) to confirm the 3D GameApp renders, and eyeballs.
