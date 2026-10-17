#!/usr/bin/env python3
"""Shard plan and coverage guard for the GPU matrix's full suites (#1438).

The direct3d11-native and vulkan-native legs run their full suite across several hosted jobs on the
schedule and on a non-bake, unfiltered workflow_dispatch. This file is the single source of the split,
and .github/workflows/cross-platform-gpu.yml reads it through the subcommands below.

THE SPLIT. Shard `rest` runs every test project in KhaozEngine.slnx except KhaozEngine.Render.Tests,
through a solution derived from the current one at run time, so a new test project lands in it with no
edit. Every other shard runs one slice of KhaozEngine.Render.Tests. The slices are an ORDERED FIRST-MATCH
partition: a test belongs to the first slice whose rules match it, and the last slice has no rules and
takes everything the earlier ones did not. Each slice's vstest filter is its own rules ANDed with the
negation of every earlier slice's rules, so no test can match two slices and no test can match none. A
new Render.Tests class or method falls into the first slice that names it, or into the last.

THE GUARD. `check-render` reads every Render.Tests method listed on the shard's own build output,
parses the filter text each slice hands to vstest, and evaluates it with the operators vstest applies
(case-insensitive equality and containment on FullyQualifiedName). It fails when any method matches no
slice or more than one. `summarize` then reads one report per shard and fails the backend when a shard
is missing, a shard's suite did not succeed, a test assembly ran on two shards, Render.Tests ran outside
its slices, or a test project in the solution ran nowhere.

THE BALANCE comes from measured per-test times: lavapipe under strict validation, serialised, in run
38991082421 (Render.Tests 2h15m of a 2h40m job), and WARP in run 39010325116. One method,
TemporalFollowPhaseGpuTests.The_orthographic_follow_holds_its_bounds_from_every_start_phase, took 22
minutes alone on lavapipe, so it has a slice to itself and sets the floor for the slowest shard.
Re-balance by moving rules between slices. Coverage cannot drift whatever the rules say.
"""
import json
import os
import re
import sys
import xml.etree.ElementTree as ET
from pathlib import Path

RENDER_ASSEMBLY = "KhaozEngine.Render.Tests"
GPU = "KhaozEngine.Tests.Gpu."
PHASE = GPU + "TemporalFollowPhaseGpuTests."

# (operator, value). "=" is vstest's FullyQualifiedName equality and pins one method, theory rows
# included. "~" is containment, and a class rule ends in "." so it cannot reach a longer class name.
RENDER_SLICES = [
    ("render-1", [
        ("=", PHASE + "The_orthographic_follow_holds_its_bounds_from_every_start_phase"),
    ]),
    ("render-2", [
        ("=", PHASE + "The_perspective_follow_holds_its_bounds_from_every_start_phase_at_Performance_and_Ultra"),
        ("~", GPU + "TemporalLongHoldFollowGpuTests."),
    ]),
    ("render-3", [
        ("~", GPU + "ShadowCascadeCullPerfGpuTests."),
        ("=", PHASE + "The_perspective_follow_holds_its_bounds_from_every_start_phase_at_Native_and_Quality"),
        ("=", PHASE + "The_follow_stop_holds_its_bounds_from_every_start_phase"),
    ]),
    ("render-4", [
        ("=", PHASE + "The_follow_lines_hold_their_share_from_every_start_phase"),
        ("~", GPU + "TemporalFollowCameraGpuTests."),
        ("~", GPU + "TemporalFollowStopGpuTests."),
        ("~", GPU + "TemporalFollowLinesGpuTests."),
    ]),
    ("render-5", [
        ("~", GPU + "Temporal"),
    ]),
    ("render-6", []),
]

REST = "rest"
SHARDS = [REST] + [name for name, _ in RENDER_SLICES]
NEGATE = {"=": "!=", "~": "!~"}


def fail(message):
    print(f"::error title=GPU full-suite shards::{message}")
    sys.exit(1)


def slice_index(name):
    for index, (slice_name, _) in enumerate(RENDER_SLICES):
        if slice_name == name:
            return index
    fail(f"'{name}' is not a Render.Tests slice. Known slices: {', '.join(n for n, _ in RENDER_SLICES)}")


def rule_text(op, value):
    return f"FullyQualifiedName{op}{value}"


def slice_filter(name):
    index = slice_index(name)
    rules = RENDER_SLICES[index][1]
    earlier = [rule for _, slice_rules in RENDER_SLICES[:index] for rule in slice_rules]
    if index < len(RENDER_SLICES) - 1 and not rules:
        fail(f"slice {name} has no rules and is not the last slice, so it would match nothing")
    parts = []
    if rules:
        parts.append("(" + "|".join(rule_text(op, value) for op, value in rules) + ")")
    parts.extend(rule_text(NEGATE[op], value) for op, value in earlier)
    return "&".join(parts)


def rule_matches(op, value, name):
    # vstest compares FullyQualifiedName with OrdinalIgnoreCase for both operators.
    if op == "=":
        return name.casefold() == value.casefold()
    return value.casefold() in name.casefold()


CONDITION = re.compile(r"FullyQualifiedName(!=|!~|=|~)([^&|()]+)")


def parse_filter(text):
    """Parse the filter subset slice_filter emits into a predicate over a FullyQualifiedName.

    The guard evaluates the TEXT handed to vstest rather than the rule table, so a generator bug that
    wrote a wrong filter fails the guard instead of passing it. vstest binds & tighter than |.
    """
    tokens = re.findall(r"\(|\)|&|\||FullyQualifiedName(?:!=|!~|=|~)[^&|()]+", text)
    if "".join(tokens) != text:
        fail(f"filter has text outside the supported subset: {text}")
    position = 0

    def peek():
        return tokens[position] if position < len(tokens) else None

    def take():
        nonlocal position
        position += 1
        return tokens[position - 1]

    def term():
        token = take()
        if token == "(":
            inner = either()
            if take() != ")":
                fail(f"unbalanced parentheses in {text}")
            return inner
        op, value = CONDITION.fullmatch(token).groups()
        positive = op.lstrip("!")
        if op.startswith("!"):
            return lambda name: not rule_matches(positive, value, name)
        return lambda name: rule_matches(positive, value, name)

    def both():
        parts = [term()]
        while peek() == "&":
            take()
            parts.append(term())
        return lambda name: all(part(name) for part in parts)

    def either():
        parts = [both()]
        while peek() == "|":
            take()
            parts.append(both())
        return lambda name: any(part(name) for part in parts)

    predicate = either()
    if position != len(tokens):
        fail(f"trailing tokens in {text}")
    return predicate


def cmd_expand(_args):
    """Turn the selected legs (JSON array on stdin) into matrix entries, one per shard where sharded."""
    legs = json.load(sys.stdin)
    event = os.environ.get("EVENT", "")
    bake = os.environ.get("BAKE", "") == "true"
    render_filter = os.environ.get("RENDER_FILTER", "")
    full_trigger = event in ("schedule", "workflow_dispatch")
    entries, summaries = [], []
    for leg in legs:
        sharded = leg["fullSuite"] == "scheduled" and full_trigger and not bake and not render_filter
        if not sharded:
            entries.append(dict(leg, shard="", primary="1"))
            continue
        summaries.append({"os": leg["os"], "backend": leg["backend"]})
        for shard in SHARDS:
            entries.append(dict(leg, shard=shard, primary="1" if shard == REST else ""))
    print(f"matrix={json.dumps({'include': entries}, separators=(',', ':'))}")
    print(f"sharded={json.dumps(summaries, separators=(',', ':'))}")
    for entry in entries:
        label = entry["backend"] + (f" / {entry['shard']}" if entry["shard"] else "")
        print(f"  job: {entry['os']} / {label}", file=sys.stderr)


def cmd_filter(args):
    print(slice_filter(args[0]))


def listed_tests(path):
    """FullyQualifiedNames from `dotnet test --list-tests` output. Theory rows carry their arguments."""
    names, listing = set(), False
    for line in Path(path).read_text(encoding="utf-8", errors="replace").splitlines():
        if line.strip() == "The following Tests are available:":
            listing = True
            continue
        if listing and line.startswith("    ") and line.strip():
            names.add(line.strip().split("(", 1)[0])
    return sorted(names)


def cmd_check_render(args):
    names = listed_tests(args[0])
    if not names:
        fail(f"no Render.Tests test was listed in {args[0]}, so the slice coverage cannot be checked")
    counts = [0] * len(RENDER_SLICES)
    unassigned, doubled = [], []
    predicates = [parse_filter(slice_filter(slice_name)) for slice_name, _ in RENDER_SLICES]
    for name in names:
        hits = [index for index, predicate in enumerate(predicates) if predicate(name)]
        if not hits:
            unassigned.append(name)
        elif len(hits) > 1:
            doubled.append((name, [RENDER_SLICES[i][0] for i in hits]))
        for index in hits:
            counts[index] += 1
    for (name, rules), count in zip(RENDER_SLICES, counts):
        print(f"{name}: {count} listed test methods")
        for op, value in rules:
            if not any(rule_matches(op, value, test) for test in names):
                print(f"::warning title=GPU full-suite shards::rule {rule_text(op, value)} on {name} matches "
                      "no Render.Tests method. Coverage is unaffected, the tests it named now run in a later "
                      "slice. Update scripts/gpu-test-shards.py to restore the balance.")
    print(f"{len(names)} Render.Tests methods listed, {len(unassigned)} unassigned, {len(doubled)} assigned twice")
    for name in unassigned[:20]:
        print(f"  unassigned: {name}")
    for name, slices in doubled[:20]:
        print(f"  assigned to {', '.join(slices)}: {name}")
    if unassigned or doubled:
        fail("the Render.Tests slices do not partition the listed tests exactly once")


MINIMAL = re.compile(r"(?:Passed|Failed)!\s+-\s+Failed:\s+(\d+),\s+Passed:\s+(\d+),\s+Skipped:\s+(\d+),"
                     r"\s+Total:\s+(\d+),.*?-\s+(\S+?)\.dll")
RUN_FOR = re.compile(r"Test run for .*?[\\/]([^\\/]+?)\.dll")
DETAIL = re.compile(r"^\s*(Total tests|Passed|Failed|Skipped):\s+(\d+)\s*$")


def assembly_totals(log_text):
    """Per-assembly totals from a dotnet test console log at minimal or detailed verbosity."""
    totals = {}

    def add(name, failed, passed, skipped, total):
        entry = totals.setdefault(name, {"total": 0, "passed": 0, "failed": 0, "skipped": 0, "runs": 0})
        entry["total"] += total
        entry["passed"] += passed
        entry["failed"] += failed
        entry["skipped"] += skipped
        entry["runs"] += 1

    lines = [re.sub(r"^﻿?\d{4}-\d\d-\d\dT[\d:.]+Z ", "", line) for line in log_text.splitlines()]
    minimal = [MINIMAL.search(line) for line in lines]
    if any(minimal):
        for match in filter(None, minimal):
            failed, passed, skipped, total = (int(match.group(i)) for i in range(1, 5))
            add(match.group(5), failed, passed, skipped, total)
        return totals
    # Detailed output names the assembly only on its "Test run for" line, so totals can be attributed only
    # when each assembly's block closes before the next one opens. That holds on the serialised Vulkan
    # shards. Interleaved output (test hosts in parallel) is refused rather than misattributed.
    current, block = None, None
    for line in lines:
        run = RUN_FOR.search(line)
        if run:
            if current is not None:
                raise ValueError(f"detailed output interleaves {current} and {run.group(1)}, so per-assembly "
                                 "totals cannot be attributed. Run the shard serialised or at minimal verbosity.")
            current, block = run.group(1), None
            continue
        if "No test matches the given testcase filter" in line:
            current, block = None, None
            continue
        detail = DETAIL.match(line)
        if detail and current:
            key, value = detail.group(1), int(detail.group(2))
            if key == "Total tests":
                block = {"Passed": 0, "Failed": 0, "Skipped": 0, "Total tests": value}
            elif block is not None:
                block[key] = value
            continue
        if block is not None and "Total time:" in line:
            add(current, block["Failed"], block["Passed"], block["Skipped"], block["Total tests"])
            current, block = None, None
    return totals


def cmd_report(args):
    backend, shard, outcome, log_path, out_path = args
    log = Path(log_path)
    try:
        totals = assembly_totals(log.read_text(encoding="utf-8", errors="replace")) if log.exists() else {}
    except ValueError as error:
        # Recorded as the outcome, so the summary job fails the backend instead of trusting the totals.
        print(f"::error title=GPU full-suite shards::{error}")
        totals, outcome = {}, f"unattributable totals ({outcome})"
    report = {"backend": backend, "shard": shard, "outcome": outcome, "assemblies": totals}
    Path(out_path).write_text(json.dumps(report, indent=1, sort_keys=True))
    print(json.dumps(report, indent=1, sort_keys=True))


def solution_test_assemblies(solution):
    """Test projects in the solution: every project that references Microsoft.NET.Test.Sdk."""
    names = []
    for project in ET.parse(solution).getroot().iter("Project"):
        path = Path(project.attrib["Path"])
        text = path.read_text(encoding="utf-8") if path.exists() else ""
        if "Microsoft.NET.Test.Sdk" in text:
            override = re.search(r"<AssemblyName>([^<]+)</AssemblyName>", text)
            names.append(override.group(1).strip() if override else path.stem)
    return sorted(names)


def cmd_summarize(args):
    backend, reports_dir, solution = args
    reports = {}
    for path in sorted(Path(reports_dir).rglob("*.json")):
        report = json.loads(path.read_text())
        if report["backend"] == backend:
            if report["shard"] in reports:
                fail(f"two reports for {backend} shard {report['shard']}")
            reports[report["shard"]] = report
    errors = []
    for shard in SHARDS:
        if shard not in reports:
            errors.append(f"shard {shard} left no report, so it did not finish (cancelled, timed out or lost)")
        elif reports[shard]["outcome"] != "success":
            errors.append(f"shard {shard} test step outcome was '{reports[shard]['outcome']}'")
    for shard in reports:
        if shard not in SHARDS:
            errors.append(f"report for unknown shard {shard}")

    expected = solution_test_assemblies(solution)
    where, combined = {}, {}
    for shard, report in reports.items():
        for name, entry in report["assemblies"].items():
            where.setdefault(name, []).append(shard)
            if entry["runs"] != 1:
                errors.append(f"{name} ran {entry['runs']} times on shard {shard}")
            total = combined.setdefault(name, {"total": 0, "passed": 0, "failed": 0, "skipped": 0})
            for key in total:
                total[key] += entry[key]
    for name in expected:
        shards = where.get(name, [])
        if name == RENDER_ASSEMBLY:
            stray = [s for s in shards if s == REST]
            if stray:
                errors.append(f"{name} ran on shard {REST}, outside its slices")
            if not shards:
                errors.append(f"{name} ran on no shard")
        elif not shards:
            errors.append(f"test project {name} is in {solution} and ran on no shard")
        elif len(shards) > 1:
            errors.append(f"{name} ran on {len(shards)} shards: {', '.join(sorted(shards))}")
        elif shards != [REST]:
            errors.append(f"{name} ran on {shards[0]} instead of {REST}")
    for name in sorted(set(where) - set(expected)):
        print(f"::warning title=GPU full-suite shards::{name} ran on {', '.join(where[name])} but is not a "
              f"test project in {solution}")

    rows = [f"### {backend} full suite across {len(SHARDS)} shards", "",
            "| assembly | shards | total | passed | failed | skipped |", "| --- | --- | --- | --- | --- | --- |"]
    for name in sorted(combined):
        entry = combined[name]
        rows.append(f"| {name} | {', '.join(sorted(where[name]))} | {entry['total']} | {entry['passed']} | "
                    f"{entry['failed']} | {entry['skipped']} |")
    grand = {key: sum(entry[key] for entry in combined.values()) for key in ("total", "passed", "failed", "skipped")}
    rows.append(f"| all {len(combined)} assemblies | | {grand['total']} | {grand['passed']} | {grand['failed']} | "
                f"{grand['skipped']} |")
    rows.append("")
    for shard in SHARDS:
        report = reports.get(shard)
        rows.append(f"- {shard}: {report['outcome'] if report else 'no report'}")
    text = "\n".join(rows)
    print(text)
    summary = os.environ.get("GITHUB_STEP_SUMMARY")
    if summary:
        with open(summary, "a", encoding="utf-8") as handle:
            handle.write(text + "\n")
    if errors:
        for error in errors:
            print(f"::error title={backend} full suite::{error}")
        sys.exit(1)


COMMANDS = {
    "expand": cmd_expand,
    "filter": cmd_filter,
    "check-render": cmd_check_render,
    "report": cmd_report,
    "summarize": cmd_summarize,
}

if __name__ == "__main__":
    if len(sys.argv) < 2 or sys.argv[1] not in COMMANDS:
        fail(f"usage: gpu-test-shards.py {{{'|'.join(COMMANDS)}}} ...")
    COMMANDS[sys.argv[1]](sys.argv[2:])
