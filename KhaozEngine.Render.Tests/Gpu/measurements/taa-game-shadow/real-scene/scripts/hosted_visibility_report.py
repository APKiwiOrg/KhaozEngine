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
    text = re.sub(r"/Users/[^\s:]+", "runtime-path", text)
    return "\n".join(line.rstrip() for line in text.splitlines()).rstrip()+"\n"


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
        elif "EntryIdentityGpuTests" in match[1]:
            reason = "entry identity, original noop control fails this class locally"
        elif "TemporalStoredLockGpuTests" in match[1] and commit.startswith("e62236e56"):
            reason = "seeded fully lit history initialization bug, fixed in dab3a8981"
        failures.append({"test": match[1], "reason": reason, "message": message})
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
