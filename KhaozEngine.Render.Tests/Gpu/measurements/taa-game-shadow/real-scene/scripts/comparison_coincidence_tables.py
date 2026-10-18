"""Tabulate both receiver regions from the saved visibility comparison reports."""
import json
from pathlib import Path
import subprocess
import sys

repo = Path(__file__).resolve().parents[6]
assert subprocess.check_output(["git", "rev-parse", "--show-toplevel"], text=True).strip() == str(repo)
assert subprocess.check_output(["git", "branch", "--show-current"], text=True).strip() == "fix/taa-grain-switches"
root = Path(sys.argv[1])
summary = json.loads((root / "summary.json").read_text())
arms = list(dict.fromkeys(key.split("/", 1)[1] for key in summary))
lines = ["# Dot and release coincidence", "",
         "Percentages use pooled pixel-frame counts. Dots use each arm's own FXAA.",
         "Bilinear-dot overlap compares the release mask to bilinear-only dots, in screen space.",
         "The last-four column includes the current frame. It does not follow a moving surface.",
         "Unset and bilinear-only have no visibility release, so their release overlap is structurally zero.",
         "The max-of-nine release overlay exists only for back15. Its other release entries are unmeasured.",
         "All measured release overlays are identical to normal output outside the marked pixels.", ""]


def percent(count, total):
    return "n/a" if not total else f"{100 * count / total:.2f}"


for region, suffix in (("character", ""), ("ground", "-ground")):
    release = json.loads((root / f"release{suffix}-coincidence.json").read_text())
    release.update(json.loads((root / f"max-release{suffix}-coincidence.json").read_text()))
    assert all(row["outside_overlay_different"] == 0 for row in release.values())
    for condition in ("back15", "day30", "back60", "ground15"):
        lines += [f"## {condition}, {region}", "",
                  "| Arm | Mode | Dots | Locked % | Dots locked % | Band released % | Dots released % | Bilinear dots released % | Bilinear dots within 1 px % | Bilinear dots released previous frame % | Bilinear dots released last four % |",
                  "| --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |"]
        for arm in arms:
            for mode in ("taa-native", "taa-quality"):
                metric = summary[condition + "/" + arm]["metrics"][mode][region]
                row = release.get(condition + "/" + arm + "/" + mode)
                fields = [str(metric["dots"]), percent(metric["locked_pixels"], metric["band_pixels"]),
                          percent(metric["locked_dots"], metric["dots"])]
                if row:
                    assert row["dots"] == metric["dots"]
                    assert row["band"] == metric["band_pixels"]
                    fields += [percent(row["released"], row["band"]),
                               percent(row["released_dots"], row["dots"])]
                    fields += [percent(row[key], row["baseline_dots"]) for key in (
                        "released_baseline_dots", "baseline_dots_near_release",
                        "baseline_dots_released_previous_frame", "baseline_dots_released_last4_screen_frames")]
                else:
                    fields += ["0.00" if arm in ("baseline", "shadow-bilinear-3x3") else "unmeasured"] * 6
                lines.append(f"| {arm} | {mode.removeprefix('taa-')} | " + " | ".join(fields) + " |")
        lines.append("")
(root / "coincidence-tables.md").write_text("\n".join(lines))
