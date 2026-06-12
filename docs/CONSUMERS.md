# KhaozEngine consumers

Which game uses which packages, at which version. Update this whenever a consumer
bumps a `<PackageReference>` or the engine ships a new version.

**Engine current version:** `3.7.0` (all packages share one version, set in `Directory.Build.props`).

> 3.7.0 adds two additive camera/viewport features. `KhaozEngine.Graphics.CameraController`: a
> reusable pan/zoom/pinch gesture controller that drives an existing `Camera2D` from an `InputManager`
> (drag + two-finger pan, wheel + pinch zoom about the cursor/focus, world-bounds clamp via
> `Camera2D.ClampPosition`, and `TryGetTap` tap-vs-pan disambiguation). Graphics now references Input.
> `PannableCanvas` is left as-is this release (consolidation onto `CameraController` is a follow-up).
> `KhaozEngine.Input.VirtualResolution` gains an opt-in desktop design-scale mode via the
> `DesignScaled(...)` factory (fill-the-width, adaptive-height, mirrors mobile) plus a headless
> `Configure(w, h)`; the `isMobile:false` desktop default (scale 1, identity) is unchanged. No consumer
> adopts either yet — Hardpoint's map (on `PannableCanvas`) is the likeliest `CameraController` adopter.

> 3.6.0 adds `KhaozEngine.Ecs.CachedQuery`: a small helper that reuses one `Query` across calls so
> sim hot paths stop allocating a fresh query per tick. Rebuilds only on a `World` instance swap
> (`ReferenceEquals`), so run-reset (`new World()`) stays correct; the underlying `Query` still
> self-refreshes its archetype list on `ArchetypeGen` changes. Additive, no breaking changes.
> SpaceGame adopts it at the 4 per-tick `world.Query()` sites (projectile motion, collision x2,
> run diagnostics x2) — behaviour-identical, pure allocation change.

> 3.4.1 fixes a 3.4.0 bug: `AudioSystem.LoadContent` now drops failed-to-load tracks so its name
> list stays aligned with the backend, keeping `CurrentTrack`/`TrackChanged`/`PlayTrack(name)` correct
> after a partial load failure. No API change. SpaceGame adopts it for the deferred music/now-playing work.

> 3.4.0 extends `KhaozEngine.Persistence` (`SettingsManager<T>` `sanitizeOnLoad` hook) and
> `KhaozEngine.Audio` (`AudioSystem` `PlayTrack`/`PlayMode`/`CurrentTrack`/`TrackChanged`, plus the
> scoped IsPlaying-read latch), and hardens `KhaozEngine.Ecs` `DeterministicRng.Next` argument guards.
> All additive. Nullwake bumped to 3.4.0 (a version-only bump: it picks up the `AudioSystem`
> IsPlaying-read resilience fix for free, and assessed but did not adopt the new music modes; see notes).

> 3.3.0 (Batch 2) adds three new packages and extends two. New: `KhaozEngine.Audio` (`AudioSystem`
> + music backends, incl. the macOS AVAudioPlayer workaround), `KhaozEngine.Effects` (data-driven
> pooled `ParticleSystem` + `Spark`/`Ember` presets), `KhaozEngine.Graphics` (`Camera2D`). Extended:
> `KhaozEngine.Persistence` gains `AtomicJsonWriter`/`PersistenceQueue` + `SettingsManager<T>` (and now
> references `KhaozEngine.App`); `KhaozEngine.Ecs` gains `DeterministicRng.CreateDerived`. The Batch 2
> additions are adopted by Nullwake (App, Localization, Persistence, Audio, Effects); the matrices below
> include columns for all of them.

> 3.2.0 adds three pure-.NET packages from the "promote into KE" batch: `KhaozEngine.App`
> (`BuildMetadata`, `AppDataPaths`, `ServiceLocator`), `KhaozEngine.Localization`
> (`LocalizationManager`), and `KhaozEngine.Persistence` (`SaveEncoder`). It also consolidates
> `AppDataPaths` into `KhaozEngine.App` and removes the duplicate 3.1.0 had shipped in
> `KhaozEngine.Diagnostics`. None of the new packages is adopted by a consumer yet.
>
> 3.1.0 replaced `KhaozEngine.Diagnostics.FileLogger` with the full logging service (`Log`/`LogManager`,
> sinks, `CrashHandler`). Consumer pins below stay at their current versions until each game's migration runs.

## Version matrix

`–` = package not referenced directly by that project. `Time` is pulled in transitively by
`Screens` 2.2.0+; consumers vendor `KhaozEngine.Time` even without a direct reference.

| Project   | Project file                         | Input | Screens | UI    | Ecs   | Content | Diagnostics | Time  | App   | Localization | Persistence | Audio | Effects | Graphics |
|-----------|--------------------------------------|-------|---------|-------|-------|---------|-------------|-------|-------|--------------|-------------|-------|---------|----------|
| Hardpoint | `Hardpoint/Hardpoint.Core`           | 3.4.1 | 3.4.1   | 3.4.1 | 3.4.1 | 3.4.1   | 3.4.1       | –     | 3.4.1 | 3.4.1        | 3.4.1       | –     | 3.4.1   | –        |
| Nullwake  | `Nullwake/Nullwake.Core`             | 3.4.0 | 3.4.0   | 3.4.0 | –     | 3.4.0   | 3.4.0       | 3.4.0 | 3.4.0 | 3.4.0        | 3.4.0       | 3.4.0 | 3.4.0   | –        |
| SpaceGame | `SpaceGame/SpaceGame.Core`           | 3.4.1 | 3.4.1   | 3.4.1 | 3.6.0 | 3.4.1   | 3.4.1       | –     | 3.4.1 | 3.4.1        | 3.4.1       | 3.4.1 | –       | 3.4.1    |

## Adoption matrix

Which packages each consumer pulls in. `✓` = direct `<PackageReference>`, `–` = not used,
`(transitive)` = vendored via `Screens` 2.2.0+ but no direct reference and (for `Time`) no
scaled-dt usage.

| Consumer  | Input | Screens | UI | Ecs | Content | Diagnostics |    Time      | App | Localization | Persistence | Audio | Effects | Graphics |
|-----------|:-----:|:-------:|:--:|:---:|:-------:|:-----------:|:------------:|:---:|:------------:|:-----------:|:-----:|:-------:|:--------:|
| Hardpoint |   ✓   |    ✓    | ✓  |  ✓  |    ✓    |      ✓      | (transitive) |  ✓  |      ✓       |      ✓      |   –   |    ✓    |    –     |
| Nullwake  |   ✓   |    ✓    | ✓  |  –  |    ✓    |      ✓      |      ✓       |  ✓  |      ✓       |      ✓      |   ✓   |    ✓    |    –     |
| SpaceGame |   ✓   |    ✓    | ✓  |  ✓  |    ✓    |      ✓      | (transitive) |  ✓  |      ✓       |      ✓      |   ✓   |    –    |    ✓     |

## Notes

- **Hardpoint** — on 3.4.1. First consumer of `KhaozEngine.Content` (JSON schema validation at
  build). Bumped from 2.4.0 and adopted Diagnostics/App/Localization/Effects. Logging via the engine
  `Log` service (FileSink + ConsoleSink under `AppDataPaths`, `CrashHandler` installed; both configured
  in the `HardpointGame` ctor and flushed via `Log.Shutdown` on dispose) — Hardpoint had no logger
  before. `LocalizationManager` swapped from the hand-rolled copy to `KhaozEngine.Localization`
  (corrected the malformed default culture `en-EN` to `en-US`). `Effects.ParticleSystem` drives
  projectile-hit and enemy-death bursts (`Spark` preset), fed by new game-side `ProjectileSystem.Hit` /
  `DamageSystem.EnemyKilled` events; tower range rings drawn with `UI.PrimitiveRenderer.DrawRing`.
  Campaign progress persists via `Persistence`: `SettingsManager<CampaignSaveData>` over
  `FileSettingsStorage` + a shared `PersistenceQueue` (`save.json` under `AppDataPaths`), seeded into
  `CampaignProgress` on load and written on each level cleared; a `sanitizeOnLoad` hook dedupes ids and
  null-guards the list. The campaign is a 10-level branching graph (linear opener, a two-route split
  that merges, then a final stretch). **Not adopted:** Audio — no audio assets yet. Graphics — the
  map already runs on `PannableCanvas`, which owns its camera; the gameplay board is fixed-size.
  `Ecs.CreateDerived`/the `DeterministicRng` guards — the game uses no RNG.
- **Nullwake** - on 3.4.0. Adopted Input/Screens/UI/Time/Content/Diagnostics/App/Localization/Persistence/Audio/Effects.
  `GameLogger` replaced by engine `Log` + `CrashHandler` (configured via `LogBootstrap`). Uses `AppDataPaths`,
  `ServiceLocator`, `BuildMetadata`, `SaveEncoder` + `AtomicJsonWriter`, `AudioSystem`, and `Effects.ParticleSystem`.
  The 3.4.0 bump is version-only: it gets the `AudioSystem` IsPlaying-read resilience fix for free (a transient
  read error now skips a frame instead of killing music). Assessed but did NOT adopt the new music modes
  (`PlayTrack`/`PlayMode.RepeatOne`/`CurrentTrack`): Nullwake plays pure random background rotation via
  `PlayRandomTrack`, so there is nothing to change. `SettingsManager` `sanitizeOnLoad` does not apply: saves
  go through `LocalSaveSystem` + `AtomicJsonWriter` (sanitize/migrate stays inline in `SaveMigrator`), not
  `SettingsManager`. **Graphics not adopted:** `OreField.RefToScreen` is a non-uniform fit-into-sub-rectangle
  projection, incompatible with `Camera2D`'s uniform full-viewport matrix. **Ecs not yet adopted:**
  `DeterministicRng` swap is a planned follow-up; still on `GameRng`.
- **SpaceGame** — on 3.4.1. Adopted Input/Screens/UI/Ecs/Content/Diagnostics + App/Localization/
  Persistence/Graphics. First consumer of `Graphics` (`Camera2D`, headless with a per-frame Viewport
  sync). Logging via the engine `Log` service (FileSink + ConsoleSink + `CrashHandler`, configured at
  startup; flushes the persistence queue + `Log.Shutdown` on exit). `AppDataPaths` + `BuildMetadata`
  from `App`; `LocalizationManager` from `Localization` (corrected the malformed default culture to
  `en-US`); settings + leaderboard persistence on `SettingsManager<T>` + `FileSettingsStorage` over one
  shared `PersistenceQueue`. `save.json` now also runs on `SettingsManager<SaveData>` with the 3.4.0
  `sanitizeOnLoad` hook (sanitizes on every load incl. the first; `[JsonExtensionData]` + `SchemaVersion`
  round-trip keeps downgrade safety); the hand-rolled `SaveSystem` static was deleted (the `SaveData` DTO
  stays) and writes flow through the shared `PersistenceQueue`. **Music (3.4.1):** runs on
  `KhaozEngine.Audio.AudioSystem` — one instance owns the 4 tracks, loops the per-screen track via
  `PlayMode.RepeatOne`, and is the source of truth for current track + music volume (`MasterVolume *
  MusicVolume`). `MusicPlaybackController` is a thin seam (`PlayContextTrack`/`ApplyVolume`); a
  `MusicCatalog` maps content-asset paths to display names; the now-playing overlay binds to
  `CurrentTrack`/`TrackChanged` (pause = stop/replay via `MusicEnabled`). The in-house `NowPlayingService`
  + `AudioVolumeMixer`'s music path + raw `MediaPlayer` playback were deleted; SFX/ambient volume stays
  game-side (KE.Audio is music-only).
  **Not adopted:** `Ecs.CreateDerived` — it would move SpaceGame's multiplayer lockstep determinism
  baseline. Deterministic lockstep: vendors `KhaozEngine.Time` transitively via Screens, reads no scaled
  dt (no `GameClock`/`TimeScale`/`TimeSkip`), and must keep it that way.
  **Ecs 3.6.0 (CachedQuery) adopted:** the 4 per-tick `world.Query()` sites (`ProjectileMotionSystem`,
  `CollisionSystem` x2, `RunSession.Diagnostics` x2) now reuse `CachedQuery` fields, so the sim no longer
  allocates a query per tick. Pure allocation change, behaviour-identical: `StateHash_MatchesCapturedBaseline`
  unchanged at `4423044029376371829` (the ECS-migration spec-1 baseline, which moved off
  `15235204183988888313` when projectiles migrated to World). Only Ecs bumped 3.4.1 → 3.6.0; the other KE
  packages stay 3.4.1 (Ecs has no cross-KE deps). SpaceGame build 0.21.4.

## Repo locations

| Project   | Path                  | Repo                         |
|-----------|-----------------------|------------------------------|
| Hardpoint | `~/Hardpoint`         | migrated                     |
| Nullwake  | `~/Nullwake/Nullwake` |                              |
| SpaceGame | `~/SpaceGame/SpaceGame`|                             |

## How to refresh this file

```sh
# engine version
grep -i '<Version>' ~/KhaozEngine/Directory.Build.props

# what each consumer pins
for d in ~/Hardpoint ~/Nullwake ~/SpaceGame; do
  find "$d" -name '*.csproj' -not -path '*/bin/*' -not -path '*/obj/*' \
    -exec grep -l KhaozEngine {} \; | while read f; do
      echo "-- $f"; grep -i KhaozEngine "$f"; done
done
```

> Nullwake migrated to 3.3.0 (Diagnostics/App/Localization/Persistence/Audio/Effects all adopted;
> Graphics and Ecs deferred), then version-bumped to 3.4.0 (free AudioSystem IsPlaying-read resilience
> fix; new music modes assessed but not adopted). SpaceGame migrated to 3.3.0 on main (Diagnostics/App/Localization/
> Persistence/Graphics adopted), then to 3.4.0 (save.json onto `SettingsManager<SaveData>` + `sanitizeOnLoad`;
> then 3.4.1 routed music onto `AudioSystem`; Ecs.CreateDerived not adopted to preserve its lockstep baseline).

_Last verified: 2026-06-13. Engine at 3.7.0 (`Graphics.CameraController`: pan/zoom/pinch gesture controller over `Camera2D`; `Input.VirtualResolution` opt-in desktop design-scale). No consumer adopts 3.7.0 yet. Consumer rows: Hardpoint 3.4.1 (adopted Diagnostics/App/Localization/Effects/Persistence; campaign progress persists to save.json, 10-level campaign; Audio/Graphics/Ecs-RNG not adopted), SpaceGame on Ecs 3.6.0 (CachedQuery adopted) with its other KE packages still 3.4.1 (music on `AudioSystem`), Nullwake 3.4.0._
