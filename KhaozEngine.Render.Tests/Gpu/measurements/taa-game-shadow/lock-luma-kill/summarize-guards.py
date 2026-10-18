"""Export printed quality guard values against the unchanged bounds at code commit 507f0d135.

Usage from the repository root: summarize-guards.py <backend> <suite-log> <relative-output-directory>
Values are printed measurements. Borderline comparisons do not supersede the test's unrounded assertions.
"""
import csv
import io
import json
import re
import subprocess
import sys
from pathlib import Path

CODE = "507f0d1357c10a8712aef46ce596547dbebac79d"
backend, logfile, output = sys.argv[1:4]
assert re.fullmatch(r"[a-z0-9-]+", backend)
out = Path(output)
assert not out.is_absolute(), "Keep report paths repo-relative."
log = Path(logfile).read_text(encoding="utf-8-sig")
log = re.sub(r"\x1b\[[0-?]*[ -/]*[@-~]", "", log)
num = r"[-+]?\d+\.\d+"
rows = []


def source(filename):
    return subprocess.check_output(["git", "show", f"{CODE}:KhaozEngine.Render.Tests/Gpu/{filename}"], text=True)


def dictionary(text, name):
    return re.search(r"\b" + name + r"\s*=\s*new\(\)\s*\{(.*?)\n\s*\};", text, re.S).group(1)


def add(case, metric, actual, bound, precision=6, baseline=None, comparison="<="):
    # Both the actual and a same-run denominator may have been rounded before they reach this report.
    epsilon = 2 * 10 ** (-precision)
    difference = actual - bound if comparison == "<=" else bound - actual
    status = "borderline" if abs(difference) <= epsilon else "pass" if difference < 0 else "fail"
    rows.append({"case": case, "metric": metric, "comparison": comparison, "actual": actual, "bound": bound,
                 "baseline": baseline, "printedComparison": status})


synthetic = re.findall(r"^\s*Textured grain (\w+), ([^:]+): (" + num + r"), error grain (" + num + r"), reference grain (" + num + r"), change (" + num + r")", log, re.M)
assert synthetic, "No textured-receiver measurement was emitted."
device = synthetic[0][1]
family = {"hardware": "Hardware", "WARP": "Warp", "lavapipe": "Lavapipe"}[device]
for preset, _, grain, error_grain, reference_grain, change in synthetic:
    if preset != "Balanced":
        add("synthetic " + preset, "output grain", float(grain), 0.008 if preset == "Quality" else 0.0113)

light_before = {
    ("dark", "Native"): (0.000360, 0.000356, 0.000045),
    ("dark", "Quality"): (0.001003, 0.000877, 0.000085),
    ("flicker", "Native"): (0.014685, 0.005503, 0.010689),
    ("flicker", "Quality"): (0.016049, 0.005340, 0.011537),
    ("moving", "Native"): (0.010694, 0.003822, 0.003290),
    ("moving", "Quality"): (0.012462, 0.004421, 0.003293),
}
lights = re.findall(r"^\s*Texture light (\w+) (\w+) \w+: error (" + num + r"), error grain (" + num + r"), change error (" + num + r"), mean luma (" + num + r")", log, re.M)
for kind, preset, error, grain, change, luma in lights:
    for metric, actual, before in zip(("error", "error grain", "temporal error"), map(float, (error, grain, change)), light_before[(kind, preset)]):
        add(f"light {kind} {preset}", metric, actual, 1.1 * before, baseline=before)
    if kind == "dark":
        add(f"light {kind} {preset}", "reference mean luma upper", float(luma), 0.08)
        add(f"light {kind} {preset}", "reference mean luma lower", float(luma), 0.001, comparison=">=")

moving = {}
pattern = r"^\s*((?:Native|Quality) (?:S |C pan |M object |MS object ).*?)\s+((?:" + num + r"[ \t]+){15}" + num + r")[ \t]*$"
for name, values in re.findall(pattern, log, re.M):
    moving[name] = list(map(float, values.split()))
assert len(moving) == 38, f"Expected 38 moving-shading cases, got {len(moving)}."
control_text = dictionary(source("TemporalMovingShadeLagGpuTests.cs"), family + "Control")
control = {k: (float(a), float(b)) for k, a, b in re.findall(r'\["([^"]+)"\]\s*=\s*\((' + num + r"), (" + num + r")\)", control_text)}
for name, values in moving.items():
    preset = name.split()[0]
    band, tail, far = values[:3]
    is_shadow = " S " in name
    speed = float(re.search(r" (\d+\.\d+) px", name).group(1))
    if (is_shadow and speed > 1) or (not is_shadow and speed >= 1):
        add(name, "band grain", values[6], 1.5 * values[7])
    if is_shadow and speed > 1:
        slow = re.sub(r" S \d+\.\d+ px", " S 1.0 px", name)
        add(name, "tail error", tail, 1.5 * moving[slow][1])
    elif " C pan " in name:
        before_band, before_tail = control[name]
        add(name, "band error", band, 1.1 * before_band, baseline=before_band)
        add(name, "tail error", tail, 1.1 * before_tail, baseline=before_tail)
    elif " object " in name and speed >= 1:
        add(name, "tail error", tail, far + 0.0055)

# Amendment 33's original shadow-edge test prints one region block per case rather than a summary row.
shadow = {}
active = None
for line in log.splitlines():
    heading = re.match(r"^\s*((?:Quality|Native) (?:still|\d+\.\d+ px (?:advancing|retreating)).*?): shadow re-records", line)
    if heading:
        active = heading.group(1)
        shadow[active] = {}
    elif active:
        region = re.match(r"^\s*(band textured|tail textured|far textured)\s+\d+\s+((?:" + num + r"[ \t]+){5}" + num + r")[ \t]*$", line)
        if region:
            shadow[active][region.group(1)] = list(map(float, region.group(2).split()))
shadow_still_text = dictionary(source("TemporalShadowEdgeLagGpuTests.cs"), family + "Still")
shadow_still = {k: (float(a), float(b)) for k, a, b in re.findall(r"\[TemporalUpscale\.(\w+)\]\s*=\s*\((" + num + r"), (" + num + r")\)", shadow_still_text)}
for name, regions in shadow.items():
    if "hold 0" in name:
        continue
    preset = name.split()[0]
    if name.endswith("still"):
        band, far = shadow_still[preset]
        add(name, "shadow band error", regions["band textured"][0], 1.1 * band, 4, band)
        add(name, "shadow control error", regions["far textured"][0], 1.1 * far, 4, far)
        for region in ("band textured", "far textured"):
            add(name, region + " grain lower", regions[region][3], 0.9 * regions[region][4], 4, comparison=">=")
    elif "1.00 px" in name:
        slow = name.replace("1.00 px", "0.25 px")
        add(name, "shadow tail error", regions["tail textured"][0], 1.75 * shadow[slow]["tail textured"][0], 4)
        if preset == "Quality":
            add(name, "shadow band error", regions["band textured"][0], 0.038, 4)

detail_text = dictionary(source("TemporalStableDetailGpuTests.cs"), family)
detail_before = {k: (float(a), float(b)) for k, a, b in re.findall(r'\["([^"]+)"\]\s*=\s*new\((' + num + r"), (" + num + r")\)", detail_text)}
details = re.findall(r"^\s*" + re.escape(device) + r" (.*?): error (" + num + r"), temporal error (" + num + r")", log, re.M)
for name, error, temporal in details:
    if name in detail_before:
        before_error, before_temporal = detail_before[name]
        add("stable detail " + name, "error", float(error), 1.1 * before_error, 5, before_error)
        add("stable detail " + name, "temporal error", float(temporal), 1.1 * before_temporal, 5, before_temporal)

peak_text = dictionary(source("TemporalStablePeakGpuTests.cs"), family)
peak_before = {k: (float(a), float(b)) for k, a, b in re.findall(r'\[new\("([^"]+)",[^\n]+?\)\]\s*=\s*\((' + num + r"), (" + num + r")\)", peak_text)}
peaks = re.findall(r"^\s*" + re.escape(device) + r" (.*?): \d+ peaks, frame-to-frame change (" + num + r").*?, error (" + num + r")", log, re.M)
for name, change, error in peaks:
    before_change, before_error = peak_before[name]
    add("stable peak " + name, "change", float(change), 1.1 * before_change, 5, before_change)
    add("stable peak " + name, "error", float(error), 1.1 * before_error, 5, before_error)

review_before = {}
for dev, kind, name, values in re.findall(r'\[\("([^"]+)", "([^"]+)", "([^"]+)"\)\]\s*=\s*new\(([^)]+)\)', source("TemporalReviewBaseline.cs")):
    review_before[(dev, kind, name)] = list(map(float, values.split(",")))
reviews = re.findall(r"^\s*(RIM|DIAG(?:ONAL)?) ([^|]+)\|([^|]+)\|(" + num + r")\|(" + num + r")\|(" + num + r")\|(" + num + r")", log, re.M)
for prefix, dev, name, error, change, temporal, reference in reviews:
    kind = "rim" if prefix == "RIM" else "diagonal"
    before_error, before_change, before_temporal, before_reference = review_before[(dev, kind, name)]
    add(kind + " " + name, "error", float(error), (before_error + 0.0000005) * 1.1, baseline=before_error)
    add(kind + " " + name, "temporal error", float(temporal), (before_temporal + 0.0000005) * 1.1, baseline=before_temporal)
    if kind == "diagonal":
        add(kind + " " + name, "change", float(change), (before_change + 0.0000005) * 1.1, baseline=before_change)
    add(kind + " " + name, "reference change", abs(float(reference) - before_reference), 0.000001, baseline=before_reference)

# Only the unprefixed console logger's final test summary and failed-case lines are counted.
failed = re.findall(r"^\s*Failed (KhaozEngine\.[^\n]+?)(?: \[\d[^\n]*\])?\s*$", log, re.M)
counts = {}
for key in ("Passed", "Failed", "Skipped", "Total tests"):
    found = re.findall(r"^[ \t]*" + key + r":\s*(\d+)[ \t]*$", log, re.M)
    counts[key] = int(found[-1]) if found else None
if counts["Skipped"] is None and all(counts[k] is not None for k in ("Total tests", "Passed", "Failed")):
    counts["Skipped"] = counts["Total tests"] - counts["Passed"] - counts["Failed"]
metadata = {
    "backend": backend, "deviceClass": device, "codeCommit": CODE, "counts": counts,
    "synthetic": [{"preset": p, "grain": float(g), "errorGrain": float(e), "referenceGrain": float(r), "change": float(c)} for p, _, g, e, r, c in synthetic],
    "lightCases": len(lights), "movingCases": len(moving), "stableDetailCases": len(details),
    "stablePeakCases": len(peaks), "shadowCases": len(shadow), "reviewCases": len(reviews), "guards": len(rows), "failedCases": failed,
    "note": "Comparisons use printed values. Borderline rows defer to the test's unrounded assertion.",
}


def guard():
    assert Path.cwd() == Path(subprocess.check_output(["git", "rev-parse", "--show-toplevel"], text=True).strip())
    assert subprocess.check_output(["git", "branch", "--show-current"], text=True).strip() == "fix/taa-game-shadow"


guard()
out.mkdir(parents=True, exist_ok=True)
for suffix, content in (("json", json.dumps({"metadata": metadata, "rows": rows}, indent=2) + "\n"),):
    guard()
    (out / f"{backend}.{suffix}").write_text(content)
buffer = io.StringIO()
writer = csv.DictWriter(buffer, fieldnames=["case", "metric", "comparison", "actual", "bound", "baseline", "printedComparison"], lineterminator="\n")
writer.writeheader()
writer.writerows(rows)
guard()
(out / f"{backend}.csv").write_text(buffer.getvalue())
print(json.dumps(metadata))
