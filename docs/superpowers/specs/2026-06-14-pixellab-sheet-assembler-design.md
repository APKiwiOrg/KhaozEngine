# PixelLab -> Direction8 sprite-sheet assembler

Date: 2026-06-14
Status: approved (brainstorming) -> implementation plan next

## Problem

Hardpoint, Nullwake and SpaceGame all generate character art via the PixelLab MCP.
PixelLab exports a character as loose per-frame PNGs plus a `metadata.json`, not a
canonical sprite sheet. KhaozEngine's `PixelLabSpriteLoader.FromGridSheet` consumes a
single grid sheet (8 direction rows x N frame columns, uniform cell size). Something has
to turn the loose export into that sheet. Today there is nothing shared; each game would
hand-roll it. This is an offline asset-prep step, not game runtime.

## Goal

One shared, offline, cross-platform tool that takes a PixelLab character export (zip or
unzipped dir) + an animation name and produces:

- one grid-sheet PNG (8 direction rows x N frame columns, uniform cell) ready for
  `PixelLabSpriteLoader.FromGridSheet`, and
- the `frameCount` and a suggested `fps` to pass to `FromGridSheet(frameCount, fps)`.

Shared by all three games; runnable offline with no GPU / `GraphicsDevice`.

## Form factor and placement (decided)

A `net10.0` **console tool** at `tools/PixelLabSheetAssembler/`, added to
`KhaozEngine.slnx`, marked `<IsPackable>false</IsPackable>`.

- Builds and tests in CI (bare `dotnet restore/build/test` over the `.slnx`) but is
  **never** packed to `local-feed` and **never** published as a NuGet package.
- It is not a consumable runtime package, so there is **no `<Version>` bump, no
  `dotnet pack`, no `v*` tag**. The release ritual in `CLAUDE.md` applies to the
  consumable `KhaozEngine.*` packages only. This tool is documented in a
  `tools/PixelLabSheetAssembler/README.md` and gets a `CHANGELOG.md` "Tools" note.
- The doc-version guard (`scripts/check-doc-versions.sh`) is unaffected (it only checks
  the package-version declarations).

### Why a console tool, not a runtime package

The assembly is pure file-in / file-out image processing: read loose PNGs + JSON,
composite onto a uniform grid, write one PNG. It needs no MonoGame. MonoGame's
`Texture2D` requires a live `GraphicsDevice`, which is painful headless. A runtime NuGet
package would pull an image-processing dependency into game runtime that games never use
at runtime. So it lives as a repo tool instead.

### Image library

**SixLabors.ImageSharp, Apache-2.0 (the 2.1.x line)** for PNG read/write and pixel
access. Pure-managed, cross-platform (macOS dev + Linux CI), no native deps, no
`GraphicsDevice`, and the 2.1.x line is Apache-2.0 (no Six Labors split-license
question). Pinned at **2.1.13** (latest Apache-2.0 2.1.x; clears the security advisories
that flagged 2.1.9) so it does not float into the split-licensed 3.x line.

## Verified facts about the real exports

From the staged fixtures `~/Hardpoint/art/iso/drone_sheet.zip` and `tank_sheet.zip`
(PixelLab `export_version` 3.0):

- Layout: `<Name>/rotations/<dir>.png` and
  `<Name>/animations/<anim>/<dir>/frame_NNN.png`, `metadata.json` at the export root.
- `<dir>` is one of: `south, east, north, west, south-east, north-east, north-west,
  south-west`.
- `metadata.json` has **one** `states[].character.size` (`{width,height}`) per character
  (drone 88x88, tank 120x120). It is **not** per-frame; all frames share that canvas
  size. Actual PNG dims match it.
- `metadata.json` -> `states[].frames.animations.<anim>.<dir>` is an **ordered array of
  frame paths**. Gaps appear as omitted entries. `states[].frames.rotations.<dir>` is a
  single PNG path per direction.
- There is **no fps / frame-timing** field anywhere in the export.
- Two real gaps in the fixtures:
  - tank `walking/north-west` is missing `frame_002` (mid-sequence) — metadata and disk.
  - drone `walking/west` is missing `frame_000` (**leading** gap) — metadata and disk.

The prompt's stated assumptions "per-frame canvas sizes" and the row order
`(N,NE,E,SE,S,SW,W,NW)` are both wrong against the repo; this design follows the repo.

## Direction -> row order (requirement 1)

Source of truth is the `KhaozEngine.Sprites.Direction8` enum:

```
S=0, SE=1, E=2, NE=3, N=4, NW=5, W=6, SW=7
```

`PixelLabSpriteLoader.RowFor(d) == (int)d`, and its doc comment states PixelLab's own row
order is exactly `S, SE, E, NE, N, NW, W, SW`. So PixelLab's export dir-names map
straight to engine rows **by name**: `"south" -> S -> row 0`, `"south-east" -> SE ->
row 1`, ... `"south-west" -> SW -> row 7`. Rows are emitted top->bottom in enum order.
The assembler hard-codes this name->Direction8 table and asserts it covers all 8 enum
members (a test pins it against the live enum so a future enum reorder fails loudly).

## Cell size and feet-at-bottom anchoring (requirements 2 & 3)

- `cellW = max` frame width, `cellH = max` frame height over all present frames of the
  chosen anim across all 8 dirs (88x88 drone, 120x120 tank). Defensive against any odd
  frame; with uniform PixelLab canvases the cell equals the canvas.
- Anchoring is **content-based** (chosen over raw-canvas bottom-align): for each frame,
  scan the opaque bounding box (alpha > `alphaThreshold`, default 0), then place the
  frame in the cell so:
  - the opaque-bbox **bottom** lands on a baseline = `cellH - bottomPad` (`bottomPad`
    default 0), and
  - the frame canvas is centered horizontally in the cell (with uniform canvases this is
    the identity; the bbox's own horizontal sway is preserved as authored).
  This keeps the planted foot (lowest opaque pixel) on the ground across all dirs/frames
  while the body bob lives above it — stops sprites floating above their tile under
  `SpriteAnchor.FootprintBottomCenter`.
- A fully transparent frame (no opaque pixels) is centered with no vertical shift and
  emits a warning.

## Missing-frame tolerance (requirement 4)

- `frameCount = maxFrameIndex + 1`, where `maxFrameIndex` is the highest `frame_NNN`
  index seen across all 8 dirs of the chosen anim. (drone/tank: indices 0..5 -> 6.)
- For each direction, for each index `0..frameCount-1`, if the frame is missing it is
  filled by **holding the nearest previous present frame**. If the gap is leading (no
  previous frame exists, e.g. drone `west` `frame_000`), fall back to the nearest
  **following** present frame. Frames are never silently shifted; the row stays in sync.
- Every fill prints `WARNING: <dir>/<anim> frame_NNN missing - held frame_MMM`.
- `--strict` turns the first detected gap into a hard error (clear message naming
  dir/anim/index) instead of filling.
- A direction with **zero** present frames for the anim is always a hard error (cannot be
  filled), regardless of `--strict`.

## CLI

```
dotnet run --project tools/PixelLabSheetAssembler -- \
  --input <char.zip|dir> --anim <name> [--out <path.png>] \
  [--fps <n>] [--bottom-pad <px>] [--alpha-threshold <0-255>] [--strict]
```

- `--input` (required): path to a PixelLab character `.zip` or an unzipped export dir.
  A zip is extracted to a temp dir (cleaned up after).
- `--anim` (required): animation name under `animations/` (e.g. `walking`).
- `--out` (optional): output PNG path. Default `<inputName>_<anim>.png` next to the input.
- `--fps` (optional, default `10`): suggested fps echoed back and used only for the
  reported `FromGridSheet` call (PixelLab exports no timing).
- `--bottom-pad` (optional, default `0`): px between the feet baseline and the cell
  bottom.
- `--alpha-threshold` (optional, default `0`): alpha above which a pixel counts as opaque
  for the bbox scan.
- `--strict` (optional): fail on the first gap instead of holding.

### Output report (stdout)

```
Wrote <out path>  (<cols>x<rows> cells, cell <cellW>x<cellH>, sheet <W>x<H>)
frameCount = <N>
suggested fps = <fps>
FromGridSheet(sheet, frameCount: <N>, fps: <fps>f)
<any WARNING lines>
```

## Architecture (testable core split from IO)

- `SheetAssembler.cs` — **pure logic, no file IO**. Input: a parsed manifest (anim name,
  per-dir ordered frame entries) + in-memory frame bitmaps; options (bottomPad,
  alphaThreshold, strict). Output: composited `Image` + `AssemblyResult { FrameCount,
  SuggestedFps, Warnings[] }`. Owns the name->Direction8 table, gap detection/fill, bbox
  scan, cell-size + layout math, compositing.
- `PixelLabExport.cs` — IO boundary. Resolves zip-or-dir, parses `metadata.json`
  (`states[0]`), enumerates the chosen anim's per-dir frame paths, parses `frame_NNN`
  indices, loads PNGs as ImageSharp images.
- `Program.cs` — arg parsing, wires export -> assembler -> PNG write, prints the report,
  maps `--strict` errors / missing-dir errors to a non-zero exit code.
- `tools/PixelLabSheetAssembler.Tests/` — xUnit. The engine repo cannot depend on the
  Hardpoint zips, so tests build **synthetic in-memory PNGs** and assert:
  1. name->row table covers every `Direction8` member, in enum order (pins against the
     live enum).
  2. mid-sequence gap is held from the previous frame, with a warning, no shift.
  3. leading gap (frame_000 missing) is held from the next frame, with a warning.
  4. content-bbox bottom aligns to the baseline across frames with differing opaque
     placement (feet planted).
  5. uniform cell size = max frame dims; smaller frames padded, none clipped.
  6. a direction with zero frames is a hard error.
  7. `--strict` semantics (first gap -> error).

## Manual verification (this session, not committed)

Run the built tool against the real `drone_sheet.zip` and `tank_sheet.zip`, eyeball the
two output sheets (row order, feet planted, gap held), and report the actual
`FromGridSheet(frameCount, fps)` values for Hardpoint's Phase 2b.

## Out of scope

PixelLab tiles / map-objects (single pre-rendered images, no assembly). Characters only.
Multi-anim batch output and a `rotations`-as-1-frame mode are possible later extensions
but not in this cut (`--anim` is explicit, single anim per invocation).
