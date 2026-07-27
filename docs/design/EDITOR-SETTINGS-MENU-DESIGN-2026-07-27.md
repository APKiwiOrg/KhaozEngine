# Map editor settings menu, default sky, and the horizon artifact (2026-07-27)

Status: in flight (17.6.0). Closes #364.

## Problem

Looking at the ocean horizon in the map editor shows a jagged black band where water meets the
background, with scattered white squares in the black sky above. The editor also has no settings
UI at all: render distance shipped in 17.4.0 as `MapEditorOptions.RenderDistance` with no way to
change it at runtime (#364), and sky, lighting, and ocean appearance are not adjustable.

## Root cause (verified in code)

- The white squares are the engine's procedural starfield. `PixelPostProcessSettings.Starfield`
  defaults to true and `SkySettings.Enabled` defaults to false, and the map editor never touches
  `Post`, so the background behind the terrain is a space backdrop
  (`ShaderSources.Sky.cs` `StarfieldFrag`: whole 220x124 grid cells lit by a hash, hence squares).
- The jagged band is the far plane (500 m) cutting through wave-displaced water geometry.
  `RenderDistanceProfile` deliberately places the ocean rim (600 m) past `FarClip`, so the visible
  water edge is the far-plane cut across displaced coarse quads, against a near-black clear color.
- The unification gap: `WaterRenderer.PackUbo` reads `SkySettings.HorizonColor`/`ZenithColor`
  unconditionally, so distant water already fades toward a blue sky gradient that is never painted
  behind it. Enabling the sky makes water and background meet in the same palette, which is the fix.

## Decisions

- Default sky preset is **Day** (sky gradient plus sun disc on, starfield off). Starfield remains
  available as a preset. Confirmed by the user 2026-07-27.
- Editor settings **persist across sessions** in their own `editor-settings.json`, following the
  `EditorRecentFiles` pattern on `ISettingsStorage`.
- Ocean options are **presets plus key sliders** (Calm/Moderate/Rough, swell amplitude, foam
  strength), not the full `WaterSettings` knob set.
- Render distance options are **Base / 2x / 4x** of the configured profile, applied live.

## Architecture

### Engine (KhaozEngine.Terrain)

`RenderDistanceProfile.Scaled(float multiplier)`: returns a validated profile with `FarClip`,
`OceanHalfExtent`, and `PropDrawRadius` scaled linearly, `DecorRadiusChunks` and
`UnloadRadiusChunks` scaled with ceiling in chunk units so the hysteresis and
`OceanHalfExtent <= DecorRadiusMeters` invariants hold, and `GameplayLoadRadiusChunks` unchanged
(fixed across tiers by design). A blind per-field multiply fails `Validate()`, so the scaling
lives next to the invariants it must respect.

### Engine (KhaozEngine.Render3D)

`EnvironmentPresets`: named sky plus lighting bundles (Day, Sunset, Night, Starfield) applied to
`PixelPostProcessSettings` (sky palette, sun disc, key/fill/ambient light values, starfield flag).
`OceanPresets`: Calm/Moderate/Rough bundles applied to `WaterSettings` (swell, ripple, foam
values). Presentation-only data, no new rendering machinery. A sun-direction helper converts
azimuth and elevation degrees to `LightDirection` (reusing `SunCycle` math where it fits) so a
sun-angle slider stays in sync with the sky's sun disc and the water glint, which both read
`Post.LightDirection` already.

### Editor (KhaozEngine.MapEditor)

- **Environment seam.** `MapEditorScene` historically never touched `Post`. That changes to an
  explicit, opt-outable responsibility: `MapEditorOptions.DriveEnvironment` (default true). When
  true, the editor applies its environment state (preset plus slider overrides) to the host
  `Scene3D.Post` each frame it draws. Embedded hosts that want to keep ownership set it false.
- **Settings dialog.** A scene-owned modal (`PopupPanel`, same shape as the exit dialog) with
  sections: Render distance (Base/2x/4x), Sky (preset, sun azimuth/elevation), Lighting (key light
  intensity, ambient intensity), Ocean (preset, swell amplitude, foam strength).
- **Esc routing.** Bare Esc opens the settings dialog only when no tool gesture is active and no
  editor field is focused. An active gesture still consumes Esc as cancel. Shift+Esc keeps opening
  the exit dialog. While the settings dialog is open, Esc closes it (existing `PopupPanel.HandleKeys`).
- **Persistence.** `EditorSettings` record persisted to `editor-settings.json` via the coalesced
  persistence queue, loaded on scene enter, saved on change.
- **Live render distance.** Selecting 2x/4x rebuilds through the existing streamer rebuild path
  with `profile.Scaled(m)`, updates the camera `FarPlane` (an independent copy today), and scales
  the editor tile window with it. This is exactly the #363 coupling, handled here rather than
  left implicit.
- Surf controls are only exposed if bathymetry can be wired from existing editor terrain data
  cheaply. The editor never sets `WaterSettings.Bathymetry`, so shoaling and breaking surf are
  currently inert in this scene. If wiring is not cheap, the surf knob is omitted and a follow-up
  issue records the gap.

## Verification

- Headless tests: profile scaling invariants, preset application, Esc routing, settings
  round-trip, render distance apply.
- Visual: windowed run with screenshot of the ocean horizon proving the black band and starfield
  squares are gone under the Day sky. The premise "enabling the sky hides the far-plane cut" is
  design intent, not yet observed, so it is proven by rendering before release.

## Out of scope

- Cubemap/skybox (#50 stays deferred).
- FFT ocean tuning UI beyond the preset/slider set.
- Bathymetry-driven surf in the editor if it is not cheaply wireable (issue instead).
