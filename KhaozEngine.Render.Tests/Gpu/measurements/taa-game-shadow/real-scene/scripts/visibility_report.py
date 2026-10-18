"""Release coincidence against each arm and against its baseline dots. Captures are untracked."""
import json
from pathlib import Path
import subprocess
import sys
import numpy as np
from PIL import Image

root, out = map(Path, sys.argv[1:3])
repo = Path(__file__).resolve().parents[6]
assert subprocess.check_output(["git", "rev-parse", "--show-toplevel"], text=True).strip() == str(repo)
assert subprocess.check_output(["git", "branch", "--show-current"], text=True).strip() == "fix/taa-grain-switches"

def rgb(p):
    return np.asarray(Image.open(p).convert("RGB"), dtype=np.int32)

def dots(run, mode, i):
    frame = rgb(run/mode/f"crop-{i:03d}.png") @ np.array([.2126,.7152,.0722]) / 255
    fxaa = rgb(run/"fxaa"/f"crop-{i:03d}.png") @ np.array([.2126,.7152,.0722]) / 255
    pad = np.pad(frame, 1, mode="edge")
    median = np.median(np.stack([pad[y:y+frame.shape[0],x:x+frame.shape[1]]
                                for y in range(3) for x in range(3)]), axis=0)
    return (frame-median > .05) & (frame-fxaa > .05)

rows = {}
for condition in ("back15", "day30", "back60", "ground15"):
    for arm in sys.argv[3:]:
        run = root/(condition+"-"+arm)
        overlay = root/(condition+"-"+arm+"+show-vis-release")
        source = "shadow-bilinear-3x3" if arm.startswith("shadow-bilinear-3x3+") else "baseline"
        baseline = root/(condition+"-"+source)
        for mode in ("taa-native", "taa-quality"):
            total = dict(band=0, released=0, dots=0, released_dots=0,
                         baseline_dots=0, released_baseline_dots=0,
                         baseline_dots_near_release=0, outside_overlay_different=0)
            for i in range(16):
                image, marked = rgb(run/mode/f"crop-{i:03d}.png"), rgb(overlay/mode/f"crop-{i:03d}.png")
                region = rgb(run/f"regions-{i:03d}.png")
                mask = np.all(region == (0,255,0),axis=2)
                release = np.all(marked == (255,0,255),axis=2)
                d, b = dots(run,mode,i), dots(baseline,mode,i)
                near = np.pad(release,1)
                near = np.logical_or.reduce([near[y:y+release.shape[0],x:x+release.shape[1]]
                                             for y in range(3) for x in range(3)])
                total["band"] += int(mask.sum())
                total["released"] += int((mask & release).sum())
                total["dots"] += int((mask & d).sum())
                total["released_dots"] += int((mask & d & release).sum())
                total["baseline_dots"] += int((mask & b).sum())
                total["released_baseline_dots"] += int((mask & b & release).sum())
                total["baseline_dots_near_release"] += int((mask & b & near).sum())
                total["outside_overlay_different"] += int((np.any(image != marked,axis=2) & ~release).sum())
            rows[condition+"/"+arm+"/"+mode] = total
            print(condition,arm,mode,total,flush=True)
out.write_text(json.dumps(rows,indent=2)+"\n")
