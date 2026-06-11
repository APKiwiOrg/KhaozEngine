# KhaozEngine consumers

Which game uses which packages, at which version. Update this whenever a consumer
bumps a `<PackageReference>` or the engine ships a new version.

**Engine current version:** `3.4.1` (all packages share one version, set in `Directory.Build.props`).

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
| Hardpoint | `Hardpoint/Hardpoint.Core`           | 2.4.0 | 2.4.0   | 2.4.0 | 2.4.0 | 2.4.0   | –           | –     | –     | –            | –           | –     | –       | –        |
| Nullwake  | `Nullwake/Nullwake.Core`             | 3.4.0 | 3.4.0   | 3.4.0 | –     | 3.4.0   | 3.4.0       | 3.4.0 | 3.4.0 | 3.4.0        | 3.4.0       | 3.4.0 | 3.4.0   | –        |
| SpaceGame | `SpaceGame/SpaceGame.Core`           | 3.4.0 | 3.4.0   | 3.4.0 | 3.4.0 | 3.4.0   | 3.4.0       | –     | 3.4.0 | 3.4.0        | 3.4.0       | –     | –       | 3.4.0    |

## Adoption matrix

Which packages each consumer pulls in. `✓` = direct `<PackageReference>`, `–` = not used,
`(transitive)` = vendored via `Screens` 2.2.0+ but no direct reference and (for `Time`) no
scaled-dt usage.

| Consumer  | Input | Screens | UI | Ecs | Content | Diagnostics |    Time      | App | Localization | Persistence | Audio | Effects | Graphics |
|-----------|:-----:|:-------:|:--:|:---:|:-------:|:-----------:|:------------:|:---:|:------------:|:-----------:|:-----:|:-------:|:--------:|
| Hardpoint |   ✓   |    ✓    | ✓  |  ✓  |    ✓    |      –      | (transitive) |  –  |      –       |      –      |   –   |    –    |    –     |
| Nullwake  |   ✓   |    ✓    | ✓  |  –  |    ✓    |      ✓      |      ✓       |  ✓  |      ✓       |      ✓      |   ✓   |    ✓    |    –     |
| SpaceGame |   ✓   |    ✓    | ✓  |  ✓  |    ✓    |      ✓      | (transitive) |  ✓  |      ✓       |      ✓      |   –   |    –    |    ✓     |

## Notes

- **Hardpoint** — fully migrated, tracks latest (2.4.0). First consumer of
  `KhaozEngine.Content` (JSON schema validation at build). Has not adopted `Diagnostics` (no file
  logger of its own yet; a candidate to migrate).
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
- **SpaceGame** — on 3.4.0. Adopted Input/Screens/UI/Ecs/Content/Diagnostics + App/Localization/
  Persistence/Graphics. First consumer of `Graphics` (`Camera2D`, headless with a per-frame Viewport
  sync). Logging via the engine `Log` service (FileSink + ConsoleSink + `CrashHandler`, configured at
  startup; flushes the persistence queue + `Log.Shutdown` on exit). `AppDataPaths` + `BuildMetadata`
  from `App`; `LocalizationManager` from `Localization` (corrected the malformed default culture to
  `en-US`); settings + leaderboard persistence on `SettingsManager<T>` + `FileSettingsStorage` over one
  shared `PersistenceQueue`. `save.json` now also runs on `SettingsManager<SaveData>` with the 3.4.0
  `sanitizeOnLoad` hook (sanitizes on every load incl. the first; `[JsonExtensionData]` + `SchemaVersion`
  round-trip keeps downgrade safety); the hand-rolled `SaveSystem` static was deleted (the `SaveData` DTO
  stays) and writes flow through the shared `PersistenceQueue`. **Deferred to a 3.4.1 follow-up:** music
  (`AudioVolumeMixer`/`MusicPlaybackController` → KE.Audio `PlayTrack`/`PlayMode.RepeatOne` + a now-playing
  overlay wired to `CurrentTrack`/`TrackChanged`); SFX volume stays game-side (KE.Audio is music-only).
  **Not adopted:** `Ecs.CreateDerived` — it would move SpaceGame's multiplayer lockstep determinism
  baseline. Deterministic lockstep: vendors `KhaozEngine.Time` transitively via Screens, reads no scaled
  dt (no `GameClock`/`TimeScale`/`TimeSkip`), and must keep it that way.

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
> music deferred to a 3.4.1 follow-up; Ecs.CreateDerived not adopted to preserve its lockstep baseline).

_Last verified: 2026-06-11. Engine at 3.4.1 (AudioSystem partial-load name-alignment fix). SpaceGame row bumped to 3.4.0 (save.json adopted). Nullwake row at 3.4.0; Hardpoint unchanged._
