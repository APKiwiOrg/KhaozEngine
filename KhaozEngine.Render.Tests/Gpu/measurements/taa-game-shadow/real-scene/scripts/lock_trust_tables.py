"""Per-token tables for the lock-trust round over the four scene conditions, next to the reference arms.

Usage: lock_trust_tables.py <root> <prior-root> <summary.json> <token>... The root holds this round's
<condition>-baseline and <condition>-<token> captures. locks-off and lock-history-ridge come from the prior round's
root. Dots use lock_rule_tables.py's detector with this round's baseline masks and FXAA.
"""
import json
from pathlib import Path
import sys
import numpy as np
from PIL import Image

ROOT, PRIOR, SUMMARY = Path(sys.argv[1]), Path(sys.argv[2]), Path(sys.argv[3])
TOKENS = sys.argv[4:]
CONDITIONS = ["back15", "day30", "back60", "ground15"]
REFERENCES = [("baseline", ROOT), ("locks-off", PRIOR), ("lock-history-ridge", PRIOR)]


def rgb(path):
    return np.asarray(Image.open(path).convert("RGB"), dtype=np.int32)


def luma(a):
    return (0.2126 * a[..., 0] + 0.7152 * a[..., 1] + 0.0722 * a[..., 2]) / 255.0


def median3(a):
    p = np.pad(a, 1, mode="edge")
    return np.median(np.stack([p[y:y + a.shape[0], x:x + a.shape[1]] for y in range(3) for x in range(3)]), axis=0)


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
    masks, fxaa = [], []
    for i in range(16):
        r = rgb(base / f"regions-{i:03d}.png")
        masks.append([np.all(r == c, axis=2) for c in [(0, 255, 0), (255, 220, 0)]])
        fxaa.append(luma(rgb(base / "fxaa" / f"crop-{i:03d}.png")))
    arms = REFERENCES + [(t, ROOT) for t in TOKENS]
    for arm, root in arms:
        path = root / f"{condition}-{arm}"
        if not path.exists():
            continue
        values = metrics(path)
        for mode in ["taa-native", "taa-quality"]:
            dots = [0, 0]
            for i in range(16):
                a = luma(rgb(path / mode / f"crop-{i:03d}.png"))
                bright = (a - median3(a) > 0.05) & (a - fxaa[i] > 0.05)
                for region in range(2):
                    dots[region] += int((bright & masks[i][region]).sum())
            results[f"{condition}/{arm}/{mode}"] = {
                region: values[(mode, region)] + [dots[j]] for j, region in enumerate(["character", "ground"])}
    values = metrics(base)
    results[f"{condition}/fxaa/fxaa"] = {r: values[("fxaa", r)] + [0] for r in ["character", "ground"]}

for token in TOKENS:
    print(f"### Real-scene {token}\n")
    print("| Condition | Arm | Mode | Character grain mean | p95 | Change mean | Dots | Ground grain mean | p95 "
          "| Change mean | Dots |")
    print("| --- | --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |")
    for condition in CONDITIONS:
        for arm in [a for a, _ in REFERENCES] + [token, "fxaa"]:
            for mode in (["fxaa"] if arm == "fxaa" else ["taa-native", "taa-quality"]):
                key = f"{condition}/{arm}/{mode}"
                if key not in results:
                    continue
                c, g = results[key]["character"], results[key]["ground"]
                print(f"| {condition} | {arm} | {mode} | {c[0]:.6f} | {c[1]:.5f} | {c[2]:.5f} | {c[3]} | "
                      f"{g[0]:.6f} | {g[1]:.5f} | {g[2]:.5f} | {g[3]} |")
    print()
SUMMARY.write_text(json.dumps(results, indent=2) + "\n")
