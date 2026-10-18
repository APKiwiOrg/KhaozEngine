"""4x nearest-neighbour crops of frame 15, panels TAA Native, TAA Quality and the condition's baseline FXAA.

Usage: make_crops.py <root> <out-dir> <prefix> <condition-run>... Windows are the earlier rounds' 96 by 96 crops of
crop-015.png, character at (144, 200) and ground at (4, 0). Writes <prefix>-<run>-<region>-015-4x.png.
"""
from pathlib import Path
import sys
from PIL import Image

ROOT, OUT, PREFIX = Path(sys.argv[1]), Path(sys.argv[2]), sys.argv[3]
WINDOWS = {"character": (144, 200), "ground": (4, 0)}
for run in sys.argv[4:]:
    condition = run.split("-", 1)[0]
    sources = [ROOT / run / "taa-native", ROOT / run / "taa-quality", ROOT / f"{condition}-baseline" / "fxaa"]
    for region, (x, y) in WINDOWS.items():
        sheet = Image.new("RGB", (3 * 384, 384))
        for i, source in enumerate(sources):
            panel = Image.open(source / "crop-015.png").convert("RGB").crop((x, y, x + 96, y + 96))
            sheet.paste(panel.resize((384, 384), Image.NEAREST), (384 * i, 0))
        sheet.save(OUT / f"{PREFIX}-{run}-{region}-015-4x.png")
