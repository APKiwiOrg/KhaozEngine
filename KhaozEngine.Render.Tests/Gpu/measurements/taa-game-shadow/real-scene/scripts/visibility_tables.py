"""Place each visibility candidate beside point, bilinear and own-FXAA controls."""
import json
from pathlib import Path
import subprocess
import sys

root = Path(__file__).resolve().parents[6]
assert subprocess.check_output(["git", "rev-parse", "--show-toplevel"], text=True).strip() == str(root)
assert subprocess.check_output(["git", "branch", "--show-current"], text=True).strip() == "fix/taa-grain-switches"
summary, output = map(Path, sys.argv[1:3])
values = json.loads(summary.read_text())
lines = ["Numbers are grain mean / p95 / change mean / dots / locked percent. FXAA omits locked percent.", ""]


def metric(row, mode, region):
    v = row["metrics"][mode][region]
    fields = f'{v["grain"]:.6f} / {v["p95"]:.5f} / {v["change"]:.5f} / {v["dots"]}'
    if mode != "fxaa":
        fields += " / " + ("n/a" if v["locked_share"] is None else f'{100*v["locked_share"]:.2f}')
    return fields


for arm in sys.argv[3:]:
    lines += ["### "+arm, "", "| Condition | Arm | Region | Native | Quality | Own FXAA |",
              "| --- | --- | --- | ---: | ---: | ---: |"]
    for condition in ("back15", "day30", "back60", "ground15"):
        for name in dict.fromkeys(("baseline", "shadow-bilinear-3x3", arm)):
            row = values[condition+"/"+name]
            for region in ("character", "ground"):
                cells = [metric(row, mode, region) for mode in ("taa-native", "taa-quality", "fxaa")]
                lines.append(f"| {condition} | {name} | {region} | "+" | ".join(cells)+" |")
    lines.append("")
output.write_text("\n".join(lines))
