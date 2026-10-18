"""Extract every hosted failure and sanitize filesystem paths before committing evidence.

Usage: hosted_visibility_report.py <download-root> <output-directory> <run-id> <source-commit>
The input is the workflow's filtered-render-measurements artifacts, one directory per backend.
"""
import json
from pathlib import Path
import re
import subprocess
import sys

source, output = map(Path, sys.argv[1:3])
run, commit = sys.argv[3:5]
root = Path(__file__).resolve().parents[6]
assert subprocess.check_output(["git", "rev-parse", "--show-toplevel"], text=True).strip() == str(root)
assert subprocess.check_output(["git", "branch", "--show-current"], text=True).strip() == "fix/taa-grain-switches"
output.mkdir(parents=True, exist_ok=True)
report = {"run_id": run, "source_commit": commit, "backends": {}}


def sanitize(text):
    text = re.sub(r"[A-Z]:\\a\\KhaozEngine\\KhaozEngine\\", "", text)
    text = re.sub(r"/[^/\s]+/[^/\s]+/work/KhaozEngine/KhaozEngine/", "", text)
    text = text.replace("/_/", "")
    text = re.sub(r"[A-Z]:\\Users\\[^\\]+\\AppData\\Local\\Temp\\", "scratch/", text)
    text = text.replace("/private/tmp/", "scratch/").replace("/tmp/", "scratch/")
    text = re.sub(r"/Users/[^\s:]+", "runtime-path", text)
    return "\n".join(line.rstrip() for line in text.splitlines()).rstrip()+"\n"


def measurement_rows(message, reason):
    rows = []
    for line in message.splitlines():
        row = {"message": line, "classification": reason}
        match = re.match(r"(.+): (error|temporal error|change) ([\d.]+) above v21.2.0 ([\d.]+) plus a tenth", line)
        if match:
            row.update(actual=float(match[3]), bound=(float(match[4])+0.0000005)*1.1)
        match = re.match(r"(.+): textured (band|tail) ([\d.]+) above ([\d.]+) plus a tenth", line)
        if match:
            row.update(actual=float(match[3]), bound=float(match[4])*1.1)
        match = re.match(r"(.+): textured tail ([\d.]+) above ([\d.]+) times the 1 px tail ([\d.]+)", line)
        if match:
            row.update(actual=float(match[2]), bound=float(match[3])*float(match[4]))
        match = re.match(r"(.+): reference changed from ([\d.]+) to ([\d.]+)", line)
        if match:
            row.update(actual=abs(float(match[3])-float(match[2])), bound=0.000001,
                       classification="expected bilinear reference movement")
        if "values differ" in line:
            match = re.search(r"largest by ([\d.]+)", line)
            if match:
                row.update(actual=float(match[1]), bound=0.0)
        rows.append(row)
    match = re.search(r"tolerance ([\d.]+)\nExpected: ([\d.]+)\nActual:\s+([\d.]+)", message)
    if match:
        rows.append({"message": "absolute seeded-lock error", "actual": abs(float(match[3])-float(match[2])),
                     "bound": float(match[1]), "classification": reason})
    return rows


for artifact in sorted(source.glob("filtered-render-measurements-*")):
    backend = artifact.name.removeprefix("filtered-render-measurements-")
    files = list(artifact.rglob("*.log")) + list(artifact.rglob("*.txt"))
    assert len(files) == 1, files
    text = sanitize(files[0].read_text())
    lines = text.splitlines()
    failures = []
    for i, line in enumerate(lines):
        match = re.match(r"^  Failed (.*) \[[^\n]+\]$", line)
        if not match:
            continue
        message, collecting = [], False
        for following in lines[i+1:]:
            if following.strip() == "Error Message:":
                collecting = True
                continue
            if following.strip() == "Stack Trace:" or re.match(r"^  (Passed|Failed|Skipped) ", following):
                break
            if collecting:
                message.append(following.strip())
        message = "\n".join(message).strip()
        reason = "acceptance failure"
        if "shader anchor" in message:
            reason = "CRLF splice bug, no visibility measurement"
        elif "E_OUTOFMEMORY" in message:
            reason = "resource exhaustion after constructor failures, measurement blocked"
        elif "no-GC region ended" in message:
            reason = "invalid allocation measurement, infrastructure or runtime"
        elif "FXC rejected" in message:
            reason = "D3D11 shader validation blocks this fact, baseline status unmeasured"
        elif "EntryIdentityGpuTests" in match[1]:
            reason = "entry identity, original noop control fails this class locally"
        elif "TemporalStoredLockGpuTests" in match[1] and commit.startswith("e62236e56"):
            reason = "seeded fully lit history initialization bug, fixed in dab3a8981"
        failures.append({"test": match[1], "reason": reason, "message": message,
                         "measurements": measurement_rows(message, reason)})
    counts = {}
    for key in ("Total tests", "Passed", "Failed", "Skipped"):
        matches = re.findall(r"^\s*"+key+r":\s*([^\n]+)", text, re.M)
        value = matches[-1].strip() if matches else None
        counts[key] = int(value) if value and value.isdigit() else value
    stamp = re.findall(r"Visibility archive token[^\n]+", text)
    report["backends"][backend] = {"counts": counts, "aborted": "Test Run Aborted." in text,
                                    "token_stamp": stamp, "failures": failures}
    (output/(backend+".txt")).write_text(text)
    print(run, backend, counts, "failure records", len(failures), "token", stamp)
(output/"results.json").write_text(json.dumps(report, indent=2)+"\n")

lines = ["# Hosted visibility failures", "", f"Run {run}, source `{commit}`.", "",
         "Bounds use the precision printed by each run. Assertions compare unrounded measurements.",
         "Bilinear pinned-reference movement is separated from temporal regressions.", ""]
for backend, data in report["backends"].items():
    lines += ["## "+backend, "", str(data["counts"]), "", *data["token_stamp"], ""]
    if len(data["failures"]) > 50:
        lines += [f'{len(data["failures"])} failing test records from the aborted leg. Every name, exception and message is in [results.json](results.json). These constructor failures have no numeric visibility bound.', ""]
        continue
    for failure in data["failures"]:
        lines += ["### "+failure["test"], "", failure["reason"], "",
                  "| Metric or exception | Value | Bound | Classification |", "| --- | ---: | ---: | --- |"]
        for row in failure["measurements"]:
            actual = f'{row["actual"]:.9f}' if "actual" in row else "see message"
            bound = f'{row["bound"]:.9f}' if "bound" in row else "not a numeric measurement"
            metric = row["message"].replace("|", "\\|")
            lines += [f'| {metric} | {actual} | {bound} | {row["classification"]} |']
        lines.append("")
(output/"failures.md").write_text("\n".join(lines).rstrip()+"\n")
