# KhaozEngine

Shared, game-agnostic **input + screen-stack** foundation for MonoGame games. One implementation, used by three games (Hardpoint, Nullwake, SpaceGame), so a fix written once — the click-through fix in particular — propagates to all of them.

KhaozEngine is **not** a full engine. It owns a set of focused, game-agnostic concerns, each a separate NuGet package, and nothing game-specific. The MonoGame-facing packages do input/UI/rendering; the rest are pure .NET so a game can pull in just what it needs:

| Package | What it gives you | Depends on |
|---|---|---|
| **KhaozEngine.Input** | A unified pointer (mouse+touch), edge detection, the `IsTapIn` press-origin invariant, per-frame region blocking, drag/scroll/pinch, keyboard + gamepad + menu-navigation, and a coordinate-transform seam — all behind a testable `IRawInput` seam. | MonoGame |
| **KhaozEngine.Screens** | A screen stack routed top-to-bottom with `receivesInput` / `PassUpdateThrough` / `AlwaysReceivesInput`, two consumption policies, and screen transitions. | KhaozEngine.Input |
| **KhaozEngine.UI** | A widget library (Button, Slider, Dropdown, ScrollablePanel, TextInput, Toggle, Tooltip, …) and a `TextInputHandler`. (Rendering primitives moved to KhaozEngine.Graphics in 4.0.0.) | KhaozEngine.Input, KhaozEngine.Graphics |
| **KhaozEngine.Ecs** | A struct-based archetype `World` / `Entity` / `ISystem` ECS: by-ref component access, `ForEach`, command buffer, system groups, `CachedQuery`, `DeterministicRng`, `WorldSerializer`. | MonoGame, KhaozEngine.Serialization |
| **KhaozEngine.Time** | `GameClock`: real vs scaled delta, slow-mo / fast-forward, pause/resume, plus `TimeSkip`. | MonoGame |
| **KhaozEngine.Content** | Config/content loading (embedded or disk JSON) with JSON-schema validation. | KhaozEngine.Serialization (+ JsonSchema.Net) |
| **KhaozEngine.Content.Validator** | Build-time JSON-schema enforcement for content. | KhaozEngine.Content |
| **KhaozEngine.Diagnostics** | Logging service: levels, pluggable sinks (file / console / debug / in-memory), category loggers, a static `Log` facade over an injectable `LogManager`, and crash hooks. | Pure .NET |
| **KhaozEngine.App** | App/runtime helpers: `BuildMetadata` (read `AssemblyMetadata` at runtime), `AppDataPaths` (OS-correct per-app data dir), `ServiceLocator` (generic `IServiceProvider`). | Pure .NET |
| **KhaozEngine.Localization** | `LocalizationManager`: discover satellite-resource cultures and set the current thread culture. | Pure .NET |
| **KhaozEngine.Persistence** | `SaveEncoder` (Base64 + HMAC-SHA256 tamper-deterrent), `AtomicJsonWriter` + `PersistenceQueue` (crash-safe atomic writes, per-path coalescing), `SettingsManager<T>` (typed settings storage). | KhaozEngine.Diagnostics, KhaozEngine.App, KhaozEngine.Serialization |
| **KhaozEngine.Audio** | `AudioSystem` music player over a pluggable `IMusicBackend`, including a macOS AVAudioPlayer backend that works around MonoGame's broken `Song` playback. | MonoGame, KhaozEngine.Diagnostics |
| **KhaozEngine.Effects** | Data-driven pooled `ParticleSystem` with config-record presets (`Spark`/`Ember`). First resident of a generic visual-effects package. | MonoGame, KhaozEngine.Graphics |
| **KhaozEngine.Graphics** | `Camera2D` (generic 2D matrix camera: position/zoom/rotation → view matrix, world↔screen, bounds clamp), the `CameraController`/`CameraFollow` feel layer, `DisplayManager`, and the rendering primitives `PrimitiveRenderer` + `ColorHelper`. | MonoGame, KhaozEngine.Input |
| **KhaozEngine.Sprites** | 2D sprite + directional animation: `SpriteSheet`, `SpriteAnimationPlayer`, `DirectionalAnimatedSprite`, `Direction8`, `SpriteRegistry`, `PixelLabSpriteLoader`. | MonoGame |
| **KhaozEngine.Serialization** | Shared `System.Text.Json` defaults (`JsonDefaults`: tolerant-read / indented-write / include-fields) so Content, Persistence, and Ecs serialize consistently. | Pure .NET |

Target framework `net10.0`, consumable from the `net10.0-android` / `net10.0-ios` heads. Built against MonoGame.Framework.DesktopGL 3.8.

## Why it exists

The input + screen code was built clean inside Hardpoint, modelled on Nullwake's mature, shipping system, with one improvement: the raw hardware read sits behind an interface so the whole input + routing surface is **unit-testable without a device**. Once there was a second and third real consumer, it was extracted here as a Rule-of-Three extraction. The headline payoff: the **click-through fix** (a tap only registers when press-origin and release are in the same target, and overlays reserve their footprint so clicks never leak to the layer beneath) now lives in one place.

## The one rule that matters most

> **`MonoGameRawInput` is the only code in the entire stack that touches `Mouse`/`Keyboard`/`GamePad`/`TouchPanel`.** Everything above it reads an immutable `RawInputState` snapshot through the `IRawInput` seam. Games must not poll the MonoGame input statics directly — doing so re-introduces the untestable, click-through-leaking pattern this library exists to kill.

Full consumer contract: [`docs/USING-KHAOZENGINE.md`](docs/USING-KHAOZENGINE.md). Read it before wiring a game in.
All docs are indexed in [`docs/INDEX.md`](docs/INDEX.md) (living docs vs the dated design archive).

## Quickstart (the canonical game-loop wiring)

```csharp
using KhaozEngine.Input;
using KhaozEngine.Screens;

// LoadContent: create once.
_rawInput = new MonoGameRawInput(Window);                 // the ONLY statics-toucher
_input    = new InputManager(isMobile: IsMobile);          // pass a coordinate transform if you scale
_screens  = new ScreenManager(_input) { ExitRequested = Exit };
_screens.GraphicsDevice = GraphicsDevice;
_screens.SpriteBatch    = _spriteBatch;
_screens.Add(new MyFirstScreen());

// Update: input first, then screens.
_input.Update(_rawInput.Read(), IsActive);                 // IsActive suppresses ghost taps on refocus
_screens.Update(gameTime);

// Draw: bottom-to-top.
_screens.Draw(gameTime, _spriteBatch);
```

A screen:

```csharp
public sealed class MyFirstScreen : GameScreen
{
    private static readonly Rectangle Button = new(300, 200, 200, 40);

    public override bool Update(GameTime gameTime, bool receivesInput)
    {
        if (!receivesInput) return false;
        if (Manager.Input.IsTapIn(Button))      // press-origin invariant = click-through-safe
            Manager.RequestExit();
        return true;                            // "I consumed input this frame"
    }

    public override void Draw(GameTime gameTime, SpriteBatch spriteBatch) { /* ... */ }
}
```

## Consuming the packages

Published to a private GitHub Packages feed on tagged releases, and packed to a local file-feed for day-to-day development.

```xml
<!-- nuget.config (additive) -->
<add key="khaozengine-local" value="/Users/antonio/KhaozEngine/local-feed" />
<!-- or the GitHub Packages feed: https://nuget.pkg.github.com/APKiwi/index.json -->
```
```xml
<!-- All packages share one version (the current release). Reference only what you use. -->
<PackageReference Include="KhaozEngine.Input"   Version="4.2.0" />
<PackageReference Include="KhaozEngine.Screens" Version="4.2.0" />
<PackageReference Include="KhaozEngine.UI"      Version="4.2.0" />
<PackageReference Include="KhaozEngine.Ecs"     Version="4.2.0" />
```

**Versioning is SemVer.** Each game pins a version and adopts fixes by bumping it — so you can keep one game on an old version while you migrate another. Don't fork the packages; if a game needs an API that isn't there, add it here and bump the version.

## Testability standard

Every input and routing path is covered by `KhaozEngine.Tests` (xUnit), headless, by constructing `RawInputState` snapshots frame-by-frame and feeding them to `InputManager.Update`. New behaviour added to the library ships with a headless test. This is the standard, not a nicety — it's the reason the raw read is behind `IRawInput`.

```bash
dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj
```

## Repo layout

```
KhaozEngine.Input/   KhaozEngine.Screens/   KhaozEngine.UI/   KhaozEngine.Ecs/   KhaozEngine.Time/
KhaozEngine.Content/   KhaozEngine.Content.Validator/   KhaozEngine.Diagnostics/
KhaozEngine.App/   KhaozEngine.Localization/   KhaozEngine.Persistence/   KhaozEngine.Serialization/
KhaozEngine.Audio/   KhaozEngine.Effects/   KhaozEngine.Graphics/   KhaozEngine.Sprites/
KhaozEngine.Tests/      docs/USING-KHAOZENGINE.md
Directory.Build.props (shared version)   nuget.config   .github/workflows/ci.yml
```

CI builds, tests, packs, and on a `v*` tag publishes to GitHub Packages.

## Consumers

| Game | Uses (KhaozEngine.*) | Status |
|---|---|---|
| **Hardpoint** | Input, Screens, UI, Ecs, Graphics, Sprites, Effects, Content, Diagnostics, App, Localization, Persistence | On 4.0.0. |
| **Nullwake** | Input, Screens, UI, Graphics, Time, Audio, Effects, Content, Diagnostics, App, Localization, Persistence, Serialization | On 4.0.0. Source of the widgets, `VirtualResolution`, transitions, and the click-through fix. |
| **SpaceGame** | Input, Screens, UI, Ecs, Graphics, Audio, Content, Diagnostics, App, Localization, Persistence | On 4.0.0. |

Full per-package version + adoption matrix: [`docs/CONSUMERS.md`](docs/CONSUMERS.md).
