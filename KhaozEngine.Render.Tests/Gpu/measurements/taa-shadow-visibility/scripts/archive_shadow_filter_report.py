"""Own-FXAA dot counts, lock shares, FXAA 10-90 widths and owner-look panels.

Usage: shadow_filter_report.py <capture-root> <report-directory> <arm>...
The report directory is normally runs/shadow-filters. Captures stay outside git.
"""
import json
from pathlib import Path
import subprocess
import sys
import numpy as np
from PIL import Image, ImageDraw

ROOT, OUT = map(Path, sys.argv[1:3])
ARMS = ["baseline"] + sys.argv[3:]
CONDITIONS = ["back15", "day30", "back60", "ground15"]
MODES = ["taa-native", "taa-quality", "fxaa"]
COLORS = {"character": (0, 255, 0), "ground": (255, 220, 0)}
LUMA = np.array([0.2126, 0.7152, 0.0722])


def guard():
    repo = Path(__file__).resolve().parents[6]
    assert subprocess.check_output(["git", "rev-parse", "--show-toplevel"], text=True).strip() == str(repo)
    assert subprocess.check_output(["git", "branch", "--show-current"], text=True).strip() == "fix/taa-grain-switches"


def rgb(p):
    return np.asarray(Image.open(p).convert("RGB"), dtype=np.int32)


def luma(a):
    return a @ LUMA / 255.0


def median3(a):
    p = np.pad(a, 1, mode="edge")
    return np.median(np.stack([p[y:y+a.shape[0], x:x+a.shape[1]]
                              for y in range(3) for x in range(3)]), axis=0)


def isotonic(values):
    blocks = []
    for value in values:
        blocks.append([float(value), 1])
        while len(blocks) > 1 and blocks[-2][0] > blocks[-1][0]:
            b, a = blocks.pop(), blocks.pop()
            count = a[1] + b[1]
            blocks.append([(a[0]*a[1]+b[0]*b[1])/count, count])
    return np.concatenate([np.repeat(v, count) for v, count in blocks])


def width(profile, endpoint=5):
    # Local lit and shadow plateaus cancel material contrast. Do not smooth the edge spatially.
    lo, hi = np.median(profile[:endpoint]), np.median(profile[-endpoint:])
    if hi < lo:
        profile, lo, hi = -profile, -lo, -hi
    if hi - lo < 0.15:
        return None
    fit = isotonic(profile)
    crossings = []
    for fraction in (0.1, 0.9):
        target = lo + fraction*(hi-lo)
        j = int(np.searchsorted(fit, target))
        if not 0 < j < len(fit):
            return None
        crossings.append(j-1+(target-fit[j-1])/(fit[j]-fit[j-1]))
    return crossings[1]-crossings[0]


def edge_widths(run, condition):
    samples = {"character": [], "ground": []}
    for i in range(16):
        f = luma(rgb(run/"fxaa"/f"crop-{i:03d}.png"))
        n = luma(rgb(run/f"no-shadow-{i:03d}.png"))
        attenuation = (n-f)/np.maximum(n, 0.02)
        if condition != "ground15":
            # Horizontal upper-arm transects stay inside the receiver even at the slow sun rate.
            body = rgb(run/f"visible-mask-{i:03d}.png")[..., 0] > 0
            for y in (202, 204, 206):
                profile = attenuation[y, 213:231]
                if not body[y-1:y+2, 212:232].all():
                    continue
                # Reject partial penumbras and a silhouette/background transition. The local plateaus
                # must include a substantially shadowed side and a substantially lit side.
                if np.median(profile[:3]) < 0.35 or np.median(profile[-3:]) > 0.15:
                    continue
                w = width(profile, endpoint=3)
                if w is not None:
                    samples["character"].append(w)
        if condition == "ground15":
            for x in range(280, 331, 10):
                w = width(attenuation[270:380, x])
                if w is not None:
                    samples["ground"].append(w)
        else:
            for y in range(80, 111, 5):
                w = width(attenuation[y, 90:220])
                if w is not None:
                    samples["ground"].append(w)
    return {region: {"median": float(np.median(v)) if v else None,
                     "p25": float(np.percentile(v, 25)) if v else None,
                     "p75": float(np.percentile(v, 75)) if v else None,
                     "count": len(v), "samples": v} for region, v in samples.items()}


def metrics(run):
    rows = {}
    for line in (run/"metrics.txt").read_text().splitlines():
        p = line.split()
        if len(p) == 7 and p[0] in MODES:
            rows[p[0]+"/"+p[1]] = dict(zip(["grain", "p95", "change", "change_p95"], map(float, p[3:])))
    return rows


guard()
OUT.mkdir(parents=True, exist_ok=True)
report = {}
for condition in CONDITIONS:
    for arm in ARMS:
        run = ROOT/(condition+"-"+arm)
        values = metrics(run)
        overlays = ROOT/(condition+"-"+("show-locked" if arm == "baseline" else arm+"+show-locked"))
        totals = {mode: {region: {"dots": 0, "band_pixels": 0, "locked_pixels": 0, "locked_dots": 0}
                         for region in COLORS} for mode in MODES}
        for i in range(16):
            regions = rgb(run/f"regions-{i:03d}.png")
            masks = {r: np.all(regions == color, axis=2) for r, color in COLORS.items()}
            fxaa = luma(rgb(run/"fxaa"/f"crop-{i:03d}.png"))
            for mode in MODES:
                frame = luma(rgb(run/mode/f"crop-{i:03d}.png"))
                dots = (frame-median3(frame) > 0.05) & (frame-fxaa > 0.05)
                locked = None
                if mode != "fxaa" and overlays.exists():
                    locked = np.all(rgb(overlays/mode/f"crop-{i:03d}.png") == (0,255,255), axis=2)
                for region, mask in masks.items():
                    t = totals[mode][region]
                    t["dots"] += int((dots & mask).sum())
                    t["band_pixels"] += int(mask.sum())
                    if locked is not None:
                        t["locked_pixels"] += int((locked & mask).sum())
                        t["locked_dots"] += int((locked & dots & mask).sum())
        row = {"width": edge_widths(run, condition), "metrics": {}}
        for mode in MODES:
            row["metrics"][mode] = {}
            for region in COLORS:
                t = totals[mode][region]
                t.update(values[mode+"/"+region])
                t["locked_share"] = t["locked_pixels"]/max(t["band_pixels"], 1) if overlays.exists() and mode != "fxaa" else None
                t["dot_locked_share"] = t["locked_dots"]/max(t["dots"], 1) if t["locked_share"] is not None else None
                row["metrics"][mode][region] = t
        report[condition+"/"+arm] = row
        print("MEASURED", condition, arm, flush=True)
guard()
(OUT/"summary.json").write_text(json.dumps(report, indent=2)+"\n")

tables = []
def metric(t):
    return f'{t["grain"]:.6f} / {t["p95"]:.5f} / {t["change"]:.5f} / {t["dots"]}'
def widths(row):
    return " / ".join("n/a" if row["width"][r]["median"] is None else f'{row["width"][r]["median"]:.2f}' for r in COLORS)
for arm in ARMS[1:]:
    tables += [f"### {arm}", "", "| Condition | Arm | Region | Native grain / p95 / change / dots | Quality grain / p95 / change / dots | Own FXAA grain / p95 / change / dots | Character locked % N / Q | FXAA width C / G px |",
               "| --- | --- | --- | ---: | ---: | ---: | ---: | ---: |"]
    for condition in CONDITIONS:
        for name in ("baseline", arm):
            row = report[condition+"/"+name]
            shares = [row["metrics"][m]["character"]["locked_share"] for m in MODES[:2]]
            locked = " / ".join("n/a" if s is None else f"{100*s:.2f}" for s in shares)
            for region in COLORS:
                cells = [metric(row["metrics"][m][region]) for m in MODES]
                tables.append(f'| {condition} | {name} | {region} | '+" | ".join(cells)+f' | {locked} | {widths(row)} |')
    tables.append("")
guard()
(OUT/"tables.md").write_text("\n".join(tables).rstrip()+"\n")

# The FXAA panel comes from the same arm, including for combinations.
guard()
panels = OUT/"visuals"
panels.mkdir(exist_ok=True)
for arm in ARMS:
    run = ROOT/("back15-"+arm)
    sheet = Image.new("RGB", (1152, 408), "#202020")
    draw = ImageDraw.Draw(sheet)
    for i, mode in enumerate(MODES):
        im = Image.open(run/mode/"crop-015.png").convert("RGB").crop((144,200,240,296))
        sheet.paste(im.resize((384,384), Image.Resampling.NEAREST), (i*384,24))
        draw.text((i*384+8,6), mode, fill="white")
        guard()
        im.resize((384,384), Image.Resampling.NEAREST).save(panels/(arm+"-"+mode+"-character-015-4x.png"))
    guard()
    sheet.save(panels/(arm+"-character-015-4x.png"))
    guard()
    Image.open(run/"contact-sheet-015.png").save(panels/(arm+"-contact-sheet-015.png"))
ground = Image.new("RGB", (320*len(ARMS),224), "#202020")
draw = ImageDraw.Draw(ground)
for i, arm in enumerate(ARMS):
    im = Image.open(ROOT/("back15-"+arm)/"fxaa"/"crop-015.png").convert("RGB").crop((70,35,230,135))
    ground.paste(im.resize((320,200),Image.Resampling.NEAREST),(320*i,24))
    draw.text((320*i+6,6),arm,fill="white")
guard()
ground.save(panels/"ground-edge-fxaa-015-2x.png")
