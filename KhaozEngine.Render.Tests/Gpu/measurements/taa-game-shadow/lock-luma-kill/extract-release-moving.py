"""Extract the passed moving-shading fact from v21.4.0 release-gate TRX artifacts.

Usage from the repository root: extract-release-moving.py <download-directory> <relative-report-directory>
The download directory contains one subdirectory per backend. No artifact filenames enter the report.
"""
import json
import re
import subprocess
import sys
import xml.etree.ElementTree as ET
from pathlib import Path

inputs, output = map(Path, sys.argv[1:3])
assert not output.is_absolute(), "Keep report paths repo-relative."
pattern = r"^\s*((?:Native|Quality) (?:S |C pan |M object |MS object ).*?)\s+((?:[-+]?\d+\.\d+[ \t]+){15}[-+]?\d+\.\d+)[ \t]*$"
result = {"runId": 39076952301, "commit": "4e05462bec95cd49e47c5592c1ce8c0fcb004f4d",
          "tag": "v21.4.0", "precisionDecimals": 4, "backends": {}}


def guard():
    assert Path.cwd() == Path(subprocess.check_output(["git", "rev-parse", "--show-toplevel"], text=True).strip())
    assert subprocess.check_output(["git", "branch", "--show-current"], text=True).strip() == "fix/taa-game-shadow"


for backend in ("metal-native", "direct3d11-native", "vulkan-native"):
    found = None
    for path in (inputs / backend).glob("*.trx"):
        for _, element in ET.iterparse(path, events=("end",)):
            if not element.tag.endswith("UnitTestResult"):
                continue
            if "Shadow_edge_lag_on_moving_pixels_and_fast_shadows_stays_within_its_bounds" in element.attrib.get("testName", ""):
                assert element.attrib["outcome"] == "Passed", "The archived baseline must have passed."
                found = "\n".join(n.text or "" for n in element.iter() if n.tag.endswith("StdOut"))
                break
            element.clear()
        if found:
            break
    assert found, f"No passed moving-shading output for {backend}."
    # TRX text entities retain Windows CRLF even after XML parsing.
    found = "\n".join(line.rstrip() for line in found.splitlines()) + "\n"
    rows = {name: list(map(float, values.split())) for name, values in re.findall(pattern, found, re.M)}
    assert len(rows) == 38, f"Expected 38 moving-shading cases for {backend}, got {len(rows)}."
    result["backends"][backend] = rows
    guard()
    (output / "logs" / f"before-21.4-{backend}-moving.txt").write_text(found)

guard()
(output / "before-21.4-moving.json").write_text(json.dumps(result, indent=2) + "\n")
print("Extracted 38 passed baseline cases per backend from release-gate run 39076952301.")
