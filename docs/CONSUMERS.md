# KhaozEngine consumers

Which game uses which packages, at which version. Current state only - for the per-version story see
[`../CHANGELOG.md`](../CHANGELOG.md). Update this whenever a consumer bumps a `<PackageReference>` or the
engine ships a new version.

**Engine current version:** `4.10.0` (all packages share one version, set in `Directory.Build.props`).

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

**Experimental 5.x line:** the custom-stack (MonoGame-free) packages `KhaozEngine.Render3D`,
`KhaozEngine.Render2D`, `KhaozEngine.Audio`, `KhaozEngine.Windowing`, and `KhaozEngine.Gui` share `5.6.0-experimental`
(`Directory.Build.props` `<KhaozEngine5xVersion>`), separate from the 4.x version above (see [`ROADMAP.md`](ROADMAP.md), "The
post-MonoGame pivot"). **No consumer adopts them yet** (the games are MonoGame; migration is deferred under
the full-custom plan). `KhaozEngine.Audio` **graduated** from the 4.x line to 5.x (OpenAL backend, no
MonoGame); the `Audio` column in the matrix below now reflects the **frozen 4.x** Audio that current
consumers still pin. The 5.x packages are otherwise not in the matrix, which tracks the 4.x MonoGame packages.

## Version matrix

`-` = package not referenced directly by that project. `Time` is pulled in transitively by
`Screens` 2.2.0+; consumers vendor `KhaozEngine.Time` even without a direct reference. `Serialization`
(4.0.0+) is transitive via `Content`/`Persistence`/`Ecs`, so it shows `-` for consumers that don't
reference it directly (Nullwake does reference it directly for `JsonDefaults`).

| Project   | Project file                         | Input | Screens | UI    | Ecs   | Content | Diagnostics | Time  | App   | Localization | Persistence | Audio | Effects | Graphics | Sprites | Serialization | Platform | Collision | Pooling | Updates | Netcode | Netcode.LiteNetLib | Netcode.Abstractions |
|-----------|--------------------------------------|-------|---------|-------|-------|---------|-------------|-------|-------|--------------|-------------|-------|---------|----------|---------|---------------|----------|-----------|---------|---------|---------|--------------------|--------------------|
| Hardpoint | `Hardpoint/Hardpoint.Core`           | 4.0.0 | 4.0.0   | 4.0.0 | 4.0.0 | 4.0.0   | 4.0.0       | -     | 4.0.0 | 4.0.0        | 4.0.0       | -     | 4.0.0   | 4.0.0    | 4.0.0   | -             | -        | -         | -       | -       | - | - | - |
| Nullwake  | `Nullwake/Nullwake.Core`             | 4.0.0 | 4.0.0   | 4.0.0 | -     | 4.0.0   | 4.0.0       | 4.0.0 | 4.0.0 | 4.0.0        | 4.0.0       | 4.0.0 | 4.0.0   | 4.0.0    | -       | 4.0.0         | -        | -         | -       | -       | - | - | - |
| SpaceGame | `SpaceGame/SpaceGame.Core`           | 4.9.0 | 4.9.0   | 4.9.0 | 4.9.0 | 4.9.0   | 4.9.0       | -     | 4.9.0 | 4.9.0        | 4.9.0       | 4.9.0 | -       | 4.9.0    | -       | -             | 4.9.0    | 4.9.0     | 4.9.0   | 4.9.0   | 4.9.0 | - | 4.9.0 |

## Adoption matrix

Which packages each consumer pulls in. `✓` = direct `<PackageReference>`, `-` = not used,
`(transitive)` = vendored via `Screens` 2.2.0+ but no direct reference and (for `Time`) no
scaled-dt usage.

| Consumer  | Input | Screens | UI | Ecs | Content | Diagnostics |    Time      | App | Localization | Persistence | Audio | Effects | Graphics | Sprites |  Serialization  | Platform | Collision | Pooling | Updates | Netcode | Netcode.LiteNetLib | Netcode.Abstractions |
|-----------|:-----:|:-------:|:--:|:---:|:-------:|:-----------:|:------------:|:---:|:------------:|:-----------:|:-----:|:-------:|:--------:|:-------:|:---------------:|:--------:|:---------:|:-------:|:-------:|:-------:|:------------------:|:------------------:|
| Hardpoint |   ✓   |    ✓    | ✓  |  ✓  |    ✓    |      ✓      | (transitive) |  ✓  |      ✓       |      ✓      |   -   |    ✓    |    ✓     |    ✓    |  (transitive)   |    -     |     -     |    -    |    -    | - | - | - |
| Nullwake  |   ✓   |    ✓    | ✓  |  -  |    ✓    |      ✓      |      ✓       |  ✓  |      ✓       |      ✓      |   ✓   |    ✓    |    ✓     |    -    |        ✓        |    -     |     -     |    -    |    -    | - | - | - |
| SpaceGame |   ✓   |    ✓    | ✓  |  ✓  |    ✓    |      ✓      | (transitive) |  ✓  |      ✓       |      ✓      |   ✓   |    -    |    ✓     |    -    |  (transitive)   |    ✓     |     ✓     |    ✓    |    ✓    | ✓ | - | ✓ |

## Notes (current state per consumer)

### Hardpoint - on 4.0.0

- **Logging:** engine `Log` service (FileSink + ConsoleSink under `AppDataPaths`, `CrashHandler`
  installed), configured in the `HardpointGame` ctor and flushed via `Log.Shutdown` on dispose. Hardpoint
  had no logger before adopting.
- **Content:** first consumer of `KhaozEngine.Content` - build-time JSON-schema validation of its `Data/`.
- **Localization:** swapped its hand-rolled copy for `LocalizationManager` (this corrected the malformed
  default culture `en-EN` to `en-US`).
- **Effects:** `ParticleSystem` drives projectile-hit and enemy-death bursts (`Spark` preset), fed by
  game-side `ProjectileSystem.Hit` / `DamageSystem.EnemyKilled` events.
- **Graphics:** gameplay camera (`Camera2D.Focus` board framing + `CameraController` pan/zoom,
  `VirtualResolution.DesignScaled` desktop scaling); tower range rings via `PrimitiveRenderer.DrawRing`.
- **Sprites:** `DirectionalAnimatedSprite` + `SpriteRegistry` for pixel-art entity rendering.
- **Persistence:** campaign progress via `SettingsManager<CampaignSaveData>` over `FileSettingsStorage` +
  a shared `PersistenceQueue` (`save.json` under `AppDataPaths`); a `sanitizeOnLoad` hook dedupes ids and
  null-guards the list. (The campaign is a 10-level branching graph.)
- **Input:** uses `IDesignViewport`, having dropped its game-side viewport adapter.
- **Not adopted:** `Audio` (no audio assets yet) - the only unreferenced package. It references `Ecs` but
  uses no RNG, so `DeterministicRng`/`CreateDerived` don't apply. `Serialization` arrives transitively;
  `JsonDefaults` not used directly.

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
# engine version (source of truth)
grep -i '<Version>' ~/KhaozEngine/Directory.Build.props

# what each consumer pins
for d in ~/Hardpoint ~/Nullwake ~/SpaceGame; do
  find "$d" -name '*.csproj' -not -path '*/bin/*' -not -path '*/obj/*' \
    -exec grep -l KhaozEngine {} \; | while read f; do
      echo "-- $f"; grep -i KhaozEngine "$f"; done
done
```

After editing, run `./scripts/check-doc-versions.sh` (CI runs it too) to confirm the engine-version line
still matches `Directory.Build.props`.

_Last verified: 2026-06-15. Engine at 4.8.0 (breaking, shipped as a minor since `5.x` is reserved for
the experimental branch: `IChannelSplittable<T>`+`NetChannelReliability` moved `Netcode.LiteNetLib` ->
`Netcode`, `ChannelSplitter` stays in `.LiteNetLib`; earlier: new `Platform` 4.4.0, `Collision`+`Pooling`
4.5.0, `Updates` 4.6.0, `Netcode`+`Netcode.LiteNetLib` 4.7.0); all three consumers on 4.0.0 (4.1.0-4.8.0
unadopted; SpaceGame is the intended first adopter of the netcode packages). Adoption
✓/- matrix matches each game's actual `<PackageReference>` set as of the 2026-06-13 no-dead-reference
pass (every direct `KhaozEngine.*` reference in all three repos is used; the one zero-code-use case,
Nullwake `Content`, is legitimate build-time schema validation)._
