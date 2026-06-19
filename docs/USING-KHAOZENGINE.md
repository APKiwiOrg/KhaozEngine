# Using KhaozEngine - the consumer contract

This is the authoritative guide to what KhaozEngine does and **how it must be used** by the games that depend
on it. The engine is **MonoGame-free**: Silk.NET windowing/input, Veldrid behind `KhaozEngine.Gpu` for the GPU,
Silk.NET.OpenAL for audio, `System.Numerics` math throughout. Read the [Hard rules](#hard-rules) first; the rest
is reference.

---

## Mental model: one data flow

A game subclasses `GameApp` (2D) or `GameApp3D` (3D). The base owns the window and the per-frame loop; the game
fills in seams.

```
hardware ──► AppWindow (the only Silk.NET/GLFW toucher) ──► InputState (immutable per-frame snapshot)
                                                                  │
                              GameApp.Run() drives, each frame, in order:
                                  Clock.Update(dt) → Viewport.Update(w,h) → OnResize? →
                                  Pointer.Update(Input, Viewport) → OnUpdate(dt) →
                                  OnRenderWorld(frame)  [3D pass]  →  Batch.Begin(Viewport) → OnDraw2D → End
                                                                  │
                 ┌────────────────────────────────────────────────┼─────────────────────────────┐
                 ▼                              ▼                                                 ▼
        SceneManager (scene stack)      Gui (GuiSurface / ScreenStack)                   your game logic
        routes input top-to-bottom      read Pointer / InputManager                      reads InputState / Pointer
```

Renderers compose into the same window: `Render2DSurface` (the 2D batch) and, for 3D, `Render3DSurface` + a
`Scene3D` drawn behind the 2D HUD.

---

## Hard rules

These are not style preferences. Breaking them re-introduces the exact bugs this library was built to remove.

1. **Only `AppWindow` touches the Silk.NET/GLFW input.** No game code reads Silk input devices directly. Each
   frame the window produces an immutable `InputState`; games read it through `GameApp.Input` /
   `InputManager` / `Pointer`. If you need a new piece of raw state, add it to `InputState` and populate it in
   `AppWindow` - never reach around the seam.
2. **Drive input through the loop, not by hand.** `GameApp.Run()` already calls `Pointer.Update(Input, Viewport)`
   once per frame before `OnUpdate`. If you use an `InputManager` directly (e.g. for menu nav), call
   `Update(input, viewport)` once per frame, before you query it.
3. **Hit-test with the bounds helpers, never with raw position + button.** Use `IsTapIn`, `IsPressingIn`,
   `IsHoveringIn`, `IsDraggingIn`, etc. `IsTapIn` enforces the press-origin invariant; a hand-rolled
   `IsPointerDown && rect.Contains(pos)` does not, and it leaks clicks.
4. **An overlay above a still-updating layer must reserve its footprint** with `BlockInputRegion(rect)` every
   frame, and the layer beneath must guard its actions with `IsInputBlocked(point)`. This is half of the
   click-through fix; the other half is `IsTapIn`.
5. **Pass the design viewport to `Pointer.Update` / `InputManager.Update`** so hit-testing lines up with what's
   drawn. `GameApp` does this for you; if you build your own loop, do it yourself.
6. **`System.Numerics` only** - `Vector2/3/4`, `Matrix4x4`. No XNA / MonoGame types anywhere.
7. **Don't fork the packages.** Need an API that isn't there? Add it to KhaozEngine, ship a headless test, bump
   the version, and consume the new version. Pinned versions are how games stay green during each other's
   migrations.

---

## Wiring a game (`KhaozEngine.Game` + `KhaozEngine.Game.Render3D`)

`GameApp` is the abstract 2D game-loop facade. It owns the `AppWindow`, `GameClock`, an `IDesignViewport`, the
`Pointer`, a `Render2DSurface` (`Surface2D`) and its `SpriteBatch` (`Batch`). You configure it with a
`GameAppOptions` and override the seams you need.

```csharp
using System.Numerics;
using KhaozEngine.Game;
using KhaozEngine.Render2D;
using KhaozEngine.Windowing;

public sealed class MyGame : GameApp
{
    public MyGame() : base(GameAppOptions.For("My Game", 1280, 720)) { }

    protected override void OnLoad()              { /* load textures/fonts via Surface2D */ }
    protected override void OnUpdate(float dt)
    {
        if (Input.WasPressed(Key.Escape)) Quit();
        if (Pointer.IsTapIn(new Rect(300, 200, 200, 40))) { /* button hit, click-through-safe */ }
    }
    protected override void OnDraw2D(SpriteBatch batch) { /* batch.Draw(...) / font.DrawString(...) */ }
    protected override void OnResize(int w, int h)      { }
}

// Program.cs
using var game = new MyGame();
game.Run();
```

`GameAppOptions` (a struct): `Title`, `Width`/`Height`, `DesignWidth`/`DesignHeight`, `ScaleMode`, `ClearColor`,
and optional `WindowFactory` / `ViewportFactory` (e.g. `AppWindow.Scaled` for a display-fitted window, or an
`AdaptiveViewport` for responsive layout). Use `GameAppOptions.For(title, w, h)` for the common case.

`GameApp` seams: `OnLoad()`, `OnUpdate(float dt)`, `OnRenderWorld(Frame)` (the 3D pass; empty in 2D),
`OnDraw2D(SpriteBatch)`, `OnResize(int, int)`, `OnDispose()`. Properties you read: `Window`, `Clock`, `Viewport`,
`Pointer`, `Input` (the frame's `InputState`), `Surface2D`, `Batch`, `FrameWidth`/`FrameHeight`/`Dt`,
`ClearColor`. Call `Quit()` to exit.

### Scenes (`SceneManager` / `GameScene`)

For multi-screen games, push `GameScene`s onto a `SceneManager` (full-frame scene stack, distinct from the
2D-only `Gui.ScreenStack`). `Push`/`Pop`/`Replace`/`SwitchTo`/`Clear`; a scene overrides `OnEnter`/`OnExit`,
`OnUpdate(dt)`, `OnDraw2D(batch)`, `OnResize`. Set `DrawBelow` / `UpdateBelow` for an overlay scene (e.g. a pause
menu over a still-rendered match). Feed the manager `Input`/`Pointer`/`Viewport`/`FrameWidth`/`FrameHeight` each
frame, then `Update(dt)` and `Draw2D(batch)`.

### 3D (`GameApp3D`, `IGameScene3D`, `SceneManager.Draw3D`)

`GameApp3D : GameApp` adds a `Render3DSurface` (`Surface3D`) and a `Scene3D` (`Scene`), and a new seam
`OnDraw3D(Scene3D scene)` (it overrides `OnRenderWorld` to run the 3D pass behind the 2D HUD). A scene that draws
3D implements `IGameScene3D` (`void OnDraw3D(Scene3D scene)`), and the app's `OnDraw3D` calls the
`SceneManager.Draw3D(scene)` extension to render the visible scene set.

```csharp
public sealed class MyGame3D : GameApp3D
{
    public MyGame3D() : base(GameAppOptions.For("My 3D Game", 1280, 720)) { }
    protected override void OnDraw3D(Scene3D scene) { _scenes.Draw3D(scene); }   // 3D world
    protected override void OnDraw2D(SpriteBatch batch) { /* HUD over the 3D */ }
}
```

---

## Input (`KhaozEngine.Windowing`)

### InputState - the immutable snapshot

`AppWindow` produces one `InputState` per frame (delivered as `Frame.Input`, surfaced as `GameApp.Input`). All
engine-native, `System.Numerics`:

```csharp
public readonly record struct InputState(
    IReadOnlySet<Key> KeysDown, KeysPressed, KeysReleased,
    IReadOnlySet<MouseButton> MouseDown, MousePressed,
    Vector2 MousePosition, Vector2 MouseDelta, float ScrollDelta,
    int Width, int Height,
    IReadOnlyList<GamepadState> Gamepads, IReadOnlyList<TouchPoint> Touches);

bool IsDown(Key) / WasPressed(Key) / WasReleased(Key);
bool IsDown(MouseButton) / WasPressed(MouseButton);
GamepadState Gamepad(int i = 0);  GamepadState PrimaryGamepad { get; }
```

`Key` and `MouseButton` are engine enums; `GamepadState` exposes `ButtonsDown/Pressed/Released`,
`LeftStick`/`RightStick` (+ `LeftStickDeadzoned(...)`), triggers, and `IsDown/WasPressed/WasReleased`.

### InputManager + Pointer - the higher-level read

`Pointer` (the unified pointer) and `InputManager` (pointer + keyboard/gamepad/menu-nav) derive per-frame state
from an `InputState`. `GameApp` owns a `Pointer` and updates it for you; construct an `InputManager` yourself if
you want edge detection or menu navigation.

```csharp
var im = new InputManager();
im.Update(input, viewport);   // once per frame, BEFORE you query
```

**Unified pointer:** `PointerPosition`, `PressOrigin`, `PointerDelta`, `IsPointerDown`, `IsPointerJustPressed`,
`IsPointerJustReleased` (+ middle/right variants).

**Bounds helpers (use these):**
- `IsTapIn(Rect)` - true on release **only if press-origin and release are both inside the rect**. The
  click-through invariant.
- `IsTapFromTo(originRect, releaseRect)` - press in one rect, release in another (tap-scrim-to-dismiss).
- `IsPressingIn(Rect)` - held, press began inside, still inside ("pressed" visual).
- `IsHoveringIn(Rect)` - inside and not pressed (desktop hover).
- `IsPointerIn(Rect)`, `IsReleasedOutside(Rect)`, `IsDraggingIn(Rect)`, `GetDragDelta(Rect)`.

**Region blocking (the other half of click-through):** `BlockInputRegion(Rect)` from the higher overlay each
frame; `IsInputBlocked(Vector2)` from the layer beneath before acting. Cleared at the start of every `Update`.

**Scroll:** `ScrollDelta`, `IsMouseWheelScrolledUp/Down`.

**Keyboard / gamepad / menu navigation:** `IsKeyDown`, `IsKeyJustPressed`, `IsNewKeyPress(key, PlayerIndex?, out
who)`, `IsNewButtonPress(button, PlayerIndex?, out who)`, `IsMenuUp/Down(PlayerIndex?)`, `IsMenuSelect/Cancel
(PlayerIndex?, out who)`, `IsSelectNext/Previous(PlayerIndex?)`, `IsPauseGame(PlayerIndex?, Rect?)`. Pass `null`
for the player to accept "any connected controller".

### Rect, viewport, clock

- `Rect(X, Y, Width, Height)` is the engine's rectangle (`Right`/`Bottom`/`Contains(Vector2)`).
- `IDesignViewport` (impls: `DesignViewport` letterbox/fill/stretch, `AdaptiveViewport` responsive) maps between
  design space and screen pixels (`DesignToScreen`/`ScreenToDesign`, `GetClipProjection`). `GameApp` owns one
  and passes it into `Pointer.Update`, so design-space coordinates and hit-tests line up.
- `GameClock`: `TimeScale`, `Pause()`/`Resume()`, `RealDeltaSeconds`/`ScaledDeltaSeconds`,
  `Paused`/`Resumed` events. `GameApp.Clock` is updated for you each frame.

---

## Gui (`KhaozEngine.Gui`)

Two styles, both built on Windowing + Render2D.

**Immediate-mode `GuiSurface`** - the common case for HUDs and simple menus. `Begin(batch?, pointer)` then call
widgets each frame; `Button(...)` returns `bool` (true the frame it's clicked). Widgets are click-through-safe by
construction (they hit-test via the `Pointer`).

```csharp
var gui = new GuiSurface(whitePixel);            // a 1x1 white Texture2D
gui.Begin(batch, pointer);
gui.Panel(new Rect(40, 40, 240, 120), bgColor);
gui.Label(font, new Rect(40, 40, 240, 24), "Pause", textColor, GuiAlign.Center);
if (gui.Button(font, new Rect(60, 90, 200, 36), "Resume")) Resume();
```

`GuiSurface` also exposes hover state (`IsHovering`/`HoverEntered`/`HoveredRect`) and a `Slider`. The
`PointerCaptured` gate lets a game suppress world clicks when the pointer is over UI.

**Retained `ScreenStack`** - a routed stack of `Screen`s (top-to-bottom input, bottom-to-top draw, transitions),
for menu-heavy games. `Add`/`Remove`, `Update(dt, input[, viewport])`, `Draw(batch)`. A `Screen` reads input via
`Manager.Pointer` and returns whether it consumed (to block screens below); set a screen non-pass-through for a
modal.

**`FocusNavigator`** - keyboard/gamepad menu focus: `SetCount`, `Focus`, `MoveNext`/`MovePrevious`, `Wrap`, and
`Update(InputManager, PlayerIndex?)` which advances focus from menu-nav edges.

---

## Render2D (`KhaozEngine.Render2D`)

2D rendering on the custom stack. `Render2DSurface(window)` owns the `SpriteBatch` and the loaders;
`GameApp.Surface2D`/`Batch` give you one already wired.

```csharp
Texture2D logo = Surface2D.LoadTexture("logo.png");                 // PNG via StbImageSharp
SpriteFont font = Surface2D.LoadFont("/path/Arial.ttf", 32f);       // runtime TTF via stb_truetype

batch.Begin(Viewport, SamplerMode.Point);        // design-viewport space, crisp pixels; or Begin(camera) / Begin()
batch.Draw(logo, new Vector2(100, 100), Vector4.One);
batch.DrawString(font, "Hello", new Vector2(100, 60), Vector4.One);
batch.End();
```

- `Begin` overloads: `Begin(Camera2D, SamplerMode)` (world space), `Begin(IDesignViewport, SamplerMode)`
  (design space), `Begin(SamplerMode)` (raw screen). `SamplerMode` is `Linear` (default) or `Point`.
- Model transform: each `Begin` also has a `Matrix4x4 transform` overload (`Begin(Matrix4x4)`,
  `Begin(Camera2D, Matrix4x4)`, `Begin(IDesignViewport, Matrix4x4)`) applied to every draw before projection, so
  a composed group (panel + icon + text) tilts/scales/translates as one. `DrawString` has no rotation of its
  own, so the model transform is how text tilts with its card. A `SetScissor` during a transformed pass clips by
  the un-rotated (design) bounds, not the rotated quad (the GPU scissor is axis-aligned).
- `Camera2D`: `Position`/`Zoom`/`Rotation`, `WorldToScreen`/`ScreenToWorld`, `CenterOn`, `PanByScreenDelta`,
  `Focus(rect, ...)`, `ClampPosition(...)`. The camera-feel layer (follow, look-ahead, blends, room cameras,
  screen shake, parallax) lives alongside it in Render2D / Effects.
- Scissor clipping: `SetScissor(Rect)` / `ClearScissor()` (composes with the design viewport).
- `ImageRgba` (CPU, no GPU): `ImageRgba.Load(path)` / `Decode(bytes)` / `Surface2D.LoadImageRgba(path)` give a
  tightly-packed RGBA8 image with `AlphaAt` / `IsOpaqueAt(threshold)` for opaque-pixel collision masks. Pass
  `img.Pixels` to `Surface2D.CreateTexture` to also draw it without re-decoding.
- Offscreen capture (headless / tooling): `Surface2D.CaptureToTexture(...)` and `CaptureToRgba(...)`.

---

## Render3D (`KhaozEngine.Render3D`)

Stylized 3D. `Render3DSurface(window)` owns a `Scene3D`; `GameApp3D.Surface3D`/`Scene` give you one. The GPU
backend (Veldrid) never appears in the API.

```csharp
var scene = Surface3D.Scene;
MeshHandle board = scene.LoadMesh(MeshPrimitives.Plane(...));        // glTF via GltfLoader, or procedural
TextureHandle tex = scene.LoadTexture("dirt.png");
MeshHandle tower = scene.LoadMesh(GltfLoader.Load("tower.glb"), tex);

scene.Camera.Azimuth = MathF.PI/4f;  scene.Camera.OrthoSize = 2.7f;       // ortho iso camera
// or auto-fit a bounds: scene.Camera.Frame(center, size, margin: 1.1f);
Vector3 ground = scene.Camera.ScreenToGround(pointer.Position, w, h, 0f); // picking

// per frame, inside OnDraw3D:
scene.Begin();
scene.Draw(board, Matrix4x4.Identity);
scene.Draw(tower, transform, tint, Material.Shiny);
scene.DrawBillboard(pos, size, color, BillboardBlend.Additive);
scene.DebugCircle(center, up, radius, color);                        // immediate-mode debug overlay
```

- `Scene3D`: `LoadMesh`/`LoadTexture`/`UnloadMesh`, `Begin()`, `Draw(handle, transform[, tint[, material]])`,
  billboards, and a debug-draw overlay (`DebugLine/Ray/Box/Grid/Axes/Circle`). `Post` is the
  `PixelPostProcess` (pixelation / quantize / dither / cel bands / palette for the chunky retro look; the smooth
  look is the default).
- Internal render-target sizing: `Post.RenderScale` (since 5.66.0). The default `FixedInternal` renders into a
  fixed `Post.RenderWidth` x `RenderHeight` target (1600x900) and blit-scales it to the window - the retro path
  (small fixed target + `Pixelated`), but on a window bigger than that target the smooth blit UPscales and
  softens. Set `Post.RenderScale = RenderScale.MatchViewport` to size the target to the actual framebuffer each
  frame instead (1:1, no upscale blur on large / Retina windows; capped at `Post.MaxRenderWidth` x
  `MaxRenderHeight`, default 3840x2160, aspect preserved). Leave it `FixedInternal` for the chunky/`Pixelated`
  look.
- `IsoCamera3D`: `Azimuth`/`Elevation`/`Target`/`OrthoSize`/`Zoom`, `Frame(target, azimuth, size)`,
  `ScreenToRay`, `ScreenToGround`, and the `View`/`Projection`/`ViewProjection` matrices.

---

## ECS (`KhaozEngine.Ecs`)

Independent of input/rendering. A struct-based archetype ECS: components are **structs** implementing
`IComponent`. `World` exposes `Spawn()` / `Despawn(e)` (with an `EntityCommandBuffer` via `World.Commands` for
deferred structural changes), component access `Add/Set/Has/TryGet/Remove<T>` plus `ref T Get<T>` (by-ref, no
boxing), iteration via `Query()` and `ForEach<T1..T8>(RefAction<…>)`, parent/child hierarchy
(`SetParent`/`DespawnTree`), per-`World` resources (`SetResource/GetResource<T>`), and systems grouped + ordered
(`AddSystem(ISystem, group)`, `SetGroupOrder`, `Update(float dt)`). `CachedQuery` reuses a query across ticks to
avoid per-tick allocation; `DeterministicRng` (xorshift128+/splitmix64, `CreateDerived(name)` for per-stream
sub-RNGs) gives platform-stable RNG for lockstep sims; `WorldSerializer` round-trips a world as JSON (uses
`KhaozEngine.Serialization.JsonDefaults.IncludeFields`).

---

## Audio (`KhaozEngine.Audio`)

`AudioSystem` over a cross-platform OpenAL (Silk.NET.OpenAL) backend, no MonoGame. Streaming music (WAV/OGG/MP3,
one track via `PlayMode`, `CurrentTrack`/`TrackChanged`), SFX one-shots, and 3D positional audio
(`PlaySfx`/`PlaySfx3D`/`SetListener`, a 16-voice pool, per-channel volume). `LoadContent(directory)` +
`Update()` per frame.

---

## Diagnostics / logging (`KhaozEngine.Diagnostics`)

One logging service for every game. Configure it once at startup and log through the static `Log`:

```csharp
var paths = new KhaozEngine.App.AppDataPaths("APKiwi", "MyGame");   // publisher-rooted, in KhaozEngine.App
var options = new LoggerOptions { MinimumLevel = LogLevel.Info, DefaultCategory = "Boot" };
options.Sinks.Add(new FileSink(new FileSinkOptions { Path = paths.LogFilePath, PreviousPath = paths.PreviousLogFilePath }));
options.Sinks.Add(new ConsoleSink());
Log.Configure(options);
CrashHandler.Install();
```

Rules for consumers:

- Configure `Log` once per process; call `Log.Shutdown()` on exit.
- Pick a **category**, then log under it (pass an exception as the optional second argument):
  - A single class's logging → `Log.For<T>()` (category = the type name).
  - A feature/subsystem spanning several classes, or a game-side module with no single owning type → 
    `Log.Get("ModuleName")` with a stable PascalCase name.
  - `Log.Info/Warn/Error(...)` (no category) is the catch-all under `DefaultCategory` - fine for one-off
    boot/shutdown lines, not for a subsystem you'll grep later.
- **Never bake a category-like prefix into the message text.** The formatter renders `[Category] message`, so
  `Log.Info("[CloudSave] uploaded")` double-tags. Put the tag in the category: `Log.Get("CloudSave").Info("uploaded")`.
- The game owns its paths via `KhaozEngine.App.AppDataPaths`; the logging core is path-agnostic.
- Add a game-specific target by implementing `ILogSink` and `Log.Manager.AddSink(...)`. Don't fork the logger.
- Logging never throws and never blocks the loop (async writer; `Flush`/`Shutdown` drain it; `CrashHandler`
  flushes on a crash). `MinimumLevel` is runtime-settable.

---

## Foundation packages (brief)

The renderer-free foundation, one line each (all pure .NET / `System.Numerics`, GPU-free):

- **`KhaozEngine.App`**: app identity / data paths: `AppDataPaths` (publisher-rooted: `<base>/APKiwi/<game>/`),
  `BuildMetadata`, `ServiceLocator`.
- **`KhaozEngine.Persistence`**: crash-safe saves: `AtomicJsonWriter`, `PersistenceQueue` (coalesced async
  writes), `SettingsManager<T>` + `FileSettingsStorage`, `SaveEncoder` (Base64 + HMAC), and the `GameStorage`
  facade (paths + queue + settings + encoder).
- **`KhaozEngine.Content`**: config loading + JSON-schema validation: `ConfigLoader` (disk-then-embedded),
  `JsonSchemaValidator`, build-time schema enforcement via the bundled `Content.Validator` tool.
- **`KhaozEngine.Serialization`**: shared `System.Text.Json` baselines (`JsonDefaults.TolerantRead` /
  `IndentedWrite` / `IncludeFields`). Consumed by Content/Persistence/Ecs.
- **`KhaozEngine.Localization`**: `LocalizationManager` (discover cultures + set the thread culture).
- **`KhaozEngine.Platform`**: `Clipboard` (cross-platform text + image, best-effort, never throws).
- **`KhaozEngine.Pooling`**: `ObjectPool<T>` (O(1) rent/return, swap-removal compaction).
- **`KhaozEngine.Collision`**: deterministic `CircleCollision` + `SpatialHashGrid` (bit-identical for lockstep).
- **`KhaozEngine.Updates`**: delta auto-update pipeline (SHA256 manifests + diffing, resumable staged downloads,
  cross-platform staged-apply).
- **`KhaozEngine.Netcode` / `.Abstractions` / `.LiteNetLib`**: transport-free netcode primitives
  (`UnitAxisQuantizer`, `ClientPrediction`, `RemoteCommandQueue`), the zero-dependency channel-split contract
  (`IChannelSplittable<TSelf>` + `NetChannelReliability`), and the LiteNetLib transport binding.

---

## Testing your game headlessly

Because input is injected, you can test logic and hit-testing without a window: construct `InputState` snapshots
frame-by-frame and feed `InputManager.Update`. `dt` is a plain `float` in seconds - there is no `GameTime`.

```csharp
var im = new InputManager();
im.Update(MouseAt(20, 20, down: true));    // press
im.Update(MouseAt(20, 20, down: false));   // release → IsTapIn(rect over 20,20) fires
Assert.True(im.IsTapIn(new Rect(0, 0, 40, 40)));
```

New behaviour added to the library ships with a headless test in `KhaozEngine.Tests`. This is the standard, not
a nicety - it's the reason the raw read sits behind the `AppWindow`/`InputState` seam. See the test project for
the `InputState` builder patterns.

---

## Versioning & change process

- **One shared version** across the whole engine: `<KhaozEngine5xVersion>` in `Directory.Build.props`. Every
  packable project sets `<Version>$(KhaozEngine5xVersion)</Version>`; bumping it releases all packages together.
  `scripts/check-doc-versions.sh` (run in CI) enforces that the engine-version declarations in `CONSUMERS.md`,
  `ROADMAP.md`, and the `README.md` `<PackageReference>` example match.
- SemVer: additive = minor, fixes = patch, breaking = major. Local file-feed for inner-loop dev; GitHub Packages
  on `v*` tags.
- To change the library: edit, add a headless test, `dotnet pack -c Release -o ./local-feed`, consume locally;
  when stable, bump the version + add a `CHANGELOG.md` entry + tag for a published release. Each game adopts on
  its own schedule by bumping its pinned version (or its umbrella metapackage version).
