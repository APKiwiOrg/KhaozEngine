"""Compare paired Quality 2 px pan captures from TemporalShadeEvidence, in luma 0..1.

Usage from the repository root:
python3 KhaozEngine.Render.Tests/Gpu/measurements/taa-game-shadow/lock-luma-kill/compare-pan.py work/before work/after
"""
import json
import sys
from pathlib import Path

import numpy as np
from PIL import Image

before, after = map(Path, sys.argv[1:3])
y, x = np.mgrid[:180, :320]
angle = np.deg2rad(15)
rows = {key: [0, 0, 0.0, 0.0, 0.0] for key in ("all", "shadow", "lit", "within4", "outside4")}


def luma(path):
    return np.asarray(Image.open(path).convert("RGB")) @ np.array([0.2126, 0.7152, 0.0722]) / 255


for i in range(8):
    name = f"Quality C pan 2.0 px-{i:02}.png"
    ref = f"Quality C pan 2.0 px-ref-{i:02}.png"
    old, new, reference = luma(before / name), luma(after / name), luma(before / ref)
    assert np.array_equal(reference, luma(after / ref)), "The two arms must use identical references."
    # ShadowEdgeStage's orthographic 15 degree line. The pan advances two pixels per frame from frame 80.
    # ShadowEdgeMeasure measured its drawn offset as 2.125 pixels, on both arms.
    distance = -np.sin(angle) * (x + 0.5 - 160) - np.cos(angle) * (y + 0.5 - (90 + 2 * i)) - 2.125
    band = (x >= 2) & (x + 0.5 < 156) & (y >= 2) & (y < 178) & (abs(distance) <= 12)
    for key, mask in (
        ("all", band), ("shadow", band & (distance > 0)), ("lit", band & (distance <= 0)),
        ("within4", band & (abs(distance) <= 4)), ("outside4", band & (abs(distance) > 4)),
    ):
        row = rows[key]
        row[0] += int(mask.sum())
        row[1] += int((mask & (abs(new - old) > 1e-6)).sum())
        row[2] += float(abs(old - reference)[mask].sum())
        row[3] += float(abs(new - reference)[mask].sum())
        row[4] += float(abs(new - old)[mask].sum())

count = rows["all"][0]
result = {
    key: {"pixels": r[0], "changed_pixels": r[1], "before_error": r[2] / r[0], "after_error": r[3] / r[0],
          "contribution_to_band_error_change": (r[3] - r[2]) / count, "change_over_whole_band": r[4] / count}
    for key, r in rows.items()
}
print(json.dumps(result, indent=2))
