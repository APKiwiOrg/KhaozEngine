# Using KhaozEngine — the consumer contract

This is the authoritative guide to what KhaozEngine does and **how it must be used** by the three games that depend on it. Read the [Hard rules](#hard-rules) section first; the rest is reference.

---

## Mental model: one data flow

```
hardware ──► MonoGameRawInput.Read() ──► RawInputState (immutable snapshot)
                                              │
                                  InputManager.Update(raw, isActive)
                                              │
                 ┌────────────────────────────┼────────────────────────────┐
                 ▼                            ▼                            ▼
        ScreenManager.Update(gameTime)   widgets (Button…)        your game logic
        routes input top-to-bottom       read InputManager        reads InputManager
```

Every frame, in this exact order:
1. `MonoGameRawInput.Read()` snapshots the hardware into a `RawInputState`.
2. `InputManager.Update(raw, IsActive)` derives the unified pointer, edges, keyboard/gamepad state, and clears the per-frame blocked regions.
3. `ScreenManager.Update(gameTime)` routes input through the screen stack.
4. Screens and widgets ask the `InputManager` questions (`IsTapIn`, `IsInputBlocked`, `IsMenuSelect`, …). They never touch hardware.

---

## Hard rules

These are not style preferences. Breaking them re-introduces the exact bugs this library was built to remove.

1. **Only `MonoGameRawInput` touches the MonoGame input statics.** No `Mouse.GetState()`, `Keyboard.GetState()`, `GamePad.GetState()`, or `TouchPanel.GetState()` anywhere else in a game. If you need a new piece of raw state, add a field to `RawInputState` and read it in `MonoGameRawInput` — never reach around the seam.
2. **Call `InputManager.Update(rawInput.Read(), IsActive)` once per frame, before `ScreenManager.Update`.** Pass the real `Game.IsActive` — it suppresses ghost taps when the window regains focus.
3. **Hit-test with the bounds helpers, never with raw position + button.** Use `IsTapIn`, `IsPressingIn`, `IsHoveringIn`, `IsDraggingIn`, etc. `IsTapIn` enforces the press-origin invariant; hand-rolled `IsPointerDown && rect.Contains(pos)` checks do not, and they leak clicks.
4. **An overlay that sits above a still-updating layer must reserve its footprint** with `BlockInputRegion(rect)` every frame, and the layer beneath must guard its actions with `IsInputBlocked(...)`. This is half of the click-through fix; the other half is `IsTapIn`.
5. **`GameScreen.Update` returns whether it consumed input this frame.** Return `true` when this screen should stop input reaching screens below it, `false` to let it fall through. Getting this wrong breaks routing.
6. **Don't fork the packages.** Need an API that isn't there? Add it to KhaozEngine, ship a headless test, bump the version, and consume the new version. Pinned versions are how games stay green during each other's migrations.

---

## Input layer (`KhaozEngine.Input`)

### RawInputState + the seam

```csharp
public readonly record struct RawInputState(
    Point MousePosition, bool MouseLeftDown, bool MouseMiddleDown, bool MouseRightDown,
    int ScrollWheelValue, KeyboardState Keyboard,
    IReadOnlyList<GamePadState> GamePads, IReadOnlyList<TouchPoint> Touches,
    Rectangle WindowBounds);

public interface IRawInput { RawInputState Read(); }
public sealed class MonoGameRawInput : IRawInput { public MonoGameRawInput(GameWindow window); }
```

Production uses `MonoGameRawInput`. Tests construct `RawInputState` directly and inject it — that is the whole point of the seam.

### InputManager

```csharp
new InputManager(bool isMobile = false, ICoordinateTransform? transform = null);
void Update(RawInputState raw, bool isActive);
```

**Unified pointer** (mouse on desktop, primary touch on mobile — chosen internally from `isMobile`, so higher layers never branch on platform):
`PointerPosition`, `PressOrigin`, `PointerDelta`, `IsPointerDown`, `IsPointerJustPressed`, `IsPointerJustReleased`, `IsMobile`.

**Bounds helpers (use these):**
- `IsTapIn(rect)` — true on release **only if press-origin and release are both inside `rect`**. The click-through invariant.
- `IsTapFromTo(originRect, releaseRect)` — press in one rect, release in another (e.g. tap-scrim-to-dismiss).
- `IsPressingIn(rect)` — held, press began inside, still inside (button "pressed" visual).
- `IsHoveringIn(rect)` — inside and not pressed (desktop hover).
- `IsPointerIn(rect)`, `IsReleasedOutside(rect)`.

**Gestures (Nullwake-derived):** `IsDraggingIn(rect)`, `GetDragDelta(rect)`, `ScrollWheelDelta`, `GetScrollIn(rect)`, `IsMouseWheelScrolledUp/Down`, `IsPinching`, `GetPinchDeltaIn(rect)`. Drag/scroll/pinch only fire when the interaction started in / is over the given bounds.

**Region blocking (the other half of click-through):** `BlockInputRegion(rect)` (call from a higher overlay each frame), `IsInputBlocked(point)` (check from the layer beneath before acting). The blocked list is cleared at the start of every `Update`.

**Keyboard / gamepad / menu-navigation (SpaceGame-derived):** `IsKeyDown`, `IsKeyJustPressed`, `IsNewKeyPress(key, PlayerIndex?, out who)`, `IsNewButtonPress(button, PlayerIndex?, out who)`, `IsMenuSelect/Cancel(PlayerIndex?, out)`, `IsMenuUp/Down(PlayerIndex?)`, `IsSelectNext/Previous(PlayerIndex?)`, `IsPauseGame(PlayerIndex?, Rectangle?)`. Pass `null` for the player to accept "any connected controller". One physical keyboard is assumed (player index is preserved for API compatibility but the keyboard is shared).

### The click-through fix, in full

Four layered defenses; a game gets all four for free if it follows the rules:

1. **`IsTapIn` invariant** — per-widget: a press that began outside a target can't register as a tap on it.
2. **`receivesInput` flag** — the first visible, non-passthrough, input-consuming screen sets `inputHandled`; every screen below sees `receivesInput == false`.
3. **`PassUpdateThrough = false`** — a modal screen stops the loop entirely; layers below neither update nor see input (gameplay freezes under a pause menu).
4. **`BlockInputRegion` / `IsInputBlocked`** — for an overlay that *passes update through* onto a live layer (a HUD/toolbar over gameplay): it reserves its rect, and the live layer checks `IsInputBlocked(PressOrigin)` before acting, so a click on the overlay never drops through.

### Coordinate transforms

`InputManager` routes every pointer position through an `ICoordinateTransform` so a resolution/camera change doesn't churn callers:

```csharp
public interface ICoordinateTransform { Vector2 ScreenToVirtual(Vector2 screen); Rectangle? VirtualBounds { get; } }
```
- `IdentityTransform.Instance` — screen pixels are virtual coords (default).
- `MatrixTransform(matrix, virtualBounds?)` — arbitrary matrix (e.g. a game's existing input-transform matrix). `SetMatrix(...)` to update.
- `VirtualResolution(graphicsDeviceManager, isMobile, baseWidth = 440, referenceHeight = 956)` — adaptive scaling (mobile: fixed virtual width, scale to fill; desktop: 1:1). Also serves as your `SpriteBatch` `ScaleMatrix`. `VirtualBounds` clamps the pointer into the viewport.

A camera's world↔screen transform is a *rendering* concern and stays in the game; only the screen→virtual mapping belongs on the InputManager.

---

## Screen layer (`KhaozEngine.Screens`)

```csharp
public enum ScreenState { TransitionOn, Active, TransitionOff, Hidden }
public enum InputConsumption { ConsumeWhenVisible, ConsumeWhenHandled }

public abstract class GameScreen {
    public int DrawOrder; public bool PassUpdateThrough; public bool AlwaysReceivesInput;
    public InputConsumption InputConsumption; public ScreenState State;
    public float TransitionOnDuration, TransitionOffDuration, TransitionAlpha;
    public PlayerIndex? ControllingPlayer; public GestureType EnabledGestures;
    public ScreenManager Manager; public bool IsExiting;
    public virtual void LoadContent(); public virtual void UnloadContent();
    public abstract bool Update(GameTime gameTime, bool receivesInput);   // returns "consumed?"
    public abstract void Draw(GameTime gameTime, SpriteBatch spriteBatch);
    public void ExitScreen();
}

public sealed class ScreenManager {
    public ScreenManager(InputManager input);
    public InputManager Input; public GraphicsDevice? GraphicsDevice; public SpriteBatch? SpriteBatch;
    public IServiceProvider? Services; public Action? ExitRequested; public IReadOnlyList<GameScreen> Screens;
    public void Add(GameScreen); public void Remove(GameScreen); public void RequestExit();
    public void Update(GameTime); public void Draw(GameTime, SpriteBatch);
}
```

### Routing (top-to-bottom by `DrawOrder`)

```
inputHandled = false
for each screen, highest DrawOrder first:
    advance transition; if Hidden: skip
    receivesInput = !inputHandled || screen.AlwaysReceivesInput
    consumed = screen.Update(gameTime, receivesInput)        // the bool you return
    if receivesInput && consumed && !AlwaysReceivesInput: inputHandled = true
    if !PassUpdateThrough: break                              // modal stops the loop
```
`Draw` runs bottom-to-top over all non-Hidden screens.

### The two consumption policies

`InputConsumption` is **intent you implement via your return value**, not magic the manager applies:
- **`ConsumeWhenVisible`** (the common case): a visible interactive screen occupies input whether or not it did anything. Implement by `return receivesInput;`.
- **`ConsumeWhenHandled`**: only block lower screens when you actually handled something (e.g. a popup that should let unrelated clicks fall through). Implement by returning the real handled result; `return false` when you didn't act.

### Flags

- `PassUpdateThrough` — `false` = modal: screens below don't update or get input. `true` = a transparent/HUD layer over a live one.
- `AlwaysReceivesInput` — receives input even when a higher screen consumed it (a persistent nav bar), and does not itself set `inputHandled`.
- `State = Hidden` — skipped entirely (no update, no input, no block). Set it before `Add` to add a screen hidden.

### Transitions

The manager owns transition timing. Set `TransitionOnDuration`/`TransitionOffDuration` (seconds; `0` = instant). On `Add`, a screen with a non-zero on-duration enters `TransitionOn` with `TransitionAlpha` ramping `0→1`; `ExitScreen()` ramps `1→0` then removes. Read `TransitionAlpha` (1 = fully visible) in `Draw` to fade.

---

## UI layer (`KhaozEngine.UI`)

Widgets (`Button`, `Slider`, `Toggle`, `Dropdown`, `ScrollablePanel`, `TextInput`, `Tooltip`, `MenuTile`, `ExpandableRow`, …) take an `InputManager` and a `PrimitiveRenderer` and call the same bounds helpers — so they are click-through-safe by construction. `LayoutConstants.TopBarHeight` / `BottomNavHeight` are settable statics (default to Nullwake's `48`/`52`); set them once at startup for your chrome. `TextInputHandler(maxLength, charValidator)` is a keystroke state machine: `ProcessInput(InputManager, PlayerIndex?)` returns whether it consumed, exposes `Text`, `CaretBlinkTimer`, `PasteRequested`, `TextDeleted` (letters map to lowercase).

> **4.0.0 note:** `PrimitiveRenderer` and `ColorHelper` no longer live in `KhaozEngine.UI`; they moved to **`KhaozEngine.Graphics`** (low-level rendering helpers, see the Graphics section). `KhaozEngine.UI` depends on Graphics, so widgets still get a `PrimitiveRenderer` exactly as before; game code that constructs or passes one needs `using KhaozEngine.Graphics;`.

---

## ECS layer (`KhaozEngine.Ecs`)

Independent of input/screens. A struct-based archetype ECS: components are **structs** implementing `IComponent` (plain data). `World` exposes `Spawn()` / `Despawn(e)` (with an `EntityCommandBuffer` via `World.Commands` for deferred structural changes), component access `Add/Set/Has/TryGet/Remove<T>` plus `ref T Get<T>` (by-ref, no boxing), iteration via `Query()` and `ForEach<T1..T8>(RefAction<…>)`, parent/child hierarchy (`SetParent`/`DespawnTree`), per-`World` resources (`SetResource/GetResource<T>`), and systems grouped + ordered (`AddSystem(ISystem, group)`, `SetGroupOrder`, `Update(float dt)`). `CachedQuery` reuses a query across ticks to avoid per-tick allocation; `DeterministicRng` gives platform-stable RNG; `WorldSerializer` saves/loads a world as JSON (uses `KhaozEngine.Serialization.JsonDefaults.IncludeFields`).

---

## Graphics layer (`KhaozEngine.Graphics`)

Independent of input/screens. `Camera2D` is a game-agnostic 2D matrix camera. `Position` is the world
point shown at the center of the viewport; `Zoom` (> 0) and `Rotation` (radians, CCW) scale and roll
the view about that point. The core methods take an explicit `Viewport`, so the math is fully headless
(no `GraphicsDevice`); no-arg overloads use the settable `Viewport` property (set it once, refresh on
`Window.ClientSizeChanged`).

```csharp
var cam = new Camera2D { Viewport = GraphicsDevice.Viewport, Zoom = 2f };
cam.Position = player.WorldPosition;                              // follow

// Render world-space content through the view matrix:
spriteBatch.Begin(transformMatrix: cam.GetViewMatrix());
// ... draw world ...
spriteBatch.End();

Vector2 mouseWorld = cam.ScreenToWorld(mouseScreenPos);          // pick/aim in world space
```

- `WorldToScreen` / `ScreenToWorld` convert between spaces (inverse requires `Zoom` > 0; a non-positive
  zoom makes the matrix singular and yields NaN).
- `ClampPosition(desired, worldBounds[, viewport])` returns `desired` clamped so the visible world rect
  stays inside `worldBounds`, centering on any axis where the world is smaller than the view. It does
  not mutate `Position` (assign the result yourself). Exact when `Rotation` is 0 (the typical
  platformer/scroller case); approximate with a rotated camera.
- `CenterOn(world)` / `Focus(rect, viewport, padding, minZoom, maxZoom)` frame a point or fit-to-rect.
  `CameraController` drives a `Camera2D` from an `InputManager` (drag/wheel/pinch pan+zoom, tap-vs-pan).
  `CameraFollow` eases `Position` toward a target with frame-rate-independent smoothing (`Stiffness`),
  an optional screen-space `Deadzone`, and bounds clamp. `PinchGestureTracker` / `CameraGestures` are
  the shared gesture core (also used by `UI.PannableCanvas`).

**Rendering primitives (moved here in 4.0.0).** `PrimitiveRenderer` draws shapes from a 1×1 white
pixel (`DrawFilledRect`/`DrawRect`/`DrawLine`/`DrawCircle`/`DrawFilledCircle`/`DrawRing` (radius-adaptive
segment count, thickness-aware)/`DrawVerticalGradient`/`DrawProgressBar`) and recreates its pixel on
device reset. `ColorHelper.ParseHex` parses hex colors. Both used to live in `KhaozEngine.UI`; game code
referencing them needs `using KhaozEngine.Graphics;`.

Also in this package: `DisplayManager` (below) for window/display config.

---

## DisplayManager (KhaozEngine.Graphics)

`DisplayManager` centralizes window/display configuration so a game does not poke
`GraphicsDeviceManager`/`GameWindow` directly. Construct it in your `Game` constructor (where
`graphicsDeviceManager` and `Window` already exist) with a declarative `DisplaySettings`:

    // 932x430 landscape (iPhone 14/15 Pro Max logical points)
    display = new DisplayManager(graphicsDeviceManager, Window, DisplaySettings.Landscape(932, 430));

    // Or via the device-size catalog (same 932x430):
    display = new DisplayManager(graphicsDeviceManager, Window, DevicePresets.IPhone15ProMax.Landscape());

`DisplaySettings` is an immutable record: `Width`, `Height`, `Mode` (`WindowMode.Windowed` /
`BorderlessFullscreen` / `ExclusiveFullscreen`), `AllowUserResizing`, `MinWidth`/`MinHeight`
floor, `SupportedOrientations`, `Title`. Build variants with `with`, or use the
`DisplaySettings.Landscape(w, h)` / `Portrait(w, h)` factories.

Runtime changes:

    display.SetResolution(1280, 720);
    display.ToggleFullscreen();
    display.SetResizable(true, minWidth: 640, minHeight: 360); // floor enforced on resize

`Width`/`Height`/`Size` report the current backbuffer size (use `display.Size` instead of reading
`PreferredBackBufferWidth/Height`). `VirtualResolution` is unchanged: `DisplayManager` owns the
device config, `VirtualResolution` reads it for its coordinate scaling.

---

## Testing your game's screens headlessly

Because input is injected, you can test routing and screen logic without a window:

```csharp
var im = new InputManager();
var m  = new ScreenManager(im);
m.Add(new MyScreen());
im.Update(MouseAt(20, 20, down: true),  isActive: true);  m.Update(Zero);   // press
im.Update(MouseAt(20, 20, down: false), isActive: true);  m.Update(Zero);   // release → IsTapIn fires
// assert on your screen's state
```
Construct `RawInputState` in a helper; `GameTime` is headless-constructible (`new GameTime(TimeSpan.Zero, TimeSpan.FromSeconds(dt))`). See `KhaozEngine.Tests` for the patterns.

---

## Required wiring for these games (the MUST list)

Every consuming game's `Game` subclass:
- Creates exactly one `MonoGameRawInput(Window)`, one `InputManager`, one `ScreenManager`.
- In `Update`: `input.Update(rawInput.Read(), IsActive);` **then** `screens.Update(gameTime);` — never the reverse, never a second input read.
- In `Draw`: `screens.Draw(gameTime, spriteBatch);`.
- Sets `IsMouseVisible = true` on desktop.
- Passes `IsMobile` into `InputManager` (and `VirtualResolution`, if used).
- Routes platform selection through `isMobile`, not per-call branching.
- Resolves dependencies for screens however the game prefers (`ScreenManager.Services` is a generic `IServiceProvider` if you use a container); screens reach the input through `Manager.Input`.

---

## Diagnostics / logging (`KhaozEngine.Diagnostics`)

One logging service for every game. Configure it once at startup and log through the static `Log`:

```csharp
var paths = new KhaozEngine.App.AppDataPaths("MyGame");   // AppDataPaths lives in KhaozEngine.App
var options = new LoggerOptions { MinimumLevel = LogLevel.Info, DefaultCategory = "Boot" };
options.Sinks.Add(new FileSink(new FileSinkOptions
{
    Path = paths.LogFilePath,
    PreviousPath = paths.PreviousLogFilePath,
}));
options.Sinks.Add(new ConsoleSink());
Log.Configure(options);
CrashHandler.Install();
```

Rules for consumers:

- Configure `Log` once per process (desktop `Program`, Android `MainActivity`, iOS `Program`). Call `Log.Shutdown()` on exit.
- Log via `Log.For<T>()` (category = type name) or `Log.Info/Warn/Error/...`. Pass an exception as the optional second argument.
- The game owns its paths: resolve them with `new KhaozEngine.App.AppDataPaths("<AppName>")` (`LogFilePath` / `PreviousLogFilePath` / `GetFilePath(...)`) and pass them into `FileSinkOptions`. The engine logging core is path-agnostic.
- Add a game-specific target (in-game console overlay, crash uploader) by implementing `ILogSink` and `Log.Manager.AddSink(...)`. Do not fork the engine logger.
- Logging never throws and never blocks the game loop (async writer thread; `Flush`/`Shutdown` drain it; `CrashHandler` flushes on a crash).
- `MinimumLevel` is runtime-settable for an in-game verbosity toggle.

---

## Other packages (brief)

The sections above cover the core flow. The rest of the 16-package set, one line each:

- **`KhaozEngine.App`**: app identity / data paths: `AppDataPaths`, `BuildMetadata`, `ServiceLocator`, `IAppDataEnvironment`. Pure BCL, no MonoGame.
- **`KhaozEngine.Persistence`**: crash-safe saves: `AtomicJsonWriter`, `PersistenceQueue` (coalesced async writes), `SettingsManager<T>` + `FileSettingsStorage`, `SaveEncoder` (Base64 + HMAC).
- **`KhaozEngine.Content`**: config loading + JSON-schema validation: `ConfigLoader` (disk-then-embedded), `JsonSchemaValidator`, build-time schema enforcement via the bundled validator.
- **`KhaozEngine.Serialization`**: shared `System.Text.Json` baselines: `JsonDefaults.TolerantRead` / `IndentedWrite` / `IncludeFields`. Consumed by Content/Persistence/Ecs. Pure BCL.
- **`KhaozEngine.Localization`**: `LocalizationManager` (culture + string lookup).
- **`KhaozEngine.Audio`**: `AudioSystem` music playback (one track at a time, `PlayMode`, now-playing events; macOS AVAudioPlayer backend). Music-only; SFX stays game-side.
- **`KhaozEngine.Effects`**: pooled rect `ParticleSystem` + `Spark`/`Ember` presets. Depends on Graphics (for `PrimitiveRenderer`).
- **`KhaozEngine.Sprites`**: 2D sprite + directional animation: `SpriteSheet`, `SpriteAnimationPlayer`, `DirectionalAnimatedSprite`, `Direction8`, `SpriteRegistry`, `PixelLabSpriteLoader`. Takes a raw `float`/`GameTime` delta.
- **`KhaozEngine.Time`**: `GameClock` (pause / `TimeScale`) + `TimeSkip`. Pulled in transitively by `Screens`; optional to use directly.

## Versioning & change process

- SemVer, one shared version across all packages (`Directory.Build.props`).
- Local file-feed for inner-loop dev; GitHub Packages on `v*` tags.
- To change the library: edit, add a headless test in `KhaozEngine.Tests`, `dotnet pack -o ./local-feed`, consume locally; when stable, bump the version + tag for a published release. Each game adopts on its own schedule by bumping its pinned version.
