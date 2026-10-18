"""Representative guard values from detailed dotnet test logs, one column per log.

Usage: guard_values.py <log>... Bounds are the guards' own, as their failure messages state them.
"""
import re
import sys
from pathlib import Path

LOGS = [Path(p) for p in sys.argv[1:]]
SUMMARY = re.compile(r"^\S*\s*(?:\[xUnit[^\]]*\]\s*)?((?:Native|Quality) (?:S|C pan|M object) [\d.]+ px(?: advancing| retreating)?)"
                     r"\s+([\d.]+) ([\d.]+) ([\d.]+)")


def values(text):
    v = {}
    for line in text.splitlines():
        line = line.strip()
        m = re.search(r"hardware Quality flat: .*change ([\d.]+) .*error ([\d.]+)", line)
        if m:
            v["peak change"], v["peak error"] = map(float, m.groups())
        m = re.search(r"hardware Quality still, 1 px texels: error ([\d.]+), temporal error ([\d.]+)", line)
        if m:
            v["1px error"], v["1px temporal"] = map(float, m.groups())
        m = re.search(r"hardware Quality still, 0.5 px texels: error ([\d.]+)", line)
        if m:
            v["0.5px error"] = float(m.group(1))
        m = re.search(r"((?:Native|Quality) (?:S|C pan) [\d.]+ px(?: advancing| retreating)?)\s+([\d.]+) ([\d.]+) ([\d.]+)\s", line)
        if m:
            v[m.group(1) + " bandT"] = float(m.group(2))
            v[m.group(1) + " tailT"] = float(m.group(3))
        m = re.search(r"Textured grain (Native|Quality): .*requested mask \d+ ([\d.]+)", line)
        if m:
            v[f"synthetic {m.group(1)}"] = float(m.group(2))
        m = re.search(r"Passed:\s+(\d+)", line)
        if m:
            v["passed"] = int(m.group(1))
        m = re.search(r"Failed:\s+(\d+)", line)
        if m:
            v["failed"] = int(m.group(1))
    return v


ROWS = [
    ("Facts passed of 8", "passed", None),
    ("Quality still 1 px texture error", "1px error", "<= 0.005885"),
    ("Quality still 1 px texture temporal error", "1px temporal", "<= 0.001155"),
    ("Quality still 0.5 px texture error", "0.5px error", "<= 0.007931"),
    ("Quality camera pan 2 px textured band error", "Quality C pan 2.0 px bandT", "<= 0.00605"),
    ("Quality camera pan 2 px textured tail error", "Quality C pan 2.0 px tailT", "<= 0.00429"),
    ("Quality flat stable-peak error", "peak error", "<= 0.016544"),
    ("Quality flat stable-peak change", "peak change", "<= 0.002233"),
    ("Native S 1 px advancing textured band error", "Native S 1.0 px advancing bandT", "lag, lower is better"),
    ("Quality S 1 px advancing textured band error", "Quality S 1.0 px advancing bandT", "lag, lower is better"),
    ("Native S 1 px advancing textured tail", "Native S 1.0 px advancing tailT", "reference for the 8 px ratio"),
    ("Native S 8 px advancing textured tail", "Native S 8.0 px advancing tailT", "<= 1.5 x the 1 px tail"),
    ("Native S 1 px retreating textured tail", "Native S 1.0 px retreating tailT", "reference for the 8 px ratio"),
    ("Native S 8 px retreating textured tail", "Native S 8.0 px retreating tailT", "<= 1.5 x the 1 px tail"),
    ("Quality S 1 px advancing textured tail", "Quality S 1.0 px advancing tailT", "reference for the 8 px ratio"),
    ("Quality S 8 px advancing textured tail", "Quality S 8.0 px advancing tailT", "<= 1.5 x the 1 px tail"),
    ("Synthetic textured grain, Native", "synthetic Native", "diagnostic"),
    ("Synthetic textured grain, Quality", "synthetic Quality", "baseline 0.010852, locks-off 0.006727"),
]
data = [values(p.read_text(errors="replace")) for p in LOGS]
print("| Guard metric | Bound | " + " | ".join(p.stem for p in LOGS) + " |")
print("| --- | --- | " + " | ".join("---:" for _ in LOGS) + " |")
for label, key, bound in ROWS:
    cells = []
    for d in data:
        x = d.get(key)
        cells.append("" if x is None else (str(x) if isinstance(x, int) else f"{x:g}"))
    print(f"| {label} | {bound or ''} | " + " | ".join(cells) + " |")
