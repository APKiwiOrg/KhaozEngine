# KhaozEngine.Showcase

The engine's runnable tour. A tile-menu hub opens feature rooms, each wearing shared chrome: a title band,
a controls hint bar, a live status line and toggle toasts. It is also the testbed a windowed performance
capture runs in. Not a package. It is never packed or published.

```bash
dotnet run --project KhaozEngine.Showcase
```

It opens a GPU window, so it needs a display. Esc leaves a room.

## Rooms

| Room | What it shows | Controls |
|---|---|---|
| 2D and GUI | A tabbed toolkit tour: widgets, sprites and text, input gestures, the clock, clipboard and audio, the immediate-mode `GuiSurface`, a screens and dialogs page with the modal Settings dialog, pause overlay, patch notes and toast stack, and a drag-and-drop page with two slot grids, a refused slot and a destroy bin. | Tab and Shift+Tab switch tabs |
| Catcher | A small 2D mini-game. | On the room's hint bar |
| Boot screen | The turn-key boot screen. | On the room's hint bar |
| 3D world | A walkable streamed overworld: terrain streaming, a follow camera, the character controller and physics, a village of CC0 houses, a collidable staircase, an authored tree line for cascade testing, and a gradient-sky day and night cycle with cascaded shadows. Retro post effects (cel, outline, palette) toggle live. | WASD move, left or right drag orbits, scroll zooms, Shift runs, F2 collision overlay, T pauses day and night, N sky or starfield, O, C, R and P post effects |
| Particles and VFX | Every authored `VfxPresets` effect, cycled live, with bloom and HDR toggles. | Left and Right switch effect, Space replays, B and H toggle bloom and HDR |
| Networked walk | An authoritative server, a local client and scripted bots, all in-process over loopback UDP. | WASD and mouse to walk among the bots |
| Dungeon | A generated dungeon walk. | On the room's hint bar |
| Map editor | `KhaozEngine.MapEditor` over a committed demo document, with undoable dungeon generation. | WASD and right drag fly, toolbar tools and Dungeon generation, Ctrl+Z and Ctrl+Y undo and redo, Ctrl+S saves. Shift+Esc leaves, Esc cancels a gesture or opens the settings menu when there is none |

From the menu, use the arrows and Enter or click a tile.

## Smoke runs

Every windowed sample honours `KE_MAX_FRAMES=N`: it renders N frames, then exits 0. So the Showcase doubles as
a smoke test on a GPU machine without anyone closing the window. `KE_SHOWCASE_ROOM` auto-enters the room whose
title starts with the given text, case-insensitive.

```bash
KE_MAX_FRAMES=5 dotnet run --project KhaozEngine.Showcase
KE_SHOWCASE_ROOM="3D" KE_MAX_FRAMES=5 dotnet run --project KhaozEngine.Showcase
```

## Field capture

The Showcase carries the levers a windowed performance capture needs. They belong to the testbed, not the
engine API, and each is a no-op unless set.

| Variable | What it does |
|---|---|
| `KE_TELEMETRY_PATH=<file>` | Arms the engine's `TelemetryRecorder` for the whole run: a JSON Lines session with the engine's header (build, backend, adapter, every `KE_` lever) and a raw sample row every 100 ms carrying frame rate, frame times, a loop frame count and the `GpuDeviceCounters` channels |
| `KE_SHOWCASE_FRAME_CAP=auto\|uncapped\|<hz>` | Pins the frame-cap intent. The default `auto` resolves to a software cap on Metal plus vsync, which sleeps the loop before the present can block, so an acquire-wait or present-pacing reading has to be taken `uncapped` |
| `KE_SHOWCASE_BACKGROUND_THROTTLE=off` | Disables the unfocused and minimised pacing. The engine caps an unfocused window at 15 Hz by default, which is right for a game and ruinous for an unattended capture that never takes focus |

A bounded capture of the 3D room on a named backend, paced by nothing but the present:

```bash
KE_TELEMETRY_PATH=/tmp/ke-capture.jsonl KE_GRAPHICS_BACKEND=metal-native \
KE_SHOWCASE_ROOM=3D KE_SHOWCASE_FRAME_CAP=uncapped KE_SHOWCASE_BACKGROUND_THROTTLE=off \
KE_MAX_FRAMES=8000 dotnet run --project KhaozEngine.Showcase -c Release
```

## Other samples

- [`SnapshotTool`](../SnapshotTool/README.md) writes a 2D and a 3D PNG through the headless snapshot harness
  and has GPU-free `diff` and `score` subcommands. It is the shape each game's own snapshot tool mirrors.
- `MmoServerSample` is a reference dedicated MMO server (cell grid and seamless handoff) on a UDP socket:
  `dotnet run --project MmoServerSample`.
