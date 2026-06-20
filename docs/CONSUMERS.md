# KhaozEngine consumers

Which game uses which packages, at which version. Current state only - for the per-version story see
[`../CHANGELOG.md`](../CHANGELOG.md). Update this whenever a consumer bumps a `<PackageReference>` or the
engine ships a new version.

**Engine current version:** `7.7.0` (the shared `<KhaozEngine5xVersion>` line, which is the engine): the
custom MonoGame-free stack (`Primitives`/`Gpu`/`Windowing`/`Render2D`/`Render3D`/`Gui`/`Audio`/`Particles`/`Effects`/`Game`/`Game.Render3D`) **plus**
the MonoGame-free foundation packages that graduated onto it at `5.46.0`
(`Ecs`/`Serialization`/`Content`/`Diagnostics`/`App`/`Localization`/`Persistence`/`Pooling`/`Platform`/
`Updates`/`Collision`/`Netcode`/`Netcode.Abstractions`/`Netcode.LiteNetLib`). 7.3.0 also adds the publish-side
`KhaozEngine.Updates.Tool` package (the `ke-updater` dotnet tool: manifest/genkey/sign/verify); it is a tool,
not a library, so no consumer references it via `<PackageReference>` and it is in no umbrella metapackage.
**The legacy 4.x line + its six
genuinely-MonoGame packages (`Graphics`/`Input`/`Screens`/`Sprites`/`Time`/`UI`) were DELETED from the repo**, so
the engine is now entirely MonoGame-free with a single version line in `Directory.Build.props` (the doc-version
guard checks it). All three consumers are now off MonoGame (SpaceGame finished its port and pins 7.3.0). The
legacy `4.9.0` nupkgs have been pruned from `local-feed` (which now floors at the 6.x line); they remain
recoverable from GitHub Packages, the durable store. All three consumers are on the 6.x line, so nothing
relies on a pre-6.x feed entry.

> **5.46.0 (graduation, non-breaking re-version):** the 14 MonoGame-free foundation packages moved from the
> 4.x `<Version>` line to the 5.x `<KhaozEngine5xVersion>` line so a 5.x game pins **only** 5.x packages. Same
> assemblies, namespaces, and public API — a consumer just swaps `Version="4.12.0"` to `Version="5.46.0"` on
> those `<PackageReference>`s. The old `4.12.0` foundation nupkgs stay in the feed (cumulative pack), so a
> consumer that hasn't bumped (Hardpoint, SpaceGame) keeps resolving its pin and nothing breaks. This is audit
> item P1#9. The genuinely-MonoGame packages stay on 4.x until SpaceGame migrates, then they're deleted.

> 4.12.0 (breaking, shipped as a 4.x minor since the version-number jump to 5.x is reserved for the custom
> stack): `KhaozEngine.Collision` (`CircleCollision`, `SpatialHashGrid`, `ICircleCollider`) and
> `KhaozEngine.Netcode` (`UnitAxisQuantizer`, `ClientPrediction`, `IPredictedState`) are now **MonoGame-free** -
> their public `Vector2` swapped from XNA (`Microsoft.Xna.Framework`) to `System.Numerics`, and the MonoGame
> package reference is gone. Determinism preserved: `CircleCollision.Intersects` now uses explicit `dx*dx+dy*dy`
> (bit-stable, not a library helper) and `UnitAxisQuantizer` uses `System.Math.Clamp` (a comparison clamp,
> bit-identical to the old `MathHelper.Clamp`); `ClientPrediction`'s `Length`/`Lerp` are client render-smoothing
> only (not in the lockstep hash). Adopting consumers swap their `using Microsoft.Xna.Framework;` to
> `using System.Numerics;` at the call sites. This makes both packages foundation-grade (consumable by a 5.x
> game), unblocking SpaceGame's eventual port; SpaceGame's `Collision` adoption stays hash-gated
> (`17709480852979803671`) and the byte-identical math keeps it stable.

> 4.9.0 is **additive**: new zero-dependency package `KhaozEngine.Netcode.Abstractions` (BCL only, no
> MonoGame, no LiteNetLib) now physically holds `IChannelSplittable<T>` + `NetChannelReliability`.
> 4.8.0 had put them in `KhaozEngine.Netcode`, but that package depends on MonoGame, so a MonoGame-free
> contracts assembly (e.g. one referenced by an ASP.NET leaderboard server) still couldn't implement
> the contract. `KhaozEngine.Netcode` now depends on Abstractions and type-forwards both types
> (`[TypeForwardedTo]` works here: the full type name is unchanged, only the assembly moved), so any
> consumer referencing `KhaozEngine.Netcode` keeps binding them with **no source change**. A
> MonoGame-free DTO project references **only** `KhaozEngine.Netcode.Abstractions`. SpaceGame is the
> intended first adopter (its `EntityUpdateBatchDto` in `SpaceGame.Multiplayer.Contracts`).
>
> 4.8.0 was a **breaking** release shipped as a minor bump (the `5.x` line is reserved for the
> experimental branch): `IChannelSplittable<T>` and the
> `NetChannelReliability` enum moved from `KhaozEngine.Netcode.LiteNetLib` to the transport-free
> `KhaozEngine.Netcode` so a batch DTO in a transport-agnostic project (MessagePack-only, no UDP) can
> implement the split contract. `ChannelSplitter` (the `DeliveryMethod` mapping + `Send`) stays in
> `.LiteNetLib`. No type-forwards (a namespace move can't be bridged by `[TypeForwardedTo]`); migration
> is a one-line `using` swap (`using KhaozEngine.Netcode;` for the contract, keep
> `using KhaozEngine.Netcode.LiteNetLib;` only if you call `ChannelSplitter`). No consumer references
> the netcode types yet, so nothing breaks in practice.
>
> The latest releases (4.6.0, new `Updates` package; 4.5.0, new `Collision` + `Pooling` packages; 4.4.0, new
> `Platform` package with the cross-platform `Clipboard`) are **not adopted by any consumer yet** - all three
> are on 4.0.0. A game adopts a release on its own schedule by bumping its pinned version; the matrices below
> show what each game actually pins, which is expected to lag the engine. SpaceGame is the intended first
> adopter of all four (clipboard, collision, pool, and auto-update code were all promoted from it). SpaceGame's
> adoption of `Collision` is **determinism-hash-gated**: it must re-run its sim-hash check after swapping in the
> engine `CircleCollision` + `SpatialHashGrid` and confirm the hash stays `17709480852979803671`. `Updates` is
> determinism-neutral (never touches sim/RNG), so it carries no hash gate.

**Shared engine line (the engine, no longer experimental):** the zero-dependency `Primitives` leaf (new at
`6.0.0`: `Color`, `DeterministicRng`, `XorRng`, `MathUtil`, `ViewportMath`, `Easing`) **plus** the custom-stack
packages `Gpu`, `Windowing`, `Render2D`, `Render3D`, `Gui`, `Audio`, `Particles`, `Effects`, `Game`, `Game.Render3D` (the
`-experimental` suffix was dropped at `5.31.0`) **plus** the 14 graduated foundation packages **plus** the four
umbrella metapackages all share `<KhaozEngine5xVersion>`. The stack packages replace
the legacy 4.x MonoGame rendering/UI/input/audio/screens/effects/time packages (UI->Gui, Graphics->Render2D,
Screens->Gui ScreenStack + Game SceneManager, Input->Windowing, Effects->Particles, Time->Windowing.GameClock).
See [`ROADMAP.md`](ROADMAP.md), "The post-MonoGame pivot".

**The 5.x games reference the engine through an umbrella metapackage** (one `<PackageReference>` instead
of a dozen). **SpaceGame** uses `Game2D` for its head plus granular foundation pins on the split-out
`SpaceGame.Sim` (it predates wrapping the sim's package set into a metapackage).

## Metapackages (the one-line entry points, 5.49.0 + 5.50.0)

Code-free NuGet metapackages, each a curated dependency group over the granular packages (which still exist for
fine-grained use - a wire-contract project references just `Netcode.Abstractions`, etc.):

| Metapackage | Pulls in | For |
|---|---|---|
| `KhaozEngine.Game2D` | 2D runtime (Windowing/Render2D/Gui/Audio/Particles) + `Game` (the Render3D-free loop framework) + `Foundation` | a desktop 2D game |
| `KhaozEngine.Game3D` | `Game2D` + `Render3D` + `Game.Render3D` (the 3D scene bridge: GameApp3D/IGameScene3D/SceneManager.Draw3D) | a desktop 3D game |
| `KhaozEngine.Server` | `Foundation` + netcode (`Netcode`/`.Abstractions`/`.LiteNetLib`) | a headless sim server (no GPU) |
| `KhaozEngine.Foundation` | the GPU-free foundation (Primitives/App/Content/Diagnostics/Ecs/Localization/Persistence/Serialization/Pooling/Collision/Platform/Updates) | a gameplay-logic library (no renderer) |

## Consumer matrix

| Consumer | Project(s) | References | Version |
|---|---|---|---|
| **Hardpoint** (7.x, 3D) | `Hardpoint.Game` / `Hardpoint.Core` | `KhaozEngine.Game3D` (head) + `KhaozEngine.Foundation` (logic); adopted the updater glue (`Updates` via Foundation, overlay via Gui) — dormant; uses `Collision.Segment2D` (7.4.0) for swept projectile collision | **7.4.0** |
| **Nullwake** (7.x, 2D) | `Nullwake.Core` | `KhaozEngine.Game2D` + `Diagnostics`/`Persistence`/`Windowing` | **7.3.0** |
| **SpaceGame** (7.x, 2D) | `SpaceGame.Core` (head) / `SpaceGame.Sim` (lockstep sim) | `Game2D` + `Netcode.LiteNetLib` + `Primitives` (head); `Ecs`/`Collision`/`Diagnostics`/`Content`/`Serialization`/`App`/`Netcode`/`Pooling` + `Primitives` (sim); `Netcode.Abstractions` (contracts); `Updates` (tools); manifest signing adopted (`ke-updater sign` + embedded RSA public key) | **7.3.0** |

Both games now run on the loop facade (5.57.0): **Hardpoint** is a `GameApp3D` subclass (`HardpointGame`) over a
`SceneManager` + `IGameScene3D` scene stack; **Nullwake** is a `GameApp` subclass (`NullwakeGame`) over a
`Gui.ScreenStack`, with a display-fitted `AppWindow.Scaled` window + responsive `AdaptiveViewport` via the
options' `WindowFactory`/`ViewportFactory`. Neither hand-writes the `AppWindow.Run` loop anymore.

## Notes (current state per consumer)

### Hardpoint - 7.x, full-3D (on `KhaozEngine.Game3D` 7.4.0)

A full-3D iso tower-defense entirely on the 7.x stack, zero legacy MonoGame packages. Two projects:

- **`Hardpoint.Game` (the head)** references one package, **`KhaozEngine.Game3D`**, which bundles the 3D
  renderer (`Render3D`: iso board, glTF/procedural meshes, per-mesh albedo textures, lighting/materials, debug
  draw, billboards, `IsoCameraController`), the 2D HUD (`Render2D`), windowing/input (`Windowing`), UI
  (`Gui.GuiSurface`), audio (`Audio` SFX + positional), particles (`Particles` via the `CombatVfx` entity-diff),
  the `Game` scene framework, and the `Game.Render3D` 3D-scene bridge. The shell is `HardpointGame`, a
  `GameApp3D` subclass (OnLoad/OnUpdate/OnDraw3D/OnDraw2D) over a `SceneManager` stack (Title/Match/Pause/
  GameOver); `MatchScene` implements `IGameScene3D` and `OnDraw3D` calls the `SceneManager.Draw3D` extension to
  render the board behind every screen.
- **`Hardpoint.Core` (gameplay logic)** references one package, **`KhaozEngine.Foundation`** - the GPU-free
  foundation (Ecs for the sim, Content for build-time JSON schema validation, Diagnostics `Log`/`CrashHandler`,
  App `AppDataPaths`/`BuildMetadata`, Localization, Persistence `SettingsManager<CampaignSaveData>`). A logic
  library deliberately pulls no renderer.

Adopted the 7.3.0 auto-updater glue (`UpdateService` + `UpdateOverlayView` wired into `HardpointGame`, a
one-line `HardpointUpdater` shim, an embedded RSA public key). It ships **dormant** (`Enabled = false`, no feed
queried) until Hardpoint has a distribution channel; flip-on checklist is in the game's `Updates/README.md`.

**Bumped to 7.4.0** to adopt `KhaozEngine.Collision.Segment2D.DistanceToSegment`: the swept (look-ahead)
collision primitive that lets a fast tower projectile hit the next enemy along its path instead of tunnelling
through a thin one between frames (used in `ProjectileSystem`'s ballistic branch after a target dies mid-flight).

### Nullwake - 7.x, 2D (on `KhaozEngine.Game2D` 7.3.0)

Fully migrated off MonoGame (the migration landed on Nullwake `main`). `Nullwake.Core` references one package,
**`KhaozEngine.Game2D`** (the 2D runtime + the Render3D-free `Game` loop framework + the foundation). The shell
is `NullwakeGame`, a `GameApp` subclass (OnLoad/OnUpdate/OnDraw2D/OnDispose) over a `Gui.ScreenStack` of 20
screens; the display-fitted `AppWindow.Scaled` window + responsive `AdaptiveViewport` come from the options'
`WindowFactory`/`ViewportFactory`. It uses `GameClock` + `TimeSkip` for offline catch-up, `AudioSystem`, and a
2D `Particles` system; saves go through its
own `LocalSaveSystem` (`SaveEncoder` + `AtomicJsonWriter`). The Android/iOS heads are parked until a
mobile-windowing engine project. (Still game-local and not yet converged onto the engine: the ~9 Gui widgets it
duplicates from `KhaozEngine.Gui`.)

**ADOPTED 7.0.0** for the mining-VFX upgrade: the generic 2D VFX module (`KhaozEngine.Render2D.Vfx`:
`Particle2DSystem`, `EnergyBeam`, `VfxRenderer`/glow + additive `SpriteBatch.BlendMode`) shipped on the 7.0 line.
Nullwake retired its in-repo `Nullwake.Core.Rendering` particle stack onto `Particle2DSystem` (gravity + drag +
rotation + per-particle additive blend); the mining laser uses `EnergyBeam`, and miner/impact/shatter glows use
`VfxRenderer`. Only Nullwake-specific presets (`Rendering/VfxPresets.cs`) stay in-repo. (Screen shake / `ShakeState`
from the VFX scope did not ship in 7.0.0; a follow-up engine round is queued.)

**Bumped to 7.2.0** to adopt `EnergyBeam` round end-caps (`BeamParams.Caps = BeamCap.Round`) so the mining beam
reads as a capsule instead of a rectangle; 7.0.1 (updater probe cap) and 7.1.0 (Render3D textured billboards)
are carried along and touch nothing Nullwake consumes.

**Bumped to 7.3.0** to adopt the auto-updater glue (`UpdateService` + `UpdateOverlayScreen` wired into
`NullwakeGame`, a one-line `NullwakeUpdater` shim, an embedded RSA public key). Desktop-only, ships dormant
(`Enabled = false`, no feed queried); flip-on checklist in `Nullwake.Core/Systems/Updates/README.md`.

### SpaceGame - on 7.3.0 (MonoGame-free, manifest signing adopted)

SpaceGame completed its de-MonoGame migration onto the 5.x stack (merged `96255c9`) and now pins **7.3.0**.
There is no MonoGame and no `.mgcb`; the desktop head `SpaceGame.Desktop` runs Silk/GLFW + Veldrid through the
`GameApp` facade. Input is the immutable `InputState` snapshot via `InputManager`/`Pointer`.

- **Head (`SpaceGame.Core`):** `KhaozEngine.Game2D` (Windowing/Render2D/Gui/Audio/Particles + the GameApp
  facade + foundation) + `Netcode.LiteNetLib` (transport) + `Primitives`. Music runs on `AudioSystem` via the
  rotation-pool track-set (5.71.0); decorative effects run on `KhaozEngine.Particles` (`ParticleEffectPresets`).
- **Sim (`SpaceGame.Sim`):** the deterministic lockstep host sim, split into its own MonoGame-free project.
  References `Ecs`/`Collision`/`Diagnostics`/`Content`/`Serialization`/`App`/`Netcode`/`Pooling` + `Primitives`.
  The **entire host-authoritative simulation runs on the Ecs `World`** (struct `IComponent`s + queries, deferred
  mutations via `EntityCommandBuffer.Defer`, events via `World.Emit`/`Events`, per-stream run RNG via
  `DeterministicRng.CreateDerived`, `WorldSerializer` for host full-state snapshot/resync). Persistence
  (settings/leaderboard/`save.json`) is on `SettingsManager<T>` + `FileSettingsStorage`; logging on the `Log`
  service; `AppDataPaths`/`BuildMetadata`/`LocalizationManager` from `App`.
- **Contracts (`SpaceGame.Multiplayer.Contracts`):** `Netcode.Abstractions` only - the MonoGame-free DTO
  project (also referenced by the ASP.NET leaderboard server) so `EntityUpdateBatchDto` can implement
  `IChannelSplittable` without pulling in MonoGame or LiteNetLib.
- **Tools:** `SpaceGameUpdater` (thin shim, forwards to `KhaozEngine.Updates.UpdaterShim.Main`) + `ke-updater`
  dotnet tool for manifest generation and signing (no bespoke `ManifestGenerator` project).
- **Auto-update signing (7.3.0):** manifests are signed via `ke-updater sign`; the client verifies
  `manifest.json.sig` against an embedded RSA public key (`SpaceGame.Core/Resources/update-signing-public.pem`)
  and rejects unsigned or wrong-key feeds. Private key is CI secret `UPDATE_PRIVATE_KEY`, never committed.
- **6.0.0 breaking surface:** `Color` and `DeterministicRng` moved to the new zero-dep `Primitives` leaf
  (`Color` is no longer re-exported by `Render2D`); the `Color` API unified off `Vector4`. Both were mechanical
  `using`/literal swaps in SpaceGame. 6.1→6.3 are additive; 7.0→7.3 are additive for SpaceGame (the adoption
  surface is the signing pipeline, not a new public API).
- **Lockstep determinism:** the `StateHash` baseline is **`17709480852979803671`** (the tentacle-hitbox
  re-baseline). It held byte-identical across the 6.x and 7.x bumps. (The pre-migration `5562709684599485702`
  is stale.)

## Repo locations

| Project   | Path                  | Repo                         |
|-----------|-----------------------|------------------------------|
| Hardpoint | `~/Hardpoint`         | migrated                     |
| Nullwake  | `~/Nullwake/Nullwake` |                              |
| SpaceGame | `~/SpaceGame/SpaceGame`|                             |

## How to refresh this file

```sh
# engine version (source of truth) - the 5.x line is the engine; 4.x is legacy-MonoGame-only
grep -iE '<KhaozEngine5xVersion>|<Version>' ~/KhaozEngine/Directory.Build.props

# what each consumer pins
for d in ~/Hardpoint ~/Nullwake ~/SpaceGame; do
  find "$d" -name '*.csproj' -not -path '*/bin/*' -not -path '*/obj/*' \
    -exec grep -l KhaozEngine {} \; | while read f; do
      echo "-- $f"; grep -i KhaozEngine "$f"; done
done
```

After editing, run `./scripts/check-doc-versions.sh` (CI runs it too) to confirm the engine-version line
still matches `Directory.Build.props`.

_Last verified: 2026-06-20. The shared line `<KhaozEngine5xVersion>` = **7.4.0** is the engine: the new
zero-dependency `Primitives` leaf + the custom-stack packages + the graduated foundation + the four umbrella
metapackages (Game2D/Game3D/Server/Foundation). The legacy 4.x `<Version>` line was deleted from
`Directory.Build.props` and its old MonoGame nupkgs pruned from the feed (recoverable from GitHub Packages).
**Hardpoint** (3D) is on **7.4.0** via `Game3D` + `Foundation` (bumped for `Collision.Segment2D`),
**Nullwake** (2D) is on **7.3.0** via `Game2D` (+ `Diagnostics`/`Persistence`/`Windowing`), and **SpaceGame**
(2D) is on **7.3.0** via `Game2D` + the split-out `SpaceGame.Sim` - all three fully off MonoGame, each pinning
the engine on its own schedule, referencing the engine in one line (plus the sim's foundation pins), and running on the
`GameApp3D`/`GameApp` loop facade. The breaking 6.0.0 `Primitives.Color` migration has been adopted by all
three. SpaceGame adopted manifest signing (`ke-updater sign` + embedded RSA public key) at 7.3.0._
