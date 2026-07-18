#!/usr/bin/env bash
#
# Selective CI test runner: build and test only the slice a push or PR actually affects.
#
# Called by .github/workflows/ci.yml for the push (main) and pull_request events. Tag pushes and
# workflow_dispatch keep the full restore/build/test/pack/publish sequence inline in the workflow and
# do NOT call this script. Spec: docs/design/CI-SELECTIVE-TESTS-DESIGN-2026-07-18.md (Decisions 4, 5).
#
# Usage: scripts/ci-selective-test.sh <BASE_SHA> <HEAD_SHA>
#   BASE_SHA  commit to diff from (github.event.before on pushes, the PR base sha on PRs)
#   HEAD_SHA  commit to diff to   (github.sha)
#
# Two outcomes:
#   FULL       restore + build + full test + determinism double-pass over the whole solution. Fired
#              when the base sha is missing or unusable, or when the diff touches an infra file class
#              that can change what "affected" means. No pack here (tag runs still pack and publish).
#   SELECTIVE  dotnet-affected walks the project graph, the affected set is intersected with the test
#              projects in KhaozEngine.slnx, and only those are restored, built, and tested. An empty
#              intersection (the common docs-only push) skips build and test entirely.
#
set -euo pipefail
cd "$(dirname "$0")/.."

BASE=${1-}
HEAD=${2-}

SLNX="KhaozEngine.slnx"
ZERO_SHA="0000000000000000000000000000000000000000"
LIVE_SOCKET_FILTER="Category!=LiveSocket"
FOUNDATION_TESTS="KhaozEngine.Foundation.Tests/KhaozEngine.Foundation.Tests.csproj"
# dotnet-affected's exit code for "no affected projects were found". It is not an error, it is the
# docs-only fast path, so the script maps it to a clean skip rather than a failure.
AFFECTED_NONE_RC=166

fail() {
  echo "ci-selective-test: $1" >&2
  exit 1
}

# The full path: everything a tag run does, minus pack and publish.
run_full() {
  echo "ci-selective-test: FULL ($1)"
  dotnet restore || fail "restore failed"
  dotnet build -c Release --no-restore || fail "build failed"
  dotnet test -c Release --no-build --filter "$LIVE_SOCKET_FILTER" || fail "test failed"
  # Determinism guard: the FP-scope tests must stay byte-identical under both JIT tiering modes. The
  # DeterministicFp tests live in KhaozEngine.Foundation.Tests, so the pass is scoped to that project
  # (the workflow's tag path scopes it the same way).
  for tc in 0 1; do
    echo "DOTNET_TieredCompilation=$tc"
    DOTNET_TieredCompilation="$tc" dotnet test "$FOUNDATION_TESTS" -c Release --no-build \
      --filter "FullyQualifiedName~DeterministicFp" || fail "determinism (tiering=$tc) failed"
  done
}

# Force-FULL file classes. A change to any of these can invalidate the selective diff, so the whole
# solution is validated instead. $1 is a file holding the changed paths, one per line.
touches_full_class() {
  grep -qE '^\.github/workflows/|^scripts/|^Directory\.Build\.props$|^nuget\.config$|\.slnx$|^\.config/dotnet-tools\.json$' "$1"
}

# FULL fallback guards on the base sha, checked before any diff is attempted.
if [ -z "$BASE" ]; then
  run_full "no base sha"
  exit 0
fi
if [ "$BASE" = "$ZERO_SHA" ]; then
  run_full "all-zero base sha (new branch or first push)"
  exit 0
fi
if ! git cat-file -e "${BASE}^{commit}" 2>/dev/null; then
  run_full "base sha ${BASE} not in history (shallow clone or force-push)"
  exit 0
fi

TMP=$(mktemp -d)
trap 'rm -rf "$TMP"' EXIT

git diff --name-only "$BASE" "$HEAD" > "$TMP/changed.txt" || fail "git diff ${BASE}..${HEAD} failed"
if touches_full_class "$TMP/changed.txt"; then
  run_full "diff touches an infra file class (workflow, script, build props, nuget config, slnx, or tool manifest)"
  exit 0
fi

# Selective path.
dotnet tool restore || fail "dotnet tool restore failed"

# dotnet-affected returns AFFECTED_NONE_RC when nothing is affected, so its exit code is captured
# rather than left to abort under set -e. The text format is written to the temp dir, never the repo
# root, so no artifact is left behind.
set +e
dotnet tool run dotnet-affected -- --from "$BASE" --to "$HEAD" \
  --format text --output-dir "$TMP" --output-name affected
affected_rc=$?
set -e

if [ "$affected_rc" -eq "$AFFECTED_NONE_RC" ]; then
  echo "selective: no affected test projects, skipping build/test"
  exit 0
fi
if [ "$affected_rc" -ne 0 ]; then
  fail "dotnet affected exited ${affected_rc}"
fi

# Intersect the affected project list with the slnx test projects. The two globs *.Tests.csproj and
# *Tests.csproj select every real test assembly and, by name, leave out the TestSupport helper libs.
grep -oE 'Path="[^"]+"' "$SLNX" | sed -E 's/^Path="(.*)"$/\1/' > "$TMP/slnx_all.txt"
: > "$TMP/slnx_tests.txt"
while IFS= read -r path; do
  base=${path##*/}
  case "$base" in
    *.Tests.csproj|*Tests.csproj) printf '%s\n' "$path" >> "$TMP/slnx_tests.txt" ;;
  esac
done < "$TMP/slnx_all.txt"

# A test project is affected when an affected line ends with "/<relpath>". dotnet-affected prints
# absolute paths and the slnx holds repo-relative ones, so the suffix match bridges the two without
# caring how either tool spells the repo root.
: > "$TMP/affected_tests.txt"
while IFS= read -r rel; do
  if grep -qF -- "/$rel" "$TMP/affected.txt"; then
    printf '%s\n' "$rel" >> "$TMP/affected_tests.txt"
  fi
done < "$TMP/slnx_tests.txt"

if [ ! -s "$TMP/affected_tests.txt" ]; then
  echo "selective: no affected test projects, skipping build/test"
  exit 0
fi

echo "selective: affected test projects:"
cat "$TMP/affected_tests.txt"

# Restore, then build, then test each affected test project. Building a test project pulls in exactly
# its (minimal) project references, which is the whole point of the split.
while IFS= read -r proj; do
  echo "selective restore: $proj"
  dotnet restore "$proj" || fail "restore failed for $proj"
done < "$TMP/affected_tests.txt"

while IFS= read -r proj; do
  echo "selective build: $proj"
  dotnet build "$proj" -c Release --no-restore || fail "build failed for $proj"
done < "$TMP/affected_tests.txt"

while IFS= read -r proj; do
  echo "selective test: $proj"
  dotnet test "$proj" -c Release --no-build --filter "$LIVE_SOCKET_FILTER" || fail "test failed for $proj"
done < "$TMP/affected_tests.txt"

# Determinism double-pass, only when the DeterministicFp owner is itself in the affected set.
if grep -qF "$FOUNDATION_TESTS" "$TMP/affected_tests.txt"; then
  for tc in 0 1; do
    echo "DOTNET_TieredCompilation=$tc"
    DOTNET_TieredCompilation="$tc" dotnet test "$FOUNDATION_TESTS" -c Release --no-build \
      --filter "FullyQualifiedName~DeterministicFp" || fail "determinism (tiering=$tc) failed"
  done
fi

echo "selective: done"
