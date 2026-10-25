#!/bin/sh
# Bounded command fixtures. No build, full test, determinism workload or network is executed.
set -eu
root=$(CDPATH= cd -- "$(dirname -- "$0")/../.." && pwd)
task_tmp=$(mktemp -d)
trap 'rm -rf "$task_tmp"' EXIT
mkdir -p "$task_tmp/bin"
export PREREQUISITE_COMMANDS="$task_tmp/commands"
cat > "$task_tmp/bin/dotnet" <<'STUB'
#!/bin/sh
printf '%s\n' "dotnet $*" >> "$PREREQUISITE_COMMANDS"
if [ "$1 $2 $3" = 'tool run dotnet-affected' ]; then
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
case "$1" in cat-file) exit 0 ;; diff) printf '%s\n' KhaozEngine.Render.Tests/Gpu/RendererPerf/RendererPerfUnit.cs ;; *) exit 2 ;; esac
STUB
chmod +x "$task_tmp/bin/dotnet" "$task_tmp/bin/git"
export PATH="$task_tmp/bin:$PATH"
cd "$root"
run_route() {
  : > "$PREREQUISITE_COMMANDS"
  bash scripts/ci-selective-test.sh "$@" > "$task_tmp/output" 2>&1
}
check_verifier_before_test() {
  awk '/dotnet build tools\/Renderer.Perf.Verify\/Renderer.Perf.Verify.csproj -c Release/{v++} /dotnet test/{if(v!=1)exit 1;t++} END{if(v!=1||!t)exit 1}' "$PREREQUISITE_COMMANDS"
}
# Execute the actual workflow's full build setup with command stubs too.
workflow_build=$(awk '/- name: Build \(full\)/ {found=1;next} found && /run:/ {sub(/^.*run: /, "");print;exit}' .github/workflows/ci.yml)
[ -n "$workflow_build" ]
: > "$PREREQUISITE_COMMANDS"
sh -c "$workflow_build"
[ "$(grep -c 'dotnet build tools/Renderer.Perf.Verify/Renderer.Perf.Verify.csproj -c Release' "$PREREQUISITE_COMMANDS")" -eq 1 ]
printf 'workflow full build produces verifier\n'
run_route
check_verifier_before_test
printf 'full route produces verifier before tests\n'
export PREREQUISITE_AFFECTED=KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj
run_route base head
check_verifier_before_test
[ "$(grep -c '^dotnet test ' "$PREREQUISITE_COMMANDS")" -eq 1 ]
printf 'selected Render.Tests route produces verifier without broadening test selection\n'
export PREREQUISITE_AFFECTED=KhaozEngine.Attachments.Tests/KhaozEngine.Attachments.Tests.csproj
run_route base head
! grep -q Renderer.Perf.Verify "$PREREQUISITE_COMMANDS"
[ "$(grep -c '^dotnet test ' "$PREREQUISITE_COMMANDS")" -eq 1 ]
printf 'unrelated selected route keeps its original slice\n'
export PREREQUISITE_AFFECTED=KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj
export PREREQUISITE_FAIL=37
if run_route base head; then echo 'prerequisite failure was ignored' >&2; exit 1; fi
! grep -q '^dotnet test ' "$PREREQUISITE_COMMANDS"
printf 'failed prerequisite refuses tests\n'
