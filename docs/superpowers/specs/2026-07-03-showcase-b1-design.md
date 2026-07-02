# KhaozEngine.Showcase - hub + simple rooms (sub-project B1)

Status: approved design, pre-plan.
Parent effort: consolidate the scattered windowed demos into one showcase app (see the demo-sprawl
inventory in the session that produced this). Split into **B1** (this spec: hub + the four simpler
rooms + retire those samples) and **B2** (a later spec: the 3D World town room with clearing + CC0
houses + post-fx, relocating the textured prop, retiring TerrainWalkSample + Render3DSample).

## Problem

The repo has 7 overlapping windowed demo projects (Render2DSample, WindowingSample, GuiSample,
SceneSample, MiniGame, TerrainWalkSample, Render3DSample) plus a stale `SlathRepro` launch config.
Each is a separate `GameApp` with its own loop; GuiSample and SceneSample are near-duplicates. The
sprawl is hard to navigate and to keep runnable. Goal: one `KhaozEngine.Showcase` app with a menu
hub whose rooms absorb the windowed demos, retiring the folded projects.

B1 folds the four non-3D demos. B2 folds the two 3D ones. Networked/server/snapshot samples and the
author-time tools stay separate (multi-process or headless, wrong fit for a single-window hub).

## Goals

1. A new `KhaozEngine.Showcase` Exe project: a `GameApp` + `SceneManager` menu hub.
2. Four rooms absorbing Render2DSample, WindowingSample, GuiSample+SceneSample, MiniGame - each
   reachable from the menu, each returning to the menu on Esc.
3. Retire the five folded sample projects (Render2DSample, WindowingSample, GuiSample, SceneSample,
   MiniGame): delete their dirs, deregister from `KhaozEngine.slnx`, `.vscode/launch.json`, and the
   README, and drop the stale `SlathRepro` launch config.
4. The solution still builds green; the app honors `KE_MAX_FRAMES` for headless smoke.

Non-goals (deferred to B2): the 3D World room (clearing + CC0 houses + character + textured prop),
Render3DSample's post-processing toggles, and retiring TerrainWalkSample / Render3DSample. Also out:
touching networked/server/snapshot samples or tools; any engine version bump (sample-only change).

## Design

### Project

`KhaozEngine.Showcase/KhaozEngine.Showcase.csproj`: net10.0, `OutputType=Exe`, `IsPackable=false`,
`Nullable=enable`, matching the other samples. `ProjectReference`s: `KhaozEngine.Windowing`,
`KhaozEngine.Render2D`, `KhaozEngine.Gui`, `KhaozEngine.Audio`, `KhaozEngine.Game` (the umbrella of
GameApp/SceneManager). Add it to `KhaozEngine.slnx`.

### Hub

`ShowcaseApp : GameApp` owns a `SceneManager` (exactly as `SceneSample` wires it: copy Input /
Pointer / Viewport / FrameWidth / FrameHeight into the manager before `Update`, forward `Draw`). At
`OnLoad` it pushes `MenuScene`.

`MenuScene` renders a clickable vertical list of room entries (this itself exercises Gui text +
the `IsTapIn` press-origin hit-test). Selecting a room (click or Enter on the highlighted row)
`Push`es that room scene; the room `Pop`s back on Esc. Menu is `DrawBelow=false` (rooms fully cover
it). Rooms set their own below-flags where they overlay (e.g. an in-room pause).

Navigation is factored OUT of the scene into a plain, GPU-free model so it is headless-testable:

```
public sealed class ShowcaseMenu
{
    public ShowcaseMenu(IReadOnlyList<string> roomNames);
    public IReadOnlyList<string> Rooms { get; }
    public int Selected { get; }         // highlighted index, wraps
    public void MoveNext();              // down/right, wraps
    public void MovePrev();              // up/left, wraps
    public void SelectAt(int index);     // e.g. from a click hit-test
    public string Current { get; }       // Rooms[Selected]
}
```

`MenuScene` holds a `ShowcaseMenu`, maps input to `MoveNext`/`MovePrev`/`SelectAt`, and on activate
pushes the room for `Current`. The room list is the single source of which rooms exist.

### Rooms (one file each, one responsibility)

Each is a `Scene` that ports the corresponding sample's core into `OnUpdate`/`OnDraw`, returning to
the menu on Esc (`manager.Pop()`), keeping the port faithful (same behaviour the sample showed):

- `Room2D` <- Render2DSample: SpriteBatch sprites, TTF text, alpha blend, batched quads.
- `RoomGui` <- GuiSample + SceneSample: widget screens (menu / modal settings / immediate) on a
  `ScreenStack`, plus SceneSample's push/switch/overlay+pop demonstration (a sub-screen that shows a
  frozen-below overlay). One room covers both, since they overlap.
- `RoomInput` <- WindowingSample: `GestureRecognizer` (drag/tap/long-press), `GameClock`
  (pause/time-scale), clipboard round-trip, positional + non-positional SFX via the audio system.
- `RoomMiniGame` <- MiniGame: the full Catcher game (paddle catches falling blocks) with its own
  internal title/play screens and looped background music.

The 3D World room is NOT listed (B2 adds it). The menu shows exactly the four above, so there is no
dead "coming soon" entry.

### Assets

MiniGame's audio (its looped music / SFX, if it ships an asset rather than a generated tone) moves
under `KhaozEngine.Showcase/assets/` with its csproj copy-to-output item and any CREDITS. The other
three rooms are asset-free.

### Retirement

- Delete project dirs: `Render2DSample/`, `WindowingSample/`, `GuiSample/`, `SceneSample/`,
  `MiniGame/`.
- `KhaozEngine.slnx`: remove those five `<Project>` entries, add `KhaozEngine.Showcase`.
- `.vscode/launch.json`: remove the five "Run <Sample>" configs and the stale `SlathRepro` config;
  add one "Run KhaozEngine.Showcase" config (internalConsole, no args), pre-launch `build`.
- `README.md` "Running the samples": replace the five folded entries with a single Showcase entry
  (menu hub, lists the rooms); note the KE_MAX_FRAMES smoke behaviour. Leave TerrainWalkSample,
  Render3DSample, networked/server/snapshot entries as-is (Render3DSample folds in B2).

Keep TerrainWalkSample, Render3DSample, SnapshotSample, MmoServerSample, NetworkedWalkServer,
NetworkedWalkSample and the tools untouched.

## Verification

- Headless unit tests (`KhaozEngine.Tests`, no GPU): `ShowcaseMenu` - room list is what it was
  constructed with; `MoveNext`/`MovePrev` wrap at the ends; `SelectAt` clamps/ignores out-of-range;
  `Current` tracks `Selected`.
- Build gate: `dotnet build KhaozEngine.slnx` succeeds after the removals (no dangling references to
  the deleted projects anywhere).
- Smoke: `KE_MAX_FRAMES=<n> dotnet run --project KhaozEngine.Showcase/...` boots the menu, renders n
  frames, exits 0 (matches the other samples' smoke contract).
- Manual: launch the Showcase, enter each of the four rooms, confirm each behaves as its old sample
  did, Esc returns to the menu.

## Concurrent-dev note

Heavy parallel dev is in flight. B1 touches shared, collision-prone files: `KhaozEngine.slnx`,
`.vscode/launch.json`, `README.md`. Before merging back: `git fetch`; if `main`/`origin/main`
advanced, merge it INTO this branch first and re-resolve those three files (another chat may have
added/removed a project or launch entry), rebuild the merged `.slnx`, then merge back clean. No
`<KhaozEngineVersion>` bump here, so the version line will not collide.

## Follow-on

B2: add the 3D World room (streamed terrain + `FlattenFeature` clearing + copied CC0 Quaternius
Medieval Village houses with baked `.coll` collision + character controller + follow camera +
Render3DSample's cel/outline/palette/starfield post-fx toggles), relocate sub-project A's textured
stone block into it, then retire TerrainWalkSample + Render3DSample and prune their `.vscode`/README
entries. Own spec + plan.
