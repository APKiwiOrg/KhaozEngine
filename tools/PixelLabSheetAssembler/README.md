# PixelLabSheetAssembler

Offline asset-prep tool. Turns a PixelLab character export (zip or unzipped dir) plus an
animation name into one grid sheet PNG (8 `Direction8` rows x N frame columns, uniform cell size),
ready for `KhaozEngine.Sprites.PixelLabSpriteLoader.FromGridSheet`. Shared by all KhaozEngine games.
Not a runtime package (IsPackable=false): it is never NuGet-packed and never published.

## Run

    dotnet run --project tools/PixelLabSheetAssembler -- \
      --input <char.zip|dir> --anim <name> [--out <path.png>] \
      [--fps <n>] [--bottom-pad <px>] [--alpha-threshold <0-255>] [--strict]

- `--input`   (required) PixelLab character `.zip` or unzipped export dir.
- `--anim`    (required) animation name under `animations/` (e.g. `walking`).
- `--out`     output PNG path. Default `<inputName>_<anim>.png` next to the input.
- `--fps`     suggested fps echoed back for `FromGridSheet` (default 10; PixelLab exports no timing).
- `--bottom-pad`      px between the feet baseline and the cell bottom (default 0).
- `--alpha-threshold` alpha above which a pixel counts as opaque for the bbox scan (default 0).
- `--strict`  fail on the first missing frame instead of holding the previous/next frame.

## What it does

- **Row order:** PixelLab dir names map to rows by name, in `Direction8` order (S, SE, E, NE, N, NW,
  W, SW). Pinned to the live enum by a test.
- **Uniform cells:** cell = max frame width/height; smaller frames are padded, none clipped.
- **Feet on the ground:** each frame's opaque bbox bottom is aligned to a baseline near the cell
  bottom, so the planted foot stays put under `SpriteAnchor.FootprintBottomCenter`.
- **Missing-frame tolerance:** a dropped frame is held from the nearest previous frame (or the next
  one for a leading gap), with a `WARNING`, never silently shifting the row. `--strict` turns the
  first gap into an error.

## Consuming the output

    var sheet = /* load the PNG as Texture2D */;
    var sprite = PixelLabSpriteLoader.FromGridSheet(sheet, frameCount, fps);

`frameCount` and the suggested `fps` are printed by the tool.

## Exit codes

- `0` success
- `1` assembly error (bad export, missing direction, `--strict` gap)
- `2` bad arguments
