# KhaozEngine consumers

Which game uses which packages, at which version. Update this whenever a consumer
bumps a `<PackageReference>` or the engine ships a new version.

**Engine current version:** `4.0.0` (all packages share one version, set in `Directory.Build.props`).

> 4.0.0 (breaking) is an inter-package tidy-up, no runtime behavior change. `PrimitiveRenderer` and
> `ColorHelper` move from `KhaozEngine.UI` to `KhaozEngine.Graphics` (namespace change only; UI already
> depends on Graphics, so add a `using KhaozEngine.Graphics;`). `KhaozEngine.Effects` now depends on
> `KhaozEngine.Graphics` instead of `KhaozEngine.UI` (its only UI use was `PrimitiveRenderer`). New leaf
> package `KhaozEngine.Serialization` holds `JsonDefaults` (shared `System.Text.Json` baselines);
> `KhaozEngine.Content`/`.Persistence`/`.Ecs` now consume it and gain a dependency on it. **Hardpoint
> adopts 4.0.0** (first adopter): added `using KhaozEngine.Graphics;` to the 9 files using
> `PrimitiveRenderer`/`ColorHelper`; `Serialization` arrives transitively (via Content/Persistence/Ecs),
> `JsonDefaults` not adopted. Tests green (95). **Nullwake also on 4.0.0:** `using KhaozEngine.Graphics;`
> added to the 21 files that draw primitives, plus an explicit `KhaozEngine.Graphics` reference (it uses
> `PrimitiveRenderer`/`ColorHelper` directly). Tests green (90). Nullwake since adopted three more shipped
> features: `PannableCanvas` (skill-tree pan, zoom off for sharp text), `DisplayManager` (desktop window +
> min-size floor), and `JsonDefaults` (a direct `KhaozEngine.Serialization` ref: `TolerantRead` in its
> `ConfigLoader`, `IndentedWrite` in `LocalSaveSystem`).
> **SpaceGame also on 4.0.0:** all KE packages unified to 4.0.0 (Ecs moved 3.6.0 ->
> 4.0.0, the rest 3.4.1 -> 4.0.0); `using KhaozEngine.Graphics;` added to the 4 files using
> `PrimitiveRenderer` (it already referenced `KhaozEngine.Graphics`); `Serialization` transitive,
> `JsonDefaults` not adopted. All three consumers are now on 4.0.0.

> 3.12.0 adds `SpriteRegistry` to `KhaozEngine.Sprites`: a keyed store of `DirectionalAnimatedSprite`
> (`Add`/`Get`/`Contains`/`Count`) with one bulk `Update(deltaSeconds)` that advances every registered
> sprite once per frame. Takes already-built sprites; loading by embedded-resource manifest name stays
> game-side. Centralizes the dictionary + bulk-advance Hardpoint hand-rolls in `SpriteLibrary`.
> Additive, MonoGame-only. Adoption target: Hardpoint `SpriteLibrary`. No consumer adopts yet.

> 3.11.0 adds `KhaozEngine.Input.IDesignViewport` — a 4-member seam (`Width`, `Height`, `Scale`,
> `ScaleMatrix`) that `VirtualResolution` now implements (no behavior change; its existing properties
> already satisfy it). Screens needing only design-space size/scale/matrix can depend on the interface
> and tests fake it directly. Hardpoint can drop its game-side `IViewport` + `VirtualResolutionViewport`
> adapter (which existed solely for headless screen tests) and reference the engine interface. Additive.
> No consumer adopts yet.

> 3.10.0 centralizes the pan/zoom/clamp/tap gesture math: `PannableCanvas` (UI) and `CameraController`
> (Graphics) both drive a `Camera2D` through shared `PinchGestureTracker` / `CameraGestures` helpers
> plus new `Camera2D.PanByScreenDelta` / `ZoomAboutScreenPoint`. `PannableCanvas` gains real pinch zoom
> (`MinZoom`/`MaxZoom`/`EnableZoom`/`EnablePan`, `Camera` accessor); drag/wheel/tap stay byte-identical.
> New `KhaozEngine.UI -> KhaozEngine.Graphics` package dependency. `Camera2D.GetViewMatrix` now honors
> viewport X/Y (only affects inset viewports; whole-screen X=Y=0 unchanged). No consumer adopts yet —
> Hardpoint's map (on `PannableCanvas`) gets pinch zoom for free once it bumps; opt out via `EnableZoom = false`.

> 3.9.0 extends `KhaozEngine.Graphics` with camera framing + follow. `Camera2D` gains `CenterOn(world)`
> and `Focus(rect, viewport, paddingFraction, minZoom, maxZoom)` (fit-to-rect contain zoom) — the framing
> math Hardpoint hand-rolled as `BoardFraming`, SpaceForge does inline, and `PannableCanvas.Focus(rect)`
> has a dormant seam for. New `CameraFollow` drives a `Camera2D` to follow a target with frame-rate-
> independent smoothing (`1 - exp(-Stiffness*dt)`) + an optional screen-space deadzone + bounds clamp.
> Additive. Adoption targets: SpaceForge → `CameraController` + `Camera2D.Focus`; Hardpoint
> `BoardView`/`BoardFraming` → `CameraController` + `Camera2D.Focus`; SpaceGame gameplay follow →
> `CameraFollow`; `PannableCanvas` consolidation depends on `Camera2D.Focus`. No consumer adopts yet.

> 3.8.0 adds a new package `KhaozEngine.Sprites`: 2D sprite + directional-animation playback.
> `Direction8` (8 facings + `FromVector` nearest-of-8 in y-down screen space), `SpriteSheet` /
> `SpriteSheetLayout` (grid -> source rects, headless math), `SpriteAnimation` +
> `SpriteAnimationPlayer` (GameTime/float-delta frame clock, looping + one-shot), and
> `DirectionalAnimatedSprite` (one animation per direction, draws the right frame via `SpriteBatch`,
> centered origin, phase preserved across facing changes). `PixelLabSpriteLoader` builds one from a
> PixelLab export (assembled grid sheet or loose per-direction frames), isolating PixelLab's
> `S,SE,E,NE,N,NW,W,SW` row order to one place. Additive, no breaking changes. No consumer adopts yet;
> Hardpoint is the first adopter (pixel-art entity rendering, replacing `PrimitiveRenderer` colored
> rects). PixelLab exports loose per-frame PNGs (not a canonical sheet) so the grid-sheet row order
> should be verified against a real export on first adoption.

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

## Notes

- **Hardpoint** — on 4.0.0. First consumer of `KhaozEngine.Content` (JSON schema validation at
  build). Bumped from 2.4.0 and adopted Diagnostics/App/Localization/Effects. Logging via the engine
  `Log` service (FileSink + ConsoleSink under `AppDataPaths`, `CrashHandler` installed; both configured
  in the `HardpointGame` ctor and flushed via `Log.Shutdown` on dispose) — Hardpoint had no logger
  before. `LocalizationManager` swapped from the hand-rolled copy to `KhaozEngine.Localization`
  (corrected the malformed default culture `en-EN` to `en-US`). `Effects.ParticleSystem` drives
  projectile-hit and enemy-death bursts (`Spark` preset), fed by new game-side `ProjectileSystem.Hit` /
  `DamageSystem.EnemyKilled` events; tower range rings drawn with `Graphics.PrimitiveRenderer.DrawRing`.
  Campaign progress persists via `Persistence`: `SettingsManager<CampaignSaveData>` over
  `FileSettingsStorage` + a shared `PersistenceQueue` (`save.json` under `AppDataPaths`), seeded into
  `CampaignProgress` on load and written on each level cleared; a `sanitizeOnLoad` hook dedupes ids and
  null-guards the list. The campaign is a 10-level branching graph (linear opener, a two-route split
  that merges, then a final stretch). **Not adopted:** Audio — no audio assets yet. Graphics — the
  map already runs on `PannableCanvas`, which owns its camera; the gameplay board is fixed-size.
  `Ecs.CreateDerived`/the `DeterministicRng` guards — the game uses no RNG.
  Since the 3.4.1 snapshot above, Hardpoint walked the chain to **4.0.0**: it now references
  `Graphics` directly (gameplay camera — `Camera2D.Focus` board framing + `CameraController` pan/zoom,
  `VirtualResolution.DesignScaled` desktop, from 3.9.0/3.10.0) and `Sprites` (`DirectionalAnimatedSprite`
  + `SpriteRegistry` for pixel-art entity rendering, 3.8.0/3.12.0), and took `Input.IDesignViewport`
  (3.11.0, dropping the game-side viewport adapter). The "Graphics not adopted" line above is therefore
  superseded; **Audio** is still the only unadopted package (no audio assets yet). 4.0.0 itself is a
  version+namespace bump: `PrimitiveRenderer`/`ColorHelper` moved to `KhaozEngine.Graphics` (using-only
  fix across 9 files), `Serialization` transitive, `JsonDefaults` not adopted.
- **Nullwake** - on 4.0.0. Adopted Input/Screens/UI/Graphics/Time/Content/Diagnostics/App/Localization/Persistence/Audio/Effects.
  `GameLogger` replaced by engine `Log` + `CrashHandler` (configured via `LogBootstrap`). Uses `AppDataPaths`,
  `ServiceLocator`, `BuildMetadata`, `SaveEncoder` + `AtomicJsonWriter`, `AudioSystem`, and `Effects.ParticleSystem`.
  Walked 3.4.0 -> 4.0.0 directly (everything 3.5.0-3.12.0 was additive, only 4.0.0 breaking). The 4.0.0 bump
  is the `PrimitiveRenderer`/`ColorHelper` namespace move: added `using KhaozEngine.Graphics;` to the 21 files
  that draw primitives, plus an explicit `KhaozEngine.Graphics` reference (Nullwake uses those types directly
  rather than transitively via UI). Then adopted three more shipped features: **`PannableCanvas`** (UI) for
  both skill-tree screens' pan/clamp/world-screen/tap, replacing the hand-rolled `_cameraOffset` math they
  carried (the code `PannableCanvas` was generalized from in 2.4.0); `EnableZoom = false` keeps nodes in the
  sharp screen-space batch (a zoom matrix would blur SpriteFont glyphs). **`DisplayManager` + `DisplaySettings`**
  (Graphics) for desktop window config + the min-size floor (mobile keeps only the portrait line; a fixed
  backbuffer would break fill-to-screen scaling). **`JsonDefaults`** (Serialization, now a direct ref):
  `TolerantRead` for `ConfigLoader`, `IndentedWrite` for `LocalSaveSystem` (byte-identical to the prior inline
  options). **`Content` is build-time only:** referenced for its MSBuild schema-validation target (validates
  `Data/*.json` against `Data/schemas/` at build, 10+ schemas); Nullwake uses its own `ConfigLoader`, not
  `KhaozEngine.Content.ConfigLoader`, so the package has no runtime/code use. The earlier-skipped 3.4.0 audio
  music modes and `SettingsManager` `sanitizeOnLoad` still do not
  apply (pure random rotation via `PlayRandomTrack`; saves go through `LocalSaveSystem` + `AtomicJsonWriter`,
  not `SettingsManager`). **Camera2D not adopted for the mining view:** `OreField.RefToScreen` is a non-uniform
  fit-into-sub-rectangle projection, incompatible with `Camera2D`'s uniform full-viewport matrix (`PannableCanvas`
  does drive a `Camera2D` internally, but only over the skill-tree node space). **Ecs not yet adopted:**
  `DeterministicRng` swap is a planned follow-up; still on `GameRng`.
- **SpaceGame** (on 4.0.0). Adopted Input/Screens/UI/Ecs/Content/Diagnostics + App/Localization/
  Persistence/Graphics/Audio. First consumer of `Graphics` (`Camera2D`, headless with a per-frame Viewport
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
  `15235204183988888313` when projectiles migrated to World). The CachedQuery work shipped while SpaceGame
  was on Ecs 3.6.0 (rest of KE at 3.4.1). **Now unified to 4.0.0:** all KE packages bumped together (Ecs
  3.6.0 -> 4.0.0, the rest 3.4.1 -> 4.0.0). The 4.0.0 step is the `PrimitiveRenderer`/`ColorHelper`
  namespace move (`using KhaozEngine.Graphics;` added to 4 files; `Graphics` was already referenced);
  `Serialization` arrives transitively, `JsonDefaults` not adopted. Ecs 4.0.0 changes nothing SpaceGame
  relies on (only `WorldSerializer`'s internal JSON defaults moved), so the lockstep `StateHash` baseline
  is unaffected. **Still not adopted:** `Effects` (own `ParticleManager`), `Sprites`, and
  `Ecs.CreateDerived` (would move the lockstep determinism baseline).

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

_Last verified: 2026-06-13. Engine at 4.0.0 (`PrimitiveRenderer`/`ColorHelper` moved `UI` -> `Graphics`; new leaf `KhaozEngine.Serialization` with `JsonDefaults`, consumed by Content/Persistence/Ecs). **All three consumers verified on 4.0.0 against their current csprojs this session.** Hardpoint (first 4.0.0 adopter; walked 3.4.1 -> 4.0.0 picking up Graphics gameplay camera, Sprites + `SpriteRegistry`, `Input.IDesignViewport`; Audio still not adopted). Nullwake (walked 3.4.0 -> 4.0.0; `PrimitiveRenderer`/`ColorHelper` move across 21 files + explicit Graphics ref; **adopted `JsonDefaults`** via explicit `Serialization` ref; Camera2D/Ecs still deferred). SpaceGame (unified all KE packages to 4.0.0 from a 3.4.1 + Ecs 3.6.0 split; `PrimitiveRenderer` move across 4 files; Effects/Sprites/`Ecs.CreateDerived` still not adopted). Adoption ✓/– matrix matches each game's actual `<PackageReference>` set. **No-dead-reference pass (2026-06-13): every direct `KhaozEngine.*` reference in all three repos is used; the one zero-code-use case (Nullwake `Content`) is legitimate build-time schema validation.**_
