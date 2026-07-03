# Game scene/state stack for KhaozEngine.Game (5.38.0)

Add a game-level **scene stack** so games stop hand-rolling an `AppState` enum + giant `switch` (Hardpoint's
Title/Playing/Paused/Win-Lose is the motivating case). A `GameScene` is a full game state - it owns its update +
3D submission + 2D HUD draw + lifecycle - and a `SceneManager` runs a stack of them with overlay support
(a pause scene drawn over a frozen gameplay scene), deferred transitions, and enter/exit lifecycle.

This is DISTINCT from and composable with the existing `KhaozEngine.Gui` `ScreenStack`/`Screen` (which is a
2D-UI-only screen stack): a `GameScene` covers the whole frame (3D world + 2D HUD), lives in `KhaozEngine.Game`
(which owns both Surface2D and Surface3D), and a scene may itself use a Gui `ScreenStack`/`GuiSurface` internally
for its menus. No change to the Gui ScreenStack.

## Part A - `GameScene` (abstract, `KhaozEngine.Game`)
```csharp
public abstract class GameScene
{
    // Set by the manager when the scene is pushed; null before/after. Scenes read shared per-frame context
    // (Input/Pointer/Viewport/FrameWidth/FrameHeight) and drive transitions through it.
    public SceneManager Manager { get; internal set; }

    /// When true, the scene directly below this one is also DRAWN (this scene is a transparent overlay,
    /// e.g. a pause menu over the frozen game). Default false (opaque: covers what's below).
    public bool DrawBelow;
    /// When true, the scene directly below this one also UPDATES (rare; default false, so an overlay freezes
    /// the scene under it).
    public bool UpdateBelow;

    public virtual void OnEnter() { }              // pushed onto the stack
    public virtual void OnExit() { }               // removed from the stack
    public virtual void OnUpdate(float dt) { }     // per-frame sim (only when this scene is "live" - see gating)
    public virtual void OnDraw3D(Scene3D scene) { }   // submit 3D instances (only when 3D enabled + visible)
    public virtual void OnDraw2D(SpriteBatch batch) { } // draw HUD/UI (only when visible)
    public virtual void OnResize(int width, int height) { }
}
```
Keep OnEnter/OnExit the only lifecycle hooks (covered/revealed is derivable: a covered scene simply stops getting
OnUpdate). Do NOT add OnCovered/OnRevealed this release (YAGNI; can add later without breaking).

## Part B - `SceneManager` (sealed, `KhaozEngine.Game`)
Holds a stack (index 0 = bottom, last = top/active). Public surface:
```csharp
public sealed class SceneManager
{
    // Per-frame context the game sets before Update (scenes read via Manager). Mirrors Gui ScreenStack.
    public InputState Input { get; set; }
    public Pointer Pointer { get; set; }
    public IDesignViewport? Viewport { get; set; }
    public int FrameWidth { get; set; }
    public int FrameHeight { get; set; }

    public IReadOnlyList<GameScene> Scenes { get; }
    public GameScene? Active { get; }     // top of stack, or null when empty
    public int Count { get; }

    public void Push(GameScene scene);    // add on top
    public void Pop();                    // remove the top (no-op if empty)
    public void Replace(GameScene scene); // swap the top: pop top (if any) + push scene
    public void SwitchTo(GameScene scene);// hard switch: clear the whole stack + push scene
    public void Clear();                  // remove all

    public void Update(float dt);         // apply pending transitions, then update live scenes
    public void Draw3D(Scene3D scene);    // draw visible scenes bottom-to-top (3D)
    public void Draw2D(SpriteBatch batch);// draw visible scenes bottom-to-top (2D)
    public void Resize(int width, int height); // forward to all scenes (or visible? -> all)
}
```

### Update gating (overlays freeze what's below unless UpdateBelow)
On `Update(dt)`: first apply pending transitions (see deferral), then update from the TOP down: the top scene
always updates; a lower scene updates only if EVERY scene above it has `UpdateBelow == true`. (Stop descending at
the first scene above that does not pass updates through.)

### Draw gating (overlays reveal what's below via DrawBelow)
Compute the bottom-most VISIBLE index: start at the top; while that scene has `DrawBelow == true` and there is a
scene below it, descend one. Draw scenes from that index UP TO the top, in order (bottom-to-top), so overlays
paint over the scenes they reveal. `Draw3D` and `Draw2D` use the SAME visibility set. (If the stack is empty,
both are no-ops.)

### Deferred transitions (mutation-safe)
Push/Pop/Replace/SwitchTo/Clear called from WITHIN `Update`/`OnUpdate` must not mutate the stack mid-iteration.
Track an `_updating` flag: while updating, queue the operations and DRAIN them at the END of `Update` (after the
update pass), preserving call order; OnEnter/OnExit fire when the op is actually applied. When NOT updating, apply
immediately (so a game can `Push` the initial scene before the first frame and see `Active` right away). Draw
never mutates the stack.

### Lifecycle ordering
- `Push(s)`: append s, set `s.Manager = this`, call `s.OnEnter()`.
- `Pop()`: call `top.OnExit()`, remove it, clear its `Manager`.
- `Replace(s)`: `Pop()` (if non-empty) then `Push(s)`.
- `SwitchTo(s)`: pop ALL (top-down, each OnExit) then `Push(s)`.
- `Clear()`: pop all (top-down, each OnExit).
- `Resize(w,h)`: set FrameWidth/Height, call `OnResize(w,h)` on every scene in the stack.

## Part C - GameApp integration (sample, NOT a new base class)
Do NOT add a SceneManager-owning GameApp subclass this release (keep `SceneManager` composable + usable with a
raw `AppWindow`). Instead, add/extend a SAMPLE that shows the intended wiring: a `GameApp` subclass holds a
`SceneManager`, and each frame:
```csharp
protected override void OnUpdate(float dt) {
    _scenes.Input = Input; _scenes.Pointer = Pointer; _scenes.Viewport = Viewport;
    _scenes.FrameWidth = FrameWidth; _scenes.FrameHeight = FrameHeight;
    _scenes.Update(dt);
}
protected override void OnDraw3D(Scene3D scene) => _scenes.Draw3D(scene);
protected override void OnDraw2D(SpriteBatch batch) => _scenes.Draw2D(batch);
protected override void OnResize(int w, int h) => _scenes.Resize(w, h);
```
Pick an existing sample to extend (e.g. `MiniGame` or `GuiSample`) OR add a tiny `SceneSample` with 2 scenes: a
menu scene (press a key / click a button -> SwitchTo a play scene) and a play scene (Esc -> Push a pause overlay
scene with `DrawBelow=true, UpdateBelow=false`; Esc again -> Pop). This proves push/pop/overlay/switch live.
Honor `KE_MAX_FRAMES`. Keep it minimal.

## Tests (headless, KhaozEngine.Tests - the core value; no GPU needed)
Use a `FakeScene : GameScene` that records calls (OnEnter/OnExit/OnUpdate counts + an ordered log) WITHOUT
drawing (OnDraw3D/OnDraw2D can be no-ops, or record that they'd draw). Test the MANAGER logic with `Update(dt)`
and a draw-visibility probe:
- Push sets Active, calls OnEnter; Pop calls OnExit and restores the previous Active; Count tracks size.
- Replace swaps the top (old top OnExit, new OnEnter, scene below untouched).
- SwitchTo clears all (each OnExit, top-down order) then pushes (OnEnter).
- Update gating: stack [A, B(top)] with B.UpdateBelow=false -> only B.OnUpdate; with B.UpdateBelow=true -> both
  A and B update. A three-deep case: [A, B, C(top)], C.UpdateBelow=true, B.UpdateBelow=false -> C and B update,
  A does NOT (descent stops at B).
- Draw visibility: expose the computed visible-from index (an `internal` method like `FirstVisibleIndex()` the
  test can read, OR have Draw2D push to a fake batch - prefer the internal index method to avoid a GPU). [A,
  B(top)] B.DrawBelow=true -> visible {A,B}; B.DrawBelow=false -> visible {B} only. Empty stack -> nothing.
- Deferred transitions: a scene whose OnUpdate calls `Manager.Pop()` (or Push) must NOT corrupt the stack; the
  op applies after the update pass; assert the stack + that the popped scene's OnExit ran once, and the rest of
  the frame's update used the pre-pop stack. Call order of multiple queued ops is preserved.
- Resize forwards OnResize to every scene and updates FrameWidth/Height.
- Pop/Clear on an empty stack are safe no-ops.

## Release
- Bump `<KhaozEngineVersion>` 5.37.0 -> 5.38.0; CHANGELOG; update the `KhaozEngine.Game` `<Description>` to
  mention the scene stack. Pack the 8 5.x packages; merge --no-ff, suite green on main, pack canonical, tag
  `v5.38.0`, push. (Hardpoint refactor onto scenes is a SEPARATE follow-up - do NOT bundle it.)

## Verification
- `dotnet build KhaozEngine.slnx` clean.
- `dotnet test KhaozEngine.Tests` green (report counts) - the new SceneManager tests pass headless.
- `KE_GPU_TESTS=1 dotnet test --filter Golden` still green (untouched render path).
- Sample smoke: `KE_MAX_FRAMES=120 dotnet run --project <sample>` exercises push/switch/overlay/pop, exits 0.
- `grep` confirms no GPU/Veldrid leak in the SceneManager public API (it uses Scene3D/SpriteBatch params + BCL/
  Windowing/Render2D types already public in Game).
