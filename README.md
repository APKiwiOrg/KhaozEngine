# KhaozEngine

<picture>
  <source media="(prefers-color-scheme: dark)" srcset="art-assets/brand/KhaozEngine-logo-dark.svg">
  <img src="art-assets/brand/KhaozEngine-logo-light.svg" alt="Khaoz Engine" width="708">
</picture>

KhaozEngine is a 2D and 3D game engine for C# and .NET 10. It renders through its own Metal, Direct3D 11 and
Vulkan backends and ships as focused NuGet packages, so a game takes only what it needs and a headless server
takes no renderer at all.

Four games are built on it: Hardpoint, Nullwake, SpaceGame and Grimhollow. A fix made once in the engine
reaches all four.

## What's in it

- **Rendering.** Engine-owned GPU backends behind one seam: Metal on macOS, Direct3D 11 on Windows and Vulkan
  on Linux, with shaders written once in GLSL and cross-compiled for each. A batched 2D renderer with crisp
  HiDPI text, and a stylized 3D renderer with glTF meshes, skinning, shadows, post effects and particles.
- **Input you can test.** One window class owns the raw device read. Everything above it reads an immutable
  input snapshot, so every input and routing path runs in unit tests without a window. Rebindable action maps,
  gamepads and pointer gestures come with it.
- **GUI.** Immediate-mode drawing and a screen stack with modal layering. A tap counts only when press and
  release land on the same target, so clicks never leak through an overlay. Player-facing text is localized,
  and an analyzer enforces that at compile time.
- **Worlds.** Streamed terrain, tile worlds, a native map format with an in-engine map editor, navigation,
  dungeon generation and opt-in physics on BepuPhysics.
- **Gameplay.** A content catalog with authoring and publishing, items, skills, stats, progression and
  objectives, plus an ECS.
- **Multiplayer.** Deterministic simulation, replication, sharding, a persistent world store on SQLite or SQL
  Server, and a headless server bundle.
- **Services.** Accounts, sign-in through OIDC or Discord, a wallet, bug reports, server status and a signed
  delta auto-updater.
- **Tooling.** A headless snapshot harness with golden-image regression tests, Roslyn analyzers, and
  command-line tools for map editing, prop baking, sound-effect generation and update manifests.

Every package is listed, one line each, in [docs/PACKAGES.md](docs/PACKAGES.md).

## Quickstart

A game subclasses `GameApp` for 2D or `GameApp3D` for 3D. The base class owns the loop, clock, viewport,
input and the 2D batch, and the game overrides the per-frame hooks. The smallest thing that runs:

```csharp
using KhaozEngine.Game;
using KhaozEngine.Render2D;
using KhaozEngine.Windowing;

public sealed class MyGame : GameApp
{
    public MyGame() : base(GameAppOptions.For("My Game", 1280, 720)) { }

    protected override void OnUpdate(float dt) { if (Input.WasPressed(Key.Escape)) Quit(); }
    protected override void OnDraw2D(SpriteBatch batch) { /* batch.Draw(...) / DrawString(...) */ }
}

// Program.cs
using var game = new MyGame();
game.Run();
```

The full wiring guide, including 3D scenes, input, fonts and hit testing, is
[Wiring a game](docs/USING-KHAOZENGINE.md#wiring-a-game-khaozenginegame--khaozenginegamerender3d) in the
consumer guide.

## Packages

Most projects need one reference. Each umbrella package carries no code of its own and pulls in a curated set:

| Package | For |
|---|---|
| [`KhaozEngine.Game2D`](KhaozEngine.Game2D/README.md) | A desktop 2D game: windowing, 2D rendering, GUI, audio, particles, the game loop and the foundation |
| [`KhaozEngine.Game3D`](KhaozEngine.Game3D/README.md) | A desktop 3D game: everything in Game2D plus the 3D renderer and its scene bridge |
| [`KhaozEngine.Server`](KhaozEngine.Server/README.md) | A headless server: the foundation plus netcode and the server stack, with no graphics |
| [`KhaozEngine.Foundation`](KhaozEngine.Foundation/README.md) | A gameplay logic library or tool: the GPU-free foundation only |

```xml
<PackageReference Include="KhaozEngine.Game3D" Version="21.20.1" />
```

Granular packages can be referenced directly or added beside an umbrella, for example `KhaozEngine.Game2D`
with `KhaozEngine.Netcode.LiteNetLib` for a 2D multiplayer game. Versions follow SemVer, every package shares
one version, and each game pins the version it uses.

Tagged releases publish to GitHub Packages at `https://nuget.pkg.github.com/APKiwiOrg/index.json`, which
needs a GitHub token with `read:packages`. Engine developers pack a local feed with
`scripts/pack-local-feed.sh`.

## Try it

With the .NET 10 SDK and a GPU-capable display:

```bash
dotnet run --project KhaozEngine.Showcase
```

The Showcase is a menu of feature rooms: a 2D and GUI tour, a mini-game, a walkable streamed 3D world, a
particles room, an in-process networked walk, a generated dungeon and the map editor. Its
[README](KhaozEngine.Showcase/README.md) lists the controls, the smoke-run switch and the capture levers.

## Platforms

| OS | GPU backend |
|---|---|
| macOS | Metal |
| Windows | Direct3D 11 |
| Linux | Vulkan |

The window and audio native libraries ship inside the packages for each runtime. Packages target `net10.0`,
and the Roslyn analyzers target `netstandard2.0`. `KhaozEngine.ServerStatus`, `KhaozEngine.Http` and their
dependencies also target `net8.0` so they can run on Azure Functions. How rendering is verified on each platform is in [docs/CROSS-PLATFORM.md](docs/CROSS-PLATFORM.md).

## Documentation

| Read | For |
|---|---|
| [docs/PACKAGES.md](docs/PACKAGES.md) | Every package, with a link to its own README |
| [docs/USING-KHAOZENGINE.md](docs/USING-KHAOZENGINE.md) | The consumer contract: hard rules and the full API guide |
| [docs/DEPENDENCY-SEAMS.md](docs/DEPENDENCY-SEAMS.md) | How each third-party library sits behind an engine seam |
| [CHANGELOG.md](CHANGELOG.md) | Release history |
| [docs/INDEX.md](docs/INDEX.md) | Every doc, including design records |
| [AGENTS.md](AGENTS.md) and [docs/CONTRIBUTOR-RULES.md](docs/CONTRIBUTOR-RULES.md) | Working on the engine itself |

## Built with KhaozEngine

| Game | Kind |
|---|---|
| Hardpoint | 3D |
| Nullwake | 2D |
| SpaceGame | 2D, with a deterministic lockstep simulation |
| Grimhollow | 3D tile-grid MMO |

## License

KhaozEngine is proprietary. The source is visible here, but no right to use, copy, modify or distribute it is
granted without written permission from APKiwi. See [LICENSE](LICENSE).

## Support

[![Support me on Ko-fi](https://ko-fi.com/img/githubbutton_sm.svg)](https://ko-fi.com/apkiwi)
