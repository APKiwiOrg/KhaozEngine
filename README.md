# KhaozEngine

A shared, game-agnostic, **MonoGame-free** 2D/3D engine: windowing + input, a GPU abstraction, 2D and 3D
renderers, an immediate-mode + screen-stack GUI, audio, particles, an ECS, netcode, and the usual foundation
(content, persistence, localization, diagnostics). One implementation, used by three games (Hardpoint,
Nullwake, SpaceGame), so a fix written once propagates to all of them.

KhaozEngine is split into focused, independently-referenceable NuGet packages plus a few umbrella metapackages,
so a game pulls in just what it needs (and a logic library or headless server can pull a renderer-free subset).

| Package | What it gives you | Depends on |
|---|---|---|
| **KhaozEngine.Gpu** | The GPU backend seam: backend selection (Metal/D3D11/Vulkan/OpenGL via RID), device + command-list abstraction over Veldrid. The only graphics-API-aware layer. | Pure .NET (+ Veldrid) |
| **KhaozEngine.Windowing** | `AppWindow` (owns the Silk.NET/GLFW window + the per-frame `Run` loop), the immutable `InputState` snapshot, `InputManager`/`Pointer` (unified pointer, edges, `IsTapIn` press-origin invariant, region blocking, drag/scroll, keyboard/gamepad/menu-nav), `GameClock`, `DesignViewport`/`AdaptiveViewport`. | KhaozEngine.Gpu |
| **KhaozEngine.Render2D** | `SpriteBatch` (textured quads + `DrawString` over stb_truetype `SpriteFont`, optional model transform + sampler mode), `Camera2D`, `Texture2D`, `ImageRgba` (CPU pixel/opaque-mask decode), `Render2DSurface`, scissor/point-sampling, offscreen capture. | Windowing, Gpu |
| **KhaozEngine.Render3D** | Stylized 3D: `Scene3D` (multi-instance mesh draw, glTF + procedural meshes, per-mesh albedo textures, materials/lighting, billboards, debug draw), `IsoCamera3D` (ortho iso + screen-to-ground/ray picking), `PixelPostProcess`, `Render3DSurface`. | Ecs, Windowing, Gpu |
| **KhaozEngine.Gui** | `GuiSurface` (immediate-mode UI: Panel/Label/Button/Slider/Toggle, hover, click-through gate), `ScreenStack` (top-to-bottom routed screen stack + widgets), `FocusNavigator` (menu navigation). | Windowing, Render2D |
| **KhaozEngine.Audio** | `AudioSystem`: an OpenAL (Silk.NET.OpenAL) streaming music backend + SFX one-shots and 3D positional audio over a voice pool. WAV/OGG/MP3. | Diagnostics |
| **KhaozEngine.Particles** | Pure, deterministic particle simulation (xorshift, `System.Numerics` + BCL only): `ParticleSystem` pool, `EmitterConfig` presets, `RateAccumulator`. | Pure .NET |
| **KhaozEngine.Effects** | Game-feel visual effects: `ScreenShake` (trauma-based), parallax helpers. | Pure .NET |
| **KhaozEngine.Game** | The 2D game-loop facade: `GameApp` (abstract base owning the per-frame compose: clock/viewport/input/draw) + `GameAppOptions`, and a `SceneManager`/`GameScene` state stack (Push/Pop/Replace/SwitchTo, overlay DrawBelow/UpdateBelow). | Windowing, Render2D, Gui |
| **KhaozEngine.Game.Render3D** | The 3D bridge for the Game framework: `GameApp3D` (a `GameApp` that stands up a `Render3DSurface` + drives the 3D pass), `IGameScene3D`, and a `SceneManager.Draw3D` extension. Kept separate so a 2D game pulls no 3D renderer. | Game, Render3D |
| **KhaozEngine.Ecs** | A struct-based archetype `World`/`Entity`/`ISystem` ECS: by-ref component access, `ForEach`, command buffer, system groups, `CachedQuery`, `DeterministicRng`, `WorldSerializer`. | Serialization |
| **KhaozEngine.Content** | Config/content loading (embedded or disk JSON) with JSON-schema validation + build-time schema enforcement. | Diagnostics, Serialization (+ JsonSchema.Net) |
| **KhaozEngine.Diagnostics** | Logging service: levels, pluggable sinks (rotating file / console / debug / in-memory), category loggers, a static `Log` facade over an injectable `LogManager`, crash hooks. | Pure .NET |
| **KhaozEngine.App** | App/runtime helpers: `BuildMetadata` (read `AssemblyMetadata` at runtime), `AppDataPaths` (publisher-rooted OS-correct per-app data dir), `ServiceLocator`. | Pure .NET |
| **KhaozEngine.Localization** | `LocalizationManager`: discover satellite-resource cultures and set the current thread culture. | Diagnostics |
| **KhaozEngine.Persistence** | `SaveEncoder` (Base64 + HMAC-SHA256 tamper-deterrent), `AtomicJsonWriter` + `PersistenceQueue` (crash-safe atomic writes), `SettingsManager<T>`, and the `GameStorage` facade. | Diagnostics, App, Serialization |
| **KhaozEngine.Serialization** | Shared `System.Text.Json` defaults (`JsonDefaults`: tolerant-read / indented-write / include-fields) so Content, Persistence, and Ecs serialize consistently. | Pure .NET |
| **KhaozEngine.Platform** | `Clipboard`: cross-platform system-clipboard facade (SDL2 / macOS / Windows CF_DIB / optional mobile bridge), best-effort and never-throwing. | Pure .NET |
| **KhaozEngine.Pooling** | `ObjectPool<T>`: fixed-capacity free-list pool with O(1) rent/return, active/free tracking, swap-removal compaction. | Pure .NET |
| **KhaozEngine.Collision** | Deterministic 2D collision + broadphase: `CircleCollision` and `SpatialHashGrid`. Bit-identical math for lockstep sims (`System.Numerics`). | Pure .NET |
| **KhaozEngine.Updates** | Delta auto-update pipeline: SHA256 manifests + diffing, a host-agnostic update source, an `UpdateService` state machine with resumable staged downloads, and a cross-platform staged-apply core (`UpdateApplier`). | Diagnostics |
| **KhaozEngine.Netcode.Abstractions** | The zero-dependency channel-split contract: `IChannelSplittable<TSelf>` + `NetChannelReliability`. Reference this alone from a transport-agnostic DTO project (e.g. one shared with a web server). | Pure .NET |
| **KhaozEngine.Netcode** | Transport-free netcode primitives (`System.Numerics`): `UnitAxisQuantizer` (deterministic 8-bit axis codec), `ClientPrediction<TState,TCommand>`, `RemoteCommandQueue<TCommand>`. Type-forwards the channel-split contract from Abstractions. | Netcode.Abstractions |
| **KhaozEngine.Netcode.LiteNetLib** | LiteNetLib transport binding: `ChannelSplitter` maps `NetChannelReliability` to LiteNetLib's `DeliveryMethod`. | LiteNetLib, Netcode |
| **KhaozEngine.Content.Validator** | Build-time JSON-schema enforcement tool for content (`IsPackable=false`; ships inside the Content package). | Content |

**Umbrella metapackages** (code-free curated dependency groups - one `<PackageReference>` instead of a dozen):

| Metapackage | Pulls in | For |
|---|---|---|
| **KhaozEngine.Game2D** | 2D runtime (Windowing/Render2D/Gui/Audio/Particles) + `Game` + `Foundation` | a desktop 2D game |
| **KhaozEngine.Game3D** | `Game2D` + `Render3D` + `Game.Render3D` (the 3D scene bridge) | a desktop 3D game |
| **KhaozEngine.Server** | `Foundation` + netcode (`Netcode`/`.Abstractions`/`.LiteNetLib`) | a headless sim server (no GPU) |
| **KhaozEngine.Foundation** | the GPU-free foundation (App/Content/Diagnostics/Ecs/Localization/Persistence/Serialization/Pooling/Collision/Platform/Updates) | a gameplay-logic library (no renderer) |

Target framework `net10.0`. MonoGame-free: Silk.NET windowing/input (GLFW natives bundled per-RID), Veldrid
behind `KhaozEngine.Gpu` for the GPU, Silk.NET.OpenAL for audio. `System.Numerics` math throughout.

## Why it exists

KhaozEngine began as a shared input + screen-stack foundation extracted from three MonoGame games (a
Rule-of-Three extraction), with one improvement: the raw hardware read sits behind a seam so the whole
input + routing surface is **unit-testable without a device**. It then grew into a full custom stack and the
games migrated **off MonoGame entirely** - MonoGame's GLSL-1.20-on-Apple dead end forced a custom Veldrid/Metal
renderer, and once 2D, 3D, text, audio, and input were all proven on the custom stack the legacy MonoGame
packages were deleted. The headline payoff is unchanged: the **click-through fix** (a tap only registers when
press-origin and release land in the same target, and overlays reserve their footprint so clicks never leak to
the layer beneath) lives in one place. See [`docs/ROADMAP.md`](docs/ROADMAP.md), "The post-MonoGame pivot".

## The one rule that matters most

> **`AppWindow` is the only code in the entire stack that touches the Silk.NET/GLFW input.** Everything above it
> reads an immutable `InputState` snapshot (handed in each frame via `Frame.Input`) through `InputManager` /
> `Pointer`. Games must not reach around the seam - doing so re-introduces the untestable, click-through-leaking
> pattern this library exists to kill. And hit-test with the bounds helpers (`IsTapIn`, …), never raw
> position + button.

Full consumer contract: [`docs/USING-KHAOZENGINE.md`](docs/USING-KHAOZENGINE.md). Read it before wiring a game in.
All docs are indexed in [`docs/INDEX.md`](docs/INDEX.md) (living docs vs the dated design archive).

## Quickstart (the canonical game-loop wiring)

A game subclasses `GameApp` (2D) or `GameApp3D` (3D) and overrides the per-frame seams; the base owns the
`AppWindow.Run` loop, clock, viewport, input, and the 2D batch.

```csharp
using System.Numerics;
using KhaozEngine.Game;
using KhaozEngine.Render2D;
using KhaozEngine.Windowing;

public sealed class MyGame : GameApp
{
    public MyGame() : base(GameAppOptions.For("My Game", 1280, 720)) { }

    protected override void OnLoad() { /* load textures/fonts via Surface2D */ }

    protected override void OnUpdate(float dt)
    {
        if (Input.WasPressed(Key.Escape)) Quit();
        if (Pointer.IsTapIn(new Rect(300, 200, 200, 40))) { /* button hit, click-through-safe */ }
    }

    protected override void OnDraw2D(SpriteBatch batch) { /* batch.Draw(...) / DrawString(...) */ }
}

// Program.cs
using var game = new MyGame();
game.Run();
```

A 3D game additionally overrides `OnDraw3D(Scene3D scene)` on `GameApp3D`; for a screen stack, push
`GameScene`s onto the base's `SceneManager` (a 3D scene implements `IGameScene3D` and calls
`SceneManager.Draw3D`).

## Consuming the packages

Published to a private GitHub Packages feed on tagged releases, and packed to a local file-feed for day-to-day development.

```xml
<!-- nuget.config (additive) -->
<add key="khaozengine-local" value="/Users/antonio/KhaozEngine/local-feed" />
<!-- or the GitHub Packages feed: https://nuget.pkg.github.com/APKiwi/index.json -->
```
```xml
<!-- One reference per project via an umbrella metapackage. Pick the bundle that fits: -->
<PackageReference Include="KhaozEngine.Game2D"     Version="5.70.0" />  <!-- desktop 2D: 2D runtime + GameApp/SceneManager + foundation -->
<PackageReference Include="KhaozEngine.Game3D"     Version="5.70.0" />  <!-- desktop 3D: Game2D + Render3D + the 3D scene bridge -->
<PackageReference Include="KhaozEngine.Server"     Version="5.70.0" />  <!-- headless: foundation + netcode, no graphics -->
<PackageReference Include="KhaozEngine.Foundation" Version="5.70.0" />  <!-- gameplay-logic lib: foundation only, no renderer/netcode -->
```

The metapackages have no code; they just pull in the granular packages. You can still reference those
directly (e.g. just `KhaozEngine.Netcode.Abstractions` for a wire-contract project) and mix a bundle with extra
packages (e.g. `KhaozEngine.Game2D` + `KhaozEngine.Netcode.LiteNetLib` for a 2D multiplayer game).

**Versioning is SemVer.** Each game pins a version and adopts fixes by bumping it - so you can keep one game on an old version while you migrate another. Don't fork the packages; if a game needs an API that isn't there, add it here and bump the version.

## Testability standard

Every input and routing path is covered by `KhaozEngine.Tests` (xUnit), headless, by constructing `InputState`
snapshots frame-by-frame and feeding them to `InputManager.Update` (`dt` is a plain `float` in seconds). New
behaviour added to the library ships with a headless test. This is the standard, not a nicety - it's the reason
the raw read sits behind the `AppWindow`/`InputState` seam.

```bash
dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj
```

## Repo layout

```
# Custom render/runtime stack
KhaozEngine.Gpu/   KhaozEngine.Windowing/   KhaozEngine.Render2D/   KhaozEngine.Render3D/   KhaozEngine.Gui/
KhaozEngine.Audio/   KhaozEngine.Particles/   KhaozEngine.Effects/   KhaozEngine.Game/   KhaozEngine.Game.Render3D/
# Foundation (GPU-free, pure .NET)
KhaozEngine.Ecs/   KhaozEngine.Serialization/   KhaozEngine.Content/   KhaozEngine.Content.Validator/
KhaozEngine.Diagnostics/   KhaozEngine.App/   KhaozEngine.Localization/   KhaozEngine.Persistence/
KhaozEngine.Pooling/   KhaozEngine.Platform/   KhaozEngine.Collision/   KhaozEngine.Updates/
KhaozEngine.Netcode/   KhaozEngine.Netcode.Abstractions/   KhaozEngine.Netcode.LiteNetLib/
# Umbrella metapackages
KhaozEngine.Foundation/   KhaozEngine.Game2D/   KhaozEngine.Game3D/   KhaozEngine.Server/
# Tests, samples, tools
KhaozEngine.Tests/   GuiSample/   Render2DSample/   Render3DSample/   SceneSample/   WindowingSample/   MiniGame/
tools/   docs/USING-KHAOZENGINE.md
Directory.Build.props (shared version)   nuget.config   .github/workflows/ci.yml
```

CI builds, tests, packs, and on a `v*` tag publishes to GitHub Packages.

## Consumers

| Game | References | Status |
|---|---|---|
| **Hardpoint** (3D) | `KhaozEngine.Game3D` (head) + `KhaozEngine.Foundation` (logic) | On 5.57.0, fully off MonoGame. |
| **Nullwake** (2D) | `KhaozEngine.Game2D` | On 5.59.0, fully off MonoGame. Source of the widgets, transitions, and the click-through fix. |
| **SpaceGame** | granular 4.x packages (the last MonoGame holdout) | On 4.9.0. 5.x port is the remaining migration work. |

Full per-package version + adoption matrix: [`docs/CONSUMERS.md`](docs/CONSUMERS.md).
