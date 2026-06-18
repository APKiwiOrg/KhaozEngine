# KhaozEngine consumers

Which game uses which packages, at which version. Current state only - for the per-version story see
[`../CHANGELOG.md`](../CHANGELOG.md). Update this whenever a consumer bumps a `<PackageReference>` or the
engine ships a new version.

**Engine current version:** `5.60.0` — the 5.x line `<KhaozEngine5xVersion>`, which is now the engine: the
custom MonoGame-free stack (`Gpu`/`Windowing`/`Render2D`/`Render3D`/`Gui`/`Audio`/`Particles`/`Game`) **plus**
the MonoGame-free foundation packages that graduated onto it at `5.46.0`
(`Ecs`/`Serialization`/`Content`/`Diagnostics`/`App`/`Localization`/`Persistence`/`Pooling`/`Platform`/
`Updates`/`Collision`/`Netcode`/`Netcode.Abstractions`/`Netcode.LiteNetLib`). The legacy 4.x line `<Version>`
is frozen-ish at `4.12.0` and now carries **only** the genuinely-MonoGame packages
(`Graphics`/`Input`/`Screens`/`Sprites`/`Time`/`UI`), consumed by the still-4.x SpaceGame. The two
lines move independently; both set in `Directory.Build.props`. The doc-version guard now checks the 5.x line.

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

**5.x line (the engine, no longer experimental):** the custom-stack packages `Gpu`, `Windowing`, `Render2D`,
`Render3D`, `Gui`, `Audio`, `Particles`, `Game`, `Game.Render3D` (the `-experimental` suffix was dropped at
`5.31.0`) **plus** the 14 graduated foundation packages **plus** the four umbrella metapackages all share
`<KhaozEngine5xVersion>`. The stack packages replace
the legacy 4.x MonoGame rendering/UI/input/audio/screens/effects/time packages (UI->Gui, Graphics->Render2D,
Screens->Gui ScreenStack + Game SceneManager, Input->Windowing, Effects->Particles, Time->Windowing.GameClock).
See [`ROADMAP.md`](ROADMAP.md), "The post-MonoGame pivot".

**Both 5.x games now reference the engine through an umbrella metapackage** (one `<PackageReference>` instead
of a dozen). **SpaceGame** is the lone holdout still on the 4.x MonoGame stack.

## Metapackages (the one-line entry points, 5.49.0 + 5.50.0)

Code-free NuGet metapackages, each a curated dependency group over the granular packages (which still exist for
fine-grained use - a wire-contract project references just `Netcode.Abstractions`, etc.):

| Metapackage | Pulls in | For |
|---|---|---|
| `KhaozEngine.Game2D` | 2D runtime (Windowing/Render2D/Gui/Audio/Particles) + `Game` (the Render3D-free loop framework) + `Foundation` | a desktop 2D game |
| `KhaozEngine.Game3D` | `Game2D` + `Render3D` + `Game.Render3D` (the 3D scene bridge: GameApp3D/IGameScene3D/SceneManager.Draw3D) | a desktop 3D game |
| `KhaozEngine.Server` | `Foundation` + netcode (`Netcode`/`.Abstractions`/`.LiteNetLib`) | a headless sim server (no GPU) |
| `KhaozEngine.Foundation` | the GPU-free foundation (App/Content/Diagnostics/Ecs/Localization/Persistence/Serialization/Pooling/Collision/Platform/Updates) | a gameplay-logic library (no renderer) |

## Consumer matrix

| Consumer | Project(s) | References | Version |
|---|---|---|---|
| **Hardpoint** (5.x, 3D) | `Hardpoint.Game` / `Hardpoint.Core` | `KhaozEngine.Game3D` (head) + `KhaozEngine.Foundation` (logic) | **5.57.0** |
| **Nullwake** (5.x, 2D) | `Nullwake.Core` | `KhaozEngine.Game2D` | **5.59.0** |
| **SpaceGame** (4.x MonoGame) | `SpaceGame.Core` | granular 4.x packages (Input/Screens/UI/Graphics + foundation + netcode) | 4.9.0 |

Both games now run on the loop facade (5.57.0): **Hardpoint** is a `GameApp3D` subclass (`HardpointGame`) over a
`SceneManager` + `IGameScene3D` scene stack; **Nullwake** is a `GameApp` subclass (`NullwakeGame`) over a
`Gui.ScreenStack`, with a display-fitted `AppWindow.Scaled` window + responsive `AdaptiveViewport` via the
options' `WindowFactory`/`ViewportFactory`. Neither hand-writes the `AppWindow.Run` loop anymore.

## Notes (current state per consumer)

### Hardpoint - 5.x, full-3D (on `KhaozEngine.Game3D` 5.57.0)

A full-3D iso tower-defense entirely on the 5.x stack, zero legacy MonoGame packages. Two projects:

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

### Nullwake - 5.x, 2D (on `KhaozEngine.Game2D` 5.57.0)

Fully migrated off MonoGame (the migration landed on Nullwake `main`). `Nullwake.Core` references one package,
**`KhaozEngine.Game2D`** (the 2D runtime + the Render3D-free `Game` loop framework + the foundation). The shell
is `NullwakeGame`, a `GameApp` subclass (OnLoad/OnUpdate/OnDraw2D/OnDispose) over a `Gui.ScreenStack` of 20
screens; the display-fitted `AppWindow.Scaled` window + responsive `AdaptiveViewport` come from the options'
`WindowFactory`/`ViewportFactory`. It uses `GameClock` + `TimeSkip` for offline catch-up, `AudioSystem`, and a
2D `Particles` system; saves go through its
own `LocalSaveSystem` (`SaveEncoder` + `AtomicJsonWriter`). The Android/iOS heads are parked until a 5.x
mobile-windowing engine project. (Still game-local and not yet converged onto the engine: the ~9 Gui widgets it
duplicates from `KhaozEngine.Gui`.)

### SpaceGame - on 4.9.0

- **Graphics:** first consumer of `Camera2D` (headless, with a per-frame `Viewport` sync).
- **Logging:** engine `Log` service (FileSink + ConsoleSink + `CrashHandler`); flushes the persistence
  queue + `Log.Shutdown` on exit. `AppDataPaths` + `BuildMetadata` from `App`; `LocalizationManager`
  (corrected the malformed default culture to `en-US`).
- **Persistence:** settings, leaderboard, and `save.json` all on `SettingsManager<T>` + `FileSettingsStorage`
  over one shared `PersistenceQueue`; `save.json` uses the `sanitizeOnLoad` hook (`[JsonExtensionData]` +
  `SchemaVersion` round-trip for downgrade safety). The hand-rolled `SaveSystem` static was deleted (the
  `SaveData` DTO stays).
- **Audio:** music runs on `AudioSystem` - one instance owns the 4 tracks, loops the per-screen track via
  `PlayMode.RepeatOne`, and is the source of truth for current track + music volume. `MusicPlaybackController`
  is a thin seam; `MusicCatalog` maps content-asset paths to display names; the now-playing overlay binds to
  `CurrentTrack`/`TrackChanged`. The in-house `NowPlayingService`, `AudioVolumeMixer`'s music path, and raw
  `MediaPlayer` playback were deleted. SFX/ambient volume stays game-side (KE.Audio is music-only).
- **Ecs:** the **entire host-authoritative simulation runs on the Ecs `World`**. The 8-spec "ECS World
  Migration" (2026-06) moved every sim entity + system off bespoke storage onto entities + struct
  `IComponent`s + queries, with deferred mutations via `EntityCommandBuffer.Defer`, events via
  `World.Emit`/`Events`, per-stream run RNG via `DeterministicRng.CreateDerived` (`root.CreateDerived
  ("loot"/"upgrade"/"upgrade-slot-{slot}")`), and `WorldSerializer` for a host full-state snapshot +
  round-trip (the foundation for the new mid-run late-join/resync feature). `CachedQuery` keeps the hot
  per-tick sites query-alloc-free. In BETA, cross-version seed replayability is explicitly not a concern, so
  the migration re-baselined the lockstep `StateHash` deliberately - **one** designated move (the
  `CreateDerived` swap); the final baseline is `5562709684599485702`.
- **Generic infra (adopted 4.9.0):** the seven packages promoted from SpaceGame's own code are now all
  consumed, each behind a thin game-side adapter that keeps the game-specific glue:
  - `Platform` - `Clipboard` replaces the in-house `ClipboardInterop` (deleted); the mobile heads set
    `Clipboard.MobileBridgeTypeName = "SpaceGame.Platform.MobileClipboardBridge"` at startup.
  - `Collision` - `CircleCollision` + `SpatialHashGrid`; `Entity : ICircleCollider` with an explicit
    `Radius => CollisionRadius` (the scaled collision radius, not the base). `EnemySpatialIndex` is now a
    thin adapter over the grid.
  - `Pooling` - `ObjectPool<XpFlyer>` (deleted the in-house `XpFlyerPool`).
  - `Updates` - the whole auto-update pipeline (in-game service + the `SpaceGameUpdater` shim via
    `UpdateApplier.Run` + the publish-side manifest tool via `UpdateManifest.GenerateFromDirectory`).
  - `Netcode` - `UnitAxisQuantizer` (the input wire codec), and `ClientPrediction` + `RemoteCommandQueue`
    behind adapters that keep the DTO/arena/config glue.
  `Collision` and the input codec are byte-identical extractions, so the lockstep `StateHash` did not move.
  (As of engine `4.12.0`, `Collision` + `Netcode` are MonoGame-free: their `Vector2` is `System.Numerics`, not
  XNA. When SpaceGame bumps from 4.9.0 to 4.12.0 it swaps `using Microsoft.Xna.Framework;` to
  `using System.Numerics;` at those call sites; the math is byte-identical so the hash gate holds.)
- **`Netcode.Abstractions` (4.9.0):** referenced by `SpaceGame.Multiplayer.Contracts` (not `.Core`), so the
  MonoGame-free DTO project - and the ASP.NET leaderboard server that references it - can have
  `EntityUpdateBatchDto` implement `IChannelSplittable` without pulling MonoGame or LiteNetLib in. The
  `ChannelSplitter` orchestration itself is not used: the host does its own UDP-frame sub-chunking.
- **Still not adopted:** `Effects` (keeps its richer game-side `ParticleManager` - see the
  particle-unification roadmap item), `Sprites`, and `Netcode.LiteNetLib`. SpaceGame vendors `Time`
  transitively but reads no scaled dt (no `GameClock`/`TimeScale`/`TimeSkip`) - the lockstep sim must keep
  it that way.

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

_Last verified: 2026-06-18. The 5.x line `<KhaozEngine5xVersion>` = **5.58.0** is the engine: the custom-stack
packages + the graduated foundation + the four umbrella metapackages (Game2D/Game3D/Server/Foundation). The
legacy 4.x line `<Version>` = **4.12.0** is frozen-ish and carries **only** the 6 genuinely-MonoGame packages
(Graphics/Input/Screens/Sprites/Time/UI). **Hardpoint** (3D) is on **5.57.0** via `Game3D` + `Foundation`,
**Nullwake** (2D) is on **5.57.0** via `Game2D` - both fully off MonoGame, referencing the engine in one line, and
now running on the `GameApp3D`/`GameApp` loop facade. **SpaceGame** is the lone 4.x MonoGame holdout (pins 4.9.0;
its 5.x port is the remaining migration work). The 7 legacy MonoGame packages get deleted once SpaceGame is off
them, at which point the 4.x line — and MonoGame — is gone._
