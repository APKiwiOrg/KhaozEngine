#!/bin/sh
# Bounded command fixtures. No build, full test, determinism workload or network is executed.
set -eu
root=$(CDPATH='' cd -- "$(dirname -- "$0")/../.." && pwd)
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
check_verifier_after_target() {
  if [ "$(sed -n '1p' "$PREREQUISITE_COMMANDS")" != "dotnet build $1 -c Release --no-restore${2:-}" ] \
    || [ "$(sed -n '2p' "$PREREQUISITE_COMMANDS")" != 'dotnet build tools/Renderer.Perf.Verify/Renderer.Perf.Verify.csproj -c Release' ] \
    || [ "$(wc -l < "$PREREQUISITE_COMMANDS" | tr -d ' ')" -ne 2 ]; then
    echo "missing or out-of-order verifier build after $1" >&2
    cat "$PREREQUISITE_COMMANDS" >&2
    return 1
  fi
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
unset PREREQUISITE_FAIL

# Execute the native workflow setup, so bypassing the shared prerequisite fails without a GPU run.
native_build=$(awk '
  /- name: Build the shard in Release/ {found=1;next}
  found && /^        run: \|/ {body=1;next}
  body && /^          / {sub(/^          /, "");print;next}
  body {exit}
' .github/workflows/cross-platform-gpu.yml)
[ -n "$native_build" ]
export SHARD='' BUILD_TARGET=KhaozEngine.slnx
: > "$PREREQUISITE_COMMANDS"
bash -e -c "$native_build"
check_verifier_after_target "$BUILD_TARGET"
printf 'native unsharded setup builds its verifier after the solution\n'

sync_build=$(awk '/^  gpu-vulkan-sync:/ {found=1} found && /- run: .*build/ {sub(/^.*run: /, "");print;exit}' .github/workflows/cross-platform-gpu.yml)
[ -n "$sync_build" ]
: > "$PREREQUISITE_COMMANDS"
bash -e -c "$sync_build"
check_verifier_after_target KhaozEngine.slnx
printf 'native sync setup builds its verifier after the solution\n'

# Full paths from Unix shells, Windows dotnet tools and Git Bash keep the selected build unchanged.
for target in KhaozEngine.slnx ./KhaozEngine.slnx "$root/KhaozEngine.slnx" \
  'D:/a/KhaozEngine/KhaozEngine/KhaozEngine.slnx' 'D:\a\KhaozEngine\KhaozEngine\KhaozEngine.slnx' \
  KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj \
  ./KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj \
  "$root/KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj" \
  'D:/a/KhaozEngine/KhaozEngine/KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj' \
  'D:\a\KhaozEngine\KhaozEngine\KhaozEngine.Render.Tests\KhaozEngine.Render.Tests.csproj'; do
  : > "$PREREQUISITE_COMMANDS"
  sh scripts/build-release-tests.sh "$target" --no-restore
  check_verifier_after_target "$target"
done
printf 'solution and Render.Tests paths retain their arguments and produce the verifier\n'

# Reduced solutions must build the verifier only when they actually include this repo's Render.Tests.
export SHARD=render-2 BUILD_TARGET="$task_tmp/render.slnx"
python3 scripts/gpu-test-shards.py build-solution metal-native "$SHARD" KhaozEngine.slnx "$BUILD_TARGET"
: > "$PREREQUISITE_COMMANDS"
bash -e -c "$native_build"
check_verifier_after_target "$BUILD_TARGET" ' -p:ShouldUnsetParentConfigurationAndPlatform=false'
printf 'native Render.Tests shard builds its verifier and preserves Release propagation\n'
export SHARD=rest BUILD_TARGET="$task_tmp/other.slnx"
python3 scripts/gpu-test-shards.py build-solution metal-native "$SHARD" KhaozEngine.slnx "$BUILD_TARGET"
: > "$PREREQUISITE_COMMANDS"
bash -e -c "$native_build"
! grep -q Renderer.Perf.Verify "$PREREQUISITE_COMMANDS"
[ "$(wc -l < "$PREREQUISITE_COMMANDS" | tr -d ' ')" -eq 1 ]
printf 'native unrelated shard keeps its original build slice\n'

export BUILD_TARGET="$task_tmp/windows-rest.slnx"
python3 scripts/gpu-test-shards.py build-solution direct3d11-native "$SHARD" KhaozEngine.slnx "$BUILD_TARGET"
: > "$PREREQUISITE_COMMANDS"
bash -e -c "$native_build"
check_verifier_after_target "$BUILD_TARGET" ' -p:ShouldUnsetParentConfigurationAndPlatform=false'
printf 'native rest shard that also owns Render.Tests builds its verifier\n'

export SHARD=render-2 BUILD_TARGET="$task_tmp/render.slnx" PREREQUISITE_FAIL=37
if bash -e -c "$native_build"; then echo 'native prerequisite failure was ignored' >&2; exit 1; fi
printf 'native failed prerequisite refuses subsequent steps\n'
