#!/usr/bin/env python3
"""Shard plan and coverage guard for the GPU matrix's full suites (#1438).

Every leg's full suite runs across several hosted jobs on the schedule and workflow_dispatch, when the run
neither bakes nor sets renderTestFilter. Those are the only triggers on which direct3d11-native and
vulkan-native (`fullSuite: scheduled`) run their full suites at all. metal-native (`fullSuite: always`) also
runs its full suite on push and pull_request, but as one job there (#1460): the org runs at most 5 macOS jobs
at once, and three shards on every trigger let a push run starve the gate dispatch of macOS slots. One job
on push and three on the gate keep two overlapping runs at 4. This file is the single source of the split,
and .github/workflows/cross-platform-gpu.yml reads it through the subcommands below.

THE SPLIT. Shard `rest` runs every test project in KhaozEngine.slnx except KhaozEngine.Render.Tests and
the named projects in PROJECT_SLICES, through a solution derived from the current one at run time, so a new
test project lands in it with no edit. Vulkan's `projects-1` takes MapEditor.Tests, Game.Tests and
Movement.Tests, and Windows' takes Catalog.Tests (#1458). Only `rest` is primary and runs the once-per-leg
steps. The unsharded Windows isolation pass still takes every non-Render project. Every `render-N` shard runs one slice of
KhaozEngine.Render.Tests. Each backend has its own slice table,
because the three rasterizers disagree about what is expensive (below). Within a table the slices are an
ORDERED FIRST-MATCH partition: a test belongs to the first slice whose rules match it, and the last slice has
no rules and takes everything the earlier ones did not. Each slice's vstest filter is its own rules ANDed with
the negation of every earlier slice's rules, so no test can match two slices and no test can match none. A new
Render.Tests class or method falls into the first slice that names it, or into the last.

THE BUILD. Measurement option B uses rest as each OS's build producer, uploading Release output before
its tests. Other shards restore packages, download that exact run's output and skip compilation. No extra
Metal jobs are needed. Unsharded paths build normally. Build-solution remains the option A test-root plan.
Test selection and the exactly-once guards do not depend on the build source.

THE GUARD. `check-render` reads every Render.Tests method listed on the shard's own build output, parses the
filter text each of the backend's slices hands to vstest, and evaluates it with the operators vstest applies
(case-insensitive equality and containment on FullyQualifiedName). It fails when any method matches no slice
or more than one. `report` reads the shard's TRX files, one per test assembly the shard ran, which name their
assembly and count every result whatever the console verbosity or parallelism. `summarize` then reads one
report per shard and fails the backend when a shard is missing, a shard's suite did not succeed, a test
assembly ran on two shards or twice on one, Render.Tests ran outside its slices, or a test project in the
solution ran nowhere.

THE BALANCE comes from the per-test durations in the TRX files of dispatch runs 39039926687 and
39072468288, with the Windows project split also reading assembly durations from 39074411898.
A job's runner factor is the geometric mean of its repeated methods' duration ratios to their
across-run geometric means. Divide durations by that factor, then take each method's geometric mean over
the runs. Hosted speed varies uniformly across a job, so balancing raw times would move work toward the
last run's fast hosts. Every job also spends about 4 minutes on setup and build before its tests start.
The shapes differ by backend:
  * vulkan-native runs serialised, so a slice costs the sum of its tests, about 123.7 minutes for
    Render.Tests. TemporalFollowPhaseGpuTests' orthographic Native and Quality
    sweep took 12.5 minutes in 39039926687, so Native and Quality now run separately. Their initial estimates
    use the 8 Native and 36 Quality rendered phases, whose individual walks cost about the same. Twelve
    slices replace nine to leave room for hosted speed variation, at 10.2 to 10.5 normalized minutes each,
    with the cascade-cull method alone. The non-Render assemblies totalled 15.25 and 14.99 minutes and the
    rest jobs reached 20m03s and 20m00s. Its three named projects move 8.55 and 8.03 minutes to projects-1,
    leaving 6.70 and 6.96 on rest plus its once-per-leg steps.
  * direct3d11-native runs classes in parallel on WARP but the methods of one class in series, so a slice
    costs its slowest class or about a third of its summed time, whichever is more. The one method of
    ShadowCascadeCullPerfGpuTests took about 15 minutes there against 1 on Metal, has a slice to itself
    and sets that backend's floor. The rest jobs took 14m24s and 18m55s, then 22m54s in 39074411898.
    Catalog.Tests took 16.30 minutes there against 13.01 for MapEditor.Tests, so Catalog.Tests moves to
    projects-1 on its own host. Every other non-Render project stays on rest. TemporalFollowPhaseGpuTests'
    methods are spread over two slices so that class is no longer a 16-minute serial chain. The other three slices sum 29 to
    35 minutes each.
  * metal-native has the same parallel shape on a real (paravirtual) GPU at about a third of the cost.
    Render.Tests is about 32 summed minutes in two slices of 7 to 9 minutes of wall time each.
Every run uploads each shard's TRX files as `shard-trx-<backend>-<shard>`, which carry the per-test durations
to re-balance from. Re-balance by moving rules between slices. Coverage cannot drift whatever the rules say.
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

ORTHO_N = PHASE + "The_orthographic_follow_holds_its_bounds_from_every_start_phase_at_Native"
ORTHO_Q = PHASE + "The_orthographic_follow_holds_its_bounds_from_every_start_phase_at_Quality"
ORTHO_PU = PHASE + "The_orthographic_follow_holds_its_bounds_from_every_start_phase_at_Performance_and_Ultra"
PERSPECTIVE_NQ = PHASE + "The_perspective_follow_holds_its_bounds_from_every_start_phase_at_Native_and_Quality"
PERSPECTIVE_PU = PHASE + "The_perspective_follow_holds_its_bounds_from_every_start_phase_at_Performance_and_Ultra"
LINES = PHASE + "The_follow_lines_hold_their_share_from_every_start_phase"
STOP = PHASE + "The_follow_stop_holds_its_bounds_from_every_start_phase"


def gpu(*prefixes):
    """Containment rules on Gpu class-name prefixes, for example gpu("TemporalA", "TemporalC")."""
    return [("~", GPU + prefix) for prefix in prefixes]


# (operator, value). "=" is vstest's FullyQualifiedName equality and pins one method, theory rows
# included. "~" is containment. A class rule ends in "." so it cannot reach a longer class name, and a
# prefix rule such as gpu("S") takes every Gpu class whose name starts with that letter that no earlier
# rule took.
RENDER_SLICES = {
    "metal-native": [
        ("render-1", gpu("TemporalFollow", "TemporalLongHoldFollowGpuTests.")),
        ("render-2", []),
    ],
    "direct3d11-native": [
        ("render-1", gpu("ShadowCascadeCullPerfGpuTests.")),
        ("render-2", [("=", ORTHO_N), ("=", ORTHO_Q), ("=", PERSPECTIVE_NQ), ("=", STOP)]
         + gpu("TemporalFollowCameraGpuTests.", "TemporalFollowStopGpuTests.", "TemporalG", "TemporalR",
               "TemporalS")),
        ("render-3", gpu("Temporal")),
        ("render-4", []),
    ],
    "vulkan-native": [
        ("render-1", [("=", ORTHO_Q)] + gpu("I")),
        ("render-2", gpu("ShadowCascadeCullPerfGpuTests.")),
        ("render-3", [("=", PERSPECTIVE_PU)] + gpu("B")),
        ("render-4", [("=", ORTHO_PU)] + gpu("G", "D")),
        ("render-5", gpu("TemporalLongHoldFollowGpuTests.", "R", "TemporalC", "C")),
        ("render-6", [("=", LINES), ("=", ORTHO_N)] + gpu("A", "TemporalM")),
        ("render-7", [("=", PERSPECTIVE_NQ)] + gpu("TemporalFastEdge", "TemporalFollowedHistory", "P",
                                                   "TemporalE", "TemporalU")),
        ("render-8", gpu("TemporalS", "TemporalR", "M", "TemporalH")),
        ("render-9", [("=", STOP)] + gpu("S", "Tab", "Target", "Telegraph", "Terrain", "Text", "Tile",
                                       "Tool", "H", "L", "N")),
        ("render-10", gpu("TemporalFollowCameraGpuTests.", "TemporalP", "O", "W", "V")),
        ("render-11", gpu("TemporalG", "TemporalFollowLinesGpuTests.", "F", "TemporalN", "TemporalO")),
        ("render-12", []),
    ],
}

REST = "rest"
RENDER_PROJECT = f"{RENDER_ASSEMBLY}/{RENDER_ASSEMBLY}.csproj"
# Only these named projects leave rest. Future test projects default to rest on every backend.
PROJECT_SLICES = {
    "direct3d11-native": [
        ("projects-1", ("KhaozEngine.Catalog.Tests/KhaozEngine.Catalog.Tests.csproj",)),
    ],
    "vulkan-native": [
        ("projects-1", tuple(f"{name}/{name}.csproj" for name in (
            "KhaozEngine.MapEditor.Tests", "KhaozEngine.Game.Tests", "KhaozEngine.Movement.Tests"))),
    ],
}
NEGATE = {"=": "!=", "~": "!~"}


def fail(message):
    print(f"::error title=GPU full-suite shards::{message}")
    sys.exit(1)


def render_slices(backend):
    if backend not in RENDER_SLICES:
        fail(f"'{backend}' has no shard plan. Known backends: {', '.join(RENDER_SLICES)}")
    return RENDER_SLICES[backend]


def shards(backend):
    return [REST] + [name for name, _ in PROJECT_SLICES.get(backend, [])] + [name for name, _ in render_slices(backend)]


def project_shard(backend, path):
    return next((name for name, projects in PROJECT_SLICES.get(backend, []) if path in projects), REST)


def cmd_solution(args):
    """Derive a project shard's solution. An empty shard keeps every non-Render project for isolation."""
    backend, shard, source, destination = args
    if shard not in [""] + [name for name in shards(backend) if not name.startswith("render-")]:
        fail(f"'{shard}' is not a {backend} project shard")
    solution = ET.parse(source)
    source_dir, destination_dir = Path(source).resolve().parent, Path(destination).resolve().parent
    paths = [p.attrib["Path"] for p in solution.iter("Project")]
    if paths.count(RENDER_PROJECT) != 1:
        fail("expected exactly one Render.Tests project")
    for name, projects in PROJECT_SLICES.get(backend, []):
        for path in projects:
            if paths.count(path) != 1:
                fail(f"{backend} {name} expects exactly one {path} project in {source}")
    for parent in solution.iter():
        for project in list(parent):
            if project.tag != "Project":
                continue
            path = project.attrib["Path"]
            if path == RENDER_PROJECT or (shard and project_shard(backend, path) != shard):
                parent.remove(project)
            else:
                project.set("Path", os.path.relpath(source_dir / path, destination_dir).replace(os.sep, "/"))
    solution.write(destination, encoding="unicode")


def cmd_build_solution(args):
    """Build test roots for one shard, plus the primary leg's Render.Tests instrumentation."""
    backend, shard, source, destination = args
    if shard not in shards(backend):
        fail(f"'{shard}' is not a {backend} shard")
    tests = set(solution_test_projects(source).values())
    if RENDER_PROJECT not in tests:
        fail("expected a Render.Tests test project")
    selected = {RENDER_PROJECT} if shard.startswith("render-") else {
        path for path in tests if path != RENDER_PROJECT and project_shard(backend, path) == shard
    }
    if shard == REST and backend in ("direct3d11-native", "vulkan-native"):
        selected.add(RENDER_PROJECT)
    solution = ET.parse(source)
    source_dir, destination_dir = Path(source).resolve().parent, Path(destination).resolve().parent
    for parent in solution.iter():
        for project in list(parent):
            if project.tag != "Project":
                continue
            path = project.attrib["Path"]
            if path not in selected:
                parent.remove(project)
            else:
                project.set("Path", os.path.relpath(source_dir / path, destination_dir).replace(os.sep, "/"))
    solution.write(destination, encoding="unicode")


def slice_index(backend, name):
    slices = render_slices(backend)
    for index, (slice_name, _) in enumerate(slices):
        if slice_name == name:
            return index
    fail(f"'{name}' is not a {backend} Render.Tests slice. Known slices: {', '.join(n for n, _ in slices)}")


def rule_text(op, value):
    return f"FullyQualifiedName{op}{value}"


def slice_filter(backend, name):
    slices = render_slices(backend)
    index = slice_index(backend, name)
    rules = slices[index][1]
    earlier = [rule for _, slice_rules in slices[:index] for rule in slice_rules]
    if index < len(slices) - 1 and not rules:
        fail(f"{backend} slice {name} has no rules and is not the last slice, so it would match nothing")
    if index == len(slices) - 1 and rules:
        fail(f"{backend} slice {name} is the last slice and has rules, so a test no slice names would run nowhere")
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


# The triggers whose full suites are sharded: the weekly sweep and the pre-tag gate dispatch. A push or
# pull_request full suite (metal-native only) stays one job, so it holds one macOS slot (#1460).
SHARD_EVENTS = ("schedule", "workflow_dispatch")


def full_suite_trigger(leg, event):
    """Whether this leg runs its full suite on this trigger, as the workflow's KE_TEST_FILTER decides it."""
    return leg["fullSuite"] == "always" or (leg["fullSuite"] == "scheduled" and event in ("schedule", "workflow_dispatch"))


def cmd_expand(_args):
    """Turn the selected legs (JSON array on stdin) into matrix entries, one per shard where sharded."""
    legs = json.load(sys.stdin)
    event = os.environ.get("EVENT", "")
    bake = os.environ.get("BAKE", "") == "true"
    render_filter = os.environ.get("RENDER_FILTER", "")
    entries, summaries = [], []
    for leg in legs:
        sharded = (leg["backend"] in RENDER_SLICES and full_suite_trigger(leg, event)
                   and event in SHARD_EVENTS and not bake and not render_filter)
        if not sharded:
            entries.append(dict(leg, shard="", primary="1"))
            continue
        summaries.append({"os": leg["os"], "backend": leg["backend"]})
        for shard in shards(leg["backend"]):
            entries.append(dict(leg, shard=shard, primary="1" if shard == REST else ""))
    print(f"matrix={json.dumps({'include': entries}, separators=(',', ':'))}")
    print(f"sharded={json.dumps(summaries, separators=(',', ':'))}")
    for entry in entries:
        label = entry["backend"] + (f" / {entry['shard']}" if entry["shard"] else "")
        print(f"  job: {entry['os']} / {label}", file=sys.stderr)


def cmd_filter(args):
    backend, shard = args
    print(slice_filter(backend, shard))


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
    backend, listing = args
    slices = render_slices(backend)
    names = listed_tests(listing)
    if not names:
        fail(f"no Render.Tests test was listed in {listing}, so the slice coverage cannot be checked")
    counts = [0] * len(slices)
    unassigned, doubled = [], []
    predicates = [parse_filter(slice_filter(backend, slice_name)) for slice_name, _ in slices]
    for name in names:
        hits = [index for index, predicate in enumerate(predicates) if predicate(name)]
        if not hits:
            unassigned.append(name)
        elif len(hits) > 1:
            doubled.append((name, [slices[i][0] for i in hits]))
        for index in hits:
            counts[index] += 1
    for (name, rules), count in zip(slices, counts):
        print(f"{backend} {name}: {count} listed test methods")
        for op, value in rules:
            if not any(rule_matches(op, value, test) for test in names):
                print(f"::warning title=GPU full-suite shards::rule {rule_text(op, value)} on {backend} {name} "
                      "matches no Render.Tests method. Coverage is unaffected, the tests it named now run in a "
                      "later slice. Update scripts/gpu-test-shards.py to restore the balance.")
    print(f"{len(names)} Render.Tests methods listed, {len(unassigned)} unassigned, {len(doubled)} assigned twice")
    for name in unassigned[:20]:
        print(f"  unassigned: {name}")
    for name, hit_slices in doubled[:20]:
        print(f"  assigned to {', '.join(hit_slices)}: {name}")
    if unassigned or doubled:
        fail(f"the {backend} Render.Tests slices do not partition the listed tests exactly once")


TRX = "{http://microsoft.com/schemas/VisualStudio/TeamTest/2010}"


def trx_totals(path):
    """(assembly, totals) from one TRX file, or None when it holds no test at all.

    One TRX is one test assembly's run. Its UnitTest definitions name the assembly through `storage`, the
    path of the test dll, which the Windows runner writes lower-cased, so the summary matches assembly names
    case-insensitively. Outcomes are counted per result: Passed, NotExecuted (an xUnit skip) and everything
    else as a failure, so an aborted or timed-out row can never read as a pass.
    """
    root = ET.parse(path).getroot()
    storages = {unit.get("storage", "") for unit in root.iter(TRX + "UnitTest")}
    if not storages:
        return None
    if len(storages) != 1:
        raise ValueError(f"{path} names {len(storages)} test assemblies, expected one")
    assembly = re.split(r"[\\/]", storages.pop())[-1]
    assembly = assembly[:-4] if assembly.lower().endswith(".dll") else assembly
    totals = {"total": 0, "passed": 0, "failed": 0, "skipped": 0, "runs": 1}
    for result in root.iter(TRX + "UnitTestResult"):
        outcome = result.get("outcome", "")
        totals["total"] += 1
        key = {"Passed": "passed", "NotExecuted": "skipped"}.get(outcome, "failed")
        totals[key] += 1
    return assembly, totals


def cmd_report(args):
    backend, shard, outcome, results_dir, out_path = args
    totals = {}
    try:
        for path in sorted(Path(results_dir).glob("*.trx")):
            read = trx_totals(path)
            if read is None:
                continue
            assembly, entry = read
            if assembly in totals:
                for key in entry:
                    totals[assembly][key] += entry[key]
            else:
                totals[assembly] = entry
    except (ET.ParseError, ValueError) as error:
        # Recorded as the outcome, so the summary job fails the backend instead of trusting partial totals.
        print(f"::error title=GPU full-suite shards::{error}")
        totals, outcome = {}, f"unreadable TRX ({outcome})"
    report = {"backend": backend, "shard": shard, "outcome": outcome, "assemblies": totals}
    Path(out_path).write_text(json.dumps(report, indent=1, sort_keys=True))
    print(json.dumps(report, indent=1, sort_keys=True))


def solution_test_projects(solution):
    """Test projects in the solution: every project that references Microsoft.NET.Test.Sdk."""
    names = {}
    base = Path(solution).parent
    for project in ET.parse(solution).getroot().iter("Project"):
        path = base / project.attrib["Path"]
        text = path.read_text(encoding="utf-8") if path.exists() else ""
        if "Microsoft.NET.Test.Sdk" in text:
            override = re.search(r"<AssemblyName>([^<]+)</AssemblyName>", text)
            names[override.group(1).strip() if override else path.stem] = project.attrib["Path"]
    return names


def cmd_summarize(args):
    backend, reports_dir, solution = args
    expected_shards = shards(backend)
    reports = {}
    for path in sorted(Path(reports_dir).rglob("*.json")):
        report = json.loads(path.read_text())
        if report["backend"] == backend:
            if report["shard"] in reports:
                fail(f"two reports for {backend} shard {report['shard']}")
            reports[report["shard"]] = report
    errors = []
    for shard in expected_shards:
        if shard not in reports:
            errors.append(f"shard {shard} left no report, so it did not finish (cancelled, timed out or lost)")
        elif reports[shard]["outcome"] != "success":
            errors.append(f"shard {shard} test step outcome was '{reports[shard]['outcome']}'")
    for shard in reports:
        if shard not in expected_shards:
            errors.append(f"report for unknown shard {shard}")

    projects = solution_test_projects(solution)
    expected = sorted(projects)
    # TRX names an assembly by its dll path, which arrives lower-cased from Windows.
    canonical = {name.casefold(): name for name in expected}
    where, combined = {}, {}
    for shard, report in reports.items():
        for raw_name, entry in report["assemblies"].items():
            name = canonical.get(raw_name.casefold(), raw_name)
            where.setdefault(name, []).append(shard)
            if entry["runs"] != 1:
                errors.append(f"{name} ran {entry['runs']} times on shard {shard}")
            total = combined.setdefault(name, {"total": 0, "passed": 0, "failed": 0, "skipped": 0})
            for key in total:
                total[key] += entry[key]
    for name in expected:
        ran_on = where.get(name, [])
        if name == RENDER_ASSEMBLY:
            slices = {slice_name for slice_name, _ in render_slices(backend)}
            if set(ran_on) != slices:
                errors.append(f"{name} ran on {sorted(ran_on)}, expected its slices {sorted(slices)}")
        elif not ran_on:
            errors.append(f"test project {name} is in {solution} and ran on no shard")
        elif len(ran_on) > 1:
            errors.append(f"{name} ran on {len(ran_on)} shards: {', '.join(sorted(ran_on))}")
        elif ran_on != [project_shard(backend, projects[name])]:
            errors.append(f"{name} ran on {ran_on[0]} instead of {project_shard(backend, projects[name])}")
    for name in sorted(set(where) - set(expected)):
        print(f"::warning title=GPU full-suite shards::{name} ran on {', '.join(where[name])} but is not a "
              f"test project in {solution}")

    rows = [f"### {backend} full suite across {len(expected_shards)} shards", "",
            "| assembly | shards | total | passed | failed | skipped |", "| --- | --- | --- | --- | --- | --- |"]
    for name in sorted(combined):
        entry = combined[name]
        rows.append(f"| {name} | {', '.join(sorted(where[name]))} | {entry['total']} | {entry['passed']} | "
                    f"{entry['failed']} | {entry['skipped']} |")
    grand = {key: sum(entry[key] for entry in combined.values()) for key in ("total", "passed", "failed", "skipped")}
    rows.append(f"| all {len(combined)} assemblies | | {grand['total']} | {grand['passed']} | {grand['failed']} | "
                f"{grand['skipped']} |")
    rows.append("")
    for shard in expected_shards:
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
    "solution": cmd_solution,
    "build-solution": cmd_build_solution,
    "check-render": cmd_check_render,
    "report": cmd_report,
    "summarize": cmd_summarize,
}

if __name__ == "__main__":
    if len(sys.argv) < 2 or sys.argv[1] not in COMMANDS:
        fail(f"usage: gpu-test-shards.py {{{'|'.join(COMMANDS)}}} ...")
    COMMANDS[sys.argv[1]](sys.argv[2:])
