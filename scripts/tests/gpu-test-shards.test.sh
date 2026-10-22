#!/bin/sh
# Exercises scripts/gpu-test-shards.py, the shard plan and coverage guard of the GPU matrix's full suites
# (#1438): which legs a trigger shards, that every backend's slice filters partition a listing exactly once,
# that a shard report reads its totals from TRX files, and that the per-backend summary fails a missing shard,
# a red shard and an assembly that ran twice. A small SDK fixture also proves transitive references stay
# in Release when their projects are absent from the derived solution.
#
# Fixture-only: every listing, TRX file, report and solution below is written under mktemp.
#
# Run it from anywhere:  sh scripts/tests/gpu-test-shards.test.sh
set -eu

here=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
PLAN=$here/../gpu-test-shards.py
[ -f "$PLAN" ] || { echo "gpu-test-shards.test: missing $PLAN" >&2; exit 2; }

TMPROOT=$(mktemp -d)
trap 'rm -rf "$TMPROOT"' EXIT
OUTFILE="$TMPROOT/out"

pass=0
fail=0
check() { # name, expected, actual
  if [ "$2" = "$3" ]; then pass=$((pass+1)); echo "  ok    $1"
  else fail=$((fail+1)); echo "  FAIL  $1 (expected $2, got $3)"; echo "  ----- output -----"; sed 's/^/  | /' "$OUTFILE"; fi
}

run() { # runs the plan with the remaining args, echoes the exit code
  _rc=0; python3 "$PLAN" "$@" > "$OUTFILE" 2>&1 || _rc=$?; echo "$_rc"
}

LEGS='[{"os":"macos-26","backend":"metal-native","fullSuite":"always"},{"os":"windows-latest","backend":"direct3d11-native","fullSuite":"scheduled"},{"os":"ubuntu-latest","backend":"vulkan-native","fullSuite":"scheduled"}]'

expand() { # event, bake, render filter. Echoes "<matrix entries> <sharded backends>"
  printf '%s' "$LEGS" | EVENT=$1 BAKE=$2 RENDER_FILTER=$3 python3 "$PLAN" expand 2>/dev/null > "$OUTFILE"
  python3 - "$OUTFILE" <<'PY'
import json, sys
lines = dict(line.split("=", 1) for line in open(sys.argv[1]).read().splitlines())
matrix = json.loads(lines["matrix"])["include"]
sharded = json.loads(lines["sharded"])
primaries = {e["backend"] for e in matrix if e["primary"] == "1"}
assert len(primaries) == 3 and sum(e["primary"] == "1" for e in matrix) == 3, "one primary entry per leg"
assert all(e["shard"] in ("", "rest") for e in matrix if e["primary"] == "1"), "only rest is primary when sharded"
print(len(matrix), ",".join(s["backend"] for s in sharded) or "none")
PY
}

echo "expand: which triggers shard which legs"
check "push keeps Metal's full suite on one job" "3 none"    "$(expand push false '')"
check "pull_request matches push"           "3 none"          "$(expand pull_request false '')"
check "dispatch shards all three"           "24 metal-native,direct3d11-native,vulkan-native" "$(expand workflow_dispatch false '')"
check "schedule matches dispatch"           "24 metal-native,direct3d11-native,vulkan-native" "$(expand schedule false '')"
check "a bake shards nothing"               "3 none"          "$(expand workflow_dispatch true '')"
check "a render filter shards nothing"      "3 none"          "$(expand workflow_dispatch false 'FullyQualifiedName~Golden')"

echo "check-render: every backend's slices partition a listing exactly once"
G=KhaozEngine.Tests.Gpu
cat > "$TMPROOT/list.txt" <<EOF
Build started.
The following Tests are available:
    $G.TemporalFollowPhaseGpuTests.The_orthographic_follow_holds_its_bounds_from_every_start_phase_at_Native
    $G.TemporalFollowPhaseGpuTests.The_orthographic_follow_holds_its_bounds_from_every_start_phase_at_Quality
    $G.TemporalFollowPhaseGpuTests.The_orthographic_follow_holds_its_bounds_from_every_start_phase_at_Performance_and_Ultra
    $G.TemporalFollowPhaseGpuTests.The_perspective_follow_holds_its_bounds_from_every_start_phase_at_Native_and_Quality
    $G.TemporalFollowPhaseGpuTests.The_follow_stop_holds_its_bounds_from_every_start_phase
    $G.TemporalFollowPhaseGpuTests.The_follow_lines_hold_their_share_from_every_start_phase
    $G.TemporalFollowCameraGpuTests.A_followed_keyed_box_leaves_no_trail_on_the_wall_it_uncovers(preset: Quality)
    $G.TemporalFollowLinesGpuTests.The_follow_lines_table_prints_every_walk
    $G.TemporalLongHoldFollowGpuTests.The_long_hold_follow_camera_table_prints_every_walk
    $G.TemporalGhostingGpuTests.TheGhostingTableCoversEveryPreset
    $G.TemporalPerspectiveFollowGpuTests.A_followed_avatar_leaves_no_trail_on_the_ground_it_uncovers
    $G.TemporalResetGpuTests.TheFrameAfterAResetMatchesAFromScratchRender
    $G.ShadowCascadeCullPerfGpuTests.Consumer_shaped_caster_load_costs_less_per_cascade_after_culling
    $G.ShadowRerecordBenchGpuTests.The_hold_releases_on_a_cadence_the_threshold_predicts
    KhaozEngine.Tests.MapDoc.NativeMapGoldenGpuTests.NativeMapFamilies_CertifyCompleteAndDetectOmission(variant: paint)
    KhaozEngine.Tests.MapDoc.NativeMapSnapshotGpuTests.TwoFocuses_ReusePublishedFoliageAndChangeCutoffAndLodPixels
    KhaozEngine.Tests.MapDoc.NativeResourceUploadGpuTests.PublishedHandles_DrawRigidMidpointAndFoliageWithoutResourceUpload
    KhaozEngine.Tests.MapDoc.NativeMapReadinessTests.NativeSink_FocusSequenceReusesItsPublishedBatch
    KhaozEngine.Tests.MapDoc.NativeMapAllocationTests.WarmGpuQueueUpdateDraw_AllocateZero
    $G.WaterGoldenTests.Golden_water
    $G.AtmosphereFogGoldenTests.Golden_fog
    KhaozEngine.Tests.Render3D.WaterFlatPlaneGoldenTests.FlatQuadsMatchTheTessellatedFlatReferenceInEveryGridMode
    KhaozEngine.Tests.Render2D.SpriteBatchTests.A_brand_new_class_lands_in_the_last_slice
EOF
for backend in metal-native direct3d11-native vulkan-native; do
  check "$backend partitions the listing" 0 "$(run check-render "$backend" "$TMPROOT/list.txt")"
  check "$backend reports 23 methods, none unassigned or doubled" 1 \
    "$(grep -c '^23 Render.Tests methods listed, 0 unassigned, 0 assigned twice$' "$OUTFILE")"
done
run check-render vulkan-native "$TMPROOT/list.txt" > /dev/null
check "vulkan render-1 holds the Quality orthographic sweep alone" 1 "$(grep -c '^vulkan-native render-1: 1 listed test methods$' "$OUTFILE")"
check "vulkan render-6 holds Native, the phase lines and atmosphere" 1 "$(grep -c '^vulkan-native render-6: 3 listed test methods$' "$OUTFILE")"
check "vulkan render-12 takes the unnamed classes" 1 "$(grep -c '^vulkan-native render-12: 2 listed test methods$' "$OUTFILE")"
run check-render direct3d11-native "$TMPROOT/list.txt" > /dev/null
check "d3d11 render-1 holds the cascade cull alone" 1 "$(grep -c '^direct3d11-native render-1: 1 listed test methods$' "$OUTFILE")"
check "d3d11 render-4 takes every class outside Temporal" 1 "$(grep -c '^direct3d11-native render-4: 5 listed test methods$' "$OUTFILE")"
run check-render metal-native "$TMPROOT/list.txt" > /dev/null
check "metal render-1 holds the follow sweeps" 1 "$(grep -c '^metal-native render-1: 14 listed test methods$' "$OUTFILE")"
check "an empty listing is refused" 1 "$(run check-render metal-native /dev/null)"
check "an unknown backend is refused" 1 "$(run filter opengl render-1)"
check "an unknown slice is refused" 1 "$(run filter metal-native render-3)"
check "the last slice negates every earlier rule" 0 "$(run filter metal-native render-2)"
check "the last slice filter" "FullyQualifiedName!~$G.TemporalFollow&FullyQualifiedName!~$G.TemporalLongHoldFollowGpuTests.&FullyQualifiedName!~KhaozEngine.Tests.MapDoc.NativeResourceUploadGpuTests.&FullyQualifiedName!~KhaozEngine.Tests.MapDoc.NativeMapReadinessTests.&FullyQualifiedName!~KhaozEngine.Tests.MapDoc.NativeMapGoldenGpuTests.&FullyQualifiedName!~KhaozEngine.Tests.MapDoc.NativeMapSnapshotGpuTests.&FullyQualifiedName!=KhaozEngine.Tests.MapDoc.NativeMapAllocationTests.WarmGpuQueueUpdateDraw_AllocateZero" "$(cat "$OUTFILE")"

echo "report: totals come from TRX files"
trx() { # path, storage, then outcome per result
  _path=$1; _storage=$2; shift 2
  {
    echo '<TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010"><Results>'
    _i=0; for _o in "$@"; do _i=$((_i+1)); echo "<UnitTestResult testId=\"t$_i\" outcome=\"$_o\" />"; done
    echo '</Results><TestDefinitions>'
    _i=0; for _o in "$@"; do _i=$((_i+1)); printf '<UnitTest id="t%s" storage="%s" />\n' "$_i" "$_storage"; done
    echo '</TestDefinitions></TestRun>'
  } > "$_path"
}
mkdir -p "$TMPROOT/results"
trx "$TMPROOT/results/a.trx" 'd:\a\khaozengine\khaozengine.alpha.tests\bin\release\net10.0\khaozengine.alpha.tests.dll' Passed Passed NotExecuted
trx "$TMPROOT/results/b.trx" '/Users/runner/work/KhaozEngine/KhaozEngine.Beta.Tests/bin/Release/net10.0/KhaozEngine.Beta.Tests.dll' Passed Failed Timeout
echo '<TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010"><Results /></TestRun>' > "$TMPROOT/results/empty.trx"
check "report reads the TRX files" 0 "$(run report metal-native rest success "$TMPROOT/results" "$TMPROOT/r.json")"
check "report totals" '{"KhaozEngine.Beta.Tests": [3, 1, 2, 0, 1], "khaozengine.alpha.tests": [3, 2, 0, 1, 1]}' \
  "$(python3 -c 'import json,sys; r=json.load(open(sys.argv[1]))["assemblies"]; print(json.dumps({k: [v[x] for x in ("total","passed","failed","skipped","runs")] for k, v in r.items()}, sort_keys=True))' "$TMPROOT/r.json")"
echo 'not xml' > "$TMPROOT/results/broken.trx"
run report metal-native rest success "$TMPROOT/results" "$TMPROOT/broken.json" > /dev/null
check "an unreadable TRX fails the shard's outcome" "unreadable TRX (success)" \
  "$(python3 -c 'import json,sys; print(json.load(open(sys.argv[1]))["outcome"])' "$TMPROOT/broken.json")"

echo "summarize: the per-backend full suite check"
S=$TMPROOT/sln
mkdir -p "$S/A.Tests" "$S/KhaozEngine.Render.Tests" "$S/Lib"
printf '<Project><ItemGroup><PackageReference Include="Microsoft.NET.Test.Sdk" /></ItemGroup></Project>\n' > "$S/A.Tests/A.Tests.csproj"
printf '<Project><PropertyGroup><AssemblyName>KhaozEngine.Render.Tests</AssemblyName></PropertyGroup><ItemGroup><PackageReference Include="Microsoft.NET.Test.Sdk" /></ItemGroup></Project>\n' > "$S/KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj"
printf '<Project />\n' > "$S/Lib/Lib.csproj"
printf '<Solution><Project Path="A.Tests/A.Tests.csproj" /><Folder Name="/x/"><Project Path="KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj" /></Folder><Project Path="Lib/Lib.csproj" /></Solution>\n' > "$S/Fixture.slnx"
reports() { # outcome of render-2, where a.tests ran (comma list)
  rm -rf "$TMPROOT/reports"; mkdir -p "$TMPROOT/reports"
  python3 - "$TMPROOT/reports" "$1" "$2" <<'PY'
import json, sys
out, render2, a_on = sys.argv[1], sys.argv[2], sys.argv[3].split(",")
one = {"total": 2, "passed": 2, "failed": 0, "skipped": 0, "runs": 1}
for shard in ["rest", "render-1", "render-2"]:
    if shard == "render-2" and render2 == "missing":
        continue
    assemblies = {}
    if shard in a_on:
        assemblies["a.tests"] = dict(one)
    if shard != "rest":
        assemblies["KhaozEngine.Render.Tests"] = dict(one)
    outcome = render2 if shard == "render-2" else "success"
    json.dump({"backend": "metal-native", "shard": shard, "outcome": outcome, "assemblies": assemblies},
              open(f"{out}/metal-native-{shard}.json", "w"))
PY
}
reports success rest
check "every shard green and every assembly once passes" 0 "$(run summarize metal-native "$TMPROOT/reports" "$S/Fixture.slnx")"
check "the lower-cased TRX name is matched to its project" 1 "$(grep -c '^| A.Tests | rest | 2 | 2 | 0 | 0 |$' "$OUTFILE")"
reports missing rest
check "a missing shard fails the backend" 1 "$(run summarize metal-native "$TMPROOT/reports" "$S/Fixture.slnx")"
reports failure rest
check "a red shard fails the backend" 1 "$(run summarize metal-native "$TMPROOT/reports" "$S/Fixture.slnx")"
reports success rest,render-1
check "an assembly on two shards fails the backend" 1 "$(run summarize metal-native "$TMPROOT/reports" "$S/Fixture.slnx")"
reports success ''
check "a test project that ran nowhere fails the backend" 1 "$(run summarize metal-native "$TMPROOT/reports" "$S/Fixture.slnx")"

echo "project shards: named projects move once, new projects stay on rest"
python3 - "$S" <<'PY'
import pathlib, sys, xml.etree.ElementTree as ET
base = pathlib.Path(sys.argv[1])
solution = ET.parse(base / "Fixture.slnx")
for name in ("KhaozEngine.MapEditor.Tests", "KhaozEngine.Game.Tests", "KhaozEngine.Movement.Tests", "KhaozEngine.Catalog.Tests",
             "KhaozEngine.Server.Tests"):
    path = base / name / (name + ".csproj")
    path.parent.mkdir()
    override = "<AssemblyName>Fixture.Editor.Tests</AssemblyName>" if "MapEditor" in name else ""
    path.write_text('<Project><PropertyGroup>' + override + '</PropertyGroup><ItemGroup>'
                    '<PackageReference Include="Microsoft.NET.Test.Sdk" /></ItemGroup></Project>')
    ET.SubElement(solution.getroot(), "Project", Path=f"{name}/{name}.csproj")
solution.write(base / "Fixture.slnx")
(base / "output").mkdir()
PY
check "rest solution excludes Render.Tests and the named projects" 0 \
  "$(run solution vulkan-native rest "$S/Fixture.slnx" "$S/output/rest.slnx")"
check "the named project solution is generated" 0 \
  "$(run solution vulkan-native projects-1 "$S/Fixture.slnx" "$S/output/projects.slnx")"
check "unsharded isolation keeps all non-render projects" 0 \
  "$(run solution vulkan-native '' "$S/Fixture.slnx" "$S/output/all.slnx")"
check "Windows rest excludes the named projects" 0 \
  "$(run solution direct3d11-native rest "$S/Fixture.slnx" "$S/output/windows.slnx")"
check "the Windows named project solution is generated" 0 \
  "$(run solution direct3d11-native projects-1 "$S/Fixture.slnx" "$S/output/windows-projects.slnx")"
check "the second Windows named project solution is generated" 0 \
  "$(run solution direct3d11-native projects-2 "$S/Fixture.slnx" "$S/output/windows-projects-2.slnx")"
check "Render slices cannot generate a project solution" 1 \
  "$(run solution vulkan-native render-1 "$S/Fixture.slnx" "$S/output/invalid.slnx")"
_rc=0
python3 - "$S" > "$OUTFILE" 2>&1 <<'PY' || _rc=$?
import pathlib, sys, xml.etree.ElementTree as ET
base = pathlib.Path(sys.argv[1])
def paths(name):
    return {p.get("Path") for p in ET.parse(base / "output" / (name + ".slnx")).iter("Project")}
rest = {"../A.Tests/A.Tests.csproj", "../Lib/Lib.csproj"}
catalog = {"../KhaozEngine.Catalog.Tests/KhaozEngine.Catalog.Tests.csproj"}
server = {"../KhaozEngine.Server.Tests/KhaozEngine.Server.Tests.csproj"}
editor = {"../KhaozEngine.MapEditor.Tests/KhaozEngine.MapEditor.Tests.csproj"}
named = {f"../{name}/{name}.csproj" for name in
         ("KhaozEngine.MapEditor.Tests", "KhaozEngine.Game.Tests", "KhaozEngine.Movement.Tests")}
assert paths("rest") == rest | catalog | server, paths("rest")
assert paths("projects") == named, paths("projects")
assert paths("all") == rest | named | catalog | server, paths("all")
assert paths("windows") == rest | (named - editor), paths("windows")
assert paths("windows-projects") == catalog, paths("windows-projects")
assert paths("windows-projects-2") == editor | server, paths("windows-projects-2")
assert not paths("rest") & paths("projects"), "project shards overlap"
for a, b in (("windows", "windows-projects"), ("windows", "windows-projects-2"), ("windows-projects", "windows-projects-2")):
    assert not paths(a) & paths(b), f"Windows shards {a} and {b} overlap"
assert all((base / "output" / p).exists() for p in paths("all")), "paths were not rebased"
PY
check "derived solutions partition projects and rebase paths" 0 "$_rc"

echo "build solutions: only shard test roots and required instrumentation"
for backend in metal-native direct3d11-native vulkan-native; do
  for shard in rest render-1; do
    check "$backend $shard build solution" 0 \
      "$(run build-solution "$backend" "$shard" "$S/Fixture.slnx" "$S/output/build-$backend-$shard.slnx")"
  done
done
for backend in direct3d11-native vulkan-native; do
  check "$backend projects-1 build solution" 0 \
    "$(run build-solution "$backend" projects-1 "$S/Fixture.slnx" "$S/output/build-$backend-projects-1.slnx")"
done
check "direct3d11-native projects-2 build solution" 0 \
  "$(run build-solution direct3d11-native projects-2 "$S/Fixture.slnx" "$S/output/build-direct3d11-native-projects-2.slnx")"
_rc=0
python3 - "$S" > "$OUTFILE" 2>&1 <<'PYBUILD' || _rc=$?
import pathlib, sys, xml.etree.ElementTree as ET
base = pathlib.Path(sys.argv[1])
def paths(backend, shard):
    return {p.get("Path") for p in ET.parse(base / "output" / f"build-{backend}-{shard}.slnx").iter("Project")}
render = {"../KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj"}
catalog = {"../KhaozEngine.Catalog.Tests/KhaozEngine.Catalog.Tests.csproj"}
server = {"../KhaozEngine.Server.Tests/KhaozEngine.Server.Tests.csproj"}
editor = {"../KhaozEngine.MapEditor.Tests/KhaozEngine.MapEditor.Tests.csproj"}
named = {f"../{n}/{n}.csproj" for n in
         ("KhaozEngine.MapEditor.Tests", "KhaozEngine.Game.Tests", "KhaozEngine.Movement.Tests")}
for backend in ("metal-native", "direct3d11-native", "vulkan-native"):
    assert paths(backend, "render-1") == render
    rest = paths(backend, "rest")
    assert "../Lib/Lib.csproj" not in rest, "standalone library roots are unnecessary"
    assert "../A.Tests/A.Tests.csproj" in rest, "new test projects must default to rest"
    assert (render <= rest) == (backend != "metal-native"), "once-per-leg checks need Render.Tests"
assert paths("metal-native", "rest") == {"../A.Tests/A.Tests.csproj"} | named | catalog | server
assert paths("direct3d11-native", "rest") == {"../A.Tests/A.Tests.csproj"} | (named - editor) | render
assert paths("vulkan-native", "rest") == {"../A.Tests/A.Tests.csproj"} | catalog | server | render
assert paths("direct3d11-native", "projects-1") == catalog
assert paths("direct3d11-native", "projects-2") == editor | server
assert paths("vulkan-native", "projects-1") == named
PYBUILD
check "build roots preserve coverage and instrumentation" 0 "$_rc"
check "an unknown build shard fails" 1 "$(run build-solution metal-native render-3 "$S/Fixture.slnx" "$S/output/no.slnx")"

project_reports() { # mutation, backend. Applied to an otherwise complete software backend
  rm -rf "$TMPROOT/reports"; mkdir -p "$TMPROOT/reports"
  python3 - "$PLAN" "$TMPROOT/reports" "$1" "$2" <<'PY'
import importlib.util, json, sys
spec = importlib.util.spec_from_file_location("plan", sys.argv[1])
plan = importlib.util.module_from_spec(spec)
spec.loader.exec_module(plan)
out, mutation, backend = sys.argv[2:]
one = {"total": 2, "passed": 2, "failed": 0, "skipped": 0, "runs": 1}
if backend == "vulkan-native":
    editor, world, server, catalog = ["projects-1"], ["projects-1"], ["rest"], ["rest"]
else:
    editor, world, server, catalog = ["projects-2"], ["rest"], ["projects-2"], ["projects-1"]
where = {"a.tests": ["rest"], "Fixture.Editor.Tests": editor,
         "KhaozEngine.Game.Tests": world.copy(), "KhaozEngine.Movement.Tests": world.copy(),
         "KhaozEngine.Server.Tests": server, "KhaozEngine.Catalog.Tests": catalog,
         "KhaozEngine.Render.Tests": [name for name, _ in plan.render_slices(backend)]}
moved = "Fixture.Editor.Tests" if backend == "vulkan-native" else "KhaozEngine.Catalog.Tests"
if mutation == "duplicate":
    where[moved].append("rest")
elif mutation == "missing":
    where[moved] = []
elif mutation == "wrong shard":
    where[moved] = ["rest"]
elif mutation == "new project moved":
    where["a.tests"] = ["projects-1"]
elif mutation == "render moved":
    where["KhaozEngine.Render.Tests"].append("projects-1")
for shard in [name for name in plan.shards(backend) if not name.startswith("render-")] + where["KhaozEngine.Render.Tests"]:
    assemblies = {name: dict(one) for name, shards in where.items() if shard in shards}
    if mutation == "twice on one" and shard == "projects-1":
        assemblies[moved]["runs"] = 2
    json.dump({"backend": backend, "shard": shard, "outcome": "success", "assemblies": assemblies},
              open(f"{out}/{backend}-{shard}.json", "w"))
PY
}
for backend in direct3d11-native vulkan-native; do
  project_reports none "$backend"
  check "$backend named assemblies once and new assemblies on rest pass" 0 \
    "$(run summarize "$backend" "$TMPROOT/reports" "$S/Fixture.slnx")"
  for mutation in duplicate missing 'wrong shard' 'new project moved' 'render moved' 'twice on one'; do
    project_reports "$mutation" "$backend"
    check "$backend project coverage refuses $mutation" 1 "$(run summarize "$backend" "$TMPROOT/reports" "$S/Fixture.slnx")"
  done
done

echo "build configuration: transitive references must stay in Release"
_rc=0
sh "$here/gpu-shard-build.test.sh" > "$OUTFILE" 2>&1 || _rc=$?
check "the root and transitive references build in Release" 0 "$_rc"

echo "gpu-test-shards.test: $pass passed, $fail failed"
[ "$fail" -eq 0 ]
