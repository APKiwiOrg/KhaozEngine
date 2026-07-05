#!/usr/bin/env python3
"""Regenerate the game's icon pack from one master PNG.

Single source of truth:
    art-assets/icon/<name>-icon-master.png

Run this whenever the master changes:
    python3 scripts/generate-icons.py
    python3 scripts/generate-icons.py --name MyGame
    python3 scripts/generate-icons.py --master path/to/icon.png --name MyGame --fill 0.94

One dependency (Pillow):
    pip install pillow

The master defaults to the single art-assets/icon/*-icon-master.png in the repo, and
--name defaults to that file's stem (the part before "-icon-master"). --name only sets
the .ico / .icns filenames, so the desktop csproj can reference a stable path.

Outputs (all under art-assets/icon/generated/, committed to the repo):
    png/icon_<N>.png              N in 16 32 48 64 128 256 512 1024 (generic square PNGs)
    windowicon/icon_<N>.png       N in 16 24 32 48 64 128 256 (KhaozEngine runtime window icon)
    windows/<Name>.ico            multi-size Windows icon, wired via <ApplicationIcon>
    macos/<Name>.icns             macOS .app bundle icon (see docs/ICONS.md)
    macos/<Name>.iconset/         the named PNGs iconutil/Pillow assemble the .icns from
    android/mipmap-<dpi>/ic_launcher.png             legacy launcher icons (48..192)
    android/mipmap-<dpi>/ic_launcher_foreground.png  adaptive foreground layer (108dp set)
    android/mipmap-anydpi-v26/ic_launcher.xml        adaptive icon (API 26+)
    android/values/ic_launcher_background.xml        adaptive background color
    android/ic_launcher-web.png                      512 Play-listing icon
    ios/AppIcon.appiconset/icon_1024.png             iOS app icon (Xcode 14+ single size)
    ios/AppIcon.appiconset/Contents.json

The .icns is built with macOS `iconutil` when present (best quality), else with Pillow's
ICNS writer so the script still runs on Linux/CI. The source art is pixel art, so the
transparent margin is trimmed and the art is refit to fill --fill (0.94) of the square,
upscales use nearest-neighbour (crisp blocks), downscales use Lanczos (legible small).
"""

from __future__ import annotations

import argparse
import json
import shutil
import subprocess
import sys
from pathlib import Path

try:
    from PIL import Image
except ImportError:
    sys.exit("Pillow is required: pip install pillow")

REPO_ROOT = Path(__file__).resolve().parent.parent
ICON_DIR = REPO_ROOT / "art-assets" / "icon"
OUT_DIR = ICON_DIR / "generated"

# Generic square PNG set. Also the source pool for the .ico / .icns members.
PNG_SIZES = [16, 32, 48, 64, 128, 256, 512, 1024]
# KhaozEngine runtime window icon (title bar + Alt-Tab + Windows taskbar). 24 is the slot
# the png pack omits, so the desktop head can copy this folder verbatim.
WINDOWICON_SIZES = [16, 24, 32, 48, 64, 128, 256]
# Sizes embedded in the Windows .ico (24 is Windows-specific).
ICO_SIZES = [16, 24, 32, 48, 64, 128, 256]
# (iconset filename, pixel size) pairs Apple's .icns expects.
ICNS_MEMBERS = [
    ("icon_16x16.png", 16),
    ("icon_16x16@2x.png", 32),
    ("icon_32x32.png", 32),
    ("icon_32x32@2x.png", 64),
    ("icon_128x128.png", 128),
    ("icon_128x128@2x.png", 256),
    ("icon_256x256.png", 256),
    ("icon_256x256@2x.png", 512),
    ("icon_512x512.png", 512),
    ("icon_512x512@2x.png", 1024),
]
# Android legacy launcher: density -> square px.
ANDROID_MIPMAPS = {"mdpi": 48, "hdpi": 72, "xhdpi": 96, "xxhdpi": 144, "xxxhdpi": 192}
# Android adaptive icon layers are a 108dp square: density -> px.
ANDROID_ADAPTIVE = {"mdpi": 108, "hdpi": 162, "xhdpi": 216, "xxhdpi": 324, "xxxhdpi": 432}
# Fraction of the 108dp adaptive canvas the art occupies (outer third may be masked away).
ANDROID_FG_FILL = 0.66
# Neutral placeholder adaptive background. Each game overrides this color.
ANDROID_BG_COLOR = "#202830"

# Alpha above this counts as content when trimming the transparent margin.
ALPHA_TRIM_THRESHOLD = 8
# Default fraction of the (square) canvas the trimmed art fills after refit.
DEFAULT_FILL = 0.94


def find_master() -> Path:
    """Return the single art-assets/icon/*-icon-master.png, or exit with guidance."""
    candidates = sorted(ICON_DIR.glob("*-icon-master.png"))
    if len(candidates) == 1:
        return candidates[0]
    if not candidates:
        sys.exit(f"no *-icon-master.png in {ICON_DIR.relative_to(REPO_ROOT)} (pass --master)")
    names = ", ".join(c.name for c in candidates)
    sys.exit(f"multiple masters ({names}) in {ICON_DIR.relative_to(REPO_ROOT)}, pass --master")


def name_from_master(master: Path) -> str:
    """Derive the icon base name from '<name>-icon-master.png'."""
    stem = master.name
    for suffix in ("-icon-master.png", ".png"):
        if stem.endswith(suffix):
            return stem[: -len(suffix)].replace("-icon-master", "") or stem
    return master.stem


def normalize(master: Image.Image, fill: float) -> Image.Image:
    """Trim the transparent margin and refit the art onto a square canvas so its larger
    side fills `fill` of the canvas. A fully transparent image is returned unchanged."""
    bbox = master.getchannel("A").point(lambda p: 255 if p > ALPHA_TRIM_THRESHOLD else 0).getbbox()
    if bbox is None:
        return master
    content = master.crop(bbox)
    cw, ch = content.size
    side = max(1, round(max(cw, ch) / max(0.05, min(1.0, fill))))
    canvas = Image.new("RGBA", (side, side), (0, 0, 0, 0))
    canvas.paste(content, ((side - cw) // 2, (side - ch) // 2), content)
    return canvas


def render(master: Image.Image, size: int) -> Image.Image:
    """Resize the master to a square `size`, picking the filter by direction."""
    src = max(master.width, master.height)
    flt = Image.Resampling.NEAREST if size >= src else Image.Resampling.LANCZOS
    if master.width != master.height:
        side = src
        square = Image.new("RGBA", (side, side), (0, 0, 0, 0))
        square.paste(master, ((side - master.width) // 2, (side - master.height) // 2))
        master = square
    return master.resize((size, size), flt)


def refit(master: Image.Image, size: int, fill: float) -> Image.Image:
    """Render the art centered at `fill` of a transparent square `size` (adaptive foreground)."""
    inner = max(1, round(size * fill))
    art = render(master, inner)
    canvas = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    canvas.paste(art, ((size - inner) // 2, (size - inner) // 2), art)
    return canvas


def rel(p: Path) -> str:
    return str(p.relative_to(REPO_ROOT))


def emit_desktop(master: Image.Image, name: str) -> None:
    png_dir = OUT_DIR / "png"
    win_dir = OUT_DIR / "windowicon"
    windows_dir = OUT_DIR / "windows"
    macos_dir = OUT_DIR / "macos"
    iconset_dir = macos_dir / f"{name}.iconset"
    for d in (png_dir, win_dir, windows_dir, iconset_dir):
        d.mkdir(parents=True, exist_ok=True)

    pool = {s: render(master, s) for s in sorted(set(PNG_SIZES + WINDOWICON_SIZES + [m[1] for m in ICNS_MEMBERS]))}

    for s in PNG_SIZES:
        pool[s].save(png_dir / f"icon_{s}.png")
    print(f"[icons] png: {', '.join(map(str, PNG_SIZES))}")

    for s in WINDOWICON_SIZES:
        pool[s].save(win_dir / f"icon_{s}.png")
    print(f"[icons] windowicon: {', '.join(map(str, WINDOWICON_SIZES))}")

    ico_path = windows_dir / f"{name}.ico"
    pool[256].save(ico_path, format="ICO", sizes=[(s, s) for s in ICO_SIZES])
    print(f"[icons] {rel(ico_path)} ({', '.join(map(str, ICO_SIZES))})")

    for member, s in ICNS_MEMBERS:
        pool[s].save(iconset_dir / member)
    icns_path = macos_dir / f"{name}.icns"
    if shutil.which("iconutil"):
        subprocess.run(["iconutil", "-c", "icns", "-o", str(icns_path), str(iconset_dir)], check=True)
        print(f"[icons] {rel(icns_path)} (iconutil)")
    else:
        pool[1024].save(icns_path, format="ICNS")
        print(f"[icons] {rel(icns_path)} (Pillow fallback)")


def emit_android(master: Image.Image) -> None:
    android = OUT_DIR / "android"
    for dpi, px in ANDROID_MIPMAPS.items():
        d = android / f"mipmap-{dpi}"
        d.mkdir(parents=True, exist_ok=True)
        render(master, px).save(d / "ic_launcher.png")
        refit(master, ANDROID_ADAPTIVE[dpi], ANDROID_FG_FILL).save(d / "ic_launcher_foreground.png")

    anydpi = android / "mipmap-anydpi-v26"
    anydpi.mkdir(parents=True, exist_ok=True)
    (anydpi / "ic_launcher.xml").write_text(
        '<?xml version="1.0" encoding="utf-8"?>\n'
        '<adaptive-icon xmlns:android="http://schemas.android.com/apk/res/android">\n'
        '    <background android:drawable="@color/ic_launcher_background" />\n'
        '    <foreground android:drawable="@mipmap/ic_launcher_foreground" />\n'
        "</adaptive-icon>\n"
    )

    values = android / "values"
    values.mkdir(parents=True, exist_ok=True)
    (values / "ic_launcher_background.xml").write_text(
        '<?xml version="1.0" encoding="utf-8"?>\n'
        "<resources>\n"
        f'    <color name="ic_launcher_background">{ANDROID_BG_COLOR}</color>\n'
        "</resources>\n"
    )

    render(master, 512).save(android / "ic_launcher-web.png")
    print(f"[icons] android: mipmaps {', '.join(map(str, ANDROID_MIPMAPS.values()))} + adaptive + web 512")


def emit_ios(master: Image.Image) -> None:
    appiconset = OUT_DIR / "ios" / "AppIcon.appiconset"
    appiconset.mkdir(parents=True, exist_ok=True)
    render(master, 1024).save(appiconset / "icon_1024.png")
    contents = {
        "images": [
            {"filename": "icon_1024.png", "idiom": "universal", "platform": "ios", "size": "1024x1024"}
        ],
        "info": {"author": "xcode", "version": 1},
    }
    (appiconset / "Contents.json").write_text(json.dumps(contents, indent=2) + "\n")
    print("[icons] ios: AppIcon.appiconset (1024 + Contents.json)")


def main() -> int:
    ap = argparse.ArgumentParser(description="Regenerate the cross-platform icon pack.")
    ap.add_argument("--master", type=Path, default=None,
                    help="master PNG (default: the single art-assets/icon/*-icon-master.png)")
    ap.add_argument("--name", default=None,
                    help="base name for the .ico / .icns (default: derived from the master filename)")
    ap.add_argument("--fill", type=float, default=DEFAULT_FILL,
                    help=f"fraction of the canvas the trimmed art fills after refit (default {DEFAULT_FILL})")
    ap.add_argument("--no-trim", action="store_true",
                    help="use the master verbatim (skip the trim + refit step)")
    args = ap.parse_args()

    master_path = args.master if args.master else find_master()
    if not master_path.exists():
        sys.exit(f"master not found: {master_path}")
    name = args.name or name_from_master(master_path)

    master = Image.open(master_path).convert("RGBA")
    print(f"[icons] master {rel(master_path)} ({master.width}x{master.height}), name {name}")
    if not args.no_trim:
        before = master.size
        master = normalize(master, args.fill)
        print(f"[icons] trim + refit {before[0]}x{before[1]} -> {master.width}x{master.height} (fill {args.fill})")

    # Clean + recreate the generated tree so removed artifacts never linger.
    if OUT_DIR.exists():
        shutil.rmtree(OUT_DIR)
    OUT_DIR.mkdir(parents=True)

    emit_desktop(master, name)
    emit_android(master)
    emit_ios(master)
    print("[icons] done.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
