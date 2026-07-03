# Clearing + CC0 houses + post-fx, retire Render3DSample (sub-project B2b)

Status: approved design, pre-plan.
Parent effort: consolidate every windowed demo into `KhaozEngine.Showcase`. B1 (2D rooms) + B2a (3D
World room, retired TerrainWalkSample) shipped. This is **B2b**: enrich the 3D World room with a town
(clearing + CC0 houses) and fold Render3DSample's post-fx, then retire Render3DSample. Follow-on:
**B2c** (in-app networked room, retire NetworkedWalkSample + NetworkedWalkServer).

## Problem

The 3D World room (`Room3D`) is a bare walkable overworld. Render3DSample is still a separate windowed
app (a model-grid orbit viewer with retro post-fx toggles). Goal: give the world a village to walk
into (a `FlattenFeature` clearing + hand-placed CC0 buildings with collision), fold Render3DSample's
post-fx toggles into the room, and retire Render3DSample so the Run/Debug dropdown loses another entry.

## Goals

1. A flat town clearing (`FlattenFeature`) near spawn in Room3D's terrain, with the tree scatter
   excluded from the town footprint.
2. All 7 CC0 Quaternius Medieval Village buildings (inn, bell_tower, blacksmith, house_1/2/3, well)
   hand-placed in the clearing, rendered, and solid (baked `.coll` collision statics you cannot walk
   through).
3. Render3DSample's post-fx toggles (retro combo + palette cycle) folded into Room3D, with OnExit
   resetting every mutated Post field.
4. Render3DSample retired; solution builds green; no engine version bump (sample-only).

Non-goals (deferred): the networked room (B2c), water/shadows (#3b/#3c), any engine change, a separate
model-viewer room.

## Design

### Clearing

`Room3D.OnEnter` composes its terrain config as `TerrainPresets.BoundedClearing()` **plus a
`FlattenFeature`** (append to `TerrainConfig.Features`) that levels a disc near spawn to a flat town
height, smoothstep-faded at its rim. Parameters follow Ruinborne's proven town (radius ~34, blend
~0.25, a flat inner disc large enough for the placed buildings + their scale), centered so the player
spawns in or beside it and it does not clash with `BoundedClearing`'s existing lake / rim wall (tuned
during implementation against the actual preset). This is a sample-side config change - the engine's
`TerrainPresets` is not modified.

The tree scatter (`ScatterConfig.ForestRing()`) gets a `ClearingRadius`/`ClearingCenter` matching the
town so trees do not spawn inside buildings.

### CC0 houses (all 7)

Copy Ruinborne's Quaternius Medieval Village assets into `KhaozEngine.Showcase/assets/buildings/`:
per building a `<id>.glb` (render mesh) + `<id>.coll` (baked compound-of-convex collision). Add a
`buildings.manifest.json` (id, file, heightMeters per building) + a CREDITS.md (CC0 Quaternius, via
world-of-claudecraft), and the `<None Include="assets/buildings/**" CopyToOutputDirectory>` item in
`KhaozEngine.Showcase.csproj`.

A hand-placed layout (a small `record struct` list like Ruinborne's `TownBuilding`: id, x, z, yaw,
scale) positions all 7 inside the flattened disc without overlapping. On enter, Room3D:
- loads each building glb via `PropLoader.LoadProp` (normalizes to its manifest `heightMeters`),
  uploads with `Scene3D.LoadMesh`,
- loads each baked `.coll` via `PropCollisionFormat` and adds it as a physics static at the building's
  pose (position with Y = `terrain.GroundHeight` at its x/z = the flat town height, yaw, uniform
  scale) so it is solid,
- renders the buildings each frame (`Scene3D.DrawProps` with the building placements + mesh dict, or a
  per-building `Scene3D.Draw`).

Buildings tear down in `OnExit` like the other room meshes/statics (unload the building meshes; the
physics world is disposed wholesale, so its statics go with it).

### Post-fx fold

Add to `Room3D.OnUpdate`, alongside its existing outline/starfield/cel toggles:
- **R** - retro combo: toggle `Quantize = Dither = Pixelated`, set `CelBands` (0 or 4), and the low
  internal render resolution (as Render3DSample's R does).
- **P** - palette cycle: advance `ActivePalette` through `Palettes.All`.

Extend `Room3D.OnExit`'s Post reset to restore EVERY field these add can mutate
(`Quantize`/`Dither`/`Pixelated`/`CelBands`/`RenderWidth`/`RenderHeight`/`ActivePalette`, plus the
existing outline fields) back to `PixelPostProcessSettings` defaults, so a retro/palette left on does
not bleed under the menu or the 2D rooms.

### Retire Render3DSample

CI does not reference Render3DSample or `--smoke` (verified: CI only builds + tests), so retiring it
is safe. Delete `Render3DSample/`; remove from `KhaozEngine.slnx`, drop its `.vscode/launch.json`
config, and update `README.md` (the samples-table row, the repo-layout block, and the
`Render3DSample --smoke` note). Its shared models live in `KhaozEngine.Render3D/assets`, so they stay.

## Verification

- Build gate: `dotnet build KhaozEngine.slnx` green after the additions + retirement (no dangling
  Render3DSample reference).
- Headless: `KE_SHOWCASE_ROOM="3D World" KE_MAX_FRAMES=<n> dotnet run --project KhaozEngine.Showcase/...`
  exits 0 - the town + building collision build without crashing on GPU.
- Manual: walk into the village, bump a building wall (collision holds), cycle the post-fx (R retro, P
  palette, plus the existing outline/starfield/cel), Esc to menu (no post-fx bleed), re-enter cleanly.
- Full suite green.

## Concurrent-dev note

B2b touches shared hotspots: `KhaozEngine.slnx`, `.vscode/launch.json`, `README.md`. Before merging
back: `git fetch`; if `main`/`origin/main` advanced, merge it in first and re-resolve those, rebuild
the merged `.slnx`, then merge back clean. No `<KhaozEngineVersion>` bump (sample-only).

## Follow-on

- **B2c**: a Networked Walk room that starts an in-process authoritative server (background thread over
  a loopback socket) and connects a local `WorldClient`, reusing Room3D's world/render; retire
  NetworkedWalkSample + NetworkedWalkServer. After that, the Run/Debug dropdown is just
  `KhaozEngine.Showcase` plus the two headless heads (`MmoServerSample`, `SnapshotSample`), which can
  optionally have their launch configs stripped too.
