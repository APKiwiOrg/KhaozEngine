#!/bin/sh
# Exercises scripts/ci-prior-gate.sh, the lookup that lets a v* tag run of ci reuse the hosted pre-tag
# gate (a successful workflow_dispatch of ci at the same commit) instead of repeating the full test,
# the determinism pass and the SQL Server facts.
#
# The property it pins down: reuse happens only for a completed, successful workflow_dispatch run of
# this repository's ci.yml at exactly the tag's commit, and every other answer, including any API
# error or doubt about the response, fails closed to the full path.
#
# Offline by construction: a stub gh on PATH replays a fixture response (or fails like an API error)
# and records its arguments. Nothing here reaches GitHub.
#
# Run it from anywhere:  sh scripts/tests/ci-prior-gate.test.sh
set -eu

here=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
SCRIPT=$here/../ci-prior-gate.sh
[ -f "$SCRIPT" ] || { echo "ci-prior-gate.test: missing $SCRIPT" >&2; exit 2; }
command -v jq > /dev/null 2>&1 || { echo "ci-prior-gate.test: jq is required" >&2; exit 2; }

TMPROOT=$(mktemp -d)
trap 'rm -rf "$TMPROOT"' EXIT
OUTFILE=$TMPROOT/out
ARGSFILE=$TMPROOT/gh-args
mkdir -p "$TMPROOT/bin"

# The stub gh. STUB_GH_FAIL=1 behaves like an HTTP error (message on stderr, exit 1). Otherwise it
# prints the file named by STUB_GH_FIXTURE. Either way it records its arguments for the query checks.
cat > "$TMPROOT/bin/gh" <<'STUB'
#!/bin/sh
printf '%s\n' "$*" > "$STUB_GH_ARGS"
if [ "${STUB_GH_FAIL:-0}" = 1 ]; then
  echo "gh: Server Error (HTTP 502)" >&2
  exit 1
fi
cat "$STUB_GH_FIXTURE"
STUB
chmod +x "$TMPROOT/bin/gh"

REPO=APKiwiOrg/KhaozEngine
SHA=0123456789abcdef0123456789abcdef01234567
OTHER=fedcba9876543210fedcba9876543210fedcba98
unset GITHUB_ACTIONS GITHUB_STEP_SUMMARY || true

# run <name> [args...]: runs the script against fixture $TMPROOT/<name>.json with a fresh GITHUB_OUTPUT.
# Sets RC, and OUT to the GITHUB_OUTPUT file.
run() {
  _fx=$1; shift
  OUT=$TMPROOT/$_fx.output
  : > "$OUT"
  RC=0
  PATH="$TMPROOT/bin:$PATH" STUB_GH_FIXTURE="$TMPROOT/$_fx.json" STUB_GH_ARGS="$ARGSFILE" \
    GITHUB_REPOSITORY=$REPO GITHUB_OUTPUT="$OUT" sh "$SCRIPT" "$@" > "$OUTFILE" 2>&1 || RC=$?
}

pass=0
fail=0
check() { # name, expected, actual
  if [ "$2" = "$3" ]; then pass=$((pass+1)); echo "  ok    $1"
  else fail=$((fail+1)); echo "  FAIL  $1 (expected $2, got $3)"; echo "  ----- output -----"; sed 's/^/  | /' "$OUTFILE"; fi
}
out() { grep "^$1=" "$OUT" | sed "s/^$1=//"; }
lines() { wc -l < "$OUT" | tr -d ' '; }
has() { grep -qF -- "$1" "$OUTFILE" && printf yes || printf no; }

# run_json <id> <event> <status> <conclusion> <sha> [created_at] [path] [repo]: one workflow run object.
run_json() {
  printf '{"id":%s,"event":"%s","status":"%s","conclusion":%s,"head_sha":"%s","created_at":"%s","path":"%s","repository":{"full_name":"%s"},"head_repository":{"full_name":"%s"},"html_url":"https://github.com/%s/actions/runs/%s"}' \
    "$1" "$2" "$3" "$4" "$5" "${6:-2026-10-17T10:00:00Z}" "${7:-.github/workflows/ci.yml}" "${8:-$REPO}" "${8:-$REPO}" "${8:-$REPO}" "$1"
}
# fixture <name> <total_count> [run objects...]
fixture() {
  _name=$1; _total=$2; shift 2
  _runs=$(printf '%s\n' "$@" | paste -sd, -)
  printf '{"total_count":%s,"workflow_runs":[%s]}\n' "$_total" "$_runs" > "$TMPROOT/$_name.json"
}

echo "ci-prior-gate: match found"
fixture match 1 "$(run_json 111 workflow_dispatch completed '"success"' $SHA)"
run match "$SHA"
check "exit 0" 0 "$RC"
check "reuse=true" true "$(out reuse)"
check "found=true" true "$(out found)"
check "run_id" 111 "$(out run_id)"
check "run_url" "https://github.com/$REPO/actions/runs/111" "$(out run_url)"
check "outputs written once" 4 "$(lines)"
check "log cites the reused URL" yes "$(has "reusing hosted gate run 111")"
check "query names the workflow file" yes "$(grep -qF "repos/$REPO/actions/workflows/ci.yml/runs?" "$ARGSFILE" && echo yes || echo no)"
check "query filters on head_sha" yes "$(grep -qF "head_sha=$SHA" "$ARGSFILE" && echo yes || echo no)"
check "query filters on workflow_dispatch" yes "$(grep -qF "event=workflow_dispatch" "$ARGSFILE" && echo yes || echo no)"

echo "ci-prior-gate: match found, report-only"
run match --report-only "$SHA"
check "exit 0" 0 "$RC"
check "reuse=false" false "$(out reuse)"
check "found=true" true "$(out found)"
check "run_url still reported" "https://github.com/$REPO/actions/runs/111" "$(out run_url)"
check "log says report-only" yes "$(has "report-only, found hosted gate run 111")"

echo "ci-prior-gate: match found, Actions annotations"
SUMMARY=$TMPROOT/summary
: > "$SUMMARY"
export GITHUB_ACTIONS=true GITHUB_STEP_SUMMARY="$SUMMARY"
run match "$SHA"
unset GITHUB_ACTIONS GITHUB_STEP_SUMMARY
check "notice annotation cites the URL" yes "$(has "::notice title=Reused hosted gate::")"
check "step summary cites the URL" yes "$(grep -qF "actions/runs/111" "$SUMMARY" && echo yes || echo no)"

echo "ci-prior-gate: newest of several successful dispatches wins"
fixture several 3 \
  "$(run_json 201 workflow_dispatch completed '"success"' $SHA 2026-10-17T09:00:00Z)" \
  "$(run_json 203 workflow_dispatch completed '"success"' $SHA 2026-10-17T11:00:00Z)" \
  "$(run_json 202 workflow_dispatch completed '"failure"' $SHA 2026-10-17T12:00:00Z)"
run several "$SHA"
check "exit 0" 0 "$RC"
check "newest successful run" 203 "$(out run_id)"

echo "ci-prior-gate: no match"
fixture nomatch 0
run nomatch "$SHA"
check "exit 1" 1 "$RC"
check "reuse=false" false "$(out reuse)"
check "found=false" false "$(out found)"
check "run_id empty" "" "$(out run_id)"
check "outputs written once" 4 "$(lines)"

echo "ci-prior-gate: failed run"
fixture failed 1 "$(run_json 301 workflow_dispatch completed '"failure"' $SHA)"
run failed "$SHA"
check "exit 1" 1 "$RC"
check "reuse=false" false "$(out reuse)"
check "log names the dispatch count" yes "$(has "1 workflow_dispatch run(s) of ci at this commit, none completed")"

echo "ci-prior-gate: cancelled run"
fixture cancelled 1 "$(run_json 302 workflow_dispatch completed '"cancelled"' $SHA)"
run cancelled "$SHA"
check "exit 1" 1 "$RC"

echo "ci-prior-gate: run still in progress"
fixture inprogress 1 "$(run_json 303 workflow_dispatch in_progress null $SHA)"
run inprogress "$SHA"
check "exit 1" 1 "$RC"
check "reuse=false" false "$(out reuse)"

echo "ci-prior-gate: other event"
fixture otherevent 1 "$(run_json 401 push completed '"success"' $SHA)"
run otherevent "$SHA"
check "exit 1" 1 "$RC"
check "reuse=false" false "$(out reuse)"
check "log says outside the filters" yes "$(has "outside the query filters")"

echo "ci-prior-gate: other commit"
fixture othersha 1 "$(run_json 402 workflow_dispatch completed '"success"' $OTHER)"
run othersha "$SHA"
check "exit 1" 1 "$RC"
check "reuse=false" false "$(out reuse)"

echo "ci-prior-gate: other workflow file"
fixture otherpath 1 "$(run_json 403 workflow_dispatch completed '"success"' $SHA 2026-10-17T10:00:00Z .github/workflows/cross-platform-gpu.yml)"
run otherpath "$SHA"
check "exit 1" 1 "$RC"

echo "ci-prior-gate: other repository"
fixture otherrepo 1 "$(run_json 404 workflow_dispatch completed '"success"' $SHA 2026-10-17T10:00:00Z .github/workflows/ci.yml someone/fork)"
run otherrepo "$SHA"
check "exit 1" 1 "$RC"

echo "ci-prior-gate: one stray run poisons an otherwise good answer"
fixture stray 2 \
  "$(run_json 501 workflow_dispatch completed '"success"' $SHA)" \
  "$(run_json 502 workflow_dispatch completed '"success"' $OTHER)"
run stray "$SHA"
check "exit 1" 1 "$RC"
check "reuse=false" false "$(out reuse)"

echo "ci-prior-gate: API error"
fixture apierror 0
export STUB_GH_FAIL=1
run apierror "$SHA"
unset STUB_GH_FAIL
check "exit 1" 1 "$RC"
check "reuse=false" false "$(out reuse)"
check "log says gh failed" yes "$(has "gh api exited non-zero")"

echo "ci-prior-gate: pagination gap"
fixture gap 150 "$(run_json 601 workflow_dispatch completed '"success"' $SHA)"
run gap "$SHA"
check "exit 1" 1 "$RC"
check "reuse=false" false "$(out reuse)"
check "log says pagination gap" yes "$(has "pagination gap")"

echo "ci-prior-gate: unparseable response"
printf 'not json\n' > "$TMPROOT/garbage.json"
run garbage "$SHA"
check "exit 1" 1 "$RC"
check "reuse=false" false "$(out reuse)"

echo "ci-prior-gate: wrong response shape"
printf '{"message":"Not Found"}\n' > "$TMPROOT/shape.json"
run shape "$SHA"
check "exit 1" 1 "$RC"
check "reuse=false" false "$(out reuse)"

echo "ci-prior-gate: usage errors"
run match
check "missing sha exits 2" 2 "$RC"
check "missing sha writes reuse=false" false "$(out reuse)"
run match "ABC"
check "malformed sha exits 2" 2 "$RC"
run match "0123456789abcdef"
check "short sha exits 2" 2 "$RC"
OUT=$TMPROOT/norepo.output
: > "$OUT"
RC=0
PATH="$TMPROOT/bin:$PATH" STUB_GH_FIXTURE="$TMPROOT/match.json" STUB_GH_ARGS="$ARGSFILE" \
  GITHUB_OUTPUT="$OUT" env -u GITHUB_REPOSITORY sh "$SCRIPT" "$SHA" > "$OUTFILE" 2>&1 || RC=$?
check "unset GITHUB_REPOSITORY exits 2" 2 "$RC"
check "unset GITHUB_REPOSITORY writes reuse=false" false "$(out reuse)"

echo
echo "ci-prior-gate.test: $pass passed, $fail failed"
[ "$fail" -eq 0 ]
