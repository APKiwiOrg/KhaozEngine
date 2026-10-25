#!/bin/sh
# Bounded command fixtures. No build, full test, determinism workload or network is executed.
set -eu
root=$(CDPATH='' cd -- "$(dirname -- "$0")/../.." && pwd)
task_tmp=$(mktemp -d)
trap 'rm -rf "$task_tmp"' EXIT
# Supply a fresh checkout without inheriting the caller's build outputs or losing local edits.
# Copy before installing command stubs so Git reads the invoking checkout's tracked file list.
python3 - "$root" "$task_tmp/source" <<'PY'
import pathlib, shutil, subprocess, sys

source, sandbox = map(pathlib.Path, sys.argv[1:])
tracked = subprocess.check_output(['git', 'ls-files', '-z'], cwd=source).decode().split('\0')
for relative in tracked:
    if relative.startswith(('scripts/', '.github/workflows/')) or relative.endswith(('.slnx', '.csproj')):
        destination = sandbox / relative
        destination.parent.mkdir(parents=True, exist_ok=True)
        shutil.copy2(source / relative, destination)
PY
root="$task_tmp/source"
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
check_selected_only() {
  if [ "$(sed -n '1p' "$PREREQUISITE_COMMANDS")" != "dotnet build $1 -c Release --no-restore${2:-}" ] \
    || [ "$(wc -l < "$PREREQUISITE_COMMANDS" | tr -d ' ')" -ne 1 ]; then
    echo "unrelated selection built more than its selected target: $1" >&2
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

# The production guard must still reject Debug outputs inside the disposable checkout.
mkdir -p "$root/bin/Debug"
: > "$PREREQUISITE_COMMANDS"
if sh scripts/gpu-shard-build.sh KhaozEngine.slnx > "$task_tmp/debug-output" 2>&1; then
  echo 'native shard accepted Debug output' >&2
  exit 1
fi
grep -q 'A Release shard produced Debug output' "$task_tmp/debug-output"
check_verifier_after_target "$root/KhaozEngine.slnx" ' -p:ShouldUnsetParentConfigurationAndPlatform=false'
rmdir "$root/bin/Debug" "$root/bin"
printf 'native shard refuses Debug output inside the disposable checkout\n'

# A canonical basename outside this checkout must still inspect its actual solution roots.
mkdir -p "$task_tmp/canonical basename"
foreign_solution="$task_tmp/canonical basename/KhaozEngine.slnx"
python3 scripts/gpu-test-shards.py build-solution metal-native rest KhaozEngine.slnx "$foreign_solution"
foreign_relative=$(python3 -c 'import os, sys; print(os.path.relpath(sys.argv[1]))' "$foreign_solution")
for target in "$foreign_solution" "$foreign_relative"; do
  : > "$PREREQUISITE_COMMANDS"
  sh scripts/build-release-tests.sh "$target" --no-restore
  check_selected_only "$target"
done
: > "$PREREQUISITE_COMMANDS"
sh scripts/gpu-shard-build.sh "$foreign_solution"
check_selected_only "$foreign_solution" ' -p:ShouldUnsetParentConfigurationAndPlatform=false'
: > "$PREREQUISITE_COMMANDS"
(cd "$task_tmp" && sh "$root/scripts/gpu-shard-build.sh" 'canonical basename/KhaozEngine.slnx')
check_selected_only "$foreign_solution" ' -p:ShouldUnsetParentConfigurationAndPlatform=false'
printf 'foreign canonical-basename solution keeps its unrelated roots on all supported routes\n'

# The same filename can legitimately select this checkout's Render.Tests through its actual roots.
python3 scripts/gpu-test-shards.py build-solution metal-native render-2 KhaozEngine.slnx "$foreign_solution"
for target in "$foreign_solution" "$foreign_relative"; do
  : > "$PREREQUISITE_COMMANDS"
  sh scripts/build-release-tests.sh "$target" --no-restore
  check_verifier_after_target "$target"
done
: > "$PREREQUISITE_COMMANDS"
sh scripts/gpu-shard-build.sh "$foreign_solution"
check_verifier_after_target "$foreign_solution" ' -p:ShouldUnsetParentConfigurationAndPlatform=false'
: > "$PREREQUISITE_COMMANDS"
(cd "$task_tmp" && sh "$root/scripts/gpu-shard-build.sh" 'canonical basename/KhaozEngine.slnx')
check_verifier_after_target "$foreign_solution" ' -p:ShouldUnsetParentConfigurationAndPlatform=false'
printf 'foreign canonical-basename solution with owned Render.Tests roots produces its verifier\n'

mkdir -p "$task_tmp/foreign project/KhaozEngine.Render.Tests"
foreign_project="$task_tmp/foreign project/KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj"
printf '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>\n' > "$foreign_project"
foreign_project_relative=$(python3 -c 'import os, sys; print(os.path.relpath(sys.argv[1]))' "$foreign_project")
for target in "$foreign_project" "$foreign_project_relative"; do
  : > "$PREREQUISITE_COMMANDS"
  sh scripts/build-release-tests.sh "$target" --no-restore
  check_selected_only "$target"
done
printf 'foreign Render.Tests project paths do not claim this checkout verifier\n'

# Ordinary canonical routes retain a shell-only fast path based on actual file identity.
mkdir -p "$task_tmp/no-python"
printf '#!/bin/sh\necho "canonical setup unexpectedly required Python" >&2\nexit 97\n' > "$task_tmp/no-python/python3"
chmod +x "$task_tmp/no-python/python3"
for target in KhaozEngine.slnx ./KhaozEngine.slnx "$root/KhaozEngine.slnx" \
  "$root/scripts/../KhaozEngine.slnx" \
  KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj \
  ./KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj \
  "$root/KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj"; do
  : > "$PREREQUISITE_COMMANDS"
  PATH="$task_tmp/no-python:$PATH" sh scripts/build-release-tests.sh "$target" --no-restore
  check_verifier_after_target "$target"
done
printf 'owned solution and Render.Tests paths produce the verifier without Python\n'

# Windows aliases are derived from the real checkout only on a native Windows shell.
case "${OS:-}" in
  Windows_NT)
    native_root_forward=$(cygpath -am "$root")
    native_root_backslash=$(cygpath -aw "$root")
    for target in "$native_root_forward/KhaozEngine.slnx" "$native_root_backslash\\KhaozEngine.slnx" \
      "$native_root_forward/KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj" \
      "$native_root_backslash\\KhaozEngine.Render.Tests\\KhaozEngine.Render.Tests.csproj"; do
      : > "$PREREQUISITE_COMMANDS"
      PATH="$task_tmp/no-python:$PATH" sh scripts/build-release-tests.sh "$target" --no-restore
      check_verifier_after_target "$target"
    done
    printf 'native Windows aliases identify the actual checkout files\n'
    ;;
  *) printf 'native Windows alias checks require a Windows shell and were not executed\n' ;;
esac

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
