# KhaozEngine consumers

Which game uses which packages, at which version. Current state only — for the per-version story see
[`../CHANGELOG.md`](../CHANGELOG.md). Update this whenever a consumer bumps a `<PackageReference>` or the
engine ships a new version.

**Engine current version:** `4.1.0` (all packages share one version, set in `Directory.Build.props`).

> The latest release (4.1.0, additive logging normalization) is **not adopted by any consumer yet** — all
> three are on 4.0.0. A game adopts a release on its own schedule by bumping its pinned version; the
> matrices below show what each game actually pins, which is expected to lag the engine.

## Version matrix

`–` = package not referenced directly by that project. `Time` is pulled in transitively by
`Screens` 2.2.0+; consumers vendor `KhaozEngine.Time` even without a direct reference. `Serialization`
(4.0.0+) is transitive via `Content`/`Persistence`/`Ecs`, so it shows `–` for consumers that don't
reference it directly (Nullwake does reference it directly for `JsonDefaults`).

| Project   | Project file                         | Input | Screens | UI    | Ecs   | Content | Diagnostics | Time  | App   | Localization | Persistence | Audio | Effects | Graphics | Sprites | Serialization |
|-----------|--------------------------------------|-------|---------|-------|-------|---------|-------------|-------|-------|--------------|-------------|-------|---------|----------|---------|---------------|
| Hardpoint | `Hardpoint/Hardpoint.Core`           | 4.0.0 | 4.0.0   | 4.0.0 | 4.0.0 | 4.0.0   | 4.0.0       | –     | 4.0.0 | 4.0.0        | 4.0.0       | –     | 4.0.0   | 4.0.0    | 4.0.0   | –             |
| Nullwake  | `Nullwake/Nullwake.Core`             | 4.0.0 | 4.0.0   | 4.0.0 | –     | 4.0.0   | 4.0.0       | 4.0.0 | 4.0.0 | 4.0.0        | 4.0.0       | 4.0.0 | 4.0.0   | 4.0.0    | –       | 4.0.0         |
| SpaceGame | `SpaceGame/SpaceGame.Core`           | 4.0.0 | 4.0.0   | 4.0.0 | 4.0.0 | 4.0.0   | 4.0.0       | –     | 4.0.0 | 4.0.0        | 4.0.0       | 4.0.0 | –       | 4.0.0    | –       | –             |

## Adoption matrix

Which packages each consumer pulls in. `✓` = direct `<PackageReference>`, `–` = not used,
`(transitive)` = vendored via `Screens` 2.2.0+ but no direct reference and (for `Time`) no
scaled-dt usage.

| Consumer  | Input | Screens | UI | Ecs | Content | Diagnostics |    Time      | App | Localization | Persistence | Audio | Effects | Graphics | Sprites |  Serialization  |
|-----------|:-----:|:-------:|:--:|:---:|:-------:|:-----------:|:------------:|:---:|:------------:|:-----------:|:-----:|:-------:|:--------:|:-------:|:---------------:|
| Hardpoint |   ✓   |    ✓    | ✓  |  ✓  |    ✓    |      ✓      | (transitive) |  ✓  |      ✓       |      ✓      |   –   |    ✓    |    ✓     |    ✓    |  (transitive)   |
| Nullwake  |   ✓   |    ✓    | ✓  |  –  |    ✓    |      ✓      |      ✓       |  ✓  |      ✓       |      ✓      |   ✓   |    ✓    |    ✓     |    –    |        ✓        |
| SpaceGame |   ✓   |    ✓    | ✓  |  ✓  |    ✓    |      ✓      | (transitive) |  ✓  |      ✓       |      ✓      |   ✓   |    –    |    ✓     |    –    |        –        |

## Notes (current state per consumer)

### Hardpoint — on 4.0.0

- **Logging:** engine `Log` service (FileSink + ConsoleSink under `AppDataPaths`, `CrashHandler`
  installed), configured in the `HardpointGame` ctor and flushed via `Log.Shutdown` on dispose. Hardpoint
  had no logger before adopting.
- **Content:** first consumer of `KhaozEngine.Content` — build-time JSON-schema validation of its `Data/`.
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
- **Not adopted:** `Audio` (no audio assets yet) — the only unreferenced package. It references `Ecs` but
  uses no RNG, so `DeterministicRng`/`CreateDerived` don't apply. `Serialization` arrives transitively;
  `JsonDefaults` not used directly.

### Nullwake — on 4.0.0

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
  package has no runtime/code use — a legitimate build-time-only reference.
- **Not adopted:** `Camera2D` for the mining view — `OreField.RefToScreen` is a non-uniform
  fit-into-a-sub-rectangle projection, incompatible with `Camera2D`'s uniform full-viewport matrix
  (`PannableCanvas` does drive a `Camera2D` internally, but only over the skill-tree node space). `Ecs`
  not yet adopted — a `DeterministicRng` swap (off `GameRng`) is a planned follow-up. `Sprites` unused.

### SpaceGame — on 4.0.0

- **Graphics:** first consumer of `Camera2D` (headless, with a per-frame `Viewport` sync).
- **Logging:** engine `Log` service (FileSink + ConsoleSink + `CrashHandler`); flushes the persistence
  queue + `Log.Shutdown` on exit. `AppDataPaths` + `BuildMetadata` from `App`; `LocalizationManager`
  (corrected the malformed default culture to `en-US`).
- **Persistence:** settings, leaderboard, and `save.json` all on `SettingsManager<T>` + `FileSettingsStorage`
  over one shared `PersistenceQueue`; `save.json` uses the `sanitizeOnLoad` hook (`[JsonExtensionData]` +
  `SchemaVersion` round-trip for downgrade safety). The hand-rolled `SaveSystem` static was deleted (the
  `SaveData` DTO stays).
- **Audio:** music runs on `AudioSystem` — one instance owns the 4 tracks, loops the per-screen track via
  `PlayMode.RepeatOne`, and is the source of truth for current track + music volume. `MusicPlaybackController`
  is a thin seam; `MusicCatalog` maps content-asset paths to display names; the now-playing overlay binds to
  `CurrentTrack`/`TrackChanged`. The in-house `NowPlayingService`, `AudioVolumeMixer`'s music path, and raw
  `MediaPlayer` playback were deleted. SFX/ambient volume stays game-side (KE.Audio is music-only).
- **Ecs:** uses `CachedQuery` at the 4 per-tick `world.Query()` sites (`ProjectileMotionSystem`,
  `CollisionSystem` ×2, `RunSession.Diagnostics` ×2), so the sim allocates no query per tick. Lockstep
  `StateHash` baseline unchanged at `4423044029376371829`.
- **Not adopted:** `Effects` (keeps its richer game-side `ParticleManager` — see the particle-unification
  roadmap item), `Sprites`, and `Ecs.CreateDerived`. Deterministic multiplayer lockstep is why
  `CreateDerived` is held back (it would move the determinism baseline) and why SpaceGame vendors `Time`
  transitively but reads no scaled dt (no `GameClock`/`TimeScale`/`TimeSkip`) — it must keep it that way.

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

_Last verified: 2026-06-14. Engine at 4.1.0; all three consumers on 4.0.0 (4.1.0 unadopted). Adoption
✓/– matrix matches each game's actual `<PackageReference>` set as of the 2026-06-13 no-dead-reference
pass (every direct `KhaozEngine.*` reference in all three repos is used; the one zero-code-use case,
Nullwake `Content`, is legitimate build-time schema validation)._
