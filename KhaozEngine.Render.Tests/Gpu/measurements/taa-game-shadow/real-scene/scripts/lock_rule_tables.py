"""Print complete per-rule tables and save machine-readable measurements for the four scene conditions."""
import json
from pathlib import Path
import sys
import numpy as np
from PIL import Image

ROOT = Path(sys.argv[1])
CONDITIONS = ["back15", "day30", "back60", "ground15"]
CANDIDATES = ["lock-history-ridge", "lock-shade-release", "lock-bound-current"]

def rgb(path):
    return np.asarray(Image.open(path).convert("RGB"), dtype=np.int32)

def luma(a):
    return (0.2126 * a[..., 0] + 0.7152 * a[..., 1] + 0.0722 * a[..., 2]) / 255.0

def median3(a):
    p = np.pad(a, 1, mode="edge")
    return np.median(np.stack([p[y:y+a.shape[0], x:x+a.shape[1]] for y in range(3) for x in range(3)]), axis=0)

def metrics(run):
    rows = {}
    for line in (run / "metrics.txt").read_text().splitlines():
        p = line.split()
        if len(p) == 7 and p[0] in ("taa-native", "taa-quality", "fxaa"):
            rows[(p[0], p[1])] = [float(x) for x in p[3:6]]
    return rows

results = {}
for condition in CONDITIONS:
    base = ROOT / f"{condition}-baseline"
    masks = []
    fxaa = []
    for i in range(16):
        r = rgb(base / f"regions-{i:03d}.png")
        masks.append([np.all(r == c, axis=2) for c in [(0, 255, 0), (255, 220, 0)]])
        fxaa.append(luma(rgb(base / "fxaa" / f"crop-{i:03d}.png")))
    for arm in ["baseline", "locks-off"] + CANDIDATES:
        path = ROOT / f"{condition}-{arm}"
        values = metrics(path)
        for mode in ["taa-native", "taa-quality"]:
            dots = [0, 0]
            for i in range(16):
                a = luma(rgb(path / mode / f"crop-{i:03d}.png"))
                bright = (a - median3(a) > 0.05) & (a - fxaa[i] > 0.05)
                for region in range(2):
                    dots[region] += int((bright & masks[i][region]).sum())
            results[f"{condition}/{arm}/{mode}"] = {
                region: values[(mode, region)] + [dots[j]] for j, region in enumerate(["character", "ground"])
            }
    values = metrics(base)
    results[f"{condition}/fxaa/fxaa"] = {r: values[("fxaa", r)] + [0] for r in ["character", "ground"]}

# Caller redirects stdout to its evidence file after checking worktree identity.
for candidate in CANDIDATES:
    print(f"### Real-scene {candidate}\n")
    print("| Condition | Arm | Mode | Character grain mean | p95 | Change mean | Dots | Ground grain mean | p95 | Change mean | Dots |")
    print("| --- | --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |")
    for condition in CONDITIONS:
        for arm in ["baseline", "locks-off", candidate, "fxaa"]:
            for mode in (["fxaa"] if arm == "fxaa" else ["taa-native", "taa-quality"]):
                r = results[f"{condition}/{arm}/{mode}"]
                c, g = r["character"], r["ground"]
                print(f"| {condition} | {arm} | {mode} | {c[0]:.6f} | {c[1]:.5f} | {c[2]:.5f} | {c[3]} | {g[0]:.6f} | {g[1]:.5f} | {g[2]:.5f} | {g[3]} |")
    print()
if len(sys.argv) > 2:
    Path(sys.argv[2]).write_text(json.dumps(results, indent=2) + "\n")
