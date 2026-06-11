# KhaozEngine consumers

Which game uses which packages, at which version. Update this whenever a consumer
bumps a `<PackageReference>` or the engine ships a new version.

**Engine current version:** `3.4.0` (all packages share one version, set in `Directory.Build.props`).

> 3.4.0 extends `KhaozEngine.Persistence` (`SettingsManager<T>` `sanitizeOnLoad` hook) and
> `KhaozEngine.Audio` (`AudioSystem` `PlayTrack`/`PlayMode`/`CurrentTrack`/`TrackChanged`, plus the
> scoped IsPlaying-read latch), and hardens `KhaozEngine.Ecs` `DeterministicRng.Next` argument guards.
> All additive; no consumer has adopted 3.4.0 yet, so the matrices below are unchanged.

> 3.3.0 (Batch 2) adds three new packages and extends two. New: `KhaozEngine.Audio` (`AudioSystem`
> + music backends, incl. the macOS AVAudioPlayer workaround), `KhaozEngine.Effects` (data-driven
> pooled `ParticleSystem` + `Spark`/`Ember` presets), `KhaozEngine.Graphics` (`Camera2D`). Extended:
> `KhaozEngine.Persistence` gains `AtomicJsonWriter`/`PersistenceQueue` + `SettingsManager<T>` (and now
> references `KhaozEngine.App`); `KhaozEngine.Ecs` gains `DeterministicRng.CreateDerived`. None of the
> Batch 2 additions is adopted by a consumer yet, so the matrices below are unchanged; add columns when
> a game first pins one.

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

| Project   | Project file                         | Input | Screens | UI    | Ecs   | Content | Diagnostics | Time  | App   | Localization | Persistence | Audio | Effects |
|-----------|--------------------------------------|-------|---------|-------|-------|---------|-------------|-------|-------|--------------|-------------|-------|---------|
| Hardpoint | `Hardpoint/Hardpoint.Core`           | 2.4.0 | 2.4.0   | 2.4.0 | 2.4.0 | 2.4.0   | –           | –     | –     | –            | –           | –     | –       |
| Nullwake  | `Nullwake/Nullwake.Core`             | 3.3.0 | 3.3.0   | 3.3.0 | –     | 3.3.0   | 3.3.0       | 3.3.0 | 3.3.0 | 3.3.0        | 3.3.0       | 3.3.0 | 3.3.0   |
| SpaceGame | `SpaceGame/SpaceGame.Core`           | 3.0.0 | 3.0.0   | 3.0.0 | 3.0.0 | 3.0.0   | 3.0.0       | –     | –     | –            | –           | –     | –       |

## Adoption matrix

Which packages each consumer pulls in. `✓` = direct `<PackageReference>`, `–` = not used,
`(transitive)` = vendored via `Screens` 2.2.0+ but no direct reference and (for `Time`) no
scaled-dt usage.

| Consumer  | Input | Screens | UI | Ecs | Content | Diagnostics |    Time      | App | Localization | Persistence | Audio | Effects |
|-----------|:-----:|:-------:|:--:|:---:|:-------:|:-----------:|:------------:|:---:|:------------:|:-----------:|:-----:|:-------:|
| Hardpoint |   ✓   |    ✓    | ✓  |  ✓  |    ✓    |      –      | (transitive) |  –  |      –       |      –      |   –   |    –    |
| Nullwake  |   ✓   |    ✓    | ✓  |  –  |    ✓    |      ✓      |      ✓       |  ✓  |      ✓       |      ✓      |   ✓   |    ✓    |
| SpaceGame |   ✓   |    ✓    | ✓  |  ✓  |    ✓    |      ✓      | (transitive) |  –  |      –       |      –      |   –   |    –    |

## Notes

- **Hardpoint** — fully migrated, tracks latest (2.4.0). First consumer of
  `KhaozEngine.Content` (JSON schema validation at build). Has not adopted `Diagnostics` (no file
  logger of its own yet; a candidate to migrate).
- **Nullwake** — on 3.3.0. Adopted Input/Screens/UI/Time/Content/Diagnostics/App/Localization/Persistence/Audio/Effects.
  `GameLogger` replaced by engine `Log` + `CrashHandler` (configured via `LogBootstrap`). Uses `AppDataPaths`,
  `ServiceLocator`, `BuildMetadata`, `SaveEncoder` + `AtomicJsonWriter`, `AudioSystem`, and `Effects.ParticleSystem`.
  **Graphics not adopted:** `OreField.RefToScreen` is a non-uniform fit-into-sub-rectangle projection,
  incompatible with `Camera2D`'s uniform full-viewport matrix. **Ecs not yet adopted:** `DeterministicRng`
  swap is a planned follow-up; still on `GameRng`.
- **SpaceGame** — uses Input/Screens/UI/Ecs/Content + `Diagnostics` (3.0.0). UI is `TextInputHandler`
  for its prompt screens. First consumer of `KhaozEngine.Diagnostics`: its logging goes through the
  engine `Log` service (`KhaozEngine.Diagnostics`); the game configures sinks + `AppDataPaths` at
  startup and logs via `Log`. Deterministic lockstep: vendors `KhaozEngine.Time` transitively via
  Screens but reads no scaled dt (no `GameClock`/`TimeScale`/`TimeSkip` usage) and must keep it that way.

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
> Graphics and Ecs deferred). SpaceGame's Bucket A (TextInputHandler→engine, UI/Content) work is on
> feature branches, re-pinned to 3.0.0; not yet merged.

_Last verified: 2026-06-11 against engine 3.3.0 (consumer rows checked against each game's main checkout)._
