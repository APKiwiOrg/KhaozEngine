# icon/

The KhaozEngine brand mark (a stylised gold "K" badge) and everything generated from it. This is the
icon the engine's sample / showcase apps show in the window, taskbar, and macOS Dock.

- `KhaozEngine-icon-master.png`: the master, the single source of truth. To change the mark, replace
  this file (or pass `--master`) and re-run the generator.
- `generated/`: produced by `scripts/generate-icons.py`. Do not hand-edit.
  - `windowicon/icon_<N>.png` (16..256): the runtime window icon set the samples point at
  - `png/icon_<N>.png` (16..1024): generic square PNGs
  - `windows/KhaozEngine.ico`, `macos/KhaozEngine.icns` (+ `iconset/`), `android/`,
    `ios/AppIcon.appiconset/`: the full per-platform pack the generator emits. The engine itself ships
    no app, so only the windowicon / png sizes are used by the samples, the rest is there for parity
    with the game repos.

## Regenerate

    python3 scripts/generate-icons.py        # needs: pip install pillow

## Sample usage

A sample sets the window / Dock icon through `GameAppOptions`:

    options.WindowIconPath = Path.Combine(AppContext.BaseDirectory, "assets", "icon.png");

and links the icon into its output in the csproj:

    <None Include="..\art-assets\icon\generated\windowicon\icon_256.png"
          Link="assets\icon.png" CopyToOutputDirectory="PreserveNewest" />

`KhaozEngine.Showcase` does exactly this. The full game-side consumer story (per-platform build wiring
and multi-size `WindowIcons`) lives in the game-template repo's `docs/ICONS.md`.
