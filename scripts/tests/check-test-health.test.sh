#!/bin/sh
# Canned build logs exercise the baseline ratchet without building the solution.
set -eu

here=$(CDPATH='' cd -- "$(dirname -- "$0")" && pwd)
SCRIPT=${1:-$here/../check-test-health.sh}
case "$SCRIPT" in /*) ;; *) SCRIPT=$(CDPATH='' cd -- "$(dirname -- "$SCRIPT")" && pwd)/$(basename -- "$SCRIPT") ;; esac

if [ ! -f "$SCRIPT" ]; then
  echo "check-test-health.test: script under test not found at $SCRIPT" >&2
  exit 2
fi

TMPROOT=$(mktemp -d "${TMPDIR:-/tmp}/khaozengine-test-health.XXXXXX")
trap 'rm -rf "$TMPROOT"' 0
trap 'exit 2' HUP INT TERM
OUTFILE="$TMPROOT/out"

pass=0
fail=0
check() { # name, expected exit, actual exit
  if [ "$2" = "$3" ]; then pass=$((pass+1)); echo "  ok    $1"
  else fail=$((fail+1)); echo "  FAIL  $1 (expected exit $2, got $3)"; sed 's/^/        /' "$OUTFILE"; fi
}
run() {
  set +e
  TESTHEALTH_BUILD_LOG="$PWD/build.log" sh "$SCRIPT" "$@" >"$OUTFILE" 2>&1
  rc=$?
  set -e
}
# run_built <dotnet exit code> <args>: the script's own build path, with a stub dotnet that prints build.log.
run_built() {
  stub="$TMPROOT/stub $case_no"
  mkdir -p "$stub"
  printf '#!/bin/sh\ncat "%s"\nexit %s\n' "$PWD/build.log" "$1" >"$stub/dotnet"
  chmod +x "$stub/dotnet"
  shift
  set +e
  TESTHEALTH_BUILD_LOG= PATH="$stub:$PATH" sh "$SCRIPT" "$@" >"$OUTFILE" 2>&1
  rc=$?
  set -e
}
said() { if ! grep -qF -- "$1" "$OUTFILE"; then rc="$rc, without '$1'"; fi; }
baseline_said() { if ! grep -qF -- "$1" .testhealth-baseline; then rc="$rc, baseline without '$1'"; fi; }
unchanged() { if ! cmp -s before .testhealth-baseline; then rc="$rc, baseline changed"; fi; }
entries_match() {
  sed -n '/^[0-9]/p' .testhealth-baseline >actual
  if ! cmp -s expected actual; then
    rc="$rc, incorrect entries"
    cat actual >>"$OUTFILE"
  fi
}
case_no=0
fresh() {
  case_no=$((case_no+1))
  mkdir "$TMPROOT/case $case_no"
  cd "$TMPROOT/case $case_no"
  : >build.log
}

# Sort the entire path before the rule, retain spaces, and ignore other diagnostics and prose.
fresh
cat >build.log <<'LOG'
/src/Z Tests/Z.cs(12,5): warning KETEST002: 'Z Tests/Z.cs' has 2 non-public reflection access(es), .testhealth-baseline allows 0. Test through the public API, or an internal seam with InternalsVisibleTo. [/src/Z Tests/Z.csproj]
/src/B Tests/B.cs(12,5): warning KETEST003: 'B Tests/B.cs' has 5 test(s) whose assertions all run inside a loop that may be empty, .testhealth-baseline allows 0. Assert the expected count or non-emptiness before the loop so an empty result fails. [/src/B Tests/B.csproj]
/src/A Tests/A.cs(12,5): error KETEST002: 'A Tests/A.cs' has 4 non-public reflection access(es), .testhealth-baseline allows 0. Test through the public API, or an internal seam with InternalsVisibleTo. [/src/A Tests/A.csproj]
/src/A Tests/A.cs(18,5): warning KETEST001: 'A Tests/A.cs' has 3 exception message assertion(s), .testhealth-baseline allows 0. Assert the exception type and a structured member (parameter name, error code, status) instead of message wording. [/src/A Tests/A.csproj]
/src/Other.cs(1,1): warning KETEST004: 'Other.cs' has 9 unrelated finding(s), .testhealth-baseline allows 0. [/src/Other.csproj]
Unrelated prose says KETEST001: 'Other.cs' has 99 exception message assertion(s).
LOG
cat >expected <<'ENTRIES'
3 KETEST001 A Tests/A.cs
4 KETEST002 A Tests/A.cs
2 KETEST002 Z Tests/Z.cs
ENTRIES
run --init
entries_match
baseline_said 'may only shrink'
baseline_said 'exempt KETEST001 path/to/File.cs'
baseline_said 'build-slot --heavy --label testhealth-update -- sh scripts/check-test-health.sh --update'
check 'init writes sorted entries and skips report-only KETEST003' 0 "$rc"

fresh
: >.testhealth-baseline
run --init
: >expected
entries_match
check 'init accepts an empty existing file' 0 "$rc"

fresh
cat >.testhealth-baseline <<'BASELINE'
# Keep the structural guard exemption.
  exempt KETEST002 Tests/Structural Guard.cs
BASELINE
cp .testhealth-baseline before
run --init
tail -n 2 .testhealth-baseline >preserved
if ! cmp -s before preserved; then rc="$rc, comments or exemptions changed"; fi
case "$(head -n 1 .testhealth-baseline)" in '#'*test-health*) ;; *) rc="$rc, missing header above existing lines" ;; esac
check 'init preserves comments and exemptions beneath its header' 0 "$rc"

fresh
printf '3 KETEST001 Tests/A.cs\n' >.testhealth-baseline
cp .testhealth-baseline before
run --init
unchanged
check 'init refuses a baseline with entries' 1 "$rc"

fresh
printf '5 KETEST001 Tests/With Spaces.cs\n' >.testhealth-baseline
cat >build.log <<'LOG'
/src/Tests/With Spaces.cs(12,5): warning KETEST001: 'Tests/With Spaces.cs' has 3 exception message assertion(s), .testhealth-baseline allows 0. Assert the exception type and a structured member (parameter name, error code, status) instead of message wording. [/src/Tests/Tests.csproj]
LOG
printf '3 KETEST001 Tests/With Spaces.cs\n' >expected
run --update
entries_match
check 'update lowers a count' 0 "$rc"

fresh
printf '5 KETEST001 Tests/Gone.cs\n2 KETEST002 Tests/Still.cs\n' >.testhealth-baseline
cat >build.log <<'LOG'
/src/Tests/Still.cs(12,5): warning KETEST002: 'Tests/Still.cs' has 2 non-public reflection access(es), .testhealth-baseline allows 0. Test through the public API, or an internal seam with InternalsVisibleTo. [/src/Tests/Tests.csproj]
LOG
printf '2 KETEST002 Tests/Still.cs\n' >expected
run --update
entries_match
check 'update drops a zero entry' 0 "$rc"

fresh
cat >.testhealth-baseline <<'BASELINE'
# Test-health baseline, with intentional whitespace.

  # Structural guard needs private members.
  exempt KETEST002 Tests/Structural Guard.cs

5 KETEST001 Tests/A.cs
# End comment.
BASELINE
cat >build.log <<'LOG'
/src/Tests/A.cs(12,5): warning KETEST001: 'Tests/A.cs' has 3 exception message assertion(s), .testhealth-baseline allows 0. Assert the exception type and a structured member (parameter name, error code, status) instead of message wording. [/src/Tests/Tests.csproj]
LOG
cat >expected <<'BASELINE'
# Test-health baseline, with intentional whitespace.

  # Structural guard needs private members.
  exempt KETEST002 Tests/Structural Guard.cs

3 KETEST001 Tests/A.cs
# End comment.
BASELINE
run --update
if ! cmp -s expected .testhealth-baseline; then rc="$rc, comments or exemptions changed"; fi
check 'update keeps exempt and comment lines' 0 "$rc"

fresh
printf '2 KETEST001 Tests/Growing.cs\n' >.testhealth-baseline
cp .testhealth-baseline before
cat >build.log <<'LOG'
/src/Tests/Growing.cs(12,5): warning KETEST001: 'Tests/Growing.cs' has 3 exception message assertion(s), .testhealth-baseline allows 0. Assert the exception type and a structured member (parameter name, error code, status) instead of message wording. [/src/Tests/Tests.csproj]
LOG
run --update
said 'Tests/Growing.cs'
unchanged
check 'update refuses growth' 1 "$rc"

fresh
printf '# No debt yet.\n' >.testhealth-baseline
cp .testhealth-baseline before
cat >build.log <<'LOG'
/src/Tests/New.cs(12,5): error KETEST002: 'Tests/New.cs' has 1 non-public reflection access(es), .testhealth-baseline allows 0. Test through the public API, or an internal seam with InternalsVisibleTo. [/src/Tests/Tests.csproj]
LOG
run --update
said 'KETEST002'
said 'Tests/New.cs'
unchanged
check 'update refuses a new pair' 1 "$rc"

fresh
printf '3 KETEST001 Tests/A.cs\n' >.testhealth-baseline
cp .testhealth-baseline before
run --update
said 'incremental'
unchanged
check 'update refuses an empty log over a populated baseline' 2 "$rc"

fresh
printf '3 KETEST001 Tests/A.cs\n' >.testhealth-baseline
cp .testhealth-baseline before
run --preview
said 'incremental'
unchanged
check 'preview refuses an empty log over a populated baseline' 2 "$rc"

fresh
cat >build.log <<'LOG'
/src/test-shared/Shared.cs(12,5): warning KETEST001: 'test-shared/Shared.cs' has 3 exception message assertion(s), .testhealth-baseline allows 0. Assert the exception type and a structured member (parameter name, error code, status) instead of message wording. [/src/A/A.csproj]
/src/test-shared/Shared.cs(18,5): warning KETEST001: 'test-shared/Shared.cs' has 3 exception message assertion(s), .testhealth-baseline allows 0. Assert the exception type and a structured member (parameter name, error code, status) instead of message wording. [/src/B/B.csproj]
LOG
printf '3 KETEST001 test-shared/Shared.cs\n' >expected
run --init
entries_match
check 'a test-shared path seen from two projects counts once' 0 "$rc"

fresh
cat >build.log <<'LOG'
/src/test-shared/Shared.cs(12,5): warning KETEST002: 'test-shared/Shared.cs' has 2 non-public reflection access(es), .testhealth-baseline allows 0. Test through the public API, or an internal seam with InternalsVisibleTo. [/src/A/A.csproj]
/src/test-shared/Shared.cs(18,5): warning KETEST002: 'test-shared/Shared.cs' has 5 non-public reflection access(es), .testhealth-baseline allows 0. Test through the public API, or an internal seam with InternalsVisibleTo. [/src/B/B.csproj]
/src/test-shared/Shared.cs(24,5): warning KETEST002: 'test-shared/Shared.cs' has 1 non-public reflection access(es), .testhealth-baseline allows 0. Test through the public API, or an internal seam with InternalsVisibleTo. [/src/C/C.csproj]
LOG
printf '5 KETEST002 test-shared/Shared.cs\n' >expected
run --init
entries_match
check 'different project counts keep the maximum' 0 "$rc"

fresh
printf '# Keep me.\n5 KETEST001 Tests/A.cs\n2 KETEST002 Tests/Gone.cs\n' >.testhealth-baseline
cp .testhealth-baseline before
cat >build.log <<'LOG'
/src/Tests/A.cs(12,5): warning KETEST001: 'Tests/A.cs' has 3 exception message assertion(s), .testhealth-baseline allows 0. Assert the exception type and a structured member (parameter name, error code, status) instead of message wording. [/src/Tests/Tests.csproj]
LOG
run --preview
said 'Tests/A.cs'
said 'Tests/Gone.cs'
unchanged
check 'preview writes nothing' 0 "$rc"

fresh
printf '1 KETEST001 Tests/A.cs\n' >.testhealth-baseline
cp .testhealth-baseline before
cat >build.log <<'LOG'
/src/Tests/A.cs(12,5): warning KETEST001: 'Tests/A.cs' has 3 exception message assertion(s), .testhealth-baseline allows 0. Assert the exception type and a structured member (parameter name, error code, status) instead of message wording. [/src/Tests/Tests.csproj]
/src/Tests/B.cs(12,5): warning KETEST002: 'Tests/B.cs' has 2 non-public reflection access(es), .testhealth-baseline allows 0. Test through the public API, or an internal seam with InternalsVisibleTo. [/src/Tests/Tests.csproj]
LOG
run --preview
said 'Tests/A.cs'
said 'Tests/B.cs'
unchanged
check 'preview reports every growing or missing pair' 1 "$rc"

fresh
printf 'exempt KETEST002 Tests/Guard.cs\n' >.testhealth-baseline
cp .testhealth-baseline before
cat >build.log <<'LOG'
/src/Tests/Guard.cs(12,5): warning KETEST002: 'Tests/Guard.cs' has 2 non-public reflection access(es), .testhealth-baseline allows 0. Test through the public API, or an internal seam with InternalsVisibleTo. [/src/Tests/Tests.csproj]
LOG
run --update
unchanged
check 'exempt pairs do not become new debt' 0 "$rc"

fresh
run --update
said '--init'
check 'update requires an existing baseline' 2 "$rc"

fresh
run --preview
if [ -e .testhealth-baseline ]; then rc="$rc, baseline created"; fi
check 'preview does not create a missing baseline' 2 "$rc"

fresh
run --nonsense
check 'unknown mode exits 2' 2 "$rc"

# The script's own non-incremental build is trusted: zero findings means the last debt is gone.
fresh
printf '# keep me\nexempt KETEST002 Tests/Guard.cs\n3 KETEST001 Tests/A.cs\n2 KETEST002 Tests/B.cs\n' >.testhealth-baseline
cat >build.log <<'LOG'
/src/Tests/C.cs(12,5): warning KETEST003: 'Tests/C.cs' has 1 test(s) whose assertions all run inside a loop that may be empty, .testhealth-baseline allows 0. Assert the expected count or non-emptiness before the loop so an empty result fails. [/src/Tests/Tests.csproj]
Build succeeded.
LOG
: >expected
run_built 0 --update
entries_match
baseline_said '# keep me'
baseline_said 'exempt KETEST002 Tests/Guard.cs'
check 'update after its own build clears the last debt' 0 "$rc"

fresh
printf '3 KETEST001 Tests/A.cs\n' >.testhealth-baseline
cp .testhealth-baseline before
printf 'Build succeeded.\n' >build.log
run_built 0 --preview
said 'Tests/A.cs'
unchanged
check 'preview after its own build reports the clearing without writing' 0 "$rc"

fresh
printf '3 KETEST001 Tests/A.cs\n' >.testhealth-baseline
cp .testhealth-baseline before
printf 'error CS1002: ; expected\n' >build.log
run_built 1 --update
said 'report build failed'
unchanged
check 'update after a failed own build leaves the baseline' 1 "$rc"

# Combined report builds must retain physical tool paths and use maximum linked-source counts.
fresh
mkdir -p tools/Renderer.Perf.Tooling.Tests
: >tools/Renderer.Perf.Tooling.Tests/Renderer.Perf.Tooling.Tests.csproj
printf '2 KETEST001 Tests/A.cs\n3 KETEST002 tools/Renderer.Perf.Tooling.Tests/Tool.cs\n5 KETEST002 test-shared/Shared.cs\n1 KETEST002 tools/NavCapture.Research.Tests/Research/NavCaptureSnapshot.cs\n' >.testhealth-baseline
cp .testhealth-baseline before
mkdir -p "$TMPROOT/combined-bin"
cat > "$TMPROOT/combined-bin/dotnet" <<'STUB'
#!/bin/sh
printf '%s\n' "$*" >>commands
case "$2" in
  KhaozEngine.slnx)
    printf "warning KETEST001: 'Tests/A.cs' has 2 assertions\nwarning KETEST002: 'test-shared/Shared.cs' has 3 accesses\n" ;;
  tools/Renderer.Perf.Tooling.Tests/Renderer.Perf.Tooling.Tests.csproj)
    printf "warning KETEST002: 'tools/Renderer.Perf.Tooling.Tests/Tool.cs' has 3 accesses\nwarning KETEST002: 'test-shared/Shared.cs' has 5 accesses\n"
    exit "${EXTRA_BUILD_EXIT:-0}" ;;
  tools/Items.Benchmark.Tests/Items.Benchmark.Tests.csproj)
    printf "warning KETEST002: 'test-shared/Shared.cs' has 4 accesses\n"
    exit "${ITEMS_BUILD_EXIT:-0}" ;;
  tools/NavCapture.Research.Tests/NavCapture.Research.Tests.csproj)
    printf "warning KETEST002: 'tools/NavCapture.Research.Tests/Research/NavCaptureSnapshot.cs' has 1 accesses\n"
    exit "${NAV_BUILD_EXIT:-0}" ;;
  *) exit 98 ;;
esac
STUB
chmod +x "$TMPROOT/combined-bin/dotnet"
set +e
TESTHEALTH_BUILD_LOG= PATH="$TMPROOT/combined-bin:$PATH" sh "$SCRIPT" --preview >"$OUTFILE" 2>&1
rc=$?
set -e
unchanged
[ "$(wc -l <commands | tr -d ' ')" -eq 4 ] || rc="$rc, extra target not built"
if grep -qE 'drop |lower ' "$OUTFILE"; then rc="$rc, combined findings lost"; fi
check 'combined report retains tool allowance and maximum linked-file count without writing' 0 "$rc"
set +e
TESTHEALTH_BUILD_LOG= EXTRA_BUILD_EXIT=39 PATH="$TMPROOT/combined-bin:$PATH" sh "$SCRIPT" --update >"$OUTFILE" 2>&1
rc=$?
set -e
said 'report build failed'
unchanged
check 'failing outside-solution target refuses baseline changes' 1 "$rc"
set +e
TESTHEALTH_BUILD_LOG= ITEMS_BUILD_EXIT=39 PATH="$TMPROOT/combined-bin:$PATH" sh "$SCRIPT" --update >"$OUTFILE" 2>&1
rc=$?
set -e
said 'report build failed for tools/Items.Benchmark.Tests/Items.Benchmark.Tests.csproj'
unchanged
check 'failing Items target refuses baseline changes' 1 "$rc"
set +e
TESTHEALTH_BUILD_LOG= NAV_BUILD_EXIT=39 PATH="$TMPROOT/combined-bin:$PATH" sh "$SCRIPT" --update >"$OUTFILE" 2>&1
rc=$?
set -e
said 'report build failed for tools/NavCapture.Research.Tests/NavCapture.Research.Tests.csproj'
unchanged
check 'failing navigation research target refuses baseline changes' 1 "$rc"

echo ""
echo "passed: $pass   failed: $fail"
[ "$fail" -eq 0 ]
