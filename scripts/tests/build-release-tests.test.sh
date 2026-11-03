#!/bin/sh
# Bounded command fixtures. No real build, test workload or network is executed.
set -eu
source=$(CDPATH='' cd -- "$(dirname -- "$0")/../.." && pwd)
task_tmp=$(mktemp -d)
trap 'rm -rf "$task_tmp"' EXIT
python3 - "$source" "$task_tmp/source" <<'PY'
import pathlib, shutil, subprocess, sys
source, sandbox = map(pathlib.Path, sys.argv[1:])
for relative in subprocess.check_output(['git', 'ls-files', '-z'], cwd=source).decode().split('\0'):
    if relative.startswith(('scripts/', '.github/workflows/')) or relative.endswith(('.slnx', '.csproj')):
        path = source / relative
        if path.is_file():
            destination = sandbox / relative
            destination.parent.mkdir(parents=True, exist_ok=True)
            shutil.copy2(path, destination)
PY
root="$task_tmp/source"
tool=tools/Renderer.Perf.Tooling.Tests/Renderer.Perf.Tooling.Tests.csproj
mkdir -p "$root/$(dirname "$tool")" "$task_tmp/bin"
# The fixture also runs before the move creates the real project.
[ -f "$root/$tool" ] || printf '<Project />\n' > "$root/$tool"
export PREREQUISITE_COMMANDS="$task_tmp/commands"
cat > "$task_tmp/bin/dotnet" <<'STUB'
#!/bin/sh
printf '%s\n' "dotnet $*" >> "$PREREQUISITE_COMMANDS"
if [ "$1 $2 $3" = 'tool run dotnet-affected' ]; then
  if [ "${PREREQUISITE_AFFECTED_NONE:-0}" = 1 ]; then exit 166; fi
  while [ "$#" -gt 0 ]; do
    if [ "$1" = --output-dir ]; then shift; out=$1; fi
    shift
  done
  printf '%s/%s\n' "$PWD" "$PREREQUISITE_AFFECTED" > "$out/affected.txt"
fi
case "$*" in *Renderer.Perf.Verify.csproj*) exit "${PREREQUISITE_FAIL:-0}" ;; esac
STUB
cat > "$task_tmp/bin/git" <<'STUB'
#!/bin/sh
case "$1" in cat-file) exit 0 ;; diff) printf '%s\n' "${PREREQUISITE_CHANGED:-tools/Renderer.Perf.Tooling.Tests/Gpu/RendererPerf/RendererPerfUnit.cs}" ;; *) exit 2 ;; esac
STUB
chmod +x "$task_tmp/bin/dotnet" "$task_tmp/bin/git"
export PATH="$task_tmp/bin:$PATH"
cd "$root"
selected_only() {
  [ "$(sed -n '1p' "$PREREQUISITE_COMMANDS")" = "dotnet build $1 -c Release --no-restore${2:-}" ]
  [ "$(wc -l < "$PREREQUISITE_COMMANDS" | tr -d ' ')" -eq 1 ]
}
with_verifier() {
  [ "$(sed -n '1p' "$PREREQUISITE_COMMANDS")" = "dotnet build $1 -c Release --no-restore" ]
  [ "$(sed -n '2p' "$PREREQUISITE_COMMANDS")" = 'dotnet build tools/Renderer.Perf.Verify/Renderer.Perf.Verify.csproj -c Release' ]
  [ "$(wc -l < "$PREREQUISITE_COMMANDS" | tr -d ' ')" -eq 2 ]
}
run_build() {
  : > "$PREREQUISITE_COMMANDS"
  sh scripts/build-release-tests.sh "$1" --no-restore
}
workflow_build=$(awk '/- name: Build \(full\)/ {found=1;next} found && /run:/ {sub(/^.*run: /, "");print;exit}' .github/workflows/ci.yml)
[ -n "$workflow_build" ]
: > "$PREREQUISITE_COMMANDS"
sh -c "$workflow_build"
! grep -q Renderer.Perf.Verify "$PREREQUISITE_COMMANDS"
printf 'workflow full build excludes tooling verifier\n'
for target in KhaozEngine.slnx ./KhaozEngine.slnx "$root/KhaozEngine.slnx" \
  KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj KhaozEngine.Attachments.Tests/KhaozEngine.Attachments.Tests.csproj; do
  run_build "$target"
  selected_only "$target"
done
printf 'engine solution and test project builds exclude tooling verifier\n'
for affected in KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj KhaozEngine.Attachments.Tests/KhaozEngine.Attachments.Tests.csproj "$tool"; do
  export PREREQUISITE_AFFECTED=$affected
  : > "$PREREQUISITE_COMMANDS"
  bash scripts/ci-selective-test.sh base head > "$task_tmp/output" 2>&1
  ! grep -q Renderer.Perf.Verify "$PREREQUISITE_COMMANDS"
  if [ "$affected" = "$tool" ]; then ! grep -q '^dotnet test ' "$PREREQUISITE_COMMANDS"
  else [ "$(grep -c '^dotnet test ' "$PREREQUISITE_COMMANDS")" -eq 1 ]; fi
done
printf 'selective engine tests stay bounded and outside-solution tooling is omitted\n'
export PREREQUISITE_CHANGED=$tool PREREQUISITE_AFFECTED=$tool
: > "$PREREQUISITE_COMMANDS"
bash scripts/ci-selective-test.sh base head > "$task_tmp/output" 2>&1
grep -q '^dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj ' "$PREREQUISITE_COMMANDS"
[ "$(grep -c '^dotnet test ' "$PREREQUISITE_COMMANDS")" -eq 1 ]
export PREREQUISITE_AFFECTED_NONE=1
: > "$PREREQUISITE_COMMANDS"
bash scripts/ci-selective-test.sh base head > "$task_tmp/output" 2>&1
grep -q '^dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj ' "$PREREQUISITE_COMMANDS"
[ "$(grep -c '^dotnet test ' "$PREREQUISITE_COMMANDS")" -eq 1 ]
unset PREREQUISITE_AFFECTED_NONE
export PREREQUISITE_CHANGED=.testhealth-baseline
: > "$PREREQUISITE_COMMANDS"
bash scripts/ci-selective-test.sh base head > "$task_tmp/output" 2>&1
grep -q 'FULL' "$task_tmp/output"
! grep -q Renderer.Perf.Verify "$PREREQUISITE_COMMANDS"
unset PREREQUISITE_CHANGED
printf 'tool csproj changes select architecture rump and baseline changes force FULL\n'
mkdir -p "$task_tmp/no-python"
printf '#!/bin/sh\nexit 97\n' > "$task_tmp/no-python/python3"
chmod +x "$task_tmp/no-python/python3"
for target in KhaozEngine.slnx ./KhaozEngine.slnx "$root/KhaozEngine.slnx" "$root/scripts/../KhaozEngine.slnx" \
  KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj; do
  : > "$PREREQUISITE_COMMANDS"
  PATH="$task_tmp/no-python:$PATH" sh scripts/build-release-tests.sh "$target" --no-restore
  selected_only "$target"
done
printf 'canonical engine setup retains its shell-only fast path\n'
for target in "$tool" "./$tool" "$root/$tool" "$root/scripts/../$tool"; do
  : > "$PREREQUISITE_COMMANDS"
  PATH="$task_tmp/no-python:$PATH" sh scripts/build-release-tests.sh "$target" --no-restore
  with_verifier "$target"
done
printf 'owned tooling paths build verifier after target without Python\n'
export PREREQUISITE_FAIL=37
: > "$PREREQUISITE_COMMANDS"
rc=0
sh scripts/build-release-tests.sh "$tool" --no-restore || rc=$?
[ "$rc" -eq 37 ]
unset PREREQUISITE_FAIL
printf 'verifier failure propagates exact exit code\n'
mkdir -p "$task_tmp/foreign project/Renderer.Perf.Tooling.Tests" "$task_tmp/canonical basename"
foreign_project="$task_tmp/foreign project/Renderer.Perf.Tooling.Tests/Renderer.Perf.Tooling.Tests.csproj"
printf '<Project />\n' > "$foreign_project"
foreign_relative=$(python3 -c 'import os,sys; print(os.path.relpath(sys.argv[1]))' "$foreign_project")
for target in "$foreign_project" "$foreign_relative"; do run_build "$target"; selected_only "$target"; done
foreign_solution="$task_tmp/canonical basename/KhaozEngine.slnx"
for project in "$foreign_project" "$root/KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj" "$root/$tool"; do
  python3 - "$foreign_solution" "$project" <<'PY'
import pathlib, sys, xml.etree.ElementTree as ET
solution, project = sys.argv[1:]
root = ET.Element('Solution')
ET.SubElement(root, 'Project', Path=project)
ET.ElementTree(root).write(solution)
PY
  relative=$(python3 -c 'import os,sys; print(os.path.relpath(sys.argv[1]))' "$foreign_solution")
  for target in "$foreign_solution" "$relative"; do
    run_build "$target"
    if [ "$project" = "$root/$tool" ]; then with_verifier "$target"; else selected_only "$target"; fi
  done
done
printf 'foreign same-basename paths inspect actual roots and never claim ownership\n'
# Windows-style separators normalize before identity and reduced-solution checks.
windows_tool=$(printf '%s' "$tool" | tr '/' '\\')
: > "$PREREQUISITE_COMMANDS"
OS=Windows_NT sh scripts/build-release-tests.sh "$windows_tool" --no-restore
with_verifier "$windows_tool"
python3 - "$foreign_solution" "$tool" "$root" <<'PY'
import sys, xml.etree.ElementTree as ET
solution, tool, root = sys.argv[1:]
node = ET.Element('Solution')
ET.SubElement(node, 'Project', Path=(root+'/'+tool).replace('/', '\\'))
ET.ElementTree(node).write(solution)
PY
run_build "$foreign_solution"
with_verifier "$foreign_solution"
printf 'Windows project and solution-root separators normalize without widening selection\n'
# Execute native workflow setup with stubs. Render shards no longer own tooling process tests.
native_build=$(awk '/- name: Build the shard in Release/ {found=1;next} found && /^        run: \|/ {body=1;next} body && /^          / {sub(/^          /, "");print;next} body {exit}' .github/workflows/cross-platform-gpu.yml)
[ -n "$native_build" ]
for shard in render-2 rest; do
  export SHARD=$shard BUILD_TARGET="$task_tmp/$shard.slnx"
  python3 scripts/gpu-test-shards.py build-solution metal-native "$shard" KhaozEngine.slnx "$BUILD_TARGET"
  : > "$PREREQUISITE_COMMANDS"
  bash -e -c "$native_build"
  selected_only "$BUILD_TARGET" ' -p:ShouldUnsetParentConfigurationAndPlatform=false'
done
export SHARD='' BUILD_TARGET=KhaozEngine.slnx
: > "$PREREQUISITE_COMMANDS"
bash -e -c "$native_build"
selected_only "$BUILD_TARGET"
printf 'native Render-only and other shards exclude verifier and retain Release propagation\n'
mkdir -p "$root/bin/Debug"
: > "$PREREQUISITE_COMMANDS"
if sh scripts/gpu-shard-build.sh KhaozEngine.slnx > "$task_tmp/debug-output" 2>&1; then exit 1; fi
grep -q 'A Release shard produced Debug output' "$task_tmp/debug-output"
rmdir "$root/bin/Debug" "$root/bin"
printf 'native shard still refuses Debug outputs\n'
