#!/bin/sh
# ci-prior-gate.sh - find the hosted pre-tag gate for a commit, so a v* tag run can skip the tests that
# gate already passed (see docs/design/CI-SELECTIVE-TESTS-DESIGN-2026-07-18.md, Decision 7).
#
#   GITHUB_REPOSITORY=<owner>/<repo> GH_TOKEN=<token> scripts/ci-prior-gate.sh [--report-only] <sha>
#
# Asks the Actions REST API (gh api, needs actions: read) for workflow_dispatch runs of this repository's
# .github/workflows/ci.yml at <sha>. A run qualifies only when ALL of these hold:
#   * head_sha is exactly <sha>
#   * event is workflow_dispatch
#   * status is completed and conclusion is success, which is the conclusion of the whole run
#   * path is .github/workflows/ci.yml, and repository and head_repository are GITHUB_REPOSITORY
# The newest qualifying run wins. Two green dispatches at one commit ran the same workflow file over the
# same tree, so either proves the same thing.
#
# Fails closed. Each of these means no reuse: a missing or malformed <sha>, an unset GITHUB_REPOSITORY,
# gh exiting non-zero (API, auth or network error), a response that is not the expected JSON shape, a
# total_count above the number of runs returned (a pagination gap), and any returned run that does not
# match the query filters (an answer the script cannot trust).
#
# When GITHUB_OUTPUT is set it appends reuse, found, run_id and run_url exactly once. --report-only runs
# the same lookup and logging but always writes reuse=false, which is how a workflow_dispatch run proves
# the real query without changing what it does.
#
# Exit 0 when a qualifying run was found, 1 when not (for any reason above), 2 on a usage error.
set -u

workflow_path=.github/workflows/ci.yml
mode=apply
reuse=false
found=false
run_id=
run_url=

emit() {
  if [ -n "${GITHUB_OUTPUT:-}" ]; then
    { echo "reuse=$reuse"; echo "found=$found"; echo "run_id=$run_id"; echo "run_url=$run_url"; } >> "$GITHUB_OUTPUT"
  fi
}

# no_reuse <reason> [exit code]
no_reuse() {
  echo "ci-prior-gate: no reuse, $1"
  emit
  exit "${2:-1}"
}

if [ "${1:-}" = --report-only ]; then
  mode=report
  shift
fi
[ "$#" -eq 1 ] || no_reuse "usage: $0 [--report-only] <40-hex commit sha>" 2
sha=$1
case "$sha" in
  *[!0-9a-f]*) no_reuse "'$sha' is not a lowercase hex commit sha" 2 ;;
esac
[ "${#sha}" -eq 40 ] || no_reuse "'$sha' is not a full 40-character commit sha" 2
repo=${GITHUB_REPOSITORY:-}
[ -n "$repo" ] || no_reuse "GITHUB_REPOSITORY is unset" 2

echo "ci-prior-gate: looking for a successful workflow_dispatch run of $workflow_path at $sha in $repo"
json=$(gh api "repos/$repo/actions/workflows/ci.yml/runs?head_sha=$sha&event=workflow_dispatch&per_page=100") ||
  no_reuse "gh api exited non-zero, so the API answer is unknown"

# One jq pass validates the whole response and prints a single decision line:
#   match <id> <url> <qualifying count> <returned count>   |   none <returned count>   |   error <reason>
decision=$(printf '%s\n' "$json" | jq -r --arg sha "$sha" --arg repo "$repo" --arg path "$workflow_path" '
  if type != "object" or (.total_count | type) != "number" or (.workflow_runs | type) != "array" then
    "error the response is not a workflow run list"
  elif .total_count > (.workflow_runs | length) then
    "error pagination gap, total_count \(.total_count) but \(.workflow_runs | length) runs returned"
  elif any(.workflow_runs[]; .head_sha != $sha or .event != "workflow_dispatch" or .path != $path
           or .repository.full_name != $repo or .head_repository.full_name != $repo) then
    "error the response holds runs outside the query filters"
  else
    (.workflow_runs | length) as $n
    | [.workflow_runs[] | select(.status == "completed" and .conclusion == "success")] as $ok
    | ($ok | sort_by(.created_at, .id) | last) as $m
    | if $m == null then "none \($n)"
      elif ($m.id | type) != "number" or ($m.html_url | type) != "string" or $m.html_url == "" then
        "error the matched run has no id or URL"
      else "match \($m.id) \($m.html_url) \($ok | length) \($n)"
      end
  end') || no_reuse "the API response is not parseable JSON"

case "$decision" in
  "match "*) ;;
  "none "*) no_reuse "${decision#none } workflow_dispatch run(s) of ci at this commit, none completed with conclusion success" ;;
  "error "*) no_reuse "${decision#error }" ;;
  *) no_reuse "unrecognised decision '$decision'" ;;
esac

read -r _ run_id run_url ok_count total <<EOF
$decision
EOF
found=true
if [ "$mode" = report ]; then
  echo "ci-prior-gate: report-only, found hosted gate run $run_id ($ok_count of $total dispatch runs at this commit succeeded): $run_url"
  echo "ci-prior-gate: a v* tag run at this commit would reuse it. This run changes nothing."
else
  reuse=true
  echo "ci-prior-gate: reusing hosted gate run $run_id ($ok_count of $total dispatch runs at this commit succeeded): $run_url"
  if [ "${GITHUB_ACTIONS:-}" = true ]; then
    echo "::notice title=Reused hosted gate::Full test, determinism pass and SQL Server facts passed in $run_url at $sha"
  fi
  if [ -n "${GITHUB_STEP_SUMMARY:-}" ]; then
    echo "Reused hosted gate run [$run_id]($run_url) at \`$sha\`. Full test, determinism pass and SQL Server facts were not repeated." >> "$GITHUB_STEP_SUMMARY"
  fi
fi
emit
exit 0
