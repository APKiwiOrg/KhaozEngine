# KhaozEngine consumers

Which game uses which packages, at which version. Current state only - for the per-version story see
[`../CHANGELOG.md`](../CHANGELOG.md). Update this whenever a consumer bumps a `<PackageReference>` or the
engine ships a new version.

**Engine current version:** `5.50.0` — the 5.x line `<KhaozEngine5xVersion>`, which is now the engine: the
custom MonoGame-free stack (`Gpu`/`Windowing`/`Render2D`/`Render3D`/`Gui`/`Audio`/`Particles`/`Game`) **plus**
the MonoGame-free foundation packages that graduated onto it at `5.46.0`
(`Ecs`/`Serialization`/`Content`/`Diagnostics`/`App`/`Localization`/`Persistence`/`Pooling`/`Platform`/
`Updates`/`Collision`/`Netcode`/`Netcode.Abstractions`/`Netcode.LiteNetLib`). The legacy 4.x line `<Version>`
is frozen-ish at `4.12.0` and now carries **only** the genuinely-MonoGame packages
(`Effects`/`Graphics`/`Input`/`Screens`/`Sprites`/`Time`/`UI`), consumed by the still-4.x SpaceGame. The two
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

**5.x line (the engine, no longer experimental):** the 8 custom-stack packages `Gpu`, `Windowing`, `Render2D`,
`Render3D`, `Gui`, `Audio`, `Particles`, `Game` (the `-experimental` suffix was dropped at `5.31.0`) **plus**
the 14 graduated foundation packages all share `<KhaozEngine5xVersion>` = `5.46.0`. The stack packages replace
the legacy 4.x MonoGame rendering/UI/input/audio/screens/effects/time packages (UI->Gui, Graphics->Render2D,
Screens->Gui ScreenStack + Game SceneManager, Input->Windowing, Effects->Particles, Time->Windowing.GameClock).
See [`ROADMAP.md`](ROADMAP.md), "The post-MonoGame pivot".

**Hardpoint is fully migrated to the 5.x stack** (the proof a game can run 100% MonoGame-free). It pins the 5.x
packages at `5.38.0` (`Windowing`/`Render3D`/`Render2D`/`Gui`/`Particles`/`Audio`/`Game`, `Gpu` transitive) and
uses only the MonoGame-free FOUNDATION packages off the 4.x line (`Ecs`/`Content`/`Diagnostics`/`App`/
`Localization`/`Persistence` at `4.12.0`). It uses NONE of the legacy MonoGame packages anymore. **Nullwake and
SpaceGame are still on the 4.x MonoGame stack** (not yet migrated); their full ports onto 5.x are the remaining
"migrate to 5.x" work. The matrices below track the 4.x MonoGame packages, so Hardpoint's legacy-package columns
now read `-` (replaced by 5.x) - see its row.

## Version matrix

`-` = package not referenced directly by that project. `Time` is pulled in transitively by
`Screens` 2.2.0+; consumers vendor `KhaozEngine.Time` even without a direct reference. `Serialization`
(4.0.0+) is transitive via `Content`/`Persistence`/`Ecs`, so it shows `-` for consumers that don't
reference it directly (Nullwake does reference it directly for `JsonDefaults`).

| Project   | Project file                         | Input | Screens | UI    | Ecs   | Content | Diagnostics | Time  | App   | Localization | Persistence | Audio | Effects | Graphics | Sprites | Serialization | Platform | Collision | Pooling | Updates | Netcode | Netcode.LiteNetLib | Netcode.Abstractions |
|-----------|--------------------------------------|-------|---------|-------|-------|---------|-------------|-------|-------|--------------|-------------|-------|---------|----------|---------|---------------|----------|-----------|---------|---------|---------|--------------------|--------------------|
| Hardpoint | `Hardpoint/Hardpoint.Core` (5.x) | -     | -       | -     | 4.12.0 | 4.12.0  | 4.12.0      | -     | 4.12.0 | 4.12.0       | 4.12.0      | -     | -       | -        | -       | -             | -        | -         | -       | -       | - | - | - |
| Nullwake  | `Nullwake/Nullwake.Core`             | 4.0.0 | 4.0.0   | 4.0.0 | -     | 4.0.0   | 4.0.0       | 4.0.0 | 4.0.0 | 4.0.0        | 4.0.0       | 4.0.0 | 4.0.0   | 4.0.0    | -       | 4.0.0         | -        | -         | -       | -       | - | - | - |
| SpaceGame | `SpaceGame/SpaceGame.Core`           | 4.9.0 | 4.9.0   | 4.9.0 | 4.9.0 | 4.9.0   | 4.9.0       | -     | 4.9.0 | 4.9.0        | 4.9.0       | 4.9.0 | -       | 4.9.0    | -       | -             | 4.9.0    | 4.9.0     | 4.9.0   | 4.9.0   | 4.9.0 | - | 4.9.0 |

## Adoption matrix

Which packages each consumer pulls in. `✓` = direct `<PackageReference>`, `-` = not used,
`(transitive)` = vendored via `Screens` 2.2.0+ but no direct reference and (for `Time`) no
scaled-dt usage.

| Consumer  | Input | Screens | UI | Ecs | Content | Diagnostics |    Time      | App | Localization | Persistence | Audio | Effects | Graphics | Sprites |  Serialization  | Platform | Collision | Pooling | Updates | Netcode | Netcode.LiteNetLib | Netcode.Abstractions |
|-----------|:-----:|:-------:|:--:|:---:|:-------:|:-----------:|:------------:|:---:|:------------:|:-----------:|:-----:|:-------:|:--------:|:-------:|:---------------:|:--------:|:---------:|:-------:|:-------:|:-------:|:------------------:|:------------------:|
| Hardpoint (5.x) | - | - | - |  ✓  |    ✓    |      ✓      |      -       |  ✓  |      ✓       |      ✓      |   -   |    -    |    -     |    -    |        -        |    -     |     -     |    -    |    -    | - | - | - |
| Nullwake  |   ✓   |    ✓    | ✓  |  -  |    ✓    |      ✓      |      ✓       |  ✓  |      ✓       |      ✓      |   ✓   |    ✓    |    ✓     |    -    |        ✓        |    -     |     -     |    -    |    -    | - | - | - |
| SpaceGame |   ✓   |    ✓    | ✓  |  ✓  |    ✓    |      ✓      | (transitive) |  ✓  |      ✓       |      ✓      |   ✓   |    -    |    ✓     |    -    |  (transitive)   |    ✓     |     ✓     |    ✓    |    ✓    | ✓ | - | ✓ |

## Notes (current state per consumer)

### Hardpoint - MIGRATED to the 5.x custom stack (MonoGame-free)

Hardpoint was rebuilt as a full-3D iso tower-defense entirely on the 5.x stack; it pins the 5.x packages at
`5.38.0` and uses zero legacy MonoGame packages. The rendering/UI/input/audio that used to come from the 4.x
packages now come from 5.x:

- **3D + 2D rendering:** `Render3D` (iso board, glTF/procedural meshes, per-mesh albedo textures for the dirt
  floor, lighting/materials, debug draw, billboards, `IsoCameraController` zoom/pan) + `Render2D` (the HUD batch).
  Replaces the old `Graphics`/`Sprites`/`UI`.
- **Windowing/input:** `Windowing` (`AppWindow` on Silk.NET, `GameClock`, `InputState`/`Pointer`,
  `DesignViewport`). Replaces `Input`/`Time`.
- **UI + screens:** `Gui` `GuiSurface` (immediate-mode HUD/menus, hover-enter for UI sounds) + `Game`
  `SceneManager`/`GameScene` (Title/Match/Pause/GameOver scene stack). Replaces `UI`/`Screens`.
- **Audio:** `Audio` (`AudioSystem` SFX mixer + positional one-shots + `WavSynth` placeholders) drives combat
  + UI sounds. Replaces the old 4.x `Audio`.
- **Effects:** `Particles` (`ParticleSystem`) drives muzzle/hit/death bursts via the game-side `CombatVfx`
  entity-diff. Replaces `Effects`.
- **Foundation (4.x line, MonoGame-free):** still uses `Ecs` (the ECS the gameplay runs on), `Content`
  (build-time JSON schema validation), `Diagnostics` (the `Log` service + `CrashHandler`), `App`
  (`AppDataPaths`/`BuildMetadata`), `Localization` (`LocalizationManager`), `Persistence`
  (`SettingsManager<CampaignSaveData>` campaign saves) - all at `4.12.0`.

### Nullwake - on 4.0.0

- **Logging:** game-side `GameLogger` replaced by engine `Log` + `CrashHandler` (configured via
  `LogBootstrap`). Also uses `AppDataPaths`, `ServiceLocator`, `BuildMetadata`.
- **Persistence:** `SaveEncoder` + `AtomicJsonWriter`; saves go through its own `LocalSaveSystem`, not
  `SettingsManager`.
- **UI / Graphics:** both skill-tree screens run on `PannableCanvas` (replacing hand-rolled `_cameraOffset`
  math); `EnableZoom = false` keeps nodes in the sharp screen-space batch (a zoom matrix would blur
  SpriteFont glyphs). `DisplayManager` + `DisplaySettings` own desktop window config + the min-size floor.
- **Serialization (direct ref):** `JsonDefaults.TolerantRead` in `ConfigLoader`, `IndentedWrite` in
  `LocalSaveSystem`.
- **Audio / Effects:** `AudioSystem` (random rotation via `PlayRandomTrack`) and `Effects.ParticleSystem`.
- **Content is build-time only:** referenced for its MSBuild schema-validation target (validates
  `Data/*.json` against `Data/schemas/`, 10+ schemas). Nullwake uses its own `ConfigLoader`, so the
  package has no runtime/code use - a legitimate build-time-only reference.
- **Not adopted:** `Camera2D` for the mining view - `OreField.RefToScreen` is a non-uniform
  fit-into-a-sub-rectangle projection, incompatible with `Camera2D`'s uniform full-viewport matrix
  (`PannableCanvas` does drive a `Camera2D` internally, but only over the skill-tree node space). `Ecs`
  not yet adopted - a `DeterministicRng` swap (off `GameRng`) is a planned follow-up. `Sprites` unused.

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

_Last verified: 2026-06-17. The 5.x line `<KhaozEngine5xVersion>` = **5.46.0** is the engine: the 8 custom-stack
packages plus the 14 foundation packages that graduated onto it at 5.46.0 (audit P1#9). The legacy 4.x line
`<Version>` = **4.12.0** is frozen-ish and now carries **only** the 7 genuinely-MonoGame packages
(Effects/Graphics/Input/Screens/Sprites/Time/UI). **Hardpoint** is fully migrated to the 5.x stack (pins 5.38.0
stack + 4.12.0 foundation — both lagging the current release; adopts 5.46.0 on its own schedule). **Nullwake**
is migrating on branch `migrate-5x` (its temp main) and pins **5.46.0** there, fully off MonoGame and 5.x-only;
the matrix rows above still show its pre-migration main and update when `migrate-5x` merges. **SpaceGame**
remains on the 4.x MonoGame stack (pins 4.9.0; its 5.x port is the remaining migration work). The 7 legacy
MonoGame packages get deleted once SpaceGame is off them, at which point the 4.x line — and MonoGame — is gone._
