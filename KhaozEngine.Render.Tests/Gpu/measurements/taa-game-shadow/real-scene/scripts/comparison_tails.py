"""Extract absolute textured tails from the one-process-per-arm TRX measurements.

Usage: comparison_tails.py <guard-root> <output-root> <arm>...
"""
import json
from pathlib import Path
import re
import subprocess
import sys
import xml.etree.ElementTree as ET

repo = Path(__file__).resolve().parents[6]
assert subprocess.check_output(["git", "rev-parse", "--show-toplevel"], text=True).strip() == str(repo)
assert subprocess.check_output(["git", "branch", "--show-current"], text=True).strip() == "fix/taa-grain-switches"
source, output = map(Path, sys.argv[1:3])
ns = {"t": "http://microsoft.com/schemas/VisualStudio/TeamTest/2010"}
rows = {}
stable = {}
for arm in sys.argv[3:]:
    tree = ET.parse(source/arm/"results.trx")
    values = {}
    stable[arm] = {}
    for result in tree.findall(".//t:UnitTestResult", ns):
        name = result.get("testName")
        text = result.findtext("t:Output/t:StdOut", "", ns)
        if "TemporalStableDetailGpuTests" in name or "TemporalStablePeakGpuTests" in name:
            stable[arm][name] = text
        if "Shadow_edge_lag_on_moving_pixels_and_fast_shadows" not in name:
            continue
        for line in text.splitlines():
            m = re.match(r"^(Native|Quality) S (1.0|8.0) px (advancing|retreating) +[\d.]+ ([\d.]+) ", line)
            if m:
                values[f"{m[1]}/{m[3]}/{m[2]}"] = float(m[4])
    assert len(values) == 8, (arm, values)
    rows[arm] = values
output.mkdir(parents=True, exist_ok=True)
(output/"absolute-tails.json").write_text(json.dumps(rows, indent=2)+"\n")
lines = ["# Absolute textured shadow tails", "", "Mean absolute luma error. The bound remains 1.5 times that arm's 1 px tail.", "", "| Arm | Mode | Direction | 1 px tail | 8 px tail | Ratio | Bound | 8 px minus unset | 8 px minus previous arm |", "| --- | --- | --- | ---: | ---: | ---: | ---: | ---: | ---: |"]
for arm, values in rows.items():
    for mode in ("Native", "Quality"):
        for direction in ("advancing", "retreating"):
            slow = values[f"{mode}/{direction}/1.0"]
            key = f"{mode}/{direction}/8.0"
            fast = values[key]
            lines.append(f"| {arm} | {mode} | {direction} | {slow:.9f} | {fast:.9f} | {fast/slow:.6f} | {1.5*slow:.9f} | {fast-rows['unset'][key]:+.9f} | {fast-rows['shadow-bilinear-3x3,vis-release-0.1'][key]:+.9f} |")
(output/"absolute-tails.md").write_text("\n".join(lines)+"\n")
identity = {reference: {arm: {name: text == stable[reference].get(name) for name, text in tests.items()}
                        for arm, tests in stable.items()}
            for reference in ("unset", "shadow-bilinear-3x3,vis-release-0.1")}
assert all(len(tests) == len(stable["unset"]) for tests in stable.values())
(output/"static-printed-value-identity.json").write_text(json.dumps(identity, indent=2)+"\n")
for reference, arms in identity.items():
    for arm, tests in arms.items():
        print(arm, "stable printed outputs identical to", reference, sum(tests.values()), "of", len(tests))
