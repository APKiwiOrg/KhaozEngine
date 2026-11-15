#!/bin/sh
# Exercises scripts/ledger.sh sync against a fake gh (issue #1802).
#
# The regression it pins down: every sync refetched all issue bodies, which took 21 s for 1,787 issues,
# so both the 20 s default and the SessionStart hook's 8 s budget abandoned it and the mirror went stale.
# A sync with a mirror now fetches only the issues updated since the mirror's newest update, less an
# overlap, and merges them in. The safety rule stays: a failed, empty or abandoned fetch leaves the
# mirror exactly as it was and exits non-zero, because a stale-but-true mirror beats a wrong one.
#
# Fixture-only by construction: the repository is a scratch git init under mktemp and gh is a stub on
# PATH, so nothing here reads the network or the real checkout's .ledger.
#
# Run it from anywhere:  sh scripts/tests/ledger.test.sh
set -eu

here=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
LEDGER=$here/../ledger.sh
[ -f "$LEDGER" ] || { echo "ledger.test: missing $LEDGER" >&2; exit 2; }
command -v jq >/dev/null 2>&1 || { echo "ledger.test: jq is required" >&2; exit 2; }

TMPROOT=$(mktemp -d)
trap 'rm -rf "$TMPROOT"' EXIT
FAKE=$TMPROOT/fake
OUTFILE=$TMPROOT/out
mkdir -p "$FAKE" "$TMPROOT/bin" "$TMPROOT/repo"

pass=0
fail=0
check() { # name, expected, actual
  if [ "$2" = "$3" ]; then pass=$((pass+1)); echo "  ok    $1"
  else fail=$((fail+1)); echo "  FAIL  $1 (expected $2, got $3)"; echo "  ----- output -----"; sed 's/^/  | /' "$OUTFILE"; fi
}

# The stub answers the three calls ledger.sh makes. The REST list honours `since` the way GitHub does,
# so an assertion on the merged mirror shows which issues the sync actually asked for.
cat > "$TMPROOT/bin/gh" <<'EOF'
#!/bin/sh
[ -f "$FAKE_GH_DIR/hang" ] && exec sleep 30
case "$1 $2" in
  "issue list")
    [ -f "$FAKE_GH_DIR/fail" ] && { echo "fake gh: issue list refused" >&2; exit 1; }
    cat "$FAKE_GH_DIR/full.json" ;;
  "api --paginate")
    [ -f "$FAKE_GH_DIR/fail" ] && { echo "fake gh: api refused" >&2; exit 1; }
    [ -f "$FAKE_GH_DIR/raw" ] && { cat "$FAKE_GH_DIR/raw"; exit 0; }
    since=$(printf '%s' "$3" | sed -n 's/.*[?&]since=\([^&]*\).*/\1/p')
    # Two pages, the way --paginate prints them: one JSON array after another.
    jq -c --arg s "$since" '[.[] | select(.updated_at >= $s)] | .[0:1], .[1:]' "$FAKE_GH_DIR/rest.json" ;;
  "repo view") echo "APKiwiOrg/KhaozEngine" ;;
  *) echo "fake gh: unexpected call: $*" >&2; exit 3 ;;
esac
EOF
chmod +x "$TMPROOT/bin/gh"
PATH=$TMPROOT/bin:$PATH
FAKE_GH_DIR=$FAKE
export PATH FAKE_GH_DIR
unset KE_LEDGER_TIMEOUT KE_LEDGER_OVERLAP KE_LEDGER_STALE_AFTER GH_REPO || true

cd "$TMPROOT/repo"
git init --quiet .

# Runs one sync with the given arguments. Echoes the exit code.
run_sync() {
  _rc=0; bash "$LEDGER" sync "$@" > "$OUTFILE" 2>&1 || _rc=$?
  printf '%s' "$_rc"
}

# One mirrored field of one issue, or "absent".
field() { jq -r --argjson n "$1" --arg f "$2" '[.[] | select(.number == $n)] | if length == 0 then "absent" else .[0][$f] end' .ledger/issues.json; }
meta() { jq -r --arg f "$1" '.[$f]' .ledger/meta.json; }
same_mirror() { cmp -s .ledger/issues.json "$TMPROOT/issues.before" && cmp -s .ledger/meta.json "$TMPROOT/meta.before" && printf same || printf changed; }
snapshot() { cp .ledger/issues.json "$TMPROOT/issues.before"; cp .ledger/meta.json "$TMPROOT/meta.before"; }

# The full fetch shape, as `gh issue list --json number,title,body,labels,url,state,updatedAt,createdAt`
# prints it. The newest mirrored update is #3 at 12:00, so the incremental cursor is 11:00.
cat > "$FAKE/full.json" <<'EOF'
[
  {"number":3,"title":"three","body":"","labels":[],"url":"https://github.com/APKiwiOrg/KhaozEngine/issues/3","state":"CLOSED","updatedAt":"2026-11-15T12:00:00Z","createdAt":"2026-11-01T00:00:00Z"},
  {"number":2,"title":"two","body":"b2","labels":[{"id":"LA_1","name":"kind/backlog","description":"d","color":"ededed"}],"url":"https://github.com/APKiwiOrg/KhaozEngine/issues/2","state":"OPEN","updatedAt":"2026-11-15T09:00:00Z","createdAt":"2026-11-01T00:00:00Z"},
  {"number":1,"title":"one","body":"b1","labels":[],"url":"https://github.com/APKiwiOrg/KhaozEngine/issues/1","state":"OPEN","updatedAt":"2026-11-15T08:00:00Z","createdAt":"2026-11-01T00:00:00Z"}
]
EOF

echo "full sync without a mirror"
check "a sync with no mirror succeeds" 0 "$(run_sync)"
check "it mirrors every issue" 3 "$(meta total)"
check "it counts the open issues" 2 "$(meta open)"
check "it records a full fetch" full "$(meta mode)"

# The REST shape. #1 changed before the cursor and must not be asked for. #2 changed inside the
# overlap, before the newest mirrored update, which is the update a previous fetch could miss. #4 is
# new and #5 is a pull request, which the REST list returns and the mirror never holds.
cat > "$FAKE/rest.json" <<'EOF'
[
  {"number":1,"title":"one, edited before the cursor","body":"b1","labels":[],"html_url":"https://github.com/APKiwiOrg/KhaozEngine/issues/1","state":"open","updated_at":"2026-11-15T10:00:00Z","created_at":"2026-11-01T00:00:00Z"},
  {"number":2,"title":"two","body":"b2","labels":[{"node_id":"LA_1","name":"kind/backlog","description":"d","color":"ededed"}],"html_url":"https://github.com/APKiwiOrg/KhaozEngine/issues/2","state":"closed","updated_at":"2026-11-15T11:30:00Z","created_at":"2026-11-01T00:00:00Z"},
  {"number":4,"title":"four","body":null,"labels":[],"html_url":"https://github.com/APKiwiOrg/KhaozEngine/issues/4","state":"open","updated_at":"2026-11-15T12:30:00Z","created_at":"2026-11-15T12:30:00Z"},
  {"number":5,"title":"a pull request","body":"","labels":[],"html_url":"https://github.com/APKiwiOrg/KhaozEngine/pull/5","state":"open","updated_at":"2026-11-15T12:30:00Z","created_at":"2026-11-15T12:30:00Z","pull_request":{"url":"x"}}
]
EOF

echo "incremental sync merges the changes since the cursor"
# The test owns the overlap so the fixture times above do not depend on the default.
KE_LEDGER_OVERLAP=3600
export KE_LEDGER_OVERLAP
check "a sync with a mirror succeeds" 0 "$(run_sync)"
check "it records an incremental fetch" incremental "$(meta mode)"
check "an update inside the overlap is applied" CLOSED "$(field 2 state)"
check "a new issue is added" four "$(field 4 title)"
check "a null body reads as empty, like a full fetch" "" "$(field 4 body)"
check "a pull request is never mirrored" absent "$(field 5 title)"
check "an issue the cursor excluded keeps its mirrored copy" one "$(field 1 title)"
check "an unchanged issue is kept" three "$(field 3 title)"
check "labels keep the full-fetch shape" "LA_1 kind/backlog" "$(jq -r '.[] | select(.number == 2) | .labels[0] | "\(.id) \(.name)"' .ledger/issues.json)"
check "the totals are recounted from the merged mirror" "4 2" "$(meta total) $(meta open)"

echo "a failed fetch leaves the mirror untouched"
snapshot
touch "$FAKE/fail"
check "a refused incremental fetch exits non-zero" 1 "$(run_sync)"
check "the mirror and its record are unchanged" same "$(same_mirror)"
check "a refused full fetch exits non-zero" 1 "$(run_sync --full)"
check "the mirror and its record are still unchanged" same "$(same_mirror)"
rm -f "$FAKE/fail"

printf '{"message":"Not Found"}' > "$FAKE/raw"
check "a payload that is not an issue list counts as a failure" 1 "$(run_sync)"
check "the mirror is unchanged after a bad payload" same "$(same_mirror)"
: > "$FAKE/raw"
check "an empty payload counts as a failure" 1 "$(run_sync)"
check "the mirror is unchanged after an empty payload" same "$(same_mirror)"
rm -f "$FAKE/raw"

touch "$FAKE/hang"
_rc=0; KE_LEDGER_TIMEOUT=1 bash "$LEDGER" sync > "$OUTFILE" 2>&1 || _rc=$?
check "an abandoned fetch exits non-zero" 1 "$_rc"
check "the mirror is unchanged after an abandoned fetch" same "$(same_mirror)"
rm -f "$FAKE/hang"

echo "--full refetches everything"
check "a forced full sync succeeds" 0 "$(run_sync --full)"
check "it records a full fetch" full "$(meta mode)"
check "an issue missing from the full fetch leaves the mirror" absent "$(field 4 title)"
check "the full fetch replaces the merged copies" OPEN "$(field 2 state)"

echo
echo "ledger.test: $pass passed, $fail failed"
[ "$fail" -eq 0 ]
