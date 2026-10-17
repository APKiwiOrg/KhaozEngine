#!/bin/sh
# Exercises the engine release lock (issue #1439): scripts/release-lock.sh and the main guard it shares
# with .githooks/pre-push and .githooks/reference-transaction through scripts/release-lock-standard.sh.
#
# The regression it pins down: during one release on 2026-10-17 sibling sessions merged into the shared
# main twice while the release waited on CI, and each merge forced a full re-gate. The lock lives in
# the git common dir, so a lock taken in one worktree must stop every other worktree from moving or
# pushing main, while the holder, tags and every other branch stay free.
#
# Fixture-only by construction: every repo, worktree and remote below is scratch, made with git init
# under mktemp. Nothing here reads or writes the real checkout's git dir or lock.
#
# Run it from anywhere:  sh scripts/tests/release-lock.test.sh
set -eu

here=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
SRC=$here/..
HOOKS=$here/../../.githooks

for f in "$SRC/release-lock.sh" "$SRC/release-lock-standard.sh" "$SRC/tag-standard.sh" "$HOOKS/pre-push" "$HOOKS/reference-transaction"; do
  [ -f "$f" ] || { echo "release-lock.test: missing $f" >&2; exit 2; }
done

TMPROOT=$(mktemp -d)
trap 'rm -rf "$TMPROOT"' EXIT
OUTFILE="$TMPROOT/out"
unset KE_RELEASE_LOCK_OWNER || true

pass=0
fail=0
check() { # name, expected, actual
  if [ "$2" = "$3" ]; then pass=$((pass+1)); echo "  ok    $1"
  else fail=$((fail+1)); echo "  FAIL  $1 (expected $2, got $3)"; echo "  ----- output -----"; sed 's/^/  | /' "$OUTFILE"; fi
}

# Runs the lock script as owner $1 (empty for none) with the remaining args. Echoes the exit code.
lock_as() {
  _o=$1; shift
  if [ -n "$_o" ]; then _rc=0; KE_RELEASE_LOCK_OWNER=$_o sh scripts/release-lock.sh "$@" > "$OUTFILE" 2>&1 || _rc=$?
  else _rc=0; sh scripts/release-lock.sh "$@" > "$OUTFILE" 2>&1 || _rc=$?; fi
  printf '%s' "$_rc"
}

# Runs a git command as owner $1 (empty for none). Echoes allowed or refused.
git_as() {
  _o=$1; shift
  if KE_RELEASE_LOCK_OWNER=$_o git "$@" > "$OUTFILE" 2>&1; then printf allowed; else printf refused; fi
}

has() { grep -qF -- "$1" "$OUTFILE" && printf yes || printf no; }

# The fixture: a main checkout with the real hooks and lock scripts, a bare origin, and one linked
# worktree on fix/side standing in for a sibling session.
ROOT=$TMPROOT/repo
WT=$TMPROOT/side
git init --quiet --bare "$TMPROOT/origin.git"
mkdir -p "$ROOT"
cd "$ROOT"
git init --quiet -b main .
git config user.email t@example.invalid
git config user.name test
git config commit.gpgsign false
git config tag.gpgsign false
mkdir -p scripts .githooks
cp "$SRC/release-lock.sh" "$SRC/release-lock-standard.sh" "$SRC/tag-standard.sh" scripts/
cp "$HOOKS/pre-push" "$HOOKS/reference-transaction" .githooks/
printf '#!/bin/sh\nexit 0\n' > scripts/check-doc-versions.sh
chmod +x .githooks/* scripts/*.sh
git config core.hooksPath .githooks
echo base > file.txt
git add -A
git commit --quiet -m "init"
git remote add origin "$TMPROOT/origin.git"
git push --quiet origin main 2>/dev/null
git worktree add --quiet -b fix/side "$WT" main
LOCK=$(cd "$ROOT" && git rev-parse --absolute-git-dir)/ke-release.lock

echo "release-lock.test"

# --- acquire and status --------------------------------------------------------------------------
cd "$ROOT"
check "status on a free lock exits 0" 0 "$(lock_as '' status)"
check "acquire without an owner is a usage error" 2 "$(lock_as '' acquire v1.0.0)"
check "acquire with a malformed tag is a usage error" 2 "$(lock_as rel acquire 1.0.0)"
check "acquire from a linked worktree succeeds" 0 "$(cd "$WT" && lock_as rel acquire v1.0.0)"
[ -f "$LOCK" ] && r=common || r=missing
check "the lock lives in the git common dir" common "$r"
for k in tag=v1.0.0 owner=rel host= started= started_epoch=; do
  grep -q "^$k" "$LOCK" && r=yes || r=no
  check "the lock records $k" yes "$r"
done
check "status from the main checkout sees the worktree's lock" 1 "$(lock_as '' status)"
check "status names the holder" yes "$(has 'v1.0.0 held by rel')"
check "status reports the age" yes "$(has 'age 0h 0m')"
before=$(cat "$LOCK")
check "re-acquire by the same owner is idempotent" 0 "$(lock_as rel acquire v1.0.0)"
[ "$(cat "$LOCK")" = "$before" ] && r=same || r=changed
check "idempotent re-acquire leaves the lock unchanged" same "$r"
check "acquire by a different owner is refused" 1 "$(lock_as other acquire v1.0.1)"
check "the refusal names the holder" yes "$(has 'held by rel')"
check "release by a different owner is refused" 1 "$(lock_as other release)"
[ -f "$LOCK" ] && r=held || r=gone
check "a refused release leaves the lock" held "$r"

# --- the hooks against a foreign holder -----------------------------------------------------------
cd "$ROOT"
main_before=$(git rev-parse main)
echo foreign >> file.txt
check "a foreign commit on main is refused" refused "$(git_as other commit --quiet -am 'feat: foreign')"
check "the refusal names the holder, tag and age" yes "$(has 'Release v1.0.0 is held by rel')"
check "the refusal points at status" yes "$(has 'scripts/release-lock.sh status')"
git checkout --quiet -- file.txt
check "an ownerless commit on main is refused" refused "$(git_as '' commit --quiet --allow-empty -m 'feat: ownerless')"
cd "$WT"
echo side >> file.txt
check "a foreign commit on another branch is allowed" allowed "$(git_as other commit --quiet -am 'feat: side work')"
check "a foreign update-ref of main from a worktree is refused" refused "$(git_as other update-ref refs/heads/main fix/side)"
check "main did not move" "$main_before" "$(git rev-parse main)"
check "a foreign push of HEAD to main is refused" refused "$(git_as other push origin HEAD:main)"
check "the push refusal names the holder" yes "$(has 'pre-push: refused')"
check "a foreign push of another branch is allowed" allowed "$(git_as other push origin fix/side)"
git tag -a v0.9.0 -m "release(0.9.0): fixture" "$main_before"
check "a foreign tag push is allowed" allowed "$(git_as other push origin v0.9.0)"
check "the remote main did not move" "$main_before" "$(git --git-dir="$TMPROOT/origin.git" rev-parse main)"

# --- the holder ---------------------------------------------------------------------------------
cd "$ROOT"
echo holder >> file.txt
check "the holder commits on main" allowed "$(git_as rel commit --quiet -am 'release: holder')"
check "the holder pushes main" allowed "$(git_as rel push origin main)"

# --- stale and malformed locks -------------------------------------------------------------------
old=$(( $(date -u +%s) - 7 * 3600 ))
sed "s/^started_epoch=.*/started_epoch=$old/" "$LOCK" > "$LOCK.new" && mv "$LOCK.new" "$LOCK"
check "status on a stale lock exits 0" 0 "$(lock_as '' status)"
check "status says the lock is stale" yes "$(has 'stale')"
echo stale >> file.txt
check "a stale lock does not stop a foreign commit on main" allowed "$(git_as other commit --quiet -am 'feat: past stale')"
check "the hook warns about the stale lock" yes "$(has 'ignoring a stale release lock')"
check "a stale lock does not stop a foreign push to main" allowed "$(git_as other push origin main)"
printf 'garbage\n' > "$LOCK"
check "a malformed lock fails open" allowed "$(git_as other commit --quiet --allow-empty -m 'feat: past malformed')"
check "a foreign acquire takes over a malformed lock" 0 "$(lock_as other acquire v1.1.0)"
sed "s/^started_epoch=.*/started_epoch=$old/" "$LOCK" > "$LOCK.new" && mv "$LOCK.new" "$LOCK"
check "a foreign acquire takes over a stale lock" 0 "$(lock_as rel acquire v1.1.0)"
check "the takeover is reported" yes "$(has 'taking over a stale lock')"
grep -q '^owner=rel$' "$LOCK" && r=rel || r=other
check "the stale lock now belongs to the new owner" rel "$r"
check "the owner moves its own lock to a new tag" 0 "$(lock_as rel acquire v1.1.1)"
grep -q '^tag=v1.1.1$' "$LOCK" && r=moved || r=kept
check "the moved lock names the new tag" moved "$r"

# --- release --------------------------------------------------------------------------------------
check "release by the owner succeeds" 0 "$(lock_as rel release)"
[ -f "$LOCK" ] && r=held || r=gone
check "release removes the lock" gone "$r"
check "status after release is free" 0 "$(lock_as '' status)"
check "a foreign commit on main is allowed once free" allowed "$(git_as other commit --quiet --allow-empty -m 'feat: after release')"
check "acquire after release succeeds" 0 "$(lock_as rel acquire v1.2.0)"
check "release --force by another owner succeeds" 0 "$(lock_as other release --force)"
[ -f "$LOCK" ] && r=held || r=gone
check "a forced release removes the lock" gone "$r"

echo "---"
echo "release-lock.test: $pass passed, $fail failed"
[ "$fail" -eq 0 ] || exit 1
