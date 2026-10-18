"""Histograms of the show-gate, show-gate-extent and show-lock-age overlays over bright dots and the character band.

Usage: gate_histogram.py <root> [condition]. The root holds <condition>-baseline, -noop, -show-gate,
-show-gate-extent and -show-lock-age captures. Dots use the coincide.py detector on the noop image: luma more than
0.05 above its 3x3 median and more than 0.05 above the baseline FXAA pixel, inside the green character band.
"""
import sys
import numpy as np
from PIL import Image

ROOT = sys.argv[1]
CONDITION = sys.argv[2] if len(sys.argv) > 2 else "back15"
COLOURS = {(255, 0, 0): 1, (0, 255, 0): 2, (0, 0, 255): 3, (255, 255, 0): 4, (255, 0, 255): 5, (0, 255, 255): 6,
           (255, 255, 255): 7}
NAMES = {
    "show-gate": {1: "upscale gate (Jitter.z >= ShadingMaxUpscale)", 2: "ridge or box-width gate",
                  3: "history within reset extents of box centre", 4: "upscaled, own shift under extents",
                  5: "ring threshold", 6: "opposite-pair requirement", 7: "no history"},
    "show-gate-extent": {2: "box <= ridgeStep", 4: "box 1-3 ridgeSteps, fires at ridgeStep",
                         3: "box 1-3 ridgeSteps, still blocked at ridgeStep", 1: "box > 3 ridgeSteps, fires at ridgeStep",
                         5: "box > 3 ridgeSteps, still blocked at ridgeStep"},
    "show-lock-age": {1: "ridge this frame", 2: "carried, age 1", 3: "carried, age 2 to 3", 4: "carried, age 4+"},
}


def rgb(path):
    return np.asarray(Image.open(path).convert("RGB"), dtype=np.int32)


def luma(a):
    return (0.2126 * a[..., 0] + 0.7152 * a[..., 1] + 0.0722 * a[..., 2]) / 255.0


def median3(a):
    p = np.pad(a, 1, mode="edge")
    return np.median(np.stack([p[y:y + a.shape[0], x:x + a.shape[1]] for y in range(3) for x in range(3)]), axis=0)


def codes(image, base):
    out = np.zeros(image.shape[:2], dtype=np.int32)
    for colour, code in COLOURS.items():
        out[np.all(image == colour, axis=2) & ~np.all(base == colour, axis=2)] = code
    return out


def run(name, mode, i):
    return rgb(f"{ROOT}/{CONDITION}-{name}/{mode}/crop-{i:03d}.png")


for mode in ["taa-native", "taa-quality"]:
    hist = {o: {"band": {}, "dots": {}} for o in NAMES}
    cross = {}
    stray = 0
    collisions = 0
    totals = {"band": 0, "dots": 0}
    for i in range(16):
        regions = rgb(f"{ROOT}/{CONDITION}-baseline/regions-{i:03d}.png")
        band = np.all(regions == (0, 255, 0), axis=2)
        base = run("noop", mode, i)
        fx = luma(rgb(f"{ROOT}/{CONDITION}-baseline/fxaa/crop-{i:03d}.png"))
        lb = luma(base)
        dots = band & (lb - median3(lb) > 0.05) & (lb - fx > 0.05)
        totals["band"] += int(band.sum())
        totals["dots"] += int(dots.sum())
        for colour in COLOURS:
            collisions += int((np.all(base == colour, axis=2) & band).sum())
        per = {}
        for overlay in NAMES:
            image = run(overlay, mode, i)
            c = codes(image, base)
            stray += int((np.abs(image - base).max(axis=2) > 0)[c == 0].sum())
            per[overlay] = c
        # Held means locked (show-lock-age painted), so a locked pixel the gate left unpainted was released.
        gate = per["show-gate"].copy()
        locked = per["show-lock-age"] > 0
        gate[(gate == 0) & locked] = -1
        gate[~locked] = -2
        per["show-gate"] = gate
        for overlay, c in per.items():
            for where, mask in (("band", band), ("dots", dots)):
                values, counts = np.unique(c[mask], return_counts=True)
                for v, k in zip(values, counts):
                    hist[overlay][where][int(v)] = hist[overlay][where].get(int(v), 0) + int(k)
        for g, a in zip(gate[dots], per["show-lock-age"][dots]):
            cross[(int(g), int(a))] = cross.get((int(g), int(a)), 0) + 1
    print(f"## {CONDITION} {mode}")
    print(f"band pixels {totals['band']}, dots {totals['dots']}, pixels differing from noop outside a code {stray}, "
          f"noop band pixels already equal to a code colour {collisions}")
    for overlay, names in NAMES.items():
        labels = dict(names)
        if overlay == "show-gate":
            labels.update({-2: "unlocked", -1: "locked and released this frame"})
        else:
            labels[0] = "not painted"
        print(f"\n### {overlay}\n")
        print("| Code | Meaning | Band pixels | Band share | Dots | Dot share |")
        print("| ---: | --- | ---: | ---: | ---: | ---: |")
        for code in sorted(labels):
            b = hist[overlay]["band"].get(code, 0)
            d = hist[overlay]["dots"].get(code, 0)
            print(f"| {code} | {labels[code]} | {b} | {100 * b / max(totals['band'], 1):.1f}% | {d} | "
                  f"{100 * d / max(totals['dots'], 1):.1f}% |")
    print("\n### dots, gate by lock age\n")
    ages = NAMES["show-lock-age"]
    print("| Gate | " + " | ".join(ages[a] for a in sorted(ages)) + " |")
    print("| --- | " + " | ".join("---:" for _ in ages) + " |")
    for g in sorted({g for g, _ in cross}):
        name = {-2: "unlocked", -1: "released"}.get(g, NAMES["show-gate"].get(g, str(g)))
        print(f"| {name} | " + " | ".join(str(cross.get((g, a), 0)) for a in sorted(ages)) + " |")
    print()
