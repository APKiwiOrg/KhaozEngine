#!/usr/bin/env python3
"""Recapture the visibility DXBC fixtures from a cross-platform-gpu dump run.

Only Windows FXC produces these listings, so they come from CI. Dispatch the workflow with the dump armed:

    gh workflow run cross-platform-gpu.yml --ref <branch> -f legs=direct3d11-native -f dumpDxbc=true

The run fails on purpose once D3D11FxcValidationTests.DumpVisibilityDisassembly has printed, so the runner shows
its output. Then:

    python3 scripts/recapture-taa-dxbc.py <run-id>           # dry run, lists the fixtures that would change
    python3 scripts/recapture-taa-dxbc.py <run-id> --write   # rewrites them

The script reads every direct3d11-native job log of the run with `gh run view <run-id> --log --job <id>`, collects
each `DXBC|program|D or S|n|text` block and the `DXBCSOURCE|program|D or S|sha256` line printed beside it, and
removes carriage returns. A fixture is the block's lines in order, each followed by LF. Fixtures are compared as
decompressed text, never as gzip bytes, because a gzip header can carry a timestamp. A write gzips at level 9
with mtime 0 and no stored file name, which is how the committed fixtures were made, and stores the hash line's
SHA-256 in the `.glsl.sha256` beside it. The guards that hold the result are
TemporalVisibilityDxbcRealProgramTests and TemporalLockReleaseDxbcTests.

`--log FILE` reads a saved job log instead of downloading, for checking the parser offline.
"""

import argparse
import gzip
import json
import re
import subprocess
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
FIXTURES = ROOT / "KhaozEngine.Render.Tests" / "Fixtures" / "taa-visibility-dxbc"
SIDES = {"D": "d", "S": "shipped"}
LEG = "direct3d11-native"

LISTING = re.compile(r"(?:^|\s)DXBC\|(\w+)\|([DS])\|(\d+)\|(.*)$")
SOURCE = re.compile(r"(?:^|\s)DXBCSOURCE\|(\w+)\|([DS])\|([0-9a-f]{64})\s*$")


def gh(*args):
    result = subprocess.run(["gh", *args], cwd=ROOT, capture_output=True, text=True, encoding="utf-8")
    if result.returncode != 0:
        sys.exit(f"gh {' '.join(args)} failed with exit {result.returncode}:\n{result.stderr}")
    return result.stdout


def download(run_id):
    jobs = json.loads(gh("run", "view", run_id, "--json", "jobs"))["jobs"]
    legs = [j for j in jobs if LEG in j["name"] and j.get("conclusion") not in ("skipped", None)]
    if not legs:
        sys.exit(f"run {run_id} has no finished {LEG} job")
    return [gh("run", "view", run_id, "--log", "--job", str(j["databaseId"])) for j in legs]


def parse(logs):
    """Returns {(program, side): (text, hash or None)} from the logs, failing on a gap or a conflicting repeat."""
    lines, hashes = {}, {}
    for log in logs:
        for raw in log.split("\n"):
            raw = raw.replace("\r", "")
            if m := SOURCE.search(raw):
                key = (m[1], m[2])
                if hashes.setdefault(key, m[3]) != m[3]:
                    sys.exit(f"{key[0]} {key[1]}: two different source hashes in the log")
            elif m := LISTING.search(raw):
                key, n = (m[1], m[2]), int(m[3])
                block = lines.setdefault(key, {})
                if block.setdefault(n, m[4]) != m[4]:
                    sys.exit(f"{key[0]} {key[1]}: line {n} printed twice with different text")
    blocks = {}
    for key, block in lines.items():
        if sorted(block) != list(range(len(block))):
            missing = sorted(set(range(max(block) + 1)) - set(block))
            sys.exit(f"{key[0]} {key[1]}: listing has gaps, missing line numbers {missing[:10]}")
        blocks[key] = ("".join(block[n] + "\n" for n in range(len(block))), hashes.get(key))
    for key in hashes.keys() - lines.keys():
        sys.exit(f"{key[0]} {key[1]}: a source hash with no listing")
    return blocks


def main():
    parser = argparse.ArgumentParser(description=__doc__.split("\n")[0])
    parser.add_argument("run_id", help="the cross-platform-gpu run that dumped the listings")
    parser.add_argument("--write", action="store_true", help="rewrite the fixtures that differ")
    parser.add_argument("--log", type=Path, help="read this saved job log instead of downloading")
    args = parser.parse_args()

    logs = [args.log.read_text(encoding="utf-8")] if args.log else download(args.run_id)
    blocks = parse(logs)
    if not blocks:
        sys.exit("no DXBC| listings in the log. Was the run dispatched with dumpDxbc=true?")

    changed, unchanged = [], 0
    for (program, side), (text, digest) in sorted(blocks.items()):
        stem = f"{program}.{SIDES[side]}"
        listing, pin = FIXTURES / (stem + ".dxbc.gz"), FIXTURES / (stem + ".glsl.sha256")
        reasons = []
        if not listing.exists():
            reasons.append("new fixture")
        elif gzip.decompress(listing.read_bytes()).decode("utf-8") != text:
            reasons.append("listing differs")
        stored = pin.read_text(encoding="utf-8").strip() if pin.exists() else None
        if digest is None:
            if reasons:
                reasons.append("no source hash in the log")
        elif digest != stored:
            reasons.append("source hash differs")
        if reasons:
            changed.append((listing, pin, text, digest, reasons))
        else:
            unchanged += 1

    dumped = {f"{p}.{SIDES[s]}.dxbc.gz" for p, s in blocks}
    absent = sorted(f.name for f in FIXTURES.glob("*.dxbc.gz") if f.name not in dumped)

    for listing, _, _, _, reasons in changed:
        print(f"would change {listing.name}: {', '.join(reasons)}" if not args.write
              else f"writing {listing.name}: {', '.join(reasons)}")
    for name in absent:
        print(f"not in the dump: {name}")
    print(f"{len(blocks)} listings in the log, {unchanged} unchanged, {len(changed)} "
          f"{'written' if args.write else 'would change'}, {len(absent)} fixtures not in the dump")

    if args.write:
        if missing := [l.name for l, _, _, d, _ in changed if d is None]:
            sys.exit(f"refusing to write without a source hash for {', '.join(missing)}")
        for listing, pin, text, digest, _ in changed:
            listing.write_bytes(gzip.compress(text.encode("utf-8"), compresslevel=9, mtime=0))
            pin.write_text(digest + "\n", encoding="utf-8", newline="\n")


if __name__ == "__main__":
    main()
