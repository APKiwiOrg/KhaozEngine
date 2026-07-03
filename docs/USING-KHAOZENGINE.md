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
                                  Pointer.Update(Input, Viewport) → OnResume? → OnUpdate(dt) →
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
6. **`System.Numerics` only** - `Vector2/3/4`, `Matrix4x4`. No XNA / MonoGame types anywhere. (An RGBA color
   is `KhaozEngine.Primitives.Color`, not a bare `Vector4`. GPU-layout structs still use `Vector4`.)
7. **Don't fork the packages.** Need an API that isn't there? Add it to KhaozEngine, ship a headless test, bump
   the version, and consume the new version. Pinned versions are how games stay green during each other's
   migrations.

---

## Game head build settings (CETCompat)

Referencing any KhaozEngine umbrella (`Game2D`/`Game3D`/`Server`, or `Foundation` directly) makes your game
head inherit two build-property defaults from `KhaozEngine.Foundation`:

```xml
<CETCompat>false</CETCompat>                            <!-- inherited; you don't write this -->
<IncludeNativeLibrariesForSelfExtract>false</IncludeNativeLibrariesForSelfExtract>  <!-- inherited -->
```

.NET 9+ marks the x64 apphost CET / shadow-stack compatible by default. On Windows 10 builds with only partial
CET support (e.g. 20H2) that hard-aborts at boot: *"Your Windows doesn't fully support CET."* `CETCompat` is an
apphost (game-head) MSBuild property, and the engine ships libraries, not an apphost, so it cannot be set from
engine code; Foundation ships it as a `buildTransitive` props default instead, which every head inherits whether
Foundation is a direct reference or pulled in through `Game2D`/`Game3D`. A `normal`-importance build message
announces it (visible at `-v normal` and in IDE build output; the default minimal `dotnet build` stays quiet).

**This is the standard for all KhaozEngine games: leave it inherited.** CET is a hardware ROP mitigation over the
small native surface; KhaozEngine games are overwhelmingly managed (memory-safe), and DEP/ASLR plus the signed
auto-updater remain, so disabling it buys broad old-Windows compatibility for a narrow, reversible tradeoff. To
opt back in for a specific head, set `<CETCompat>true</CETCompat>` in that head's `Directory.Build.props` or
`.csproj` (your value wins, and the inherited default plus its build message step aside).

### Publishing (self-contained, single-file)

Publish each game **self-contained** for its RID (`dotnet publish -c Release -r <rid> --self-contained`): the
game ships its own pinned .NET runtime plus the native libs (GLFW, Veldrid's backend, OpenAL) so a runtime or
native-lib CVE is patched by re-publishing, not by waiting on an engine package bump. See
[SECURITY-BASELINE.md](SECURITY-BASELINE.md).

If you also single-file publish (`PublishSingleFile=true`), the native libs **must stay loose** next to the
exe. KhaozEngine reaches GLFW/Veldrid/OpenAL through native libraries the runtime locates by probing the apphost
directory. .NET's default for single-file is `IncludeNativeLibrariesForSelfExtract=true`, which packs them into
the self-extracting exe where Silk.NET's loader can't find them, so the game dies at boot with *"Couldn't find a
suitable window platform (GlfwPlatform - not applicable)"*. Foundation therefore defaults
`IncludeNativeLibrariesForSelfExtract=false` (inherited, overridable the same way as `CETCompat`); leave it
inherited. The default is a no-op unless you set `PublishSingleFile=true`.

**New game head checklist:** `CETCompat` and `IncludeNativeLibrariesForSelfExtract` are the engine-imposed
build-property defaults. Pin your umbrella package version, publish self-contained for your RID, and leave both
defaults inherited unless you have a specific reason to re-enable either.

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
`ResumeGapThresholdSeconds` (see the resume hook below), and optional `WindowFactory` / `ViewportFactory` (e.g.
`AppWindow.Scaled` for a display-fitted window, or an `AdaptiveViewport` for responsive layout). Use
`GameAppOptions.For(title, w, h)` for the common case.

**Runtime window/taskbar icon.** Set `WindowIconPath` to a PNG (the simple case) or `WindowIcons` to an explicit
list of decoded `ImageRgba` (16/32/48 px so GLFW picks per DPI; `WindowIcons` wins over `WindowIconPath`). `GameApp`
applies it during construction. On Windows and Linux/X11 this sets the title-bar + taskbar icon at runtime. The
Windows `.exe` icon shown when the app is not running is a separate per-game `<ApplicationIcon>` in the desktop
csproj, independent of this API. Under the hood `GameApp` decodes the PNG via `Render2D.ImageRgba` and hands
already-decoded `WindowIcon`s to `AppWindow.SetIcon(...)`, which a non-`GameApp` host can also call directly; the
`KhaozEngine.Windowing` package itself stays decode-free (no Render2D dependency).

**macOS Dock icon.** GLFW cannot set the Cocoa Dock icon, so `SetIcon` is a no-op on macOS, and an app launched via
`dotnet run` has no `.app` bundle `.icns` - so without help it shows the generic document icon in the Dock and
Cmd-Tab. `GameApp` fixes this automatically: when `WindowIconPath` is set, on macOS it also feeds that PNG to
`AppWindow.SetMacDockIcon(pngBytes)`, which sets the Dock icon at runtime via
`Platform.ApplicationIcon.TrySetMacDockIcon` (`NSApplication.setApplicationIconImage:`). A `WindowIcons`-only config
(no PNG path) leaves the Dock icon untouched, since `NSImage` decodes from the PNG. A non-`GameApp` host can call
`AppWindow.SetMacDockIcon(...)` directly. It returns `false` (never throws) off macOS or on empty input. A packaged
`.app` bundle with its own `.icns` still owns the Dock icon the normal way; this is for the unbundled dev/run case.

`GameApp` seams: `OnLoad()`, `OnUpdate(float dt)`, `OnRenderWorld(Frame)` (the 3D pass; empty in 2D),
`OnDraw2D(SpriteBatch)`, `OnResize(int, int)`, `OnResume(TimeSpan)` (see below), `OnDispose()`. Properties you
read: `Window`, `Clock`, `Viewport`, `Pointer`, `Input` (the frame's `InputState`), `Surface2D`, `Batch`,
`FrameWidth`/`FrameHeight`/`Dt`, `ClearColor`. Call `Quit()` to exit.

**Resume after OS sleep/suspend (`OnResume`).** The frame `dt` is clamped to 0.1s (a spiral-of-death guard) and
comes from a `Stopwatch`-style timer that does not reliably advance across an OS sleep/S3/hibernate, so a game
that leaves the process running while the machine suspends gets no signal that hours of real time just passed.
`GameClock` also samples a UTC wall clock each frame and reports `RealWallGapSeconds` (the wall gap to the
previous frame, clamped to `>= 0`; `LastRealTimestamp` is the sample). When that gap exceeds
`GameAppOptions.ResumeGapThresholdSeconds` (default 30s; 0 or negative disables), `GameApp` calls
`OnResume(TimeSpan wallGap)` once, before `OnUpdate` on that frame and never on the first frame. Override it to run
offline/AFK catch-up (e.g. a one-shot `TimeSkip.Advance`), re-sync timers, or pause. This is a separate signal
from the sim delta: the 0.1s clamp and `Dt` are unchanged.

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
public sealed class InputState   // immutable per-frame snapshot; InputState.Empty is a blank one
{
    // read-only properties; the constructor takes them in this order:
    IReadOnlySet<Key> KeysDown, KeysPressed, KeysReleased;
    IReadOnlySet<MouseButton> MouseDown, MousePressed;
    Vector2 MousePosition, MouseDelta;  float ScrollDelta;  int Width, Height;
    IReadOnlyList<GamepadState> Gamepads, Touches;  // ctor args optional, default empty
    bool WindowFocused;                             // optional trailing ctor arg, default true (Empty = false)
    IReadOnlySet<Key> KeysRepeated;                 // optional trailing ctor arg, default empty
}

bool IsDown(Key) / WasPressed(Key) / WasReleased(Key);
bool WasRepeated(Key) / WasTyped(Key);   // OS auto-repeat tick / press-or-repeat
bool IsDown(MouseButton) / WasPressed(MouseButton);
GamepadState Gamepad(int i = 0);  GamepadState PrimaryGamepad { get; }
```

`Key` and `MouseButton` are engine enums; `GamepadState` exposes `ButtonsDown/Pressed/Released`,
`LeftStick`/`RightStick` (+ `LeftStickDeadzoned(...)`), triggers, and `IsDown/WasPressed/WasReleased`.

`WasRepeated(Key)` is `true` on a frame the key fired an OS auto-repeat tick (held past the user's OS repeat delay,
then recurring at the OS repeat rate); `AppWindow` fills `KeysRepeated` from GLFW's `REPEAT` key action. `WasPressed`
stays the press edge only (auto-repeat excluded), so existing callers are unchanged; `WasTyped(Key)` is the union
(`WasPressed || WasRepeated`) - the "a character was typed this frame" signal hold-to-repeat text entry wants.
`TextEntry`/`TextInput` use it, so a held Backspace or character key repeats with no game code.

`WindowFocused` is `true` while the window owning this snapshot is the frontmost (OS-focused) window. The render
loop keeps running and the cursor stays live while the window is in the background, so gate input that should stop
when unfocused on it (e.g. `if (Input.WindowFocused) { /* world clicks, scroll-zoom, hotkeys */ }`). The Gui hover
and capture gates already honour it for free (below), so the common "don't trigger UI hover while in the
background" case needs no game code. Defaults to `true` (windows open focused); `InputState.Empty` is `false`.

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
- `IsHoveringIn(Rect)` - inside and not pressed (desktop hover). Also false while the window is unfocused (a
  background window reports no hover; `Pointer.WindowFocused` carries the bit). The press-origin / tap queries
  above are deliberately NOT focus-gated, so the click-through invariant is unchanged.
- `IsPointerIn(Rect)`, `IsReleasedOutside(Rect)`, `IsDraggingIn(Rect)`, `GetDragDelta(Rect)`.

**Gesture consumption (cross-layer click-through):** `IsTapIn` is purely geometric, so a release that swaps the
layout mid-gesture (push an overlay, open a popup) would let a widget that appears under the press-origin act on a
gesture that began before it existed. Call `ConsumeGesture()` to spend the current press/release: `IsTapIn`/
`IsTapFromTo` then report false until the next fresh press (`IsConsumed` exposes the flag; drag/hover/press
queries are left intact, so a slider grab survives). `SceneManager` already calls this on every push/pop, so a
scene transition can't double-fire a tap on a freshly-drawn widget; call it yourself if you change the
interactive layout mid-gesture without a scene transition.

**Region blocking (the other half of click-through):** `BlockInputRegion(Rect)` from the higher overlay each
frame; `IsInputBlocked(Vector2)` from the layer beneath before acting. Cleared at the start of every `Update`.

**Scroll:** `ScrollDelta`, `IsMouseWheelScrolledUp/Down`.

**Keyboard / gamepad / menu navigation:** `IsKeyDown`, `IsKeyJustPressed`, `IsNewKeyPress(key, PlayerIndex?, out
who)`, `IsNewButtonPress(button, PlayerIndex?, out who)`, `IsMenuUp/Down(PlayerIndex?)`, `IsMenuSelect/Cancel
(PlayerIndex?, out who)`, `IsSelectNext/Previous(PlayerIndex?)`, `IsPauseGame(PlayerIndex?, Rect?)`. Pass `null`
for the player to accept "any connected controller".

**Feeding the text-entry core:** `State` returns this frame's `InputState` snapshot (the value last passed to
`Update`), so a retained, custom-rendered widget that holds an `InputManager` can drive the headless
`TextEntry.Apply(text, input, maxLength, filter, allowPaste)` editing core - the full printable map, hold-to-repeat,
and Ctrl/Cmd+V clipboard paste - without reaching for the raw window input. There is an `InputManager` overload of
`Apply` that reads `State` for you, so the call is one line; pass `allowPaste: false` to suppress paste. Paste
appends `Clipboard.TryGetClipboardText()` through the same `filter` + `maxLength` path as typed chars (so a digits
filter strips letters out of pasted text too), firing on the V press edge only.

### Rect, viewport, clock

- `Rect(X, Y, Width, Height)` is the engine's rectangle (`Right`/`Bottom`/`Contains(Vector2)`).
- `IDesignViewport` (impls: `DesignViewport` letterbox/fill/stretch, `AdaptiveViewport` responsive) maps between
  design space and screen pixels (`DesignToScreen`/`ScreenToDesign`, `GetClipProjection`). `GameApp` owns one
  and passes it into `Pointer.Update`, so design-space coordinates and hit-tests line up.
- `GameClock`: `TimeScale`, `Pause()`/`Resume()`, `RealDeltaSeconds`/`ScaledDeltaSeconds`,
  `RealWallGapSeconds`/`LastRealTimestamp` (the suspend-robust wall-clock gap that drives `GameApp.OnResume`),
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
`PointerCaptured` gate lets a game suppress world clicks when the pointer is over UI. While the window is
unfocused, hover (`IsHovering`/`HoverEntered`) and both capture gates (`PointerCaptured`/`HoverCaptured`) report
false automatically (via `Pointer.WindowFocused`), so a background window fires no UI hover SFX or highlights
without any game code.

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
SpriteFont font = Surface2D.LoadDefaultFont(32f);                    // engine's embedded font (no system font, no path)

batch.Begin(Viewport, SamplerMode.Point);        // design-viewport space, crisp pixels; or Begin(camera) / Begin()
batch.Draw(logo, new Vector2(100, 100), Color.White);
batch.DrawString(font, "Hello", new Vector2(100, 60), Color.White);
batch.End();
```

### Fonts: no system font, no hard-coded path

The engine never depends on a system font and you should never hard-code one (e.g. the macOS-only
`/System/Library/Fonts/Supplemental/Arial.ttf`, which throws `DirectoryNotFoundException` on Windows/Linux).
`Render2DSurface`/`Render2DContext` give you three ways to bake a `SpriteFont`, none of which need a system path:

```csharp
// 1. The engine's embedded default face (Roboto, Apache-2.0) - shipped transitively, nothing to bundle.
SpriteFont ui = Surface2D.LoadDefaultFont(32f);

// 2. Raw TTF bytes you loaded yourself (your own bundled asset, a pak, a download...).
byte[] ttf = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "assets", "fonts", "title.ttf"));
SpriteFont title = Surface2D.LoadFont(ttf, 48f);

// 3. A FontManager key (the SFX-style register/resolve shape). The reserved "default" key is pre-registered
//    to the embedded face; register your own keys from the content dir or from bytes, then bake by key.
var fonts = new FontManager();                       // content dir defaults to {BaseDirectory}/assets/fonts
fonts.RegisterFont("title");                         // probes assets/fonts/title.ttf (then .otf); key == path, no ext
fonts.RegisterFont(FontManager.DefaultKey, ttf);     // override the default with your own face if you like
SpriteFont byKey = Surface2D.LoadFont(fonts, "title", 48f);
```

`LoadFont(string path, ...)` still exists for an explicit absolute path, but games should prefer the byte /
default / `FontManager` overloads so nothing breaks across platforms. `oversample > 1` (2-3) keeps text crisp
when a design-viewport upscales; layout/metrics are reported at the logical size regardless. The same overloads
exist on `Render2DContext` (the `Render2DSnapshot` headless callback).

- `Begin` overloads: `Begin(Camera2D, SamplerMode)` (world space), `Begin(IDesignViewport, SamplerMode)`
  (design space), `Begin(SamplerMode)` (raw screen). `SamplerMode` is `Linear` (default) or `Point`.
- Model transform: each `Begin` also has a `Matrix4x4 transform` overload (`Begin(Matrix4x4)`,
  `Begin(Camera2D, Matrix4x4)`, `Begin(IDesignViewport, Matrix4x4)`) applied to every draw before projection, so
  a composed group (panel + icon + text) tilts/scales/translates as one. `DrawString` has no rotation of its
  own, so the model transform is how text tilts with its card. A `SetScissor` during a transformed pass clips by
  the un-rotated (design) bounds, not the rotated quad (the GPU scissor is axis-aligned).
- `Camera2D`: `Position`/`Zoom`/`Rotation`, `WorldToScreen`/`ScreenToWorld`, `CenterOn`, `PanByScreenDelta`,
  `Focus(rect, ...)`, `ClampPosition(...)`. The camera-feel layer (follow, look-ahead, blends, room cameras,
  parallax) lives alongside it in Render2D; screen shake is in `KhaozEngine.Particles`.
- Scissor clipping: `SetScissor(Rect)` / `ClearScissor()` (composes with the design viewport).
- `ImageRgba` (CPU, no GPU): `ImageRgba.Load(path)` / `Decode(bytes)` / `Surface2D.LoadImageRgba(path)` give a
  tightly-packed RGBA8 image with `AlphaAt` / `IsOpaqueAt(threshold)` for opaque-pixel collision masks. Pass
  `img.Pixels` to `Surface2D.CreateTexture` to also draw it without re-decoding.
- Offscreen capture (headless / tooling): `Surface2D.CaptureToTexture(...)` and `CaptureToRgba(...)`.
- Blend mode: `batch.BlendMode = BlendMode.Additive` switches subsequent draws to additive compositing (glows,
  sparks, beams); it can change mid-batch (per quad) and painter's order is preserved across modes. Each `Begin`
  resets it to `BlendMode.Alpha` (the default, source-over).

### 2D VFX (`KhaozEngine.Render2D.Vfx`)

Glowing sprites, animated energy beams, and rich pooled particles - all additive, no shipped asset. The
convenience entry point is `VfxRenderer`, which bakes the textures it needs at construction:

```csharp
using var vfx = new VfxRenderer(Surface2D);          // bakes a glow, a ring, and a 1x1 white pixel

// Pooled, zero-alloc, deterministic (seeded XorRng) screen-space particles.
var sparks = new Particle2DSystem(capacity: 256, seed: 1);
var cfg = new Particle2DEmitterConfig {
    MinLife = 0.2f, MaxLife = 0.5f, MinSpeed = 60f, MaxSpeed = 140f,
    Emission = Particle2DEmission.Radial, StartSize = 4f, EndSize = 0f,
    Acceleration = new Vector2(0, 300f),             // gravity
    Drag = 1.5f, RotationJitter = 3.14f, MinAngularVelocity = -6f, MaxAngularVelocity = 6f,
    StartColor = new Color(1f, 0.9f, 0.5f, 1f), EndColor = new Color(1f, 0.3f, 0f, 0f),
    Blend = BlendMode.Additive,
};
sparks.Emit(cfg, hitPoint, count: 24);               // burst; call each frame for a continuous stream
// ... each frame:
sparks.Update(dt);
batch.Begin();
sparks.Draw(batch, vfx.GlowTexture);                 // or vfx.WhitePixel for solid squares
vfx.DrawGlow(batch, hitPoint, radius: 20f, new Color(1f, 0.8f, 0.4f, 1f));   // halo / impact flare
vfx.DrawBeam(batch, muzzle, target, BeamParams.Default with { Caps = BeamCap.Round }, timeSeconds);  // capsule ends
vfx.DrawAttentionBeacon(batch, pickup, AttentionBeaconParams.Default, realTimeSeconds);  // sonar rings + glints
batch.End();
```

- `Particle2DSystem`: ring-buffer pool; per particle = velocity, acceleration (gravity), drag, sway, rotation +
  angular velocity, size/colour lerp over life, per-particle blend. `Emit(in cfg, origin, count)` (+ tint
  overload), `Update(dt)`, `Clear()`, `ActiveParticles()` (snapshots for tests), `Draw(batch, texture)` (per-
  particle blend) or `Draw(batch, texture, BlendMode)` (forced). Deterministic per seed.
- `Particle2DEmitterConfig` is an immutable `record struct` - keep presets in content and derive with `with`.
- `EnergyBeam.Draw(batch, white, glow, a, b, in BeamParams, timeSeconds)`: additive A->B beam (glow band + core,
  flowing dashes, pulse, jitter, endpoint flares); time-driven and stateless. `VfxRenderer.DrawBeam` wraps it
  with the owned textures. `BeamParams.Caps = BeamCap.Round` rounds both ends into a capsule: a soft disc cap of
  radius half each band's pulse-adjusted width at every endpoint (glow cap under, core cap over), independent of
  `FlareRadius` and sampled from the `glow` texture (square ends if `glow` is null). Default `BeamCap.None` keeps
  the original square ends.
- `AttentionBeacon.Draw(batch, ring, glow, center, in AttentionBeaconParams, timeSeconds)`: an additive "look at
  me" pulse (pickups, quest markers, objectives), expanding sonar-ping rings plus a configurable number of
  twinkling glints; time-driven and stateless like `EnergyBeam`, so feed an unscaled real-time accumulator and it
  animates regardless of game time-scale. `VfxRenderer.DrawAttentionBeacon(batch, center, in p, timeSeconds)`
  wraps it with the owned ring (rings) and glow (glints) textures. Tunables on `AttentionBeaconParams`:
  `RingCount`/`RingPeriod`/`InnerRadius`/`MaxRadius`/`RingThickness` (relative band thickness, 1 = texture-native),
  `GlintCount`/`GlintRadius`/`GlintSize`/`TwinkleRate`/`GlintStyle` (`Disc` or `Star`), plus `Color` and
  `Intensity`. `RingCount = 0` and `GlintCount = 0` draw nothing; a null `ring`/`glow` skips that sub-effect.
- `VfxTextures`: `BakeGlowPixels`/`BakeRingPixels` (pure RGBA8, headless) and `BakeGlow`/`BakeRing`/`White`
  (upload to a `Render2DSurface` / `Render2DContext`).
- Screen shake is **not** here - use `KhaozEngine.Particles.ScreenShake` (trauma-based, camera-independent: `Add` /
  `Update(dt)` / `Offset` / `Angle`); compose `Offset`/`Angle` onto your own camera.

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
scene.AddLight(muzzlePos, new Color(1f, 0.6f, 0.2f, 1f), radius: 6f, intensity: 3f); // point light
scene.DrawBillboard(pos, size, color, BillboardBlend.Additive);
scene.DebugCircle(center, up, radius, color);                        // immediate-mode debug overlay
```

- `Scene3D`: `LoadMesh`/`LoadTexture`/`UnloadMesh`/`UnloadTexture`, `Begin()`, `Draw(handle, transform[, tint[, material]])`,
  billboards, and a debug-draw overlay (`DebugLine/Ray/Box/Grid/Axes/Circle`). `LoadTexture` builds and generates a
  full mip chain for each texture (from 9.2.0), so model/prop surfaces stay smooth at distance instead of aliasing
  into "pixely" sparkle as the camera moves. `Post` is the
  `PixelPostProcess` (pixelation / quantize / dither / cel bands / palette for the chunky retro look; the smooth
  look is the default).
- Rigid glTF honours node world transforms: `GltfLoader.Load` / `LoadWithMaterial` walk the scene
  graph and bake each mesh node's world matrix into the loaded vertices (POSITION by the world matrix, NORMAL +
  TANGENT.xyz by the normal matrix, correct under non-uniform scale), matching the already-node-aware skinned
  path. So a Blender export or a multi-piece / instanced kit that positions geometry via nodes loads correctly
  with no manual baking; a mesh instanced by several nodes loads one placed copy per node. The kit-ingest
  `transform_apply` step (Blender) is therefore no longer required for placement, only harmless if kept; an
  identity-node or pre-baked asset is byte-identical to before. (`PropLoader.LoadProp` additionally renormalizes
  to the manifest height, so props were already placement-robust; this matters most for `GltfLoader.Load` used
  directly.)
- PBR-lite materials: the rigid lit model pass takes an optional tangent-space NORMAL map and a
  ROUGHNESS map alongside the albedo. Load each map with `LoadTexture`, then bind them with `Scene3D.SurfaceMaps`:
  `scene.LoadMesh(mesh, new Scene3D.SurfaceMaps(albedo, normal, roughness))` - any handle may be `default` to fall
  back to its 1x1 default (white albedo / flat normal / zero roughness). Normal mapping needs per-vertex tangents,
  so it applies to glTF meshes (`GltfLoader.Load` reads the `TANGENT` accessor, or computes one from the UVs) and
  `MeshAssembler` output; `MeshPrimitives` carry no tangent, so a normal map is inert on a primitive (it stays lit
  by its geometric normal). Roughness uses the glTF metallic-roughness `.g` convention (0 = smooth/glossy,
  1 = matte; metallic is ignored) and modulates the Blinn-Phong specular. Meshes with no maps render exactly as
  before. Skinned meshes take normal/roughness too: bind via
  `scene.LoadSkinnedMesh(mesh, new Scene3D.SurfaceMaps(albedo, normal, roughness))`; `GltfLoader.LoadSkinned` and
  `SkinnedMeshBuilder.BuildTube` compute tangents, and the tangent rides the per-frame skin deform so the TBN
  tracks the pose. The pure `SurfaceShading` helper mirrors the shader math (handy for headless tests / tooling).
- Auto-read glTF material textures (opt-in): instead of exporting PNGs and binding them by hand, let
  the loader read the material's textures straight off the glb. `GltfLoader.LoadWithMaterial(path)` returns
  `(GltfMesh Mesh, GltfMaterialMaps Maps)` and `LoadSkinnedWithMaterial(path)` the skinned equivalent; the mesh is
  identical to `Load`/`LoadSkinned`, and `GltfMaterialMaps` carries the decoded baseColor / normal /
  metallicRoughness textures as raw RGBA8 `DecodedImage?` (the loader has no GPU device, so it decodes only -
  metallicRoughness passes through unchanged since the shader reads roughness from `.g`, normal stays
  tangent-space RGB). Upload them with `scene.LoadSurfaceMaps(maps)` → `SurfaceMaps`, or skip a step with the
  one-call overloads `scene.LoadMesh(mesh, maps)` / `scene.LoadSkinnedMesh(mesh, maps)` (both taking a
  `GltfMaterialMaps`). Embedded GLB images and external image files are both read; a material with no textures - or
  a missing/undecodable image - yields an all-absent bundle (`maps.IsEmpty`) and falls back to the renderer
  defaults, never a throw. The default explicit-`SurfaceMaps` path is unchanged, so this is purely a convenience
  on top.
- Smooth / realistic look: a realistic material is still quantized + outlined by the FULLSCREEN post passes
  (palette, edge outline, cel bands), so call `scene.Post.UseSmoothPreset()` to turn those off
  (cel bands / quantize / dither / outline / starfield / pixelated) in one call for a smooth look. Lighting and
  colours are left untouched; flip individual `Post` toggles back on as needed.
- Translucent filled overlay (alpha-blended flat shapes, drawn under the debug lines): `DebugFilledQuad`
  (ground tiles / rects), `DebugFilledCircle` (discs / ranges), and `DebugFilledFan(center, rim, color, closed)`
  for an arbitrary, already-ordered boundary polygon - fan an outline out from a centre point to
  fill a star-shaped area (e.g. a turret's line-of-sight footprint) that a quad or disc can't express. Wind the
  rim CCW about the desired facing normal (`Vector3.UnitY` for a ground fan); `closed: true` (the default) seals
  the loop with a wrap triangle, `false` leaves an open arc.
- Dynamic point lights: `scene.AddLight(worldPos, color, radius, intensity)` queues a per-frame
  effect light (muzzle flashes, explosions, thrusters) that adds diffuse + cheap specular to the lit mesh pass,
  on top of the global key+fill+ambient term, with a smooth falloff to zero at `radius`. Cleared each `Begin()`
  like the draw queue. Only the first `Scene3D.MaxPointLights` (16) queued in a frame are uploaded - the host
  picks the N nearest to the action so a dense scene stays within the GPU budget. Zero lights renders
  byte-identical to the key+fill path. Presentation only: never feed a light back into simulation/collision.
- 3D beams: `scene.DrawBeam(a, b, width, color, BeamStyle?)` queues a camera-facing, additive,
  depth-interleaved glowing beam between two world points (lasers, thrusters, tethers): a bright core in a soft
  halo. It draws INTO the model pass with the depth test on (no write), like the textured billboard, so geometry
  occludes it. `color` tints the core; `BeamStyle` (default `BeamStyle.Default`) splits core/glow colour and adds
  `CoreFraction`, `GlowSoftness`, end `Taper`, and time-driven `PulseSpeed`/`PulseAmount` + `ScrollSpeed`.
  Animation reads `scene.EffectTimeSeconds`, a per-frame clock you set in your draw callback (it is NOT cleared by
  `Begin`); leave it at 0 for a static beam. A degenerate beam (`a` ~= `b` or `width <= 0`) is a no-op.

  ```csharp
  scene.EffectTimeSeconds = totalSeconds;                 // once per frame (host clock)
  var style = BeamStyle.Default with { Taper = 0.15f, PulseSpeed = 8f, PulseAmount = 0.2f, ScrollSpeed = 1.5f };
  scene.DrawBeam(muzzle, hit, 0.4f, new Color(1f, 0.3f, 0.9f, 1f), style);
  ```

  Recommended combo for an impactful beam: pair `DrawBeam` with an `AddLight` at each endpoint (so the beam lights
  nearby geometry) and a `ParticleSystem` spark burst at the impact point:

  ```csharp
  scene.DrawBeam(muzzle, hit, 0.4f, beamColor, style);
  scene.AddLight(muzzle, beamColor, radius: 4f, intensity: 2f);
  scene.AddLight(hit,    beamColor, radius: 5f, intensity: 3f);   // brighter flash at the impact
  // sparks at the impact: loop your particle system's Active span and DrawBillboard each (Additive)
  ```

- Transparent compositing: set `Post.TransparentBackground = true` (default on for `Render3DPreview`) to emit the
  background as alpha 0 so a captured `Texture2D` overlays a 2D scene; the stylized post chain preserves the
  per-pixel alpha (geometry opaque, cleared background clear). Leave `Starfield` off when transparent.
- Internal render-target sizing: `Post.RenderScale`. The default `FixedInternal` renders into a
  fixed `Post.RenderWidth` x `RenderHeight` target (1600x900) and blit-scales it to the window - the retro path
  (small fixed target + `Pixelated`), but on a window bigger than that target the smooth blit UPscales and
  softens. Set `Post.RenderScale = RenderScale.MatchViewport` to size the target to the actual framebuffer each
  frame instead (1:1, no upscale blur on large / Retina windows; capped at `Post.MaxRenderWidth` x
  `MaxRenderHeight`, default 3840x2160, aspect preserved). Leave it `FixedInternal` for the chunky/`Pixelated`
  look.
- Supersampling (SSAA): `Post.Supersample` (default `1`, MatchViewport only). Renders the internal 3D target at
  framebuffer x this factor per axis and downsamples in the final blit, so it anti-aliases BOTH geometry edges
  and shaded texture interiors (unlike MSAA). `2` = 2x per axis (4x pixels), the same effective AA a 2x/Retina
  display gives for free - use it to remove the motion "shimmer/vibration" high-frequency terrain or thin foliage
  throws on a standard-DPI display. Still clamped to `MaxRenderWidth`/`MaxRenderHeight`. The downscale is a correct
  mip-filtered (trilinear) box at ANY factor - the internal target carries a mip chain the final blit samples at
  LOD ~= log2(factor) - so `3` and `4` anti-alias properly, not just `2`. Cost scales ~factor^2 in fragment shading
  (`3` = 9x the pixels), so keep it off by default and measure on the target GPU before going above `2`.
- Anti-aliasing options (the AA dropdown): `Post.Quality.AntiAliasing` picks one technique -
  `AntiAliasing.Off` (default), `.Fxaa` (cheap one-pass edge smoother), `.Msaa(2|4|8)` (hardware multisample,
  geometry edges only), or `.Ssaa(factor)` (supersample the whole image, the strongest, also kills shaded-interior
  shimmer). Build a menu from `AppWindow.Capabilities.MaxMsaaSampleCount` and validate a choice with
  `aa.ResolveFor(caps)` (clamps an unsupported MSAA level down, or falls back to FXAA; never throws). `Ssaa(f)` is
  the high-level equivalent of `RenderScale.MatchViewport` + `Supersample = f`; the raw fields remain and, with AA
  `Off`, still govern (so existing scenes are unchanged). The `Pixelated` retro path forces AA off. Costs: SSAA is
  ~factor^2 fragment shading, MSAA adds a per-frame resolve, FXAA one pass - keep AA off by default and measure.
  `Post.Quality` (a `RenderQuality`) is where future quality knobs (anisotropy, shadow/texture quality, TAA) will
  live, so a game's options menu binds to it.
- Edge outline: `Post.Outline` (on by default) draws a depth/normal toon outline. `OutlineColor`,
  `OutlineDepthThreshold` (depth-discontinuity sensitivity), and `OutlineNormalThreshold` (interior-crease
  sensitivity from the geometric normal) tune it. The outline is perspective-correct: under a
  perspective camera (`FollowCamera3D`) the depth test is linearized to view-space distance and distance-relative,
  so a given threshold is stable on zoom and distance instead of popping (the orthographic `IsoCamera3D` path is
  unchanged). The normal term carries silhouettes + creases; keep the depth threshold conservative on near-grazing
  ground planes (a grazing plane has genuinely high per-pixel depth change, so a low depth threshold lights it up).
  `Post.OutlineDistanceFade` (default off, perspective only) fades the outline out between `OutlineFadeStart` and
  `OutlineFadeEnd` view-space units so far terrain/foliage stops aliasing into mush.
- `IsoCamera3D`: `Azimuth`/`Elevation`/`Target`/`OrthoSize`/`Zoom`, `Frame(target, azimuth, size)`,
  `ScreenToRay`, `ScreenToGround`, and the `View`/`Projection`/`ViewProjection` matrices.
- `IsoCameraController`: input-agnostic gestures driving an `IsoCamera3D` (pure `System.Numerics`, headless-testable;
  the game wires its own input policy - which button does what). Cursor-anchored `Zoom(wheelDelta, cursorPx, vw, vh)`
  and the grab-pan (`BeginPan`/`UpdatePan(cursorPx, vw, vh)`/`EndPan`, optional `PanMin`/`PanMax` target clamp). Orbit
  gesture: `BeginOrbit(cursorPx)` / `UpdateOrbit(cursorPx)` / `EndOrbit()` swings `Azimuth` by the
  horizontal drag (`OrbitYawSpeed` rad/px) and tilts `Elevation` by the vertical drag (`OrbitPitchSpeed` rad/px,
  dragging up raises elevation), clamped to `[MinElevation, MaxElevation]` (defaults ~15 deg .. ~88 deg, kept off both
  the ground plane and the degenerate top so the view never goes flat/under the board). Orbit keeps `Target` fixed, so
  the camera swings around the board centre for free. Wire each gesture to whatever button the game prefers.

---

## Skinned / deformable meshes (runtime bone control)

Render3D supports GPU bone-palette skinning for organic, code-driven deformation (tentacles,
limbs, cables, soft-body) without authored animation tracks. One skinned draw replaces many
rigid-segment draws.

```csharp
// Procedural: a tube weighted to a bone chain.
SkinnedGltfMesh tube = SkinnedMeshBuilder.BuildTube(radius: 0.4f, length: 5f,
    ringSegments: 12, radialSegments: 8, boneCount: 8, axis: Axis.Z);
SkinnedMeshHandle h = scene.LoadSkinnedMesh(tube, albedoTex);

// PBR-lite on a skinned mesh: bind normal + roughness alongside the albedo.
// SkinnedMeshHandle h = scene.LoadSkinnedMesh(tube, new Scene3D.SurfaceMaps(albedoTex, normalTex, roughTex));

// Or load an authored rig (reads JOINTS_0/WEIGHTS_0 + inverse-bind + TANGENT; embedded images ignored):
// SkinnedMeshHandle h = scene.LoadSkinnedMesh(GltfLoader.LoadSkinned("creature.glb"), albedoTex);

// Each frame: supply one joint world transform per bone (model space). Passing tube.RestPose
// gives no deformation. A chain of points can be turned into frames with PolylineFrames.Build.
scene.Begin();
scene.DrawSkinned(h, boneMatrices, model: Matrix4x4.Identity, tint: Color.White);
```

**Turn-key: `SkinnedLimb`.** Wiring `BuildTube` + the chain solver + `PolylineFrames`
+ `DrawSkinned` by hand every frame is the manual path above. `SkinnedLimb` bundles all of it into one
stateful component so a tentacle / cable / tail stands up in two calls (construct, then per-frame
`Update` + `Draw`), with reusable scratch buffers so the motion path allocates nothing per frame:

```csharp
// Construct once: builds the tube and uploads it (optionally with a texture / SurfaceMaps).
var limb = new SkinnedLimb(scene, radius: 0.4f, length: 5f, ringSegments: 12, radialSegments: 8,
    boneCount: 8, ChainConfig.Writhe, Axis.Z);            // + a TextureHandle / SurfaceMaps overload

// Each frame: writhe only, or writhe + FABRIK reach toward a target.
limb.Update(root, forward: Vector3.UnitZ, up: Vector3.UnitY, clockSeconds: t);
// limb.Update(root, forward, up, t, target: grabPoint, reachWeight: solver.SlamWeight);
scene.Begin();
limb.Draw(scene, model: Matrix4x4.Identity, tint: Color.White);   // + a Material overload
// limb.Config = ChainConfig.Calm;  // retune the writhe at runtime (mutable)
// limb.Dispose();                  // frees the tube's GPU buffers when the limb is retired
```

`limb.Bones` (`ReadOnlySpan<Matrix4x4>`) and `limb.Spine` expose the current pose for game reads
(copy if you keep it past the next `Update`). The whole solve->frames->bones step is headless-testable
with no GPU via `SkinnedLimb.CreateHeadless(boneCount, config, axis)` (a limb with no GPU mesh; its
`Draw` is a no-op). Reach for the manual `BuildTube` + `ProceduralChainSolver` + `PolylineFrames` calls
only when you need to deviate from this orchestration.

The bone matrices are joint **world** transforms (model space); the engine composes them with the
mesh's inverse-bind. Skinning rewrites position and normal only, so the lit colour path
(`albedo = vColor * vTint * texRgb`), tint, and texture semantics are unchanged.

**Bones are independent joints (no implicit hierarchy).** The engine does NOT chain bones
parent-to-child for you: each entry in `boneMatrices` is that bone's full world transform, applied
directly. To bend a tentacle/limb smoothly you supply already-composed world transforms that encode
the chain (each joint inheriting its ancestors' rotation), exactly what `PolylineFrames.Build`
produces from a point chain, or what a consumer's per-segment layout (e.g. an accumulated
base-to-tip transform walk) computes. Rotating a single bone "in place" does not bend the rest of
the mesh - that is by design, since the caller owns the kinematics.

**Limits.** One skinned mesh has at most 128 bones (the per-draw bone window); a mesh with more
throws on `DrawSkinned`. Many skinned meshes per frame are fine: each skinned `DrawSkinned` is its
own draw call (they are not GPU-instanced), so a creature with several tentacles costs one draw per
tentacle, still far below the dozens of rigid-segment draws it replaces.

**Determinism: presentation only.** Bone matrices and `DrawSkinned` must never feed simulation,
RNG, or netcode. Skinning is a render-time visual; drive bones from already-computed gameplay
state, not the reverse.

---

## Animated characters (glTF clip playback + locomotion blend)

The skinned path above poses a mesh from bone matrices you supply. To play *authored glTF animation
clips* (idle/walk/run/jump) and crossfade them off a character's movement, ingest the rig through the
skinned loader (which now also reads the joint hierarchy) and read its clips:

```csharp
// Skinned-ingest a rigged + animated glb (rig + animation channels preserved; NOT the flatten-prop path).
var (mesh, maps) = GltfLoader.LoadSkinnedWithMaterial("player.glb");
SkinnedMeshHandle handle = scene.LoadSkinnedMesh(mesh, maps);
Skeleton skeleton = mesh.Skeleton!;                       // the joint hierarchy LoadSkinned now attaches

// Read every clip and map the ones you want to locomotion states (clip names are whatever your asset uses).
var byName = new Dictionary<string, AnimationClip>();
foreach (AnimationClip c in GltfLoader.LoadAnimations("player.glb")) byName[c.Name] = c;
var clips = new Dictionary<LocomotionState, AnimationClip>
{
    [LocomotionState.Idle] = byName["Idle"],
    [LocomotionState.Walk] = byName["Walk"],
    [LocomotionState.Run]  = byName["Run"],
    [LocomotionState.Jump] = byName["Jump"],
    [LocomotionState.Fall] = byName["Fall"],
};

// AnimatedCharacter (KhaozEngine.Game / Game.Render3D) wraps the skeleton + clips + player + state machine.
var character = new AnimatedCharacter(skeleton, clips, new LocomotionThresholds(0.1f, 4.5f), crossfade: 0.15f);
```

Each frame, feed it the movement state your controller already computes, then draw with its pose
(the bone palette `DrawSkinned` consumes - it is joint-WORLD, the loader-attached skeleton composes it):

```csharp
// horizontalSpeed e.g. from the XZ position delta over dt; Grounded / VerticalVelocity from the controller.
character.Update(horizontalSpeed, controller.Grounded, controller.VerticalVelocity, dt);
scene.DrawSkinned(handle, character.Pose, model, Color.White);   // model places + faces + scales the character
```

`AnimatedCharacter` picks the clip via `LocomotionStateMachine.Evaluate` (speed picks idle/walk/run;
while airborne the air state wins - rising = `Jump`, otherwise `Fall`) and crossfades between clips
(per-joint TRS blend, composed once). A movement state with no clip in the map falls back to `Idle`
(then to the first clip), so a partial clip set never throws.

**Local AND remote players.** Drive one `AnimatedCharacter` per visible character: the local player from
its own movement, each remote player from its *replicated* position / `VerticalVelocity` / `Grounded`
(derive horizontal speed from the replicated position delta if no velocity is replicated). It is purely
client-cosmetic - the clip is chosen from already-known state, so there are **no netcode changes** and the
server stays authoritative on position only.

**Driving one per networked player** (`ReplicatedCharacterAnimators`). Rather than wiring a brain
per entity by hand, hand the set a factory (or a shared skeleton + clip map) and feed it one position sample
per visible entity each frame. It creates a brain on a new id, drops one whose id is gone (no leak on
disconnect), derives planar speed / vertical velocity / facing from the position displacement averaged over a
short window (the one signal every netcode surfaces for every entity), and returns draw-ready transforms +
bone palettes:

```csharp
// One brain per entity, off the shared (immutable) skeleton + clip map (applies the tuning's thresholds/crossfade).
var animators = new ReplicatedCharacterAnimators(skeleton, clips, CharacterAnimatorTuning.Default);
// ...or full control per brain: new ReplicatedCharacterAnimators(() => new AnimatedCharacter(skeleton, clips), tuning);

// Each frame: map the netcode's render states to engine-neutral samples (keeps Game.Render3D off NetWorld).
// Feed the EXACT grounded + vertical velocity for EVERY entity (EntityRenderState carries them for
// remotes too - local from prediction, remote from the replicated MovementState). Horizontal speed + facing are
// still derived from the position stream. Do NOT derive air state for remotes from position: a remote's vertical
// motion is mostly terrain-following, so the faster it moves over a slope the more a position delta reads "falling".
var samples = new List<CharacterSample>();
foreach (EntityRenderState e in client.Snapshot())
    samples.Add(new CharacterSample(e.Id.Value, e.Position, e.IsLocal, e.Grounded, e.VerticalVelocity));
animators.Update(samples, dt);

// Draw: World already places + faces + scales the avatar (Scale + FacingYawOffset live on the tuning).
foreach (CharacterPose p in animators.Live)
    scene.DrawSkinned(handle, p.Pose, p.World, p.IsLocal ? Color.White : Color.LightGray);
```

The set is render-free and headless-testable (owns no GPU handle, never calls `Scene3D`). Facing assumes the
asset's rest pose looks down +Z; set `CharacterAnimatorTuning.FacingYawOffset` if yours does not. A
`CharacterPose.Pose` is the brain's own buffer reused each frame - draw it this frame, do not retain it.

The derived velocity is averaged over a short sliding window (`CharacterAnimatorTuning.VelocityWindowSeconds`,
default 1/30 s = one tick) rather than a single frame's delta. That matters because the position you feed
PLATEAUS between server ticks - `ClientPrediction.RenderedState` clamps the inter-tick fraction at 1, so once
the interpolation saturates the rendered position is constant until the next `Predict`. Whenever render fps >
tick rate that produces zero-delta frames; a single-frame derivation would read speed 0 on them and strobe the
locomotion state Idle&lt;-&gt;moving every frame (restarting the clip and freezing the animation while the avatar
glides). The window holds the last good velocity across the plateau; set it to one tick of your source. A
genuine stop still resolves to Idle within one window; `&lt;= 0` reverts to per-frame derivation.

For the LOCAL player you do not have to derive horizontal speed from the position stream at all: read
`WorldClient.LocalHorizontalSpeed`, the predicted planar speed straight off the prediction tick.
It is immune to reconciliation snaps and does not wobble under lag, so it is the clean drive for a local speed
HUD / footstep audio / locomotion blend. Remotes still derive from the (windowed) position stream above.

Even windowed, the derived speed ripples a little - the prediction/reconcile render stream is not perfectly
smooth, and a remote's replicated position arrives as a ~30 Hz staircase - enough to occasionally cross a band
threshold and, since `AnimationPlayer.Play` restarts a clip on every state change, reset the walk/run cycle to
frame 0 every few seconds (worst while sprinting, where the ripple straddles the walk/run split). So
`AnimatedCharacter` DEBOUNCES ground-state transitions: a new idle/walk/run takes effect only after it has held
for `stateDebounceSeconds` (ctor param / `CharacterAnimatorTuning.StateDebounceSeconds`, default
`AnimatedCharacter.DefaultStateDebounceSeconds` = 0.08 s), so a one-tick excursion is ignored; air states
(jump/fall) are exempt and switch instantly so a real jump never lags. Pass 0 to switch immediately.

**Lower-level pieces** (if you are not using `AnimatedCharacter`): `AnimationSampler.SampleToBonePalette(clip,
skeleton, time)` is a one-shot pose; `AnimationPlayer` holds a playhead, loops, and crossfades
(`Play(clip, crossfade)` then `Update(dt)` then `GetBonePalette(buffer)`). `JointPose` is the TRS unit clips
interpolate; `InterpolationMode` is LINEAR or STEP (CUBICSPLINE is read as its value keys).

**Out of scope.** Animation events (attack hitframes, footstep sounds), root motion, IK, additive/facial
layers, and full blend trees beyond the locomotion state machine are not provided - drive those from your
own gameplay layer.

**Determinism: presentation only.** As with raw skinning, the sampled pose / bone palette is render-time
visual - never feed it back into simulation, RNG, or netcode.

---

## Attack telegraphs / danger zones

`KhaozEngine.Telegraphs` (2D) + `KhaozEngine.Telegraphs.Render3D` (ground plane) draw animated
danger-zone indicators. Presentation only: feed shape + position + a 0..1 progress + a TelegraphStyle
from your own sim each frame; the engine holds no telegraph state (safe under lockstep, never in the
determinism hash).

3D (ground plane), `using KhaozEngine.Telegraphs;`:

    float progress = 1f - emitter.TelegraphSeconds / window;   // 0 at telegraph start, 1 at impact
    scene.GroundCircle(emitter.Target, emitter.Radius, progress, TelegraphStyle.Fire);
    scene.GroundRing(emitter.Target, 0f, emitter.ShockwaveRadius, progress, TelegraphStyle.Generic);

2D:

    tg.Begin(spriteBatch, primitiveRenderer);
    tg.Circle(center, radius, progress, TelegraphStyle.Generic);
    tg.End();

Shapes: Circle, Ring, Beam, Cone, Arc. Styles: Generic / Fire / Poison presets, or a TelegraphStyle
(fill/outline color, edge thickness, opacity, FillMode, TelegraphAnim flags
[OutlinePulse | FillSweep | ColorRamp | ImpactFlash], blend). The 3D path paints onto the ground/terrain
via the depth buffer and is occluded by meshes. (EdgeThickness is authored in 2D pixels; the 3D ground
path derives its own world-space edge from the decal size.)

---

## Terrain (`KhaozEngine.Terrain` / `KhaozEngine.Terrain.Render3D`)

The overworld ground is an **analytic field**, not a baked heightmap. `TerrainField.SampleHeight(x, z)` is the
single source of truth for ground height; it depends only on `(x, z, seed)` (never on which neighbour chunks
are loaded), so the authoritative server and the visual client evaluate the same math and streamed chunks line
up. Plain `float` (NOT `DeterministicFp`): the tiny cross-platform float drift is invisible and the replication
layer corrects it. The leaf (`KhaozEngine.Terrain`) is render-free and lives in the `Foundation` umbrella, so a
headless server references it without pulling in `Render3D`.

Build a field (server and client both do this), `using KhaozEngine.Terrain;`:

    var field = new TerrainField(TerrainPresets.Clearing());   // gentle meadow -> mountains + a lake basin
    float h = field.SampleHeight(x, z);                        // ground height (Y up)
    Vector3 n = field.SampleNormal(x, z);                      // finite-difference normal, for lighting/slope
    BiomeId b = field.SampleBiome(x, z);

`TerrainConfig` composes the field: `BiomeBand[]` (designed regions smoothstep-blended along Z, each with a
base height + hill amplitude + `BiomeId`), the base-noise knobs, and an ordered `ITerrainFeature[]` folded in
order: `LakeFeature` (carves a basin), `RidgeFeature` (a gaussian wall pierced by a pass), `FlattenFeature`
(levels a hub). Write your own `ITerrainFeature` (`float Apply(float x, float z, float h)`) for new shapes.

The sim keeps entities on the ground with `TerrainCollision` (render-free, in the leaf):

    var col = new TerrainCollision(field);
    float ground = col.GroundHeight(x, z);                 // = field.SampleHeight
    bool ok = col.IsWalkable(x, z, maxSlopeRadians);       // false on terrain steeper than the budget

On the client, `KhaozEngine.Terrain.Render3D` (in the `Game3D` umbrella) meshes finite chunks off the field,
`using KhaozEngine.Terrain;`:

    int lod = TerrainLod.PickLod(distanceToCamera);        // 0 dense (near) .. 2 coarse (far)
    var region = new TerrainChunkRegion { OriginX = cx, OriginZ = cz, Size = TerrainChunkRegion.DefaultSize };
    TerrainChunkMesh chunk = TerrainChunkBuilder.Build(field, region, lod);
    var handle = scene.LoadTerrainChunk(chunk);            // cache this; rebuild cadence is streaming's job
    scene.DrawTerrainChunk(handle);                        // each frame

Each chunk is a Render3D `GltfMesh` with ~0.3 m edge skirts to hide cracks where a dense chunk meets a coarse
neighbour, a `TerrainChunkBounds` AABB for frustum culling, and per-vertex splat weights (grass/dirt/rock/sand/snow
in `ModelVertex.Color`). With a `SplatMaterialHandle` supplied the weights drive the PBR splat pipeline (five
tileable PBR layers, triplanar); without one the weights are blended into a height/slope vertex-colour ramp
(the fallback). *Which* chunks exist and *when* they rebuild is the **World streaming** sub-project below
(`TerrainStreamer`). See "Textured terrain (PBR splat)" below for the material API. A real water shader is a
later sub-project.

---

## Third-person follow camera + character controller (`FollowCamera3D` / `CharacterController3D`)

For a walkable 3D world, pair `FollowCamera3D` (`KhaozEngine.Render3D`) with `CharacterController3D`
(`KhaozEngine.Game.Render3D`). The camera is a perspective sibling of `IsoCamera3D`: it orbits behind a `Target`
at a clamped `Pitch`/`Distance` and always looks at the target (same Y-up convention, same `Eye`/`Forward`/
`ScreenToGround`; it implements `IIsoCamera3D`). Drive it from the input snapshot with `FollowCameraController`
(hold the orbit button and drag to swing yaw/pitch, scroll to zoom). To render through it, set
`Scene3D.CameraOverride` (null = the built-in iso `Camera`) and feed the override its aspect ratio each frame:

```csharp
var camera = new FollowCamera3D { Target = character.Position, Distance = 9f };
camera.GroundHeight = terrain.GroundHeight;   // keep the eye above the ground in a dip (optional, terrain-agnostic)
var camController = new FollowCameraController(camera);
scene.CameraOverride = camera;   // a sibling camera drives the render path; null = built-in iso Camera

// each frame:
character.Update(input, dt, camera.Yaw, terrain.GroundHeight);   // WASD camera-relative; Space jumps; gravity + landing
camera.Target = character.Position;
camera.AspectRatio = (float)frameWidth / frameHeight;
camController.Update(input, dt);
```

`CharacterController3D` is terrain-agnostic: it takes ground height (and optionally ground normal) as delegates,
so any height source works. Pair it with `TerrainCollision.GroundHeight` for analytic terrain. WASD is
camera-relative on XZ (normalized diagonals, left/right shift to run); `Position` is the capsule centre.

**Jump + gravity (vertical physics).** `Update` reads `Space` (edge-triggered) as a jump and runs the vertical
movement step: while airborne the character falls under `Gravity` (clamped to `MaxFallSpeed`) until it lands back
on the ground; a jump launches at `JumpSpeed` only when grounded (or within `CoyoteTime` of leaving the ground),
and a jump pressed just before landing fires on contact (`JumpBuffer`). `character.Grounded` and
`character.VerticalVelocity` are exposed (e.g. for jump/land animation or SFX). The feel is tunable via public
fields - `Gravity` (25), `JumpSpeed` (8, apex ~1.28 m), `MaxFallSpeed` (50), `CoyoteTime` (0.1), `JumpBuffer`
(0.1), `AirControl` (1, horizontal control while airborne), `GroundedEpsilon` (0.3, the slope skin so a downhill
run does not flicker grounded/airborne) - matching `MoveTuning`. Run off a cliff or the bounded-clearing rim and
you fall.

Speeds, capsule half-height, max slope, the vertical-feel fields above, the camera distance/pitch limits,
orbit/zoom sensitivity, per-axis drag inversion (`FollowCameraController.InvertX` / `InvertY`, for an "invert axis"
setting), and the camera ground-clamp (`FollowCamera3D.GroundHeight` / `GroundClearance`) are public fields
(feel-tuned later). See the 3D World room (`Room3D`) in `KhaozEngine.Showcase` for the full wiring (Space to jump). It now drives an animated
character off this controller's movement state (see "Animated characters" above) rather than a static capsule.

**Optional target damping (off by default).** Set `FollowCamera3D.EnableTargetDamping = true` (rate
`TargetDampingRate`, default 10/s) to have the camera follow a smoothed `EffectiveTarget` that eases toward
`Target` each frame instead of snapping 1:1 - belt-and-suspenders against residual avatar jitter on a remote
server. `FollowCameraController.Update(input, dt)` drives it (so pass the real frame `dt`); with damping off the
camera reads `Target` directly and is unchanged. Read `EffectiveTarget` for the smoothed look-at point.

---

## Prop scatter + asset pipeline (`AssetManifest` / `PropScatter` / `Scene3D.DrawProps`)

Forest (or rock, or bush) the terrain in three parts: an **asset pipeline** that normalizes kit glTF, a
**render-free scatter** that decides where props go, and an **instanced render helper** that draws the
in-range ones.

**1. Asset pipeline (`KhaozEngine.Render3D`).** A prop kit ships a JSON manifest plus its glTF files:

```json
{ "props": [ { "id": "pine_a", "file": "pine_a.glb", "heightMeters": 12, "source": "Quaternius", "license": "CC0" } ] }
```

`AssetManifest.Load(path)` parses it (relative `file` resolves against the manifest directory; it is also the
provenance record). `PropLoader.LoadProp(entry)` loads the glTF via `GltfLoader`, scales the mesh uniformly to
the declared `heightMeters`, drops the origin to the base (feet on the ground), and re-centres X/Z on the
origin. Validation throws loudly on an implausible declared-vs-actual size (the 1.8 m human-scale guard): a
height outside `PropValidation.MinHeightMeters..MaxHeightMeters`, or an implied raw-to-declared scale outside
`MinScale..MaxScale` (the asset is in the wrong units). `using KhaozEngine.Render3D;`:

```csharp
var manifest = AssetManifest.Load(Path.Combine(AppContext.BaseDirectory, "assets/props/props.manifest.json"));
var meshes = new Dictionary<string, MeshHandle>();
foreach (AssetEntry e in manifest.Props)
    meshes[e.Id] = scene.LoadMesh(PropLoader.LoadProp(e));   // one uploaded mesh per id
```

> **Decompress kit glTF offline.** The loader reads **plain glTF 2.0 only** - it has no meshopt decoder, and
> chokes on required extensions (`EXT_meshopt_compression`, `KHR_mesh_quantization`, `EXT_texture_webp`). Bake
> kit assets to plain glTF as an ingest step with [`gltf-transform`](https://gltf-transform.dev), e.g.
> `npx --yes @gltf-transform/cli@latest cp <in>.glb <out>.glb` (drops meshopt), plus `dequantize` and a
> texture-flatten where the kit uses quantization / webp. The committed `KhaozEngine.Showcase` kit was baked this
> way (see its `assets/props/CREDITS.md`); multi-material props are flattened to per-material flat base colours
> so the single-mesh loader colours them correctly.

**2. Scatter (`KhaozEngine.Terrain`, render-free leaf).** `PropScatter.Generate(field, config, area)` returns
deterministic `PropPlacement[]` via the same coordinate hash as the terrain (`TerrainNoise.Hash2`): a jittered
grid, per-biome density + weighted kind mix, exclusions (below `WaterLevel`, inside a clearing radius, above a
height cap), and per-instance scale/yaw/variant. `Y` is `field.SampleHeight`. A placement depends only on
`(cell, seed)`, so `Generate` over an area equals the union over its tiles - **streaming-ready** (generate per
cell as the world streams). `ScatterConfig` is data-driven; `ScatterConfig.ForestRing()` is the greybox-parity
default. `using KhaozEngine.Terrain;`:

```csharp
var placements = PropScatter.Generate(field, ScatterConfig.ForestRing(), new RectArea(-58, -58, 58, 16));
```

**3. Instanced draw (`KhaozEngine.Terrain.Render3D`).** Each frame, `Scene3D.DrawProps` queues the existing
instancing path for placements within a **horizontal** draw radius of a focus point (the player) and
distance-culls the rest, so an N-tree forest batches into a handful of draws (one per kit mesh):

```csharp
scene.DrawProps(placements, meshes, focus: character.Position, drawRadius: 90f);   // each frame
```

`PropRenderer.Queue(SceneInstances, ...)` is the same logic against a raw instance queue (headless-testable).
See the 3D World room (`Room3D`) in `KhaozEngine.Showcase` for the full wiring. Mesh-LOD/impostors, textured prop materials, and animated props are
later sub-projects. Terrain PBR splat textures are covered in "Textured terrain" below.

---

## Bounded zones (`RimFeature` + `WorldBounds` + the slope gate)

A designed zone (a start town/lake ringed by impassable mountains with one road out) wants a border that is both
**diegetic** (you see why you can't pass) and **enforced** (the authoritative server won't let you). Three pieces
compose it:

**1. `RimFeature` (the visual wall, `KhaozEngine.Terrain`).** An `ITerrainFeature` that raises terrain into an
enclosing wall around a bounded region: unchanged inside `InnerRadius`, a smoothstep ramp up to `WallHeight` by
`OuterRadius` (and held there beyond - a plateau, so you cannot see/walk past it), modulated by a coordinate-hash
jagged crest (`Ruggedness`) so it reads as mountains not a berm. `RimPass`es cut corridors through the wall on a
heading (the road out). `using KhaozEngine.Terrain;` (+ `System.Numerics` for `Vector2`):

```csharp
var rim = new RimFeature(
    center: Vector2.Zero, innerRadius: 38f, outerRadius: 56f, wallHeight: 30f,
    ruggedness: 0.3f, seed: 5,
    passes: new[] { new RimPass(angleRadians: MathF.PI / 2f /* +Z */, halfWidth: 10f, falloff: 6f) });
// add it to a TerrainConfig.Features list, or just use the ready-made preset:
var field = new TerrainField(TerrainPresets.BoundedClearing());   // meadow ringed by a rim, one +Z pass, a lake
```

**2. The slope gate (so the wall can't be climbed).** `CharacterMovement.Step` already rejects a step onto ground
steeper than `MoveTuning.MaxSlopeRadians` (default 45 deg) - but only when a `groundNormal` delegate is supplied.
Pass `TerrainCollision.GroundNormal` as that delegate everywhere movement runs, so the steep rim blocks and the
gentle pass corridor stays walkable. Local controller:

```csharp
var terrain = new TerrainCollision(field);
character.Update(input, dt, camera.Yaw, terrain.GroundHeight, terrain.GroundNormal);   // groundNormal = the slope gate
```

**3. `WorldBounds` (the authoritative hard stop, `KhaozEngine.NetWorld`).** A play-area shape the server clamps
movement to every tick, so the rim can't be glitched past. `WorldBounds.Clamp(x, z)` returns the nearest in-bounds
point - a no-op inside (idempotent) and a projection onto the boundary outside, which produces **clamp-and-slide**
(the tangential part of a blocked move survives, so movement stays smooth). Two shapes ship: `CircleBounds` and
`RectBounds`. Wire it (nullable - null = today's unbounded behaviour) into the movement step; pass the same bounds
to the client so prediction clamps identically and reconciliation stays clean at the wall:

```csharp
WorldBounds bounds = new CircleBounds(center: Vector2.Zero, radius: 56f);
var server = new WorldServer(transport, cfg, terrain.GroundHeight, MoveTuning.Default,
                             groundNormal: terrain.GroundNormal, bounds: bounds);          // authoritative
var client = new WorldClient(transport, terrain.GroundHeight, MoveTuning.Default,
                             groundNormal: terrain.GroundNormal, bounds: bounds);          // predicts identically
// ShardedWorldServer takes the same (groundNormal, bounds) params - the clamp is authoritative across the cell grid.
```

`RimFeature` makes the edge *look* enclosed; the slope gate stops you walking up it; `WorldBounds` *guarantees* it.
A `RimPass` corresponds to an opening in the play area (or the bounds is simply larger than the walled region until
gate/zone-transition content exists - those are later). The 3D World room in `KhaozEngine.Showcase` uses this bounded preset:
held inside by the mountains, out through the +Z pass. The circular rim is the MVP; a
rect/polygon rim and gravity/jump are named follow-ups (prop/building collision shipped, see below).

---

## 3D physics (`KhaozEngine.Physics` / `KhaozEngine.Physics.Bepu`)

The physics layer is split into a dependency-free seam (`KhaozEngine.Physics`, in `Foundation`) and an opt-in
backend (`KhaozEngine.Physics.Bepu`, NOT in any umbrella, added explicitly like `WorldStore.Sqlite`). This is the
same opt-in-backend pattern the `WorldStore.*` durable backends use.

**Seam (`KhaozEngine.Physics`)** - what every caller sees:
- `IPhysicsWorld`: `AddStatic(PhysicsShape shape, Pose pose, PhysicsMaterial? material = null) -> StaticHandle`,
  `RemoveStatic(StaticHandle handle)`, `Step(float dt)`,
  `Raycast(Vector3 origin, Vector3 direction, float maxDistance, out RayHit hit, QueryFilter filter = default) -> bool`,
  `SweepCapsule(CapsuleShape capsule, Pose pose, Vector3 direction, float maxDistance, out SweepHit hit, QueryFilter filter = default) -> bool`,
  `ComputePenetration(CapsuleShape capsule, Pose pose, out Vector3 mtv) -> bool` (mtv = minimum translation vector, push-out direction * depth).
- Concrete shape classes (all extend `PhysicsShape`): `SphereShape(radius)`, `CapsuleShape(radius, length)`,
  `BoxShape(halfExtents)`, `CylinderShape(radius, length)`, `ConvexHullShape(points)`,
  `TriangleMeshShape(vertices, indices)`, `CompoundShape(children)`.
- `Pose(Vector3 position, Quaternion orientation)` / `Pose.At(Vector3 position)` (identity orientation).
- `PhysicsMaterial(float Friction, float Restitution)`. `PhysicsMaterial.Default` = full friction, no bounce.
- `QueryFilter(uint Layers)` (layer mask; `QueryFilter.All` / default matches every body).
- `StaticHandle`, `RayHit(Distance, Point, Normal, Body)`, `SweepHit(Distance, Point, Normal, Body)`.

**Backend (`KhaozEngine.Physics.Bepu`)** - add this package to your game head / server:

```xml
<PackageReference Include="KhaozEngine.Physics.Bepu" Version="8.10.0" />
```

```csharp
using KhaozEngine.Physics;
using KhaozEngine.Physics.Bepu;

// Create a single-threaded deterministic Simulation (SP1 = static bodies only; no gravity parameter):
IPhysicsWorld physics = new BepuPhysicsWorld();

// Add a static mesh (triangle mesh for a building, convex hull for a rock).
// Shapes come from PropCollisionLoader (baked by ke-propbake) or built inline:
StaticHandle rockHandle = physics.AddStatic(
    new ConvexHullShape(rockPoints),
    Pose.At(new Vector3(10f, 0f, 5f)),
    PhysicsMaterial.Default);

// Step the simulation each tick (usually driven by FixedTickHost in the server loop):
physics.Step(dt);

// Raycast for line-of-sight / AI queries:
if (physics.Raycast(eye, forward, maxDistance: 50f, out RayHit hit))
    Console.WriteLine($"hit at {hit.Point}, normal {hit.Normal}");

// Remove a static when a chunk unloads:
physics.RemoveStatic(rockHandle);
```

**Wiring into character movement** - pass the physics world as `IPhysicsWorld?` wherever the movement step runs.
The step moves the capsule freely to its desired position, then depenetrates it against all static bodies in
full 3D (`ComputePenetration` push-out, up to 6 iterations), plus a downward sweep probe for vertical support
from prop tops. Terrain height stays analytic; null = terrain-only.

```csharp
using KhaozEngine.Locomotion;
using KhaozEngine.Physics;
using KhaozEngine.Physics.Bepu;

IPhysicsWorld physics = new BepuPhysicsWorld();
// ... add statics from the streamer ...

// Local single-player controller (CharacterController3D takes IPhysicsWorld? as a ctor param):
var character = new CharacterController3D(terrain.GroundHeight, MoveTuning.Default,
                                          groundNormal: terrain.GroundNormal, physics: physics);
character.Update(input, dt, cameraYaw);

// Authoritative server (WorldServer / ShardedWorldServer - IPhysicsWorld? replaces WorldColliders?/WorldSurfaces?):
var server = new WorldServer(transport, config, terrain.GroundHeight, MoveTuning.Default,
                             terrain.GroundNormal, bounds: null, physics: physics);
var sharded = new ShardedWorldServer(transport, shardConfig, terrain.GroundHeight, MoveTuning.Default,
                                     terrain.GroundNormal, bounds: null, physics: physics);

// Prediction client (same physics world, so client predicts around props instead of rubber-banding):
var client = new WorldClient(transport, terrain.GroundHeight, MoveTuning.Default,
                             groundNormal: terrain.GroundNormal, physics: physics);
```

**Wiring into the chunk streamer** - `Scene3DChunkSink` accepts an `IPhysicsWorld?` and calls `AddStatic`/
`RemoveStatic` for each prop in `Load`/`Unload`. Collision shapes come from `PropCollisionLoader`,
which reads the `CollisionShape` baked by `ke-propbake` alongside the `.surf` heightmap:

```csharp
using KhaozEngine.Render3D;
using KhaozEngine.Physics;

AssetManifest manifest = AssetManifest.Load(manifestPath);
// Load baked 3D collision shapes (ke-propbake stamps CollisionShape on each walkable-solid entry):
IReadOnlyDictionary<string, PhysicsShape> collisionShapes = PropCollisionLoader.LoadAll(manifest);

var sink = new Scene3DChunkSink(
    scene, field, manifest, scatterConfig, streamConfig,
    collisionShapes: collisionShapes,     // pass the baked shapes
    physics: physics);                    // the physics world to add/remove statics in

// The streamer drives the sink - props appear in / leave the physics world as chunks load / unload.
```

**Headless server: load baked collision without Render3D.** `PropCollisionLoader.LoadAll` lives in
`KhaozEngine.Render3D` because `AssetManifest` does, and Render3D pulls `Gpu` + `Windowing` (Silk.NET/GLFW). An
authoritative server must not carry those. The render-free KECL `.coll` reader and manifest-free loaders are in
the dependency-free `KhaozEngine.Physics` package as `PropCollisionFormat`, so a server referencing only
`KhaozEngine.Physics` (+ the opt-in `KhaozEngine.Physics.Bepu` backend - both reachable from the `Server`
umbrella) builds the exact same `BepuPhysicsWorld` the client predicts against. The shapes are byte-identical, so
`ComputePenetration` / `SweepCapsule` match and client prediction reconciles.

```csharp
using KhaozEngine.Physics;
using KhaozEngine.Physics.Bepu;

// A flat kit of baked shapes shipped to the server as <id>.coll files (no manifest needed):
IReadOnlyDictionary<string, PhysicsShape> shapes = PropCollisionFormat.LoadDirectory(kitDir);
// ...or explicit (id, path) pairs if the layout differs:
//   PropCollisionFormat.Load(new[] { ("rock_01", "/data/rock_01.coll"), ("oak", "/data/oak.coll") });

IPhysicsWorld physics = new BepuPhysicsWorld();
foreach ((string id, Pose pose) in placements)        // the server's deterministic prop placements
    physics.AddStatic(shapes[id], pose);
physics.Step(dt);                                     // now authoritative against the same shapes the client uses
```

The client still loads via `PropCollisionLoader.LoadAll(manifest)` (Render3D); both decode through
`PropCollisionFormat`, so the bytes - and therefore the queries - are identical. The shape-producing
`PropCollisionBake.Bake(GltfMesh)` (and `ke-propbake`) stay in Render3D: baking is an offline/client concern,
only loading goes headless.

**NativeAOT note:** `BepuPhysicsWorld` requires an `rd.xml` shim for `Dynamic=Required All` on `BepuPhysics`
when publishing under NativeAOT (iOS/AOT reach). Desktop and headless server targets are fine without it.

**What's next (sub-project 2):** dynamic rigid bodies + replication, terrain-as-physics-geometry (replace the
`TerrainCollision` delegate with a static terrain `TriangleMesh` body). See `docs/ROADMAP.md` item #2.

---

## Baking prop collision (`ke-propbake`)

`ke-propbake` is the offline bake step - run it as the last kit-ingest step (re-ingest = re-bake). For every
prop it writes a render-free `.coll` collision shape (trees get a leaning convex hull of the lower trunk,
other solids get a hull/mesh from their geometry) and stamps the manifest:

```bash
# Bakes a .coll collision shape for EVERY prop (trees get a leaning trunk-hull) and stamps the manifest.
dotnet ke-propbake path/to/props.manifest.json
```

Those `.coll` shapes are what `PropCollisionLoader` (client) and `PropCollisionFormat` (headless server) load
into the `IPhysicsWorld` - see "3D physics" above. The swept capsule-vs-mesh resolver makes a prop both solid
and standable: vertical support comes from a downward sweep probe onto prop tops, so you walk and jump onto a
prop's real top contour directly off its collision mesh (a domed rock is mountable up its flank and standable
across its top). Out of scope: overhangs / interiors / caves, dynamic/moving props, player-vs-player.

`ke-propbake` also writes a `.surf` top-down height map per walkable solid, read only by the legacy
`WorldSurfaces` path below. The `IPhysicsWorld` path does not use it.

---

## Static world collision (`WorldColliders` / `WorldSurfaces`) - legacy

Before the physics layer, props and buildings were made solid with an XZ capsule-vs-static-collider push-out
(plus a height-aware `.surf` walkable surface for standing and jumping onto prop tops), wired into the shared
movement step. The 3D path now uses `IPhysicsWorld` (above), and the `CharacterMovement.Step` / `WorldServer` /
`WorldClient` / `ShardedWorldServer` / `PlayerMoveSimulator` / `PlayerMovementSystem` ctors no longer take a
collider/surface set. The queryable types (`WorldColliders` / `PropColliders` / `WorldCollider`, `WorldSurfaces`
/ `PropSurfaces`, derived from the prop scatter via `PropFootprint` / `PropSurfaceLoader`) still exist for 2D
games and lockstep sims that query them directly - see the `KhaozEngine.Collision` README. This path will be
removed once no consumer needs it.

---

## Social / Discord presence (`KhaozEngine.Social` / `KhaozEngine.Social.Discord`)

`KhaozEngine.Social` is the provider-neutral seam (in `Foundation`): `ISocialProvider` +
`SocialPresenceController`. `KhaozEngine.Social.Discord` is the opt-in Discord backend (add it
explicitly on the client head, like `Physics.Bepu`); a headless server or a game without it uses the
silent `NullSocialProvider`, so the same game code runs everywhere.

```csharp
using KhaozEngine.Social;
using KhaozEngine.Social.Discord;

// Desktop head: real Discord presence.
var provider = new DiscordSocialProvider(new DiscordSocialOptions { ApplicationId = "<discord-app-id>" });
var social = new SocialPresenceController(provider);
social.Initialize();

// Menu / gameplay set high-level presence; the controller dedupes + throttles:
social.SetPresence(new RichPresence { Details = "In Menu", State = "Idle" });
social.SetElapsedPresence(new RichPresence { Details = "In Game", State = "Boss Rush" }, runElapsed);

// One-click "Join Game" from a friend's profile (needs a JoinSecret on the presence):
social.JoinRequested += secret => myNetcode.JoinFromSecret(secret);

// Pump once per frame; dispose at shutdown.
social.Update();
```

A game keeps only its Discord Application id, its presence copy, and its mode->`RichPresence` mapping.
Everything else (connection, throttling, error handling, self-disable on failure) is engine-owned. The
Discord backend talks to the local Discord client over its IPC socket (Windows named pipe, unix domain
socket) with zero native libraries; if Discord is not running the provider stays disconnected and every
call is a silent no-op. The native Discord Social SDK (friends/lobbies/voice) is out of scope and would
be a separate opt-in backend behind the same `ISocialProvider`.

---

## World streaming (`TerrainStreamer` / `Scene3DChunkSink`)

`TerrainStreamer` (`KhaozEngine.Terrain.Render3D`) makes the world effectively endless: it keeps a ring of
terrain chunks (and their props) loaded around the player and unloads the ones left behind, so you can walk
any direction forever. It is pure bookkeeping (no GPU, no field), so the real mesh/prop/draw work lives behind
an `IChunkSink`; the package ships a production sink (`Scene3DChunkSink`) and the streamer is headless-tested
with a fake one. `using KhaozEngine.Terrain;`:

```csharp
var field = new TerrainField(TerrainPresets.Clearing());
var sink  = new Scene3DChunkSink(scene, field, ScatterConfig.ForestRing(), propMeshes,
                                 chunkSize: TerrainChunkRegion.DefaultSize, propDrawRadius: 90f);
var streamer = new TerrainStreamer(StreamerConfig.Default, sink);

// each frame:
streamer.Update(playerPos, dt);   // load ring / unload (hysteresis) / re-LOD, amortized to MaxLoadsPerFrame
// in your 3D draw pass:
sink.Draw(playerPos);             // draws every loaded chunk + its in-range props
```

`Update(playerPos, dt)` each frame: (1) **loads** chunks inside `LoadRadius` (Euclidean chunk-distance) that
are not yet loaded, (2) **unloads** chunks past `UnloadRadius` immediately, (3) **re-LODs** loaded chunks whose
`TerrainLod.PickLod(distance-to-chunk-center)` tier changed (the chunk is rebuilt at the new resolution), and
(4) **amortizes**: at most `MaxLoadsPerFrame` load/re-LOD ops per update (nearest first), so a build burst never
hitches. `UnloadRadius > LoadRadius` is a **hysteresis band** that stops churn when the player oscillates across
a chunk boundary. `StreamerConfig.Default` is LoadRadius 4 (~240 m view) / UnloadRadius 6 / MaxLoadsPerFrame 3 /
60 m chunks. At load time (a loading moment, not a frame) pump `Update` until `streamer.Loaded.Count` stops
growing to prime the full first ring before the first frame.

`ChunkGrid` maps a `ChunkCoord(int X, int Z)` to and from world space for a chunk size (`CoordOf`/`CenterOf`/
`RegionOf`/`AreaOf`); `AreaOf` is half-open so adjacent chunks tile `PropScatter` exactly once. Because
`TerrainField.SampleHeight` and `PropScatter.Generate` are per-area deterministic, streaming is orthogonal to
replication: the **networked client streams locally with the same code** (nothing about the world is sent over
the wire). For a custom mesh/prop pipeline, implement `IChunkSink` yourself and pass it to `TerrainStreamer`.
Threaded/background chunk build (an `IJobScheduler` build) and multi-cell server sharding are later sub-projects.

**Teardown / rebuild.** Both `Scene3DChunkSink` and `TerrainStreamer` are `IDisposable`. Steady-state
walking already frees each chunk as it leaves the ring, but if you tear streaming down and rebuild it while the
**same `Scene3D` survives** (level change, world reload, a teleport that recreates the streamer), the
currently-loaded ring of chunk meshes would otherwise leak until whole-scene `Scene3D.Dispose`. Flush it first:

```csharp
// rebuild streaming, REUSING the same sink + scene (e.g. teleport to a new region):
streamer.UnloadAll();                         // frees every loaded chunk through the sink; sink stays alive
streamer = new TerrainStreamer(StreamerConfig.Default, sink);

// OR full teardown of the ring AND the sink's owned GPU resources:
streamer.Dispose();                           // = UnloadAll() then disposes the sink if it is IDisposable
```

The splat material handed to `Scene3DChunkSink` is **caller-owned by default**: it is shared across chunks and is
never freed per-chunk, so you must `scene.UnloadSplatMaterial(handle)` it yourself (or reuse it for the rebuilt
sink). Pass `ownsMaterial: true` to the ctor to hand it to the sink, whose `Dispose` then frees it too.

---

## Textured terrain (PBR splat)

Terrain chunks can render five tileable PBR layers (grass/dirt/rock/sand/snow) blended per-fragment by the splat
weights baked into each vertex, with world-space triplanar tiling, normal maps, mips, and 16x anisotropic
filtering plus a `+1` mip LOD bias (D3D11/Vulkan) that tames distance shimmer from a high-frequency tiling albedo.
Without a material supplied the chunk falls back to the height/slope vertex-colour ramp (byte-identical).

**1. Load the material.** `TerrainMaterialPresets.Procedural()` returns a ready-made `TerrainLayeredMaterial`
built from five `TerrainMaterialLayer`s (one per biome layer). Each layer carries an albedo image, a normal map,
a tiling scale, and a roughness scalar. Call `LoadTerrainMaterial` on the `Scene3D`-backed extension to upload
all layers to the GPU as texture arrays. To trade grazing sharpness for less distance "fuzz" from a high-frequency
albedo, set `material.Sampler = new TerrainSamplerConfig(GpuSamplerFilter.MinLinearMagLinearMipLinear, maximumAnisotropy: 1, mipLodBias: 3)`
(or a lower anisotropy) before loading; leave it null for the tuned default (anisotropic 16x + a +1 bias):

```csharp
using KhaozEngine.Terrain;

// scene is your Scene3D (from GameApp3D.Scene or Render3DSurface.Scene).
TerrainLayeredMaterial mat = TerrainMaterialPresets.Procedural();   // placeholder 1x1 solid-colour layers
Scene3D.SplatMaterialHandle splatHandle = scene.LoadTerrainMaterial(mat);
```

Supply real PBR textures by constructing `TerrainLayeredMaterial` directly with `TerrainMaterialLayer` instances
whose `Albedo`/`Normal` fields are `SplatLayerImage`s loaded from PNG bytes.

**2. Pass the handle to the streamer.** `Scene3DChunkSink` accepts an optional `SplatMaterialHandle`; when set,
every chunk it loads uses the textured splat pipeline instead of the ramp:

```csharp
var sink = new Scene3DChunkSink(scene, field, ScatterConfig.ForestRing(), propMeshes,
                                chunkSize: TerrainChunkRegion.DefaultSize, propDrawRadius: 90f,
                                material: splatHandle);   // optional; omit for the ramp fallback
                                                          // (add ownsMaterial: true to have sink.Dispose() free it)
var streamer = new TerrainStreamer(StreamerConfig.Default, sink);
```

Or load a single chunk directly with the textured overload:

```csharp
int lod = TerrainLod.PickLod(distanceToCamera);
var region = new TerrainChunkRegion { OriginX = cx, OriginZ = cz, Size = TerrainChunkRegion.DefaultSize };
TerrainChunkMesh chunk = TerrainChunkBuilder.Build(field, region, lod);
var handle = scene.LoadTerrainChunk(chunk, splatHandle);   // textured overload
scene.DrawTerrainChunk(handle);
```

**3. Unload when done.** Call `scene.UnloadSplatMaterial(splatHandle)` to free the GPU texture arrays (the
two `texture2DArray`s - albedo + normal - are shared by all chunks using this material). The material may be
reloaded; each `LoadTerrainMaterial` call allocates a fresh set of arrays.

**Out of scope.** Runtime layer blending tweaks, streaming of different materials per biome region, and
per-chunk material overrides are not provided - swap the handle on `Scene3DChunkSink` and rebuild the ring
(`streamer.UnloadAll()` then a fresh sink with the new handle) to change the look at stream time.

---

## Textured props

Props (trees, rocks, buildings) can carry the same albedo/normal/roughness surface detail as terrain, instead
of rendering with a single flat base-colour. A prop with no textures still renders exactly as before, so this
is opt-in and backward compatible.

**Real glTF textures.** Set `"textured": true` on the prop's manifest entry, then load it with
`PropLoader.LoadPropWithMaterial` instead of `PropLoader.LoadProp`. It reads the glTF's first textured
material's baseColor/normal/metallicRoughness textures alongside the normalized mesh, and upload both together
with `Scene3D.LoadMesh(GltfMesh, GltfMaterialMaps)`:

```csharp
AssetEntry entry = manifest.Find("mossy_rock");   // manifest entry has "textured": true
(GltfMesh mesh, GltfMaterialMaps maps) = PropLoader.LoadPropWithMaterial(entry);
MeshHandle handle = scene.LoadMesh(mesh, maps);
```

If the glTF turns out to have no textures, `maps.IsEmpty` is true and the mesh renders with its flat
per-material base colour, same as `LoadProp` - no throw, no special-casing needed at the call site.

**Asset-free procedural placeholder.** For samples, tests, or prototyping without shipping binary textures,
`PropMaterialPresets.Procedural()` generates a deterministic mossy-stone albedo + normal in memory (mirrors
`TerrainMaterialPresets.Procedural`). Primitive meshes (e.g. `MeshPrimitives.Box`) have UVs but no tangents, so
run them through `MeshOps.WithTangents` first to give normal maps something to map against. Tile the UVs with
`MeshOps.ScaleUv` so the material repeats and reads crisp rather than as one stretched (blurry) copy:

```csharp
MeshHandle prop = scene.LoadMesh(
    MeshOps.WithTangents(MeshOps.ScaleUv(MeshPrimitives.Box(1.5f), 3f)),
    PropMaterialPresets.Procedural());
```

The 3D World room in `KhaozEngine.Showcase` places one of these procedural textured blocks near spawn as a live demo.

---

## Ground-cover scatter and understory companions

`Scene3DChunkSink` now accepts N `PropLayer`s, so a scene can have sparse tall trees at a long draw radius
alongside dense short-radius ground cover, with a companion layer that rings each tree base with foliage.
Foliage ids carry no collider - this is render-only; the server, client prediction, and collision are untouched.

```csharp
using KhaozEngine.Terrain;
using KhaozEngine.Terrain.Render3D;

// Three layers: trees (scatter), ferns (scatter, short radius), fern ring around each tree (companion).
var layers = new PropLayer[]
{
    PropLayer.ScatterLayer(TreeScatterConfig(),  treeMeshes,  drawRadius: 90f),   // layer 0: trees
    PropLayer.ScatterLayer(FernScatterConfig(),  fernMeshes,  drawRadius: 40f),   // layer 1: ferns
    PropLayer.CompanionLayer(hostLayerIndex: 0,              // companions of layer 0 (trees)
        new CompanionConfig
        {
            HostKinds  = new[] { "oak", "pine" },              // which tree ids get companions
            CountMin   = 4,                                     // foliage instances per host (inclusive range)
            CountMax   = 6,
            RadiusMin  = 0.6f,
            RadiusMax  = 1.2f,
            Kinds      = new[] { new PropKind("fern_small", 1f) },
        },
        fernMeshes, drawRadius: 8f),
};

var sink     = new Scene3DChunkSink(scene, field, layers,
                                    chunkSize: TerrainChunkRegion.DefaultSize);
var streamer = new TerrainStreamer(StreamerConfig.Default, sink);
```

The single-layer ctor (`new Scene3DChunkSink(scene, field, ScatterConfig, meshes, chunkSize, propDrawRadius)`)
is unchanged and byte-identical to the single-layer behaviour.

`PropScatter.GenerateCompanions(field, hosts, config)` is the underlying primitive if you want companions
outside the streamer: it returns a `IReadOnlyList<PropPlacement>` that is tiling-invariant (companions over a host set
equal the union over any chunk tiling), with `Y` resampled from the field and `MaxHeight` filtering out
off-mountain placements.

---

## Networked overworld (`KhaozEngine.Locomotion` + `KhaozEngine.NetWorld`)

The movement math lives in one render-free place so local feel and networked feel are the same code.
`KhaozEngine.Locomotion` (leaf, `Foundation` umbrella) is `CharacterMovement.Step` (camera-relative move from a
`MoveCommand` (WASD axis + run + camera yaw + jump) over a timestep, normalized diagonals, ground-clamped via a
height delegate + optional slope gate) and one `MoveTuning`. It has two overloads: the horizontal-only
`Step(Vector3,...) -> Vector3` (Y clamped to the ground each tick), and the vertical-physics
`Step(in MoveState,...) -> MoveState` that adds gravity, jump (coyote-time + jump-buffer), and air control over the
carried `MoveState` (position + vertical velocity + grounded + feel timers). The local `CharacterController3D`
wraps the vertical step; the authoritative server and client prediction run the same `Step`, so the vertical axis
reconciles identically.

**3D collision (swept resolver).** When an `IPhysicsWorld` is supplied, the vertical-physics step resolves
against static props via a **substepped swept collide-and-slide** (`SweepCapsule`): the capsule is advanced in
substeps no larger than a fraction of its radius, so a fast jump / sprint / terminal fall cannot tunnel through a
thin one-sided wall, and the capsule can never be trapped inside a closed building mesh. Walkable contacts (slope
at or below the gate) are followed so the character walks across prop tops and mounts domed surfaces; steep contacts
block and redirect tangentially. A **step-up probe** wires `MoveTuning.StepHeight`: a stair tread or curb below
`StepHeight` (default 0.4 m) is auto-mounted without a jump. Depenetration is retained as a residual settle pass.
Pass `world: null` for terrain-only (byte-identical). Client `WorldClient` and server `WorldServer` /
`ShardedWorldServer` both accept the same optional `IPhysicsWorld?`, so prediction and authority resolve against
the same baked shapes.

```csharp
using KhaozEngine.Locomotion;
// Horizontal-only (top-down / no air):
Vector3 next = CharacterMovement.Step(pos, new MoveCommand(move, run, cameraYaw), dt, terrain.GroundHeight, MoveTuning.Default);

// Vertical physics (gravity + jump + swept 3D collision):
MoveState s = CharacterMovement.Step(s, new MoveCommand(move, run, cameraYaw, jump), dt, terrain.GroundHeight, MoveTuning.Default,
                                     groundNormal: terrain.GroundNormal, world: physicsWorld);
// s.Position, s.VerticalVelocity, s.Grounded
```

`KhaozEngine.NetWorld` (`Server` umbrella; deps Locomotion/Netcode/Replication/Ecs, render-free) wires that
core to the shipped authoritative netcode for a **single authoritative `World`** (multi-cell `Sharding` folds
in with streaming later):

- **`WorldServer`** (headless): a `NetServer` session spawns one player entity per connection, drains that
  client's queued `MoveCommand` each tick via `RemoteCommandQueue`, runs `PlayerMoveSimulator`
  (`ITickSimulator` over `CharacterMovement.Step`, ground-clamped), and serves each client a per-area-of-
  interest snapshot (`SnapshotWriter.WriteFiltered` over an `InterestGrid`) framed with the receiver's net id
  + last-acked move seq. Drive it on a `FixedTickHost` like `MmoServerSample`. The terrain is injected as a
  plain ground delegate, so NetWorld has no terrain dependency.

```csharp
using KhaozEngine.NetWorld;
var server = new WorldServer(transport, new WorldServerConfig { TickSeconds = 1f/30f, InterestRadius = 200f },
                            terrain.GroundHeight, MoveTuning.Default);
// loop: server.Poll(); clock.Advance(elapsed, _ => server.Tick(1f/30f));
```

- **`WorldClient`** (render-free): wraps `NetClient` + `ClientReplicationView` + `ClientPrediction`. `Poll()`
  ingests AoI snapshots, applies remote entities, and reconciles the local avatar against the authoritative
  basis; `SendInput(cmd)` predicts one tick forward and transmits it (a no-op returning `-1`
  unless `ConnectionState == Connected`, so a per-frame loop builds no stale-input backlog during a reconnect
  outage); `RequestSelfRescue()` asks the
  server to teleport the local player to a server-decided safe spot (an "unstuck" - see below); `Snapshot()` returns
  `IReadOnlyList<EntityRenderState>` (`{ NetId Id; Vector3 Position; bool IsLocal; string? DisplayName; bool
  Grounded; float VerticalVelocity; }`) for the renderer - the local player is the predicted position, remotes the
  replicated one (smoothly interpolated between snapshots by default - see `InterpolateRemotes` below).
  `Grounded` + `VerticalVelocity` are the EXACT air state (local: predicted; remote: replicated
  `MovementState`), surfaced for every entity so an animator bridge reads jump/fall for remotes instead of
  finite-differencing their terrain-following position. Optional trailing ctor params
  `WorldBounds? bounds`, `IPhysicsWorld? physics` (mirroring `WorldServer`) feed the
  internal prediction simulator, so the client predicts around the **same** static
  props/buildings and play-area bound the server is authoritative over (null = terrain only). The local avatar's
  rendered position is smoothed in 3D - the inter-tick interpolation and the reconciliation glide both carry the
  vertical axis, so a jump/fall eases instead of stair-stepping or popping, and a snapshot landing mid inter-tick
  no longer jolts the avatar (the source of the moving/jumping jitter on a remote server). Remotes are smoothed too:
  with `WorldClientConfig.InterpolateRemotes` (default `true`), `AdvancePresentation` interpolates each remote's
  replicated position between its last two snapshots, so a remote glides instead of teleporting one ~tick-rate
  snapshot-step per ingest - at the cost of ~one tick (~33 ms) of remote render latency (it renders ~one snapshot in
  the past, never extrapolating). Set `InterpolateRemotes = false` to read the raw latest position instead. Call
  `AdvancePresentation(dt)` once per render frame to drive both the local smoothing and the remote interpolation.
  Read-only local-avatar shorthands: `LocalRenderState` / `LocalGrounded` / `LocalVerticalVelocity`, plus
  `LocalHorizontalSpeed` - the predicted planar speed in m/s straight off
  `ClientPrediction.PredictedHorizontalSpeed`, computed per prediction tick and immune to reconciliation snaps,
  so it stays steady under lag. Use it to drive a speed HUD, footstep audio, or a locomotion blend instead of
  differencing `LocalRenderState.Position`, which carries the decaying reconciliation render offset and wobbles
  during a steady run.

```csharp
var client = new WorldClient(transport, terrain.GroundHeight, MoveTuning.Default, new WorldClientConfig { TickSeconds = 1f/30f });
// per fixed tick: client.SendInput(new MoveCommand(move, run, camera.Yaw));
// per frame:      client.Poll(); client.AdvancePresentation(dt);
foreach (EntityRenderState e in client.Snapshot())
    scene.Draw(capsule, Matrix4x4.CreateTranslation(e.Position - up * halfHeight), e.IsLocal ? localTint : remoteTint);
```

Client and server must build the **same** terrain field (`TerrainPresets.Clearing()`) and use the same
`MoveTuning` so prediction matches authority. Props are **not** replicated - each client scatters them
deterministically from the seed, so only players consume bandwidth. Demo: the **Networked walk** room in
`KhaozEngine.Showcase` runs an authoritative `WorldServer` + a local `WorldClient` + scripted bot clients all
in-process over loopback UDP, so you see replicated players without launching a separate server. Spec:
`docs/superpowers/specs/2026-06-27-networked-overworld-design.md`.

### Player display names / nameplates

A player can carry a replicated **display name** (a cosmetic label like "Daniel", kept distinct from the account
id / verified token subject). Set it server-side - from a `PlayerJoined` handler against your DB, or carry it on the
connect token and let the server auto-apply it:

```csharp
// (a) DB-sourced: set it when the player joins.
server.PlayerJoined += (slot, accountId) => server.SetPlayerDisplayName(slot, LookupName(accountId));

// (b) Token-sourced: mint a v2 SignedToken with a name claim; HmacTokenAuthenticator + the server auto-apply it.
string token = SignedToken.Mint(accountId, "Daniel", DateTimeOffset.UtcNow.AddHours(8), secret);
// client: new WorldClient(transport, ground, tuning, cfg, token: Encoding.UTF8.GetBytes(token));
```

It rides every AoI snapshot (UTF-8, capped at `MoveProtocol.MaxDisplayNameBytes` = 64 bytes) and surfaces on the
client as `EntityRenderState.DisplayName` (`null` when the entity has none). Draw it above the avatar by projecting
the head with the camera and using the `WorldLabel` helper, in your 2D pass after the 3D scene:

```csharp
batch.Begin();
foreach (EntityRenderState e in client.Snapshot())
    if (e.DisplayName is { } name)
        WorldLabel.Draw(batch, font, camera, e.Position, new Vector3(0, headHeight, 0), name, Color.White, fbW, fbH);
batch.End();
```

`WorldLabel.Draw` projects via the new `IIsoCamera3D.WorldToScreen(world, w, h, out pixel)` (the forward inverse of
`ScreenToRay`; `false` when the point is behind the camera) and draws centered `SpriteFont` text. If you want to
place labels yourself, call `WorldToScreen` directly. Labels are screen-space and drawn on top - they are **not**
depth-tested, so a name is not hidden when its owner stands behind terrain or a prop (occluded nameplates are out
of scope).

For a distance cap, pass `maxDistance` (in metres). By default the ring is measured from the camera eye; for a
third-person camera that orbits offset from the player, pass `cullFrom: localPlayerPos` so labels cull on
player-to-target distance and don't pop in/out as the camera rotates around a stationary scene:

```csharp
WorldLabel.Draw(batch, font, camera, e.Position, headOffset, name, Color.White, fbW, fbH,
    maxDistance: 90f, cullFrom: localPlayerPos);
```

The cull predicate is also exposed render-free as `WorldLabel.ShouldCull(worldPos, cullFrom, maxDistance)` if you
want to filter the label set before drawing.

#### The nameplate widget (name + bars plate)

`WorldLabel` draws text only. For the MMO-style plate - a rounded panel holding the name and one or more bars
(health/resource) - use **`NameplateRenderer`** (also in `KhaozEngine.Render3D`). It is data-driven: a `Nameplate`
carries the title and a `Bars` list, so a game can ship one bar now and add more later without a rewrite. Build a
plate per entity and draw it in the same 2D pass, after the 3D scene:

```csharp
var white = /* a 1x1 white Texture2D, as DiagnosticsOverlay uses */;
batch.Begin();
foreach (EntityRenderState e in client.Snapshot())
    if (e.DisplayName is { } name)
    {
        var plate = new Nameplate
        {
            Title = name,
            TitleColor = Color.White,
            Bars = new[] { new NameplateBar(e.Health / e.MaxHealth, green, darkTrack) },
        };
        NameplateRenderer.Draw(batch, font, white, camera, e.Position, new Vector3(0, headHeight, 0),
            plate, NameplateStyle.Default, fbW, fbH, maxDistance: 90f, cullFrom: localPlayerPos);
    }
batch.End();
```

`NameplateRenderer.Draw` projects `worldPos + offset` via `IIsoCamera3D.WorldToScreen` exactly like `WorldLabel`,
centres the panel horizontally on the head pixel and bottom-anchors it there (the plate floats above the head). It
returns `false` on the same cull paths (empty plate, behind camera, off-screen, beyond `maxDistance` - the distance
predicate is the shared `NameplateRenderer.ShouldCull`, identical to `WorldLabel.ShouldCull`). Like `WorldLabel` it
is screen-space, not depth-tested. `NameplateBar.Fraction` is clamped to 0..1 at draw; the draw path allocates no
per-frame heap (bar rects are computed in the loop).

`NameplateStyle` is the look, split from the data. `NameplateStyle.Default` is the unified opaque-plate preset
(dark rounded panel, subtle border, one-bar geometry). Tweak it with a `with` expression, or reach the panel-less
"classic pill" look (just a name with a drop shadow, no panel) by dropping the fill alpha and adding a shadow:

```csharp
var pill = NameplateStyle.Default with
{
    PanelFill = NameplateStyle.Default.PanelFill.WithAlpha(0f),   // no panel
    TitleShadow = Color.Black,                                    // readability without a plate
};
```

Set `MaxWidth` (>0) to cap the panel width; the title is ellipsized (ASCII "...") to fit. The panel size math is
exposed render-free as `NameplateLayout.Measure(font, plate, style)` if you want to place or batch plates yourself.

### Sharded authoritative server (many players / a large world)

For scale, swap `WorldServer` for **`ShardedWorldServer`**: the *same* movement stack run across a
`KhaozEngine.Sharding` `ShardHost` grid of authoritative cells, so the world holds many players / a huge area
without one giant `World`. The **`WorldClient` and `MoveProtocol` are identical** - a client cannot tell it is
talking to a sharded server (a player's `NetId` is stable across cell handoffs, so its replication view +
prediction continue without a respawn; there is no cell concept on the client).

```csharp
using KhaozEngine.NetWorld;
var field = new TerrainField(TerrainPresets.Clearing());
var terrain = new TerrainCollision(field);
var config = new ShardedWorldServerConfig
{
    CellSize = 60f,          // align to the terrain / streaming chunk grid (one chunk per cell here)
    OverlapMargin = 24f,     // border ghost band; MUST be >= InterestRadius
    InterestRadius = 24f,
};
var server = new ShardedWorldServer(transport, config, terrain.GroundHeight, MoveTuning.Default);
server.Scheduler = new ThreadPoolJobScheduler();   // optional: tick cells across cores (deterministic)

// Persistence is identical to the single-World server (player-keyed, cell-agnostic):
var persistence = new WorldPersistence(server, store);

while (running)   // per fixed tick:
{
    server.Poll();                       // join/leave + client input
    server.Tick(config.TickSeconds);     // route input to owning cell -> per-cell movement -> handoff -> ghost sync -> serve home-cell AoI
    persistence.Update(config.TickSeconds);
}
```

Each tick routes every client's `MoveCommand` to the cell that **owns** its player, steps each cell's
`PlayerMovementSystem` (the shared `CharacterMovement.Step`, ground-clamped) via `ShardHost.Tick`, transfers
authority for boundary crossers exactly-once, refreshes border ghosts, then serves each client its single
home-cell area-of-interest (owned + ghosts). Walking across a boundary hands off with no hitch; two players in
adjacent cells see each other via ghosting. `WorldServer` stays the single-`World` option for a modest player
count; both share `WorldPersistence` via `IWorldPersistenceHost`. `MmoServerSample` is the reference dedicated
server built on the multi-cell `ShardedWorldServer` (cellSize 60). Spec:
`docs/superpowers/specs/2026-06-27-multicell-sharding-design.md`.

### Server-side anti-cheat / input-hardening

The authoritative movement model already prevents teleport, speedhack, noclip, wall-climb, token forgery, and
replay (the client sends only an 18-byte `MoveCommand`; the server re-simulates). Three additional hardening
layers ship on both `WorldServer` and `ShardedWorldServer`, all **additive and opt-in** (the wire format is
unchanged; defaults are off):

1. **NaN/Inf rejection (always on, no config).** `MoveProtocol.TryDecodeMove` rejects a move axis or camera yaw
   that is NaN or infinite as a malformed packet, so a poisoned value can never reach the sim (a NaN would slip
   past `CircleBounds.Clamp` - every NaN comparison is false - and replicate to every client in range).
   `CharacterMovement.Step` carries a defense-in-depth finite guard (it holds the last good position rather than
   ever returning a non-finite one).
2. **Per-connection message rate limiting** via `AntiCheatConfig.MaxMessagesPerSecond` (+ `MessageBurst`,
   `DisconnectOnRateLimit`). Over-budget messages are dropped; backed by the deterministic `Netcode.RateLimiter`.
3. **An anomaly signal hook** - `OnSuspiciousActivity` - the engine signals, the game decides policy.

```csharp
var config = new WorldServerConfig
{
    AntiCheat = new AntiCheatConfig
    {
        MaxMessagesPerSecond = 90f,     // ~3x the 30 Hz tick; 0 (default) = unlimited
        MessageBurst = 30f,             // allow a short burst
        MaxCorrectionDistance = 0.25f,  // per-tick "intended move denied" distance that counts as a correction
        CorrectionStreak = 30,          // consecutive corrected ticks before the signal fires; 0/default off
    },
};
var server = new WorldServer(transport, config, terrain.GroundHeight, MoveTuning.Default, bounds: bounds);

server.OnSuspiciousActivity += a =>
{
    // a.Slot, a.Reason (MalformedPacket | RateLimited | MovementCorrection), a.Magnitude (correction distance)
    log.Warn($"suspicious: slot {a.Slot} {a.Reason} ({a.Magnitude:0.00})");
    if (a.Reason == SuspiciousReason.MovementCorrection) server.Disconnect(a.Slot);   // your policy: log / kick / ban
};
```

`MovementCorrection` fires when the authoritative sim has to deny a player's *intended* move (the slope gate,
static collision, or play-area bound pulls them back) by more than `MaxCorrectionDistance` for `CorrectionStreak`
consecutive ticks - a cheat hammering a wall trips it; a legitimate player brushing one does not. It is a
server-side proxy: the authoritative model carries no client position to reconcile against, so the engine measures
how far it had to correct the client's intent (via `CharacterMovement.IntendedHorizontalTarget`). Per-IP
connection-attempt limiting is out of scope: the `INetTransport` seam exposes no remote address.

---

## ECS (`KhaozEngine.Ecs`)

Independent of input/rendering. A struct-based archetype ECS: components are **structs** implementing
`IComponent`. `World` exposes `Spawn()` / `Despawn(e)` (with an `EntityCommandBuffer` via `World.Commands` for
deferred structural changes), component access `Add/Set/Has/TryGet/Remove<T>` plus `ref T Get<T>` (by-ref, no
boxing), iteration via `Query()` and `ForEach<T1..T8>(RefAction<…>)` (plus opt-in data-parallel `ParallelForEach`,
see "Parallel `ForEach` + access declarations" below), parent/child hierarchy
(`SetParent`/`DespawnTree`), per-`World` resources (`SetResource/GetResource<T>`), and systems grouped + ordered
(`AddSystem(ISystem, group)`, `SetGroupOrder`, `Update(float dt)`). `CachedQuery` reuses a query across ticks to
avoid per-tick allocation; `DeterministicRng` (xorshift128+/splitmix64, `CreateDerived(name)` for per-stream
sub-RNGs) gives platform-stable RNG for lockstep sims; `WorldSerializer` round-trips a world as JSON (uses
`KhaozEngine.Serialization.JsonDefaults.IncludeFields`). (`DeterministicRng` lives in
`KhaozEngine.Primitives`, and the ECS uses it for lockstep RNG.)

### `[ComponentId]` and save-format stability (policy)

`WorldSerializer` keys each component in a save by `[ComponentId("...")]` if present, else by `Type.FullName`.
The `FullName` default means renaming or moving a component struct silently breaks every existing save.
`[ComponentId]` pins a stable key so the type can be renamed/moved freely. The dup-key guard rejects two
types resolving to the same key, and `WorldSerializer.RegisterMigration(fromVersion, upgrade)` rewrites older
save documents up to `CurrentFormatVersion` before deserialize.

Rules:

- **New component types SHOULD carry a stable `[ComponentId("...")]` from creation.** It costs nothing up
  front (no saves exist yet) and buys free renames forever. Pick a short, stable string that is not the type
  name (so the key survives a rename), unique within the world's component set.
- **Adding `[ComponentId]` to an already-shipped component is a save-format break, not a free win.** Old saves
  stored that component under its `Type.FullName`; the annotated build looks it up under the new id and the old
  entries vanish. So only annotate a shipped component *together with* a paired migration: bump
  `CurrentFormatVersion` and `RegisterMigration` a hook that renames the old `Type.FullName` key to the new id
  in older documents. Never annotate a shipped component without that migration.
- Practically: annotate freely while a component has no shipped saves; for shipped ones, defer the
  `[ComponentId]` until a rename is actually needed, then land it with its migration in the same change.

---

## Audio (`KhaozEngine.Audio`)

`AudioSystem` over a cross-platform OpenAL (Silk.NET.OpenAL) backend, no MonoGame. Streaming music (WAV/OGG/MP3,
one track via `PlayMode`, `CurrentTrack`/`TrackChanged`), SFX one-shots, and 3D positional audio
(`PlaySfx`/`PlaySfx3D`/`SetListener`, a 16-voice pool, per-channel volume). `LoadContent(directory)` +
`Update()` per frame.

`IsSfxLoaded(name)` reports whether a name resolved to a loaded buffer, and the `PlaySfx`/`PlaySfx3D`
overloads taking an `IReadOnlyList<string>` of candidate keys play the first loaded one in priority order
(returning whether any played). The engine stays convention-agnostic: the game builds the candidate list
(e.g. a per-entity variant then a shared fallback like `towers/railgun/fire` -> `towers/default/fire`);
the engine just plays the first that loaded. An all-unloaded list warns once and is a no-op; null/empty is a
silent no-op.

**Authoring SFX assets (`ke-sfxbake`).** The `KhaozEngine.Sfx.Tool` dotnet tool bulk-generates and bakes a
game's sound effects from a `sfx.manifest.jsonc` (prompt -> ElevenLabs sound-effects API -> ffmpeg/oggenc ->
your asset tree). It defaults to the formats this `AudioSystem` wants: mono OGG Vorbis (mono because OpenAL only
spatializes mono sources - a stereo buffer plays flat and skips `PlaySfx3D` positioning), and forces `wav`
output to the 16-bit PCM 44.1 kHz that `WavDecoder` accepts. It is idempotent (a `.sfxmeta` hash sidecar per
output, skip-unchanged + `--force`) and has a `--dry-run` plan-with-credit-estimate. See the README "Dev tools"
section. The tool only produces the audio files; loading and playing them is the normal `AudioSystem` flow
above. Each game owns its manifest and its play-key wiring.

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

## Diagnostics overlay + telemetry recording (`DiagnosticsOverlay` / `FrameStats` / `TelemetryRecorder` / `WorldClient.NetStats`)

A reusable in-game telemetry HUD for every game: an F1-toggled corner panel that shows whatever rows the
game hands it, a frame-time meter, a client network-stats snapshot, and a crash-safe session recorder. The
widget is content-agnostic - **the game assembles the rows each frame**, so the metric catalog stays
game-owned; the engine ships populators for the common Performance / Network sections.

The four pieces (`FrameStats`, `TelemetryRecorder`, `ClientNetStats` are in `KhaozEngine.Diagnostics`;
`DiagnosticsOverlay` + `DiagnosticsOverlayTheme` in `KhaozEngine.Gui`; `WorldClient.NetStats` in
`KhaozEngine.NetWorld`). All reachable from a `Game3D` + `NetWorld` consumer.

**OnLoad** - build the meter, recorder, and overlay once:

```csharp
_frameStats = new FrameStats();                                   // KhaozEngine.Diagnostics
_recorder   = new TelemetryRecorder();                            // KhaozEngine.Diagnostics
_overlay    = new DiagnosticsOverlay(new DiagnosticsOverlayTheme  // KhaozEngine.Gui
{
    Corner = OverlayCorner.TopLeft,        // anchor; TopRight/BottomLeft/BottomRight also available
    Scale = 0.5f,                          // text scale
    // ToggleKey defaults to Key.F1; set TriggerButton for an optional gamepad toggle.
});
_overlay.Visible = overlayOnByDefault;     // e.g. true for an alpha build, false on release (F1 still works)
```

**OnUpdate(dt)** - sample, assemble sections, drive the toggle:

```csharp
_frameStats.Sample(dt);

// Reuse a buffer to stay allocation-light; the engine populators cover the common cases.
_sections.Clear();
_sections.Add(DiagnosticsOverlay.PerformanceSection(_frameStats));
_sections.Add(DiagnosticsOverlay.NetworkSection(_client?.NetStats ?? default));  // default => "not connected"
_sections.Add(new OverlaySection("World", _worldRows));   // custom rows are always available
_overlay.SetSections(_sections);
_overlay.Update(Input, dt);                // reads the toggle key, advances the fade, returns Visible
```

**OnDraw2D(batch)** - draw it over the scene (under any modal screen):

```csharp
_overlay.Draw(batch, font, white, viewport);   // no-op when hidden / faded out / empty
```

`WorldClient.NetStats` is a read-only `ClientNetStats` snapshot (RTT, packet loss, in/out byte rates, AoI
snapshot rate, and the prediction-reconciliation correction magnitude - last + rolling average; `Connected`
tracks `Joined`). RTT / loss / bytes come from the transport (the LiteNetLib UDP binding fills them; the
in-memory loopback reports zeros); the snapshot rate and correction come from `WorldClient` itself. The byte
and snapshot rates refresh once per ~1s window as you pump `WorldClient.AdvancePresentation(dt)` each frame.
Reading `NetStats` never mutates state. No `WorldClient` ctor or method signature changed to add it.

**Recording.** `TelemetryRecorder` streams **raw numeric channels** (not the overlay's formatted strings) to
a JSON Lines file, so the output is chartable. It flushes after every line, so a crash leaves a valid partial
file. The arm/confirm UX is the game's to build; the recorder is just the mechanism:

```csharp
_recorder.Start(path);                                   // opens <path>, creating parent dirs
// each frame while recording, from the same source data behind the rows:
_recorder.Sample(elapsedSeconds, new[]
{
    new TelemetryChannel("fps", _frameStats.Fps),
    new TelemetryChannel("rttMs", stats.RttMs),
    new TelemetryChannel("correctionM", stats.LastCorrectionMeters),
});
_recorder.Stop();                                        // flush + close (also via Dispose())
```

Each line is one object: `{"t":12.34,"fps":59.7,"rttMs":48,"correctionM":0.02}`. Non-finite values serialize
as JSON `null` so every line stays parseable.

`DiagnosticsOverlay.Update`, `FrameStats`, and `TelemetryRecorder` are headless-testable (no GPU); only `Draw`
needs a `SpriteBatch`. Like `UpdateOverlayView`, the widget never reads raw input - it consumes the immutable
`InputState` snapshot.

---

## Collision-shape debug overlay (`CollisionShapeOverlay` / `OverlayLegend`)

A toggleable translucent, color-coded proxy drawn over each collision static in the live scene, plus a legend
panel naming the colors. Built from two pieces: `CollisionShapeOverlay` (`KhaozEngine.Render3D.Debug`) builds
and draws the proxy meshes, and `OverlayLegend` (`KhaozEngine.Gui`) draws the swatch/label panel. Both are
headless-testable except their `Draw` calls. The 3D World room (`Room3D`) in `KhaozEngine.Showcase` wires this behind an F2 toggle - read
`KhaozEngine.Showcase/Room3D.cs` for the full example.

**The general primitive underneath it: `Scene3D.DrawOverlayMesh(MeshHandle mesh, Matrix4x4 world)`.** Queues a
translucent, unlit, depth-tested-but-not-depth-writing, alpha-blended draw of an already-loaded mesh, colored
by the mesh's per-vertex color. It never writes depth, so it never hides the scene, only sits over it. Nearer
scene geometry still occludes it. Drawn after the meshes/beams and before the pixel post. This is a reusable
overlay pass, not collision-specific: `CollisionShapeOverlay` is the first consumer, and a future nav-mesh or
area-of-interest-bounds overlay is a new type over the same `DrawOverlayMesh` call, not a new render path.

**Build the static list.** The game collects the shapes it wants outlined as a flat
`IReadOnlyList<CollisionStatic>` (`readonly record struct CollisionStatic(PhysicsShape Shape, Pose Pose)`) -
typically the same `PhysicsShape`/`Pose` pairs already registered with `IPhysicsWorld`, or a hand-placed debug
fixture:

```csharp
var statics = new List<CollisionStatic> { new(buildingProxyShape, buildingProxyPose) };
```

**OnLoad** - build the overlay once against the scene:

```csharp
_overlay = new CollisionShapeOverlay();      // KhaozEngine.Render3D.Debug
_overlay.Build(scene, statics);              // uploads one proxy mesh per static; call again if the set changes
_legend = new OverlayLegend();               // KhaozEngine.Gui
_legend.SetEntries(BuildLegendEntries(_overlay));
```

`CollisionShapeMesh.Build(PhysicsShape, CollisionOverlayPalette) -> GltfMesh` is the headless core `Build` uses
internally to turn a shape into a colored local-space mesh (box/sphere/capsule/cylinder/triangle mesh directly,
`ConvexHullShape` via `ConvexHull3D.Triangulate(IReadOnlyList<Vector3>) -> (Vector3[] Vertices, int[] Indices)`,
a dependency-free 3D convex-hull triangulator). `CompoundShape` recurses into its children so a compound
contributes one proxy per child. You will not normally call these directly - `CollisionShapeOverlay.Build`
does it for you - but they are public because a game may want a one-off proxy mesh outside the overlay flow.

**OnUpdate(dt)** - drive the toggle:

```csharp
if (Input.WasPressed(Key.F2)) _overlay.Enabled = !_overlay.Enabled;
```

**OnDraw3D** / **OnDraw2D(batch)** - draw the proxies with the scene, the legend over the HUD:

```csharp
_overlay.Draw(scene);                                              // no-op unless Enabled; in the 3D pass
_legend.Draw(batch, font, white, Viewport.DesignBounds);            // in the 2D pass; anchors to Theme.Corner; no-op when empty
```

**Placing the legend beside another panel (side by side, not overlapping).** By default the legend anchors to
its `Theme.Corner` (top-left), which is the same corner a `DiagnosticsOverlay` uses - so drawn together they
overlap. To sit the legend *directly right of* the diagnostics panel, style it to match with
`OverlayLegendTheme.FromDiagnostics(diagTheme)` and draw it at the diagnostics panel's right edge via its
`Bounds`, using the explicit-position `Draw(..., Vector2 topLeft)` overload:

```csharp
_legend = new OverlayLegend(OverlayLegendTheme.FromDiagnostics(_diagTheme));  // shares the diag panel's look
...
_diagnostics.Draw(batch, font, white, Viewport.DesignBounds);      // top-left panel
if (_overlay.Enabled)
{
    // right of the diag panel while it is up (Bounds is empty when it is hidden), else top-left.
    Vector2 at = _diagnostics.Bounds.Width > 0f
        ? new Vector2(_diagnostics.Bounds.Right + 8f, _diagnostics.Bounds.Y)
        : new Vector2(Viewport.DesignBounds.X + 12f, Viewport.DesignBounds.Y + 12f);
    _legend.Draw(batch, font, white, at);
}
```

**Palette.** `CollisionOverlayPalette` gives each `CollisionShapeKind` (`Box`/`Sphere`/`Capsule`/`Cylinder`/
`ConvexHull`/`TriangleMesh`) a translucent color and a display name: `For(kind)` reads the color, the `this
[kind]` indexer lets a game override it before calling `Build`, `NameFor(kind)` is the display label, and the
static `KindOf(PhysicsShape) -> CollisionShapeKind` classifies a shape. Assign `_overlay.Palette` before
`Build` to customize. A palette change after `Build` has no effect until the next rebuild.

**Legend.** `OverlayLegend` is domain-agnostic, it just draws whatever `LegendEntry` (`readonly record struct
LegendEntry(Color Swatch, string Label)`) rows it is given, so it is reusable by any future overlay, not just
collision shapes. Build the rows from the overlay's `PresentKinds` (the distinct kinds actually present in the
last-built static set, compound children counted individually) so the legend never lists a color that is not
on screen:

```csharp
static IReadOnlyList<LegendEntry> BuildLegendEntries(CollisionShapeOverlay overlay)
{
    var entries = new List<LegendEntry>();
    foreach (var kind in overlay.PresentKinds)
        entries.Add(new LegendEntry(overlay.Palette.For(kind), overlay.Palette.NameFor(kind)));
    return entries;
}
```

`OverlayLegend.Measure(SpriteFont) -> Rect` returns the panel's size at the origin (empty when there are no
entries, so a caller can skip drawing without touching the font) if you need to lay out other UI around it, and
`OverlayLegend.Bounds` is the panel rect of the last `Draw`. The look + layout are injected via
`OverlayLegendTheme` (fill, border, label colour, thickness, padding, swatch size/gap, row spacing, text scale,
and a `Corner`/`Margin` anchor): `OverlayLegendTheme.Default` is the neutral grey palette it shipped with, and
`OverlayLegendTheme.FromDiagnostics(DiagnosticsOverlayTheme)` derives a matching palette (see the side-by-side
snippet above).

Render-only: the overlay reads existing `PhysicsShape`/`Pose` data and draws it. Nothing here feeds back into
simulation, determinism, or `.coll` bakes.

---

## Install / update stamp (`KhaozEngine.App.AppInstallStamp`)

A local record of when the **current app version** first ran on this machine and when it last changed - the
thing an About screen surfaces as "Installed" / "Updated". It is distinct from the build's release date, which
stays a per-game build property read via `BuildMetadata`.

The core is a pure, storage-free resolver. `utcNow` is injected (no hidden `DateTime.UtcNow`), so it is
deterministic and snapshot/headless replay stays stable:

```csharp
public sealed record AppInstallStamp(string Version, DateTime FirstInstalledAtUtc, DateTime UpdatedAtUtc);
public readonly record struct AppInstallStampResult(AppInstallStamp Stamp, bool Changed);

AppInstallStampResult AppInstallStamp.Resolve(AppInstallStamp? previous, string currentVersion, DateTime utcNow);
```

- **First run** (`previous` is null): both dates are set to `utcNow`; `Changed` is true.
- **Same version**: returns `previous` untouched (same reference); `Changed` is false.
- **Different version** (upgrade *or* downgrade): `FirstInstalledAtUtc` is preserved, `Version` + `UpdatedAtUtc`
  are bumped; `Changed` is true. Versions are compared for ordinal inequality only - no semver ordering - so a
  downgrade is treated exactly like an upgrade. (A null `currentVersion` throws.)

**Recommended pattern.** Don't invent a separate file: store the stamp on the game's existing settings/save DTO,
resolve once at boot, and persist only if it changed.

```csharp
public sealed class GameSettings
{
    public AppInstallStamp? Install { get; set; }
    // ... the rest of the game's settings
}
```

With `KhaozEngine.Persistence` (how games like Hardpoint persist), the `StampInstall` extension wires the
resolver to a `SettingsManager<T>` - it resolves against the live settings and saves only when changed:

```csharp
string version = BuildMetadata.Read("Version", "0.0.0", typeof(MyGame).Assembly);
AppInstallStampResult stamp = settingsManager.StampInstall(
    read:  s => s.Install,
    write: (s, v) => s.Install = v,
    currentVersion: version,
    utcNow: DateTime.UtcNow);   // inject a fixed instant in tests / deterministic capture
```

Then the About screen reads `settings.Install` and renders `UpdatedAtUtc.ToLocalTime()`. Without Persistence,
call `AppInstallStamp.Resolve(...)` directly over whatever the game already persists.

---

## Foundation packages (brief)

The renderer-free foundation, one line each (all pure .NET / `System.Numerics`, GPU-free):

- **`KhaozEngine.Primitives`**: the zero-dependency leaf: `Color` (`FromHex`/`ToHex`, `* float`, `Lerp`),
  `DeterministicRng`, `XorRng`, `MathUtil`, `ViewportMath`, `Easing`. The bottom of the dependency graph.
- **`KhaozEngine.App`**: app identity / data paths: `AppDataPaths` (publisher-rooted: `<base>/APKiwi/<game>/`),
  `BuildMetadata`, `ServiceLocator`, and `AppInstallStamp` (local first-ran/updated stamp; see "Install / update
  stamp" below).
- **`KhaozEngine.Persistence`**: crash-safe saves: `AtomicJsonWriter`, `PersistenceQueue` (coalesced async
  writes), `SettingsManager<T>` + `FileSettingsStorage`, `SaveEncoder` (Base64 + HMAC), the `GameStorage`
  facade (paths + queue + settings + encoder), the `SettingsManager<T>.StampInstall(...)` convenience, and
  versioned schema migration via `MigrationChain<T>` (see "Versioned save migrations" below).
- **`KhaozEngine.Content`**: config loading + JSON-schema validation: `ConfigLoader` (disk-then-embedded),
  `JsonSchemaValidator`, build-time schema enforcement via the bundled `Content.Validator` tool.
- **`KhaozEngine.Serialization`**: shared `System.Text.Json` baselines. **JSONC (JSON with `//` / `/* */`
  comments and trailing commas) is the engine standard for hand-authored config, manifests, settings, and
  saves**: the `Jsonc` class is the single read policy every engine JSON load routes through (`Jsonc.Deserialize`
  / `DeserializeFile` / `ParseDocument` / `ParseNode`, or the raw `Jsonc.Options` / `DocumentOptions` /
  `NodeOptions`). `JsonDefaults.TolerantRead` is the same instance under its old name. JSONC is read-time only -
  `System.Text.Json` cannot emit comments, so generated files (settings/saves) are written as plain indented JSON
  via `JsonDefaults.IndentedWrite`, and signed/wire formats (the `Updates` manifest, AOT apply-config) stay
  strict JSON by design. Consumed by Content/Persistence/Ecs.
- **`KhaozEngine.App`** also carries `LocalizationManager` (discover cultures + set the thread culture; absorbed from the retired `KhaozEngine.Localization` in 9.0.0) and, from 9.14.0, `IStringCatalog`/`ResourceStringCatalog` for resolving UI strings by key against the current UI culture over a `ResourceManager`. `Get(key)` returns the key itself when absent (never throws); `Format`/`TryGet` build on it; `LocalizationManager.Catalog` hands out a catalog over the same resources the manager was built with.
- **`KhaozEngine.Platform`**: `Clipboard` (cross-platform text + image, best-effort, never throws). Text get/set
  uses the GLFW provider `AppWindow` registers at startup (the working Windows/Linux/macOS path), so a windowed
  game gets a working text clipboard for free; a windowless/headless tool registers none and has text only on
  macOS (via `NSPasteboard`).
- **`KhaozEngine.Primitives`** also carries `ObjectPool<T>` (O(1) rent/return, swap-removal compaction; absorbed from the retired `KhaozEngine.Pooling` in 9.0.0).
- **`KhaozEngine.Collision`**: deterministic `CircleCollision` + `SpatialHashGrid` (bit-identical for lockstep).
- **`KhaozEngine.Determinism`**: `DeterministicFpScope` - forces a canonical CPU floating-point environment
  for fixed-tick / lockstep sims (see "Deterministic floating point" below).
- **`KhaozEngine.Updates`**: delta auto-update pipeline (SHA256 manifests + diffing, resumable staged downloads,
  cross-platform staged-apply). Feeds either a dynamic API or a server-less static blob (no backend - the
  client reads the full `LatestVersionInfo` straight from `latest-{platform}.json`); both have a ready-to-fill
  publish template. See the package README "Publish + feed layout".
- **`KhaozEngine.Netcode` / `.Abstractions` / `.LiteNetLib`**: transport-free netcode primitives
  (`UnitAxisQuantizer`, `ClientPrediction`, `RemoteCommandQueue`, `BoundedEventQueue<T>`), the zero-dependency
  channel-split contract (`IChannelSplittable<TSelf>` + `NetChannelReliability`), and the LiteNetLib transport
  binding. `BoundedEventQueue<T>` is the defensive hard cap (drop-oldest, keep-newest, `DroppedCount` observable)
  the `NetServer` session inbox and the LiteNetLib transport inboxes use so a stalled or flooded host can't grow
  undrained events without bound; tune it with the optional `maxQueuedEvents` ctor arg (default 10,000) and watch
  `DroppedEventCount`, which stays 0 for a host that drains each poll as contracted.

---

## Versioned save migrations (`MigrationChain<T>`)

`SettingsManager<T>` and `GameStorage` take an optional `MigrationChain<T>` that upgrades an old on-disk
schema to the current one on load, before the `sanitizeOnLoad` clamp pass. Register one stepper per version
instead of branching inside a single sanitize callback:

```csharp
// Type carries an int version field; implement ISchemaVersioned for the zero-config factory.
public sealed class CampaignSaveData : ISchemaVersioned
{
    public int SchemaVersion { get; set; } = 3;   // default = current, so a fresh save no-ops
    // ... fields ...
}

var migrations = MigrationChain.For<CampaignSaveData>()      // or For<T>(getVersion, setVersion) for a plain POCO
    .Step(1, d => { /* v1 -> v2 data change */ return d; })
    .Step(2, d => { /* v2 -> v3 data change */ return d; })
    .Build(currentVersion: 3);

// Settings file:
var mgr = storage.CreateSettingsManager<CampaignSaveData>(sanitizeOnLoad: Clamp, migrations: migrations);
// Or a raw save file:
var save = storage.Load<CampaignSaveData>("campaign.json", migrations);
```

Each `Step` does only the data transform; the chain stamps the version after each step. `Build` throws on a
gap, a duplicate `fromVersion`, or a step at/beyond `currentVersion` (caught at startup). `Migrate` never
throws on a bad save: a save at/above current is left untouched, one older than the oldest step is logged and
returned as-is, and a throwing step halts the chain with the partially-migrated value. The opt-in
`For<T>()` factory is reference-type only; use the `For<T>(getVersion, setVersion)` delegate overload for any
other type.

## Deterministic floating point (`KhaozEngine.Determinism`)

A fixed-tick host sim that must be bit-reproducible (replays, lockstep multiplayer, a determinism
tripwire) has a hidden input most code forgets: the CPU's per-thread floating-point control register
(ARM64 `FPCR`, x86 `MXCSR`). Its rounding mode and flush-to-zero / denormals-are-zero flags are NOT
guaranteed to match across threads, machines, or even process runs, and a native library on the
thread can change them behind your back. Different flags give different low bits, and over thousands
of ticks that compounds into a visibly different result. (This was a real SpaceGame bug: the same
seed + same input gave two different final states across runs on one machine.)

`DeterministicFpScope` pins that register to the IEEE default - round-to-nearest-even, FTZ/DAZ off,
FP traps masked - for the duration of a tick or a sim thread, and restores the prior state after.
It is allocation-free and cheap enough to wrap every tick.

```csharp
using KhaozEngine.Determinism;

// Per tick (or wrap the whole fixed-step run):
using (DeterministicFpScope.Enter())
{
    sim.Tick(dt);
}

// Or set once on a dedicated sim thread, restore on teardown:
var prior = DeterministicFp.SetCanonical();
try   { RunSimLoop(); }
finally { DeterministicFp.Restore(prior); }
```

It is implemented over the platform C library's `<fenv.h>` (no native build asset; pure-managed
P/Invoke), and works on arm64 and x64 (macOS / Linux / Windows). On an unsupported platform
`IsSupported` is `false` and the scope is a safe no-op (it never corrupts FP state) - assert
`DeterministicFp.IsSupported` in a debug build if your game requires the guarantee.

**What it does NOT fix.** The scope controls the FP *register* only. It does not remove
non-determinism that comes from JIT *codegen* differences:

- **Fused multiply-add.** `MathF.FusedMultiplyAdd(a, b, c)` (and any op the JIT contracts into one)
  rounds once; `a * b + c` rounds twice. They give different bits. For sim math that must be
  reproducible, pick one form explicitly and use it everywhere - do not mix.
- **Auto-vectorization / reduction order.** A horizontal sum over a `System.Numerics.Vector<T>` can
  add lanes in a different order than a scalar loop, which changes rounding. Keep state that must be
  bit-identical on a fixed, scalar accumulation order.

Rule of thumb: integers and `DeterministicRng` are already bit-stable; for the float math in a
deterministic tick, wrap it in `DeterministicFpScope`, keep operation order fixed, and avoid fused
or vectorized forms for the values you hash or send over the wire.

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

## Headless snapshots / screenshots (`KhaozEngine.Snapshot`)

For an art/UI screenshot tool, you only want to write the *scenes* - not the capture/encode/write/log boilerplate
around each one. `SnapshotRunner` wraps the existing headless capture helpers (`Render2DSnapshot.Capture` /
`Render3DSnapshot.Capture`) with: capture → PNG-encode → write `<outDir>/<name>.png` → log the path, plus a final
`done -> <dir> (N shots)` summary. Deterministic (no timestamps), window-free; the underlying capture still needs
a GPU device, so a snapshot tool runs on a dev box / GPU CI, not the headless unit-test lane.

```csharp
var runner = new SnapshotRunner("/tmp/shots");        // creates the dir; logger defaults to Console.WriteLine

// 2D: you build your own GuiSurface/theme/scene inside the callback (the Render2DSnapshot context).
runner.Shot2D("menu", 1280, 720, clear: new Color(0.08f, 0.09f, 0.12f, 1f), ctx =>
{
    ctx.Batch.Begin();
    /* draw your UI/sprites here */
    ctx.Batch.End();
});

// 3D: setup runs once (load meshes, frame the camera), drawFrame runs per frame.
runner.Shot3D("boss", 1280, 720,
    setup:     scene => { var h = scene.LoadMesh(MeshPrimitives.Box(1f)); scene.Camera.Frame(Vector3.Zero, new Vector3(3,3,3)); /* ... */ },
    drawFrame: scene => scene.Draw(h, Matrix4x4.Identity, Color.White));

runner.Done();                                          // prints "done -> /tmp/shots (2 shots)"
```

The per-shot boilerplate this replaces (x14 in Hardpoint's tool):

```csharp
// before                                            // after
byte[] rgba = Render3DSnapshot.Capture(W, H, setup, drawFrame);
string path = Path.Combine(outDir, name + ".png");   runner.Shot3D(name, W, H, setup, drawFrame);
PngWriter.Save(path, rgba, W, H);                     // (capture + encode + write + log, one call)
Console.WriteLine(path);
```

- **`SnapshotHost`** makes a tool's `Program.cs` one line: `static int Main(string[] a) => SnapshotHost.Main(a, Register);`
  resolves `outDir` from `args[0]` (a deterministic temp default - `SnapshotHost.DefaultOutDir` - otherwise), runs
  your `register` delegate against a fresh runner, prints the summary, returns exit code 0.
- **`SnapshotRunner.Save(name, rgba, w, h)`** is the shared sink (encode + write + log + bump `Count`) if you
  captured a buffer some other way. `OutDir` and `Count` are exposed.
- **PNG encoder**: `KhaozEngine.Imaging.PngWriter.Save(path, rgba, w, h)` / `.Encode(...)` is a dependency-free,
  BCL-only RGBA8 PNG writer (no ImageSharp). `KhaozEngine.Render2D.Png` is a back-compat shim that forwards to it.

**Packaging / which package to reference.** The 2D core (`KhaozEngine.Snapshot`) deliberately does **not** depend
on Render3D, so a Game2D-only game (SpaceGame, Nullwake) can use `Shot2D` without dragging in the 3D renderer. The
`Shot3D` method is an extension in **`KhaozEngine.Snapshot.Render3D`** (which adds the Render3D dependency). These
are tooling packages and are **not** in the `Game2D`/`Game3D` umbrellas, so a snapshot tool project adds the ref(s)
it needs directly: `KhaozEngine.Snapshot` for 2D, plus `KhaozEngine.Snapshot.Render3D` for 3D. A runnable example
lives in `SnapshotSample` (`dotnet run --project SnapshotSample -- /tmp/ke-snapshot-demo`).

---

## Multiplayer: transport seam + fixed-tick host (`KhaozEngine.Netcode` / `KhaozEngine.Simulation`)

Phase 0 of the authoritative-multiplayer stack (full design:
`docs/superpowers/specs/2026-06-25-mmo-netcode-stack-design.md`). Two pieces a game wires together; both are
headless and deterministic.

**`INetTransport`** is the byte-transport seam - the only thing above it that knows about the wire. You drive
it by pumping then draining:

```csharp
transport.Poll();                               // pump the underlying transport
while (transport.TryDequeueEvent(out NetEvent ev))
{
    switch (ev.Type)
    {
        case NetEventType.Connected:    /* ev.Connection joined */          break;
        case NetEventType.Disconnected: /* ev.Connection left */            break;
        case NetEventType.Data:         Handle(ev.Connection, ev.Data, ev.Reliability); break;
    }
}
transport.Send(target, payload, NetChannelReliability.UnreliableSequenced);
```

Implementations:

- **`LoopbackTransport`** (in `KhaozEngine.Netcode`) - a deterministic, socket-free, thread-free in-memory
  pair for headless tests and single-process local play. `var (server, client) = LoopbackTransport.CreatePair();`
  A `Send` on one surfaces as a `Data` event on the other after that other endpoint `Poll`s; both see the peer
  as connection id 1. This is what netcode tests run on - no real sockets needed.
- **`LiteNetLibServerTransport(port)` / `LiteNetLibClientTransport(host, port)`** (in
  `KhaozEngine.Netcode.LiteNetLib`) - reliable-UDP over LiteNetLib, reusing `ChannelSplitter.ToDeliveryMethod`
  for the reliability mapping. A peer is surfaced as `NetConnectionId` = `peer.Id + 1`.

**`FixedTickHost`** (in `KhaozEngine.Simulation`) decouples the simulation rate from the render/frame rate: feed
it variable elapsed time, it calls your tick callback a whole number of times at a fixed `dt`. Deterministic
(same elapsed-time sequence -> same tick count), with a spiral-of-death guard.

```csharp
var host = new FixedTickHost(tickSeconds: 1f / 30f);   // 30 Hz authoritative tick
// each network pump / frame:
host.Advance(elapsedSeconds, tickIndex =>
{
    var cmd = commandQueue.Dequeue(slot, out _);       // RemoteCommandQueue
    state = simulator.Step(state, cmd, host.TickSeconds);  // ITickSimulator
});
```

This is the server-side spine: drain per-connection commands once per fixed tick and step the sim, with no
window or GPU. When a connection drops, call `commandQueue.Forget(slot)` before that slot is recycled to a new
connection: the queue rejects any seq at or below a slot's high-water mark (anti-replay), and a recycled slot
whose mark is stale would reject the new player's seq-0-onward input and freeze them. (`WorldServer` and
`ShardedWorldServer` do this for you.)

### Worker-pool seam (`IJobScheduler`) + parallel cell ticks

**`IJobScheduler`** (in `KhaozEngine.Simulation`) is the engine's one worker-pool abstraction: `For(int count,
Action<int> body)` runs `count` independent jobs and blocks until all finish. Two implementations ship:
`SingleThreadedJobScheduler` (the default everywhere - runs jobs inline in index order, deterministic and
allocation-free) and `ThreadPoolJobScheduler` (fans them across the BCL thread pool via `Parallel.For`, optional
`maxDegreeOfParallelism`).

A `ShardHost`'s cells are disjoint `World`s, so its `Tick` is embarrassingly parallel. Opt in by assigning a
scheduler; everything else is unchanged, and the parallel result is identical to the single-threaded one:

```csharp
var host = new ShardHost(cellSize: 256f, tickSeconds: 1f / 30f, registry);
host.Scheduler = new ThreadPoolJobScheduler();   // tick cells across cores (default is inline single-threaded)

host.Tick(elapsedSeconds);   // per-cell sim steps now fan across the pool; SyncGhosts/ProcessHandoffs stay sequential
```

Opt-in and default-off: a lockstep / single-player sim simply never sets a scheduler and its single-threaded
determinism is untouched. The cross-cell passes (`SyncGhosts`, `ProcessHandoffs`) mutate neighbouring cells via the
`ICellLink`, so they are not cell-independent and deliberately stay single-threaded. Measure on the headless
`KhaozEngine.Benchmarks` server-tick benchmark (`dotnet run --project KhaozEngine.Benchmarks -c Release`): on a
12-core box it shows ~10x at 1024 cells, and ~1x for a single hot cell (one cell can't be split by the cell axis -
that is the entities axis, parallel `ForEach`, below).

### Parallel `ForEach` + access declarations (`World.ParallelForEach`, `AccessSet`)

The cell axis above can't speed up a single hot cell holding most of the entities. The **entities axis** can:
`World.ParallelForEach<...>` mirrors `ForEach<...>` but partitions each matched archetype's rows across the same
`IJobScheduler`, so one hot system over many entities uses every core. Archetype rows are independent memory, so a
**per-row-pure** action (it touches only the `ref` components handed in for the current entity) is race-free and
order-independent - the result is bit-identical to the sequential `ForEach` no matter how the rows partition. It is
opt-in: the scheduler is a trailing optional arg defaulting to inline, so omitting it is exactly `ForEach`.

```csharp
// Hot integrate over a single big world - fanned across cores. Bit-identical to the ForEach version.
world.ParallelForEach<Position, Velocity>((Entity _, ref Position p, ref Velocity v) =>
{
    p.X += v.X * dt;   // per-row-pure: only THIS entity's components, no cross-entity reads, no structural changes
    p.Y += v.Y * dt;
}, new ThreadPoolJobScheduler());
```

**The per-row-pure contract is enforced (debug guard).** While a parallel section runs, any reentrant world call
from a worker action - a structural change (`Spawn`/`Despawn`/`Set`/`Add`/`Remove`), a component read/write through
the world (`Get`/`TryGet`), or a nested `ForEach`/`ParallelForEach` - throws `ParallelAccessViolationException`,
because it breaks per-row-purity. The guard is on by default (`World.ParallelHazardChecks`, one bool check per world
call); a shipping server may set it `false` for a proven-pure hot loop.

**Need structural changes from a parallel action?** Use the buffered overload: each worker chunk gets its own
`EntityCommandBuffer`, and the buffers replay in row order after the section - thread-safe and deterministic, identical
to a sequential `ForEach` recording into one buffer.

```csharp
world.ParallelForEach<Health>((Entity e, ref Health h, EntityCommandBuffer cmd) =>
{
    if (h.Value <= 0) cmd.Despawn(e);   // recorded now, applied deterministically at the join (never inline)
}, new ThreadPoolJobScheduler());
```

**`AccessSet` - the read/write declaration model.** `Access.Read<T>()` / `Access.Write<T>()` build an immutable
declaration of which components a unit of work reads vs writes; `a.ConflictsWith(b)` is true iff one writes a type the
other touches (write-write or read-write; two readers never conflict). `ParallelForEach`'s own safety is the runtime
guard above, but `AccessSet` is the explicit vocabulary a future system scheduler reuses to decide which systems may
run concurrently.

```csharp
AccessSet move = Access.Write<Position>().Read<Velocity>();
AccessSet ai   = Access.Write<Brain>().Read<Position>();
bool canOverlap = !move.ConflictsWith(ai);   // false: ai reads Position, move writes it
```

Benchmark the entities axis on `KhaozEngine.Benchmarks` (the "entities axis" sweep): a fork/join has a fixed cost, so
trivial per-row work is overhead-bound (parallel < 1x) while a realistic hot system scales toward ~P× - the sweep
prints the crossover so the win is only claimed where it's real.

### Sessions (`NetServer` / `NetClient`)

Above the raw transport, the session layer turns connections into authenticated, slotted players. The server
runs a Hello/Welcome/Reject handshake, authenticates via an `IConnectionAuthenticator`, assigns a player slot, and
raises events. The authenticator decides accept/reject **and**, on accept, returns the **verified subject** the
connection is bound to (the stable account/player identity) - that subject rides the `Joined` event as
`ev.Subject`:

```csharp
bool TryAuthenticate(ReadOnlySpan<byte> token, out string subject, out string rejectReason);
```

Ship `AllowAllAuthenticator` for dev (it accepts everyone and uses the connect token, decoded UTF-8, as the
subject; empty token -> empty subject). For an exposed server, gate on a signed bearer token instead:

```csharp
// Issuer (your account service) mints a short-lived token bound to the player's account id:
byte[] secret = LoadSharedSecret();                                  // same secret on issuer + game server
string token = SignedToken.Mint("acct-42", DateTimeOffset.UtcNow.AddHours(1), secret);
// ...hand `token` to the client; it presents it as its connect token (the NetClient `token` arg).

// Game server verifies it (zero-dependency HMAC-SHA256, expiry-checked, fixed-time compare):
var auth = new HmacTokenAuthenticator(secret, () => DateTimeOffset.UtcNow);
var server = new NetServer(serverTransport, maxPlayers: 64, auth);
server.Poll();
while (server.TryDequeueEvent(out ServerSessionEvent ev))
{
    if (ev.Kind == ServerSessionEventKind.Joined)      Bind(ev.Slot, ev.Subject);   // subject = verified "acct-42"
    else if (ev.Kind == ServerSessionEventKind.Left)   commandQueues.Remove(ev.Slot);
    else /* Data */                                     HandleCommand(ev.Slot, ev.Data, ev.Reliability);
}
server.SendTo(slot, snapshotBytes, NetChannelReliability.UnreliableSequenced);

var client = new NetClient(clientTransport, token);
client.Poll();
while (client.TryDequeueEvent(out ClientSessionEvent ce)) { /* Joined(ce.Slot) / Rejected / Data / Disconnected */ }
```

`SignedToken` is `v1.<subject>.<expUnix>.<base64url-HMACSHA256>` (the subject may not contain `.`); a re-issued
token for the same account carries the same subject, so persistence keyed on the subject survives token rotation.
`WorldServer`/`ShardedWorldServer` take the authenticator as an optional last constructor argument (default
`AllowAllAuthenticator`) and use `ev.Subject` as the persisted `accountId`, falling back to `guest:{slot}` when it
is empty.

Slots are the same small-int key `RemoteCommandQueue` uses, so commands and replication line up.

### Entity replication (`KhaozEngine.Replication`)

Replicate the authoritative ECS `World` to clients. Register each replicated component once (server and client
must agree on type ids), then snapshot on the server and apply on the client:

```csharp
var registry = new ReplicationRegistry();
registry.Register<Position>(1,
    write: (p, bw) => { bw.Write(p.X); bw.Write(p.Y); },
    read:  br => new Position { X = br.ReadSingle(), Y = br.ReadSingle() },
    lerp:  (a, b, t) => Position.Lerp(a, b, t));     // optional -> interpolatable

// Server: entities carrying a NetId are replicated.
byte[] full = SnapshotWriter.Write(serverWorld, registry);

// Client:
var view = new ClientReplicationView(registry);
view.Apply(clientWorld, full);                        // spawn new, despawn gone, update existing
view.Interpolate(clientWorld, renderAlpha);           // smooth interpolatable components between snapshots
```

For bandwidth, use the delta path: `ServerReplicator` keeps per-client acked baselines and sends only what
changed; the client applies deltas and acks the seq it received:

```csharp
var replicator = new ServerReplicator(registry);
int seq = replicator.Capture(serverWorld);            // once per tick
byte[] delta = replicator.WriteFor(slot);             // only changes since this client's baseline
// ...client: view.ApplyDelta(clientWorld, delta); then send view.LastAppliedSeq back ->
replicator.Acknowledge(slot, ackedSeq);
```

### Area of interest (`InterestGrid`)

Send each client only nearby entities. Rebuild the grid each tick from positions, query per client viewpoint,
and write an interest-filtered snapshot; the existing `Apply` spawns entities that entered the client's view
and despawns those that left:

```csharp
grid.Clear();
serverWorld.ForEach<NetId, Position>((e, ref NetId id, ref Position p) => grid.Insert(id.Value, p.X, p.Y));
HashSet<int> interest = grid.Query(viewX, viewY, viewRadius);
byte[] snap = SnapshotWriter.WriteFiltered(serverWorld, registry, interest);
```

### Server-owned NPCs / consumer components (`ShardedWorldServer.SpawnEntity`, `WorldClient.TryGetComponent`)

Replicate a server-spawned NPC / enemy the client can tell apart from a player, and read your own components off
any entity. Register the component ONCE, above the reserved floor, and build both ends from the same registry:

```csharp
struct NpcKind : IComponent { public int Kind; }

// The SAME call on server and client. Consumer ids start at MoveProtocol.FirstConsumerTypeId (16); the movement
// built-ins keep 1..3. Extension components are length-prefixed on the wire, so a client that never registered
// NpcKind simply skips it (no disconnect) - client and server can deploy independently.
ReplicationRegistry Registry() => MoveProtocol.CreateRegistry(r => r.Register<NpcKind>(
    MoveProtocol.FirstConsumerTypeId,
    write: (n, bw) => bw.Write(n.Kind),
    read:  br => new NpcKind { Kind = br.ReadInt32() }));

var server = new ShardedWorldServer(transport, cfg, groundHeight, tuning, registry: Registry());
var client = new WorldClient(clientTransport, groundHeight, tuning, registry: Registry());

// Spawn a server-owned NPC (non-colliding NetId, placed + persisted in its owning cell) and tag it.
int npcId = server.SpawnEntity(x: 120f, z: 40f, (world, e) => world.Set(e, new NpcKind { Kind = 2 }));

// Drive it each tick BEFORE the snapshot pass, so its move reaches clients the same tick.
server.OnBeforeTick += dt =>
{
    if (server.Host.TryGetOwner(npcId, out CellSim cell, out Entity e))
        cell.World.Set(e, new ReplicatedPosition { Value = BrainStep(cell.World, e, dt) });
};

// Client render loop: pick a model per entity.
foreach (EntityRenderState s in client.Snapshot())
{
    if (client.TryGetComponent(s.Id.Value, out NpcKind kind)) DrawNpc(s, kind.Kind);
    else                                                      DrawPlayer(s);   // a player carries no NpcKind
}
```

`WorldServer` exposes the same `SpawnEntity` / `OnBeforeTick` for a single-world (non-sharded) server, and
`WorldClient.TryGetComponent<T>` returns `false` against an older server that never sends `T` (no handshake, no
disconnect). `MmoServerSample` wires the whole seam with a `Creature` kind component.

### Durable state (`KhaozEngine.WorldStore` + backends)

Persist authoritative character/world records through `IWorldStore` (async, keyed `byte[]`, DB-shaped). Use
`InMemoryWorldStore` for tests/dev; for real durability pick a backend package (each pulls its own ADO.NET
provider; the dep-free `KhaozEngine.WorldStore` core stays clean). The `KhaozEngine.Server` umbrella carries
**only** the dep-free core - add the backend `<PackageReference>` you want explicitly, so a
server using one backend or none never pulls the other's provider:

- **`KhaozEngine.WorldStore.Sqlite`** - `SqliteWorldStore` over `Microsoft.Data.Sqlite`. Embedded, zero-infra;
  the dev/test + single-node backend (and what keeps persistence headless-testable).
- **`KhaozEngine.WorldStore.SqlServer`** - `SqlServerWorldStore` over `Microsoft.Data.SqlClient`. The production
  backend (Azure SQL).

Both bootstrap one `world_store(key, data, updated_at)` table on construction, upsert via dialect SQL (SQLite
`ON CONFLICT`, SQL Server `MERGE WITH (HOLDLOCK)`), raw parameterized async ADO.NET, no EF/ORM. The same
contract, so dev and prod differ only in which line you construct:

```csharp
using KhaozEngine.WorldStore.Sqlite;
IWorldStore store = new SqliteWorldStore("Data Source=world.db");   // dev/test + single-node
await store.SaveAsync($"player:{accountId}", bytes);
byte[]? loaded = await store.LoadAsync($"player:{accountId}");
```

### Persisting players so the world survives a restart (`WorldPersistence`)

`KhaozEngine.NetWorld.WorldPersistence` wires an `IWorldStore` into the `WorldServer` lifecycle so the
authoritative world survives a restart. It is backend-agnostic (only `IWorldStore` + `KhaozEngine.Serialization`):
**load-on-join** (spawn at the saved position, or the default if absent), **save-on-leave**, and a **periodic
snapshot** of players whose state changed since their last save. Players are keyed `player:{accountId}`, where
the `accountId` is the **verified subject** the `IConnectionAuthenticator` bound the connection to (with
`AllowAllAuthenticator` that is the connect token decoded UTF-8; with `HmacTokenAuthenticator` it is the
`SignedToken` subject, stable across token re-issue), or `guest:{slot}` when the subject is empty. The
record is a forward-tolerant JSON `PlayerRecord` (adding fields later never breaks an old save).

```csharp
using KhaozEngine.NetWorld;
using KhaozEngine.WorldStore.Sqlite;

var server = new WorldServer(transport, config, groundHeight, MoveTuning.Default);
using var store = new SqliteWorldStore("Data Source=world.db");
var persistence = new WorldPersistence(server, store,
    new WorldPersistenceConfig { SaveIntervalSeconds = 10f });

// per server frame:
server.Poll();
server.Tick(dt);
persistence.Update(dt);     // applies any load-on-join state (on this thread) + runs the periodic snapshot
// on shutdown (e.g. Ctrl+C):
await persistence.FlushAsync();
```

The client must present a **stable** account token for reconnect/restart to restore the same player. With the
dev `AllowAllAuthenticator` the token *is* the account id; with `HmacTokenAuthenticator` the client presents a
minted `SignedToken` whose subject is the account id (`SignedToken.Mint(accountId, expiry, secret)`), and the
server keys on that verified subject either way:

```csharp
var client = new WorldClient(transport, groundHeight, MoveTuning.Default,
    token: System.Text.Encoding.UTF8.GetBytes(accountId));   // dev: raw id; prod: SignedToken.Mint(...) bytes
```

**Azure SQL (Ruinborne).** For production, swap the backend - same `IWorldStore`, so `WorldPersistence` is
unchanged:

```csharp
using KhaozEngine.WorldStore.SqlServer;
using var store = new SqlServerWorldStore(
    "Server=tcp:<srv>.database.windows.net,1433;Database=<db>;Authentication=Active Directory Default;Encrypt=True;");
```

Out of scope here (later sub-projects): record-schema migrations and accounts/auth.

### Per-cell world persistence (`CellPersistence`)

`KhaozEngine.NetWorld.CellPersistence` wires an `IWorldStore` into a `ShardHost`-based server (through
`ICellPersistenceHost`, which `ShardedWorldServer` implements) so a cell's authoritative non-player entities
survive a restart, next to `WorldPersistence` handling players. It is keyed by cell coordinate rather than
account: **lazy load-on-cell-create** (a cell's saved state loads the first time that coordinate is
instantiated), a **periodic dirty save** of cells changed since their last save, and a **NetId high-water
record** so restored entities never collide with a freshly spawned one after a restart. Players are excluded
(they persist separately, player-keyed, through `WorldPersistence`), and so are ghosts and migrating entities -
only a cell's owned, non-player state is saved.

```csharp
using KhaozEngine.NetWorld;
using KhaozEngine.WorldStore.Sqlite;

var server = new ShardedWorldServer(transport, config, terrain.GroundHeight, MoveTuning.Default);
using var store = new SqliteWorldStore("Data Source=world.db");
var cellPersistence = new CellPersistence(server, store,
    new CellPersistenceConfig { SaveIntervalSeconds = 30f });

// at boot, before the first tick:
await cellPersistence.LoadMetaAsync();   // resumes the NetId allocator above the saved high-water mark
await cellPersistence.PreloadAsync();    // instantiates every saved cell so its load path runs

// per fixed tick:
server.Poll();
server.Tick(config.TickSeconds);
cellPersistence.Update(config.TickSeconds);   // applies completed cell loads + runs the periodic dirty save

// on shutdown:
await cellPersistence.FlushAsync();   // quiesces in-flight loads/saves, then a final dirty + meta save
```

A game supplies the `ICellPersistenceHost` its server implements: `ShardedWorldServer` already does, so most
consumers wire `CellPersistence` straight onto it. A custom `ShardHost`-based server implements the same seam
over `CellSim.SnapshotOwned`/`RestoreOwned` and `ShardHost.CellCreated`/`EnsureCell` (see
[`KhaozEngine.Sharding`](../KhaozEngine.Sharding)). Cell records are keyed `cell:{x}:{y}`, distinct from the
`player:{accountId}` keyspace `WorldPersistence` uses, so the two coexist on the same `IWorldStore` without
collision.

### Reconnect + server notices (`KhaozEngine.NetWorld`)

**Auto-reconnect client.** Use the factory ctor so `WorldClient` owns the transport lifecycle and reconnects automatically on drop:

```csharp
using var client = new WorldClient(
    connect: () => new LiteNetLibClientTransport("127.0.0.1", 9000),
    groundHeight: terrain.SampleHeight,
    tuning: MoveTuning.Default,
    token: myAccountTokenBytes);     // stable identity - used on every reconnect attempt

client.ConnectionStateChanged += state =>
{
    if (state == WorldConnectionState.Reconnecting)
        ShowReconnectingUI(client.ReconnectAttempt, client.SecondsUntilNextRetry);
    else if (state == WorldConnectionState.Connected)
        HideReconnectingUI();
    else if (state == WorldConnectionState.Disconnected)
        ShowDisconnectedUI(client.DisconnectReason, client.DisconnectReasonDetail);
};
```

Pass real frame dt to `Poll` so the timeout and reconnect timers advance:

```csharp
// per frame:
client.Poll(dt);          // dt = 0 pumps the net only (no timeout/reconnect); pass real frame seconds normally
client.AdvancePresentation(dt);
EntityRenderState[] snapshot = client.Snapshot();
```

`ConnectionState` (a `WorldConnectionState`) is one of: `Connecting` (initial handshake), `Connected` (in-session), `Reconnecting` (between drop and re-join), `Disconnected` (terminal - bad token or explicit give-up). `DisconnectReason` values: `None`, `RejectedToken`, `Unreachable`, `ServerShutdown`, `Timeout`, `IncompatibleVersion` (the client is out of date - see "Version skew resilience" below). The single-transport ctor `WorldClient(INetTransport, ...)` is unchanged (no reconnect, `IDisposable` is a no-op).

**Version skew resilience.** Two opt-in backstops so a client running an older build than the server never hard-crashes on a snapshot it cannot decode (and ideally never connects on a stale build at all). They compose with the existing token/auth flow.

1. *Update before connecting (`KhaozEngine.Updates`).* Run the composed startup gate ONCE, before opening the transport, so an out-of-date client self-heals:

```csharp
UpdateGateResult gate = await updates.EnsureUpToDateAsync(
    progress: new Progress<UpdateGateProgress>(p => ShowUpdateScreen(p.Phase, p.BytesDownloaded, p.TotalBytes)),
    checkTimeout: TimeSpan.FromSeconds(10));   // bounded: a down feed won't stall startup

switch (gate.Outcome)
{
    case UpdateGateOutcome.UpToDate:        break;                 // proceed to connect
    case UpdateGateOutcome.Updating:        return;                // applying + relaunching; process is exiting
    case UpdateGateOutcome.FeedUnreachable: break;                 // feed down/slow: proceed, handshake is the backstop
    case UpdateGateOutcome.Failed:          break;                 // couldn't update: proceed, handshake is the backstop
}
```

If a newer signed build exists the gate downloads, verifies, applies, and relaunches into it (the process exits - it does not return). A down/slow feed or a failed apply is non-fatal: the caller continues on the current build and relies on the handshake below.

2. *Connect-time version handshake (`KhaozEngine.NetWorld`).* The backstop for when the gate could not run (feed down, dev build, server upgraded mid-session). The client declares its protocol/build version and the server rejects an incompatible one cleanly, before any snapshot is sent:

```csharp
// client: declare the version (wraps the connect token; null = no handshake, byte-identical to before)
var client = new WorldClient(transport, terrain.SampleHeight, MoveTuning.Default,
    new WorldClientConfig { ProtocolVersion = MyGame.ProtocolVersion }, token: myAccountTokenBytes);

// server: gate on a consumer rule BEFORE the real auth check (compose with any IConnectionAuthenticator)
var server = new WorldServer(transport, config, terrain.SampleHeight, MoveTuning.Default,
    authenticator: new VersionCheckingAuthenticator(
        serverVersion: MyGame.ProtocolVersion,
        isCompatible: v => v == MyGame.ProtocolVersion,    // or a min-version / range rule
        inner: new HmacTokenAuthenticator(secret, () => DateTimeOffset.UtcNow)));
```

A mismatch surfaces on the client as `DisconnectReason.IncompatibleVersion` with the server's required version in `DisconnectReasonDetail`; the client never proceeds to receive snapshots. A version-less client (one that did not set `ProtocolVersion`, e.g. an old build) presents version `""`, so the rule can reject it too. On a compatible version the inner token is delegated to the inner authenticator unchanged.

3. *Graceful decode (last resort).* Even if both above are bypassed, an undecodable snapshot (an unregistered BUILT-IN component type id from a newer core protocol) becomes a clean `DisconnectReason.IncompatibleVersion` disconnect plus a `SnapshotDecodeFailed` event - never an unhandled exception in your frame loop. (An unregistered consumer *extension* id, at/above `ReplicationRegistry.FirstExtensionTypeId`, is skipped instead, so a newer server's added component never disconnects an older client - see the server-owned NPCs section above.)

```csharp
client.SnapshotDecodeFailed += err => ShowOutOfDateUI(err);   // "client out of date, please update"
```

**Server notices.** Broadcast a typed notice to every connected client:

```csharp
// on the server (WorldServer or ShardedWorldServer):
server.BroadcastNotice(new ServerNotice(
    ServerNoticeKind.Maintenance,
    message: "Server restarting in 60 seconds",
    secondsUntil: 60f));

// on the client:
client.NoticeReceived += notice =>
    ShowNotice(notice.Kind, notice.Message, notice.SecondsUntil);
// or poll the latest:
if (client.LastNotice is { } n) ShowBanner(n.Message);
```

`ServerNoticeKind` values: `Custom`, `Maintenance`, `Shutdown`.

**Graceful drain (server shutdown).** Call `BeginDrain`, tick until complete, flush persistence, then dispose:

```csharp
// signal all clients (broadcasts the notice, starts the tick-driven countdown):
server.BeginDrain(new ServerNotice(ServerNoticeKind.Shutdown, "Server shutting down", secondsUntil: 5f),
    graceSeconds: 5f);

// in the server loop (or a short spin after the loop):
while (!server.IsDrainComplete)
{
    server.Poll();
    server.Tick(dt);
    persistence.Update(dt);
}
await persistence.FlushAsync();
// dispose server / transport
```

`IsDraining` is true once `BeginDrain` has been called; `IsDrainComplete` flips when the grace period has elapsed and all clients have been disconnected. The drain is tick-driven (no wall-clock sleep); the host controls how fast ticks run.

### Server administration (`ServerAdmin` / `IBanStore` / `IEnumerableWorldStore` / `KhaozEngine.Server.Admin`)

A generic, opt-in admin surface for a live server. Nothing changes for a server that does not use it.

**Live commands.** Both `WorldServer` and `ShardedWorldServer` implement `IAdminControllable`:
`ListOnline()` returns the connected players (slot, account id, display name, position, grounded, vertical velocity,
net id) from a snapshot published once per tick; `Teleport(PlayerRef, Vector3)`, `Kick(PlayerRef, reason)`, and
`Broadcast(text)` are queued and applied on the host thread between ticks, so you can call them safely from another
thread (an HTTP handler). Target a player by `PlayerRef.Slot(n)` or `PlayerRef.Account("...")`.

**Bans.** `IBanStore` is consulted at connect (alongside the authenticator): a banned account is rejected before it
spawns. `InMemoryBanStore` is the default; `WorldStoreBanStore` persists over any `IWorldStore` keyspace
(`ban:{accountId}`) and caches in memory so the connect check stays synchronous (call `LoadAsync()` once at startup
to hydrate from the store). Pass it as the trailing `banStore:` ctor arg on either server. Bans key on the verified
account id; guests are not bannable.

**Account enumeration.** Stores opt into `IEnumerableWorldStore` (`InMemoryWorldStore`, `SqliteWorldStore`,
`SqlServerWorldStore` all do): `EnumerateAsync(keyPrefix?)` streams `WorldStoreEntry { Key, UpdatedAt, Size? }`.
Feature-detect with `store is IEnumerableWorldStore`.

**Facade.** `ServerAdmin(IAdminControllable server, IBanStore? bans = null, IEnumerableWorldStore? accounts = null)`
composes the three: `BanAsync` persists then kicks if the account is online; `ListAccountsAsync(prefix)` materializes
the enumeration; unwired capabilities throw `NotSupportedException` (feature-detect via `BansSupported` /
`AccountsSupported`).

**HTTPS endpoint (`KhaozEngine.Server.Admin`).** An opt-in package (the only one that pulls ASP.NET Core, via a
`FrameworkReference`; not in the `Server` umbrella - add it explicitly). It hosts a minimal Kestrel REST API over a
`ServerAdmin`, TLS + a single bearer token:

```csharp
var admin = new ServerAdmin(worldServer, new WorldStoreBanStore(store), store);
var endpoint = new AdminHttpServer(admin, new AdminEndpointOptions
{
    Port = 9443,
    BearerToken = "<long-random-secret>",
    Certificate = AdminTlsCertificate.CreateSelfSigned("my-game-admin"),   // pin its thumbprint in your console
});
await endpoint.StartAsync();
// ... run the server loop ...
await endpoint.StopAsync();
```

Routes (all under `/admin`, all require `Authorization: Bearer <token>`): `GET /online`, `POST /teleport`,
`POST /kick`, `POST /broadcast`, `GET /accounts?prefix=`, `GET /bans`, `POST /ban`, `POST /unban`. Mutations return
202; capabilities not wired return 501. Bind defaults to loopback. There are no changes to the game client wire
protocol.

### Client self-rescue / unstuck

Let a normal game client ask the authoritative server to teleport **itself** to a server-decided safe spot (a
"return to spawn" / "unstuck", e.g. a `T` key). The server is authoritative, so a client-side position overwrite
would reconcile away within ~1 RTT - the move has to happen on the server. Opt-in, off by default, additive (no
wire-protocol break).

```csharp
// Server: hand the feature a destination provider (null = off). A fixed point is just _ => point.
var config = new WorldServerConfig            // same knobs on ShardedWorldServerConfig
{
    TickSeconds = 1f / 30f,
    SelfRescueDestination = _ => new Vector3(spawn.X, collision.GroundHeight(spawn.X, spawn.Z) + 0.9f, spawn.Z),
    SelfRescueCooldownSeconds = 5f,           // per-player; spam inside the window is dropped
};

// Client: fire-and-forget. Returns false (sends nothing) if not connected.
if (Input.WasPressed(Key.T)) client.RequestSelfRescue();
```

The client never names the destination (that would be a teleport-anywhere cheat); the **server** computes it from
the `PlayerRef`. The request reuses the admin `Teleport` apply path (position set, vertical velocity zeroed), so it
reconciles to the client exactly like an admin teleport. Both `WorldServer` and `ShardedWorldServer` handle it
identically (the sharded one teleports across cells). Under the hood it rides a short control frame on the existing
client->server channel (`MoveProtocol.ClientControlKind`), distinct by length from a move, so a server that predates
the feature just ignores it.

#### Reconnect input backlog

A client that holds the movement key through a long auto-reconnect outage used to freeze/vibrate on rejoin: input
sent while disconnected inflated the prediction sequence and the server replayed the stale backlog one move per
tick. Two engine-side guards, both on by default, fix it with no protocol-version break:

- `WorldClient.SendInput` is a no-op (returns `-1`, predicts/sends nothing) unless `ConnectionState == Connected`,
  so a per-frame send loop accrues no backlog during the outage. No game-side guard needed.
- `WorldServerConfig.MaxInputBacklog` / `ShardedWorldServerConfig.MaxInputBacklog` (default 8 ticks; 0 disables)
  caps how far behind live the server falls: when a player's queued moves exceed it the server skips the stale ones
  and applies the most recent (movement is latest-wins), so a flush/lag-burst can't drive minutes of old input.

### World cell grid (`KhaozEngine.Sharding`)

Partition a seamless world into a uniform grid of authoritative **cells** and run them in one process. A
`ShardHost` owns the `CellCoord -> CellSim` map, creates cells on demand, routes a world position (and the
entities spawned there) to the cell that contains it, and ticks every cell at one shared fixed rate. Each
`CellSim` bundles an ECS `World` + a `FixedTickHost` + a `ServerReplicator` + an `InterestGrid`:

```csharp
var registry = new ReplicationRegistry();
// register replicated components ...
var host = new ShardHost(cellSize: 256f, tickSeconds: 1f / 30f, registry);

// Spawn into the cell that owns a position (the cell is created on first touch):
Entity e = host.SpawnAt(worldX, worldY, out CellSim cell);
cell.World.Set(e, new NetId(nextId++));
cell.World.Set(e, new Position { X = worldX, Y = worldY });

// One host tick advances every cell's fixed-tick sim (cells step their ECS systems per tick):
host.Tick(elapsedSeconds);

// Per cell, capture/query when you choose (snapshot rate is decoupled from tick rate):
foreach (CellSim c in host.Cells)
    c.Replicator.Capture(c.World);
```

`CellCoord.FromWorld(x, y, cellSize)` floors a position into its cell (same math as `InterestGrid`).

**Border ghosting (Phase 3B).** Give the host an `overlapMargin` and a `CellPositionAccessor`, then call
`SyncGhosts()`: owned entities within the margin of a cell edge are mirrored into the neighbor across that edge
(corners mirror into the two edge neighbors plus the diagonal) as read-only **ghosts**, so a cell's systems can
see across borders for collision / visibility / targeting. Mirroring runs over the `ICellLink` seam (in-process
`InProcessCellLink` shipped; a network link is infra) using the Replication codecs:

```csharp
// positionAccessor reads the game's own position component:
static bool PosOf(World w, Entity e, out float x, out float y)
{
    if (w.TryGet(e, out Position p)) { x = p.X; y = p.Y; return true; }
    x = y = 0f; return false;
}
var host = new ShardHost(cellSize: 256f, tickSeconds: 1f / 30f, registry,
    interestCellSize: 256f, overlapMargin: 32f, positionAccessor: PosOf);

host.SyncGhosts();   // mirror border entities to existing neighbor cells

// In a neighbor, mirrored entities carry a Ghost component (with the owner CellCoord):
host.TryGetCell(new CellCoord(1, 0), out CellSim east);
if (east.TryGetGhost(netId, out Entity ghost)) { /* read ghost.Get<Position>(), do not mutate */ }
```

Ghosts are tagged with `Ghost { Source }` and are read-only - the owner cell stays the sole simulator, so game
systems must exclude `Ghost`-tagged entities from authoritative mutation. `SyncGhosts` only mirrors to cells that
already exist (it never creates a neighbor), re-mirrors each sync (a moved owner updates its ghost; one that
leaves the border is despawned), and assumes globally-unique `NetId`s across cells.

**Authority handoff (Phase 3C).** When an owned entity's position crosses into another cell, `ProcessHandoffs()`
transfers authority with **exactly-once** semantics (never two owners, never zero):

```csharp
host.ProcessHandoffs();   // after Tick(); typically Tick -> ProcessHandoffs -> SyncGhosts each host step

// who owns it now (and the owned entity):
if (host.TryGetOwner(netId, out CellSim owner, out Entity owned)) { /* ... */ }
int owners = host.OwnerCount(netId);   // == 1 for a live entity at every call boundary
```

The owner captures the entity's full registered component set, sends a `Migrate` over `ICellLink`, and freezes it
(`Migrating`, not simulated, not counted as an owner); the destination adopts it as a new owned entity (despawning
any prior ghost of it) and acks; the owner releases it. The entity keeps its `NetId` across the move, and the
destination cell is created if needed. The in-process link completes the whole handshake within the call (so the
exactly-once invariant holds at every call boundary); a networked link would keep the entity `Migrating` on the
source until the ack arrives - never double-simulated, never permanently lost. Game systems must treat
`Migrating` entities as frozen, like `Ghost`s.

**Client home-cell serving (Phase 3D).** Each client is served its entire area-of-interest from a single **home
cell** - the cell that owns its player. Because of the invariant **overlap margin >= interest radius**, that cell
already holds (as border ghosts) everything within the player's interest, so no client-side multi-cell
aggregation is needed:

```csharp
host.BindClient(slot, playerNetId);                // bind a session slot to its player entity
// each serve cycle (after Tick -> ProcessHandoffs -> SyncGhosts):
byte[] aoi = host.SnapshotForClient(slot, interestRadius: 32f);   // owned + ghosts within interest, from the home cell
// ship `aoi` over your NetServer session; the client applies it with ClientReplicationView
host.TryGetHomeCell(slot, out CellSim home);       // the cell currently serving this client
```

`SnapshotForClient` throws if `interestRadius > OverlapMargin` (the home-cell guarantee would break) or if the
player is not currently owned. When the player crosses a boundary, `ProcessHandoffs` moves ownership and the home
cell **re-binds automatically** (it is derived from the current owner); the new home cell already held the
player's surroundings as ghosts, so the client's view is continuous across the crossing (nothing in-interest
disappears then reappears).

**Reference dedicated server (Phase 3E).** `MmoServerSample` wires the whole stack into a runnable headless
server: a multi-cell `ShardHost` driven over the `NetServer` session layer (any `INetTransport` - LiteNetLib in
production, `LoopbackTransport` in tests), per-client home-cell AoI serving, `RemoteCommandQueue` input, and
`IWorldStore` persistence, all on a `FixedTickHost`. Its `MmoServer` class is transport-injected: `Poll()` ingests
join/leave + client input, `Tick(dt)` steps one authoritative frame (apply input -> `ProcessHandoffs` ->
`SyncGhosts` -> serve each client). `dotnet run --project MmoServerSample` boots it on a UDP socket; a thin client
connects and walks across cell boundaries. The `ICellLink` seam is finalized with the in-process impl shipped and
a documented network-impl contract (route by target `CellCoord`, kind-scoped FIFO `Drain`, reliable delivery) for
an infra implementation to drop in.

---

## Versioning & change process

- **One shared version** across the whole engine: `<KhaozEngineVersion>` in `Directory.Build.props`. Every
  packable project sets `<Version>$(KhaozEngineVersion)</Version>`; bumping it releases all packages together.
  `scripts/check-doc-versions.sh` (run in CI) enforces that the engine-version declarations in `CONSUMERS.md`,
  `ROADMAP.md`, and the `README.md` `<PackageReference>` example match.
- SemVer: additive = minor, fixes = patch, breaking = major. Local file-feed for inner-loop dev; GitHub Packages
  on `v*` tags.
- To change the library: edit, add a headless test, `dotnet pack -c Release -o ./local-feed`, consume locally;
  when stable, bump the version + add a `CHANGELOG.md` entry + tag for a published release. Each game adopts on
  its own schedule by bumping its pinned version (or its umbrella metapackage version).
