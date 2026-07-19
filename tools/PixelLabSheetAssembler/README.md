# PixelLabSheetAssembler

Offline asset-prep tool. Turns a PixelLab character export (zip or unzipped dir) plus an
animation name into one grid sheet PNG (8 rows in S, SE, E, NE, N, NW, W, SW order x N frame
columns, uniform cell size). Dormant one-off tool today: no current KhaozEngine game consumes its
output, and the engine has no directional-sprite loader for it (`KhaozEngine.Sprites.PixelLabSpriteLoader`
and the rest of `KhaozEngine.Sprites` were deleted in the MonoGame purge, commit `df097d84`). A
consuming game loads the PNG itself and slices the grid until one adopts this tool's output.
Not a runtime package (IsPackable=false): it is never NuGet-packed and never published.

## Run

    dotnet run --project tools/PixelLabSheetAssembler -- \
      --input <char.zip|dir> --anim <name> [--out <path.png>] \
      [--fps <n>] [--bottom-pad <px>] [--alpha-threshold <0-255>] [--strict]

- `--input`   (required) PixelLab character `.zip` or unzipped export dir.
- `--anim`    (required) animation name under `animations/` (e.g. `walking`).
- `--out`     output PNG path. Default `<inputName>_<anim>.png` next to the input.
- `--fps`     suggested fps echoed back for whatever loads the sheet (default 10; PixelLab exports no timing).
- `--bottom-pad`      px between the feet baseline and the cell bottom (default 0).
- `--alpha-threshold` alpha above which a pixel counts as opaque for the bbox scan (default 0).
- `--strict`  fail on the first missing frame instead of holding the previous/next frame.

## What it does

- **Row order:** PixelLab dir names map to rows by name, in the canonical S, SE, E, NE, N, NW, W, SW
  order (see `DirectionRows`). Asserted directly by a test; there is no live enum to pin against any
  more, `KhaozEngine.Sprites.Direction8` was deleted in the MonoGame purge.
- **Uniform cells:** cell = max frame width/height; smaller frames are padded, none clipped.
- **Feet on the ground:** each frame's opaque bbox bottom is aligned to a baseline near the cell
  bottom, so the planted foot lands at a consistent row-relative position for whatever foot-anchor
  convention the consuming game uses.
- **Missing-frame tolerance:** a dropped frame is held from the nearest previous frame (or the next
  one for a leading gap), with a `WARNING`, never silently shifting the row. `--strict` turns the
  first gap into an error.

## Consuming the output

There is no engine loader for this today: the pre-purge `PixelLabSpriteLoader.FromGridSheet` is
gone along with the rest of `KhaozEngine.Sprites`. The output is a plain grid-sheet PNG (8 rows in
S, SE, E, NE, N, NW, W, SW order, `frameCount` columns, uniform cell size). A consuming game loads
it as its own `Texture2D` and slices the `frameCount` x 8 grid itself, until a game adopts this
tool and the engine grows a matching loader.

`frameCount` and the suggested `fps` are printed by the tool.

## Exit codes

- `0` success
- `1` assembly error (bad export, missing direction, `--strict` gap)
- `2` bad arguments
