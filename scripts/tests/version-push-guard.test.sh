#!/bin/sh
# Exercises the version-past-release push guard (issue #1033) against throwaway fixture repos: the leg
# in .githooks/pre-push reading tag_release_window and tag_version_bearing from scripts/tag-standard.sh.
#
# The regression it pins down: the repo refuses to TAG a taken version and to PACK one, but nothing
# refused the push that left the integration branch SITTING on one. That happened twice on 2026-09-20
# and cost a repair commit each time, because the version knob named a released version while the
# changelog entry for that version described content its tag does not carry.
#
# The allow cases matter as much as the refusal. Documentation, tooling and governance ride a released
# version without bumping it by the contributor rules, and a side branch may legitimately sit at an old
# release. A guard that refused either would be routed around with --no-verify within a day.
#
# Fixture-only by construction: every repo, remote and tag below is scratch, made with git init under
# mktemp. Nothing here reads or writes the real checkout or a real tag, and the only "origin" is a bare
# repo in the same temp dir.
#
# Run it from anywhere:  sh scripts/tests/version-push-guard.test.sh
set -eu

here=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
SRC=$here/..
HOOKS=$here/../../.githooks

[ -f "$SRC/tag-standard.sh" ] || { echo "version-push-guard.test: missing $SRC/tag-standard.sh" >&2; exit 2; }
[ -f "$HOOKS/pre-push" ] || { echo "version-push-guard.test: missing $HOOKS/pre-push" >&2; exit 2; }

TMPROOT=$(mktemp -d)
trap 'rm -rf "$TMPROOT"' EXIT
OUTFILE="$TMPROOT/out"

pass=0
fail=0
check() { # name, expected, actual
  if [ "$2" = "$3" ]; then pass=$((pass+1)); echo "  ok    $1"
  else fail=$((fail+1)); echo "  FAIL  $1 (expected $2, got $3)"; echo "  ----- output -----"; sed 's/^/  | /' "$OUTFILE"; fi
}

# A fixture repo whose version knob is $1, with a bare origin, the real hook installed, and one commit
# on main. Echoes the work-tree path.
new_repo() {
  _ver=$1
  _root=$TMPROOT/r$$_$(date +%s%N 2>/dev/null || date +%s)
  mkdir -p "$_root/work"
  git init --quiet --bare "$_root/origin.git"
  cd "$_root/work"
  git init --quiet -b main .
  git config user.email t@example.invalid
  git config user.name test
  git config commit.gpgsign false
  mkdir -p scripts .githooks
  cp "$SRC/tag-standard.sh" scripts/tag-standard.sh
  cp "$HOOKS/pre-push" .githooks/pre-push
  chmod +x .githooks/pre-push
  # The engine marker tag_version_knob keys on, so the fixture reads <KhaozEngineVersion>. Trivially
  # passing, so the hook's own doc-version leg cannot colour this test's result.
  printf '#!/bin/sh\nexit 0\n' > scripts/check-doc-versions.sh
  chmod +x scripts/check-doc-versions.sh
  git config core.hooksPath .githooks
  printf '<Project><PropertyGroup><KhaozEngineVersion>%s</KhaozEngineVersion></PropertyGroup></Project>\n' "$_ver" > Directory.Build.props
  mkdir -p src docs tools/kit
  echo 'shipped' > src/Engine.cs
  echo 'notes' > docs/NOTES.md
  echo '{}' > tools/kit/package-lock.json
  git add -A
  git commit --quiet -m "init"
  git remote add origin "$_root/origin.git"
  git push --quiet origin main 2>/dev/null
  printf '%s' "$_root/work"
}

set_version() { printf '<Project><PropertyGroup><KhaozEngineVersion>%s</KhaozEngineVersion></PropertyGroup></Project>\n' "$1" > Directory.Build.props; }

# Runs a push and reports 'refused' or 'allowed'.
try_push() {
  if git push origin "$1" > "$OUTFILE" 2>&1; then printf allowed; else printf refused; fi
}

echo "version-push-guard.test"

# --- 1. main past its own release tag, carrying shipped code: REFUSED -------------------------------
w=$(new_repo 1.0.0); cd "$w"
git tag -a v1.0.0 -m "release(1.0.0): first" >/dev/null 2>&1
git push --quiet origin v1.0.0 2>/dev/null
echo 'changed' >> src/Engine.cs
git commit --quiet -am "engine: a shipped change"
check "code past the tag at a released version is refused" refused "$(try_push main)"
grep -qF "already released" "$OUTFILE" && r=named || r=silent
check "the refusal names the released version" named "$r"

# --- 2. main past the tag with documentation only: ALLOWED ------------------------------------------
w=$(new_repo 1.0.0); cd "$w"
git tag -a v1.0.0 -m "release(1.0.0): first" >/dev/null 2>&1
git push --quiet origin v1.0.0 2>/dev/null
echo 'more notes' >> docs/NOTES.md
git commit --quiet -am "docs: explain the thing"
check "documentation rides a released version" allowed "$(try_push main)"

# --- 2b. main past the tag with a dev-tool dependency bump: ALLOWED ---------------------------------
# The shape of the dependabot fix that found this carve-out: nothing under tools/ is packable, so a
# lockfile there cannot reach the version it would otherwise be refused for failing to bump.
w=$(new_repo 1.0.0); cd "$w"
git tag -a v1.0.0 -m "release(1.0.0): first" >/dev/null 2>&1
git push --quiet origin v1.0.0 2>/dev/null
echo '{"sharp":"0.35.4"}' > tools/kit/package-lock.json
git commit --quiet -am "deps: patch a dev tool's dependency"
check "a dev-tool dependency bump rides a release" allowed "$(try_push main)"

# --- 3. main sitting exactly at the tag: ALLOWED ----------------------------------------------------
w=$(new_repo 1.0.0); cd "$w"
echo 'changed' >> src/Engine.cs
git commit --quiet -am "engine: a shipped change"
git tag -a v1.0.0 -m "release(1.0.0): first" >/dev/null 2>&1
check "the release commit itself pushes" allowed "$(try_push main)"

# --- 4. version bumped past the release: ALLOWED ----------------------------------------------------
w=$(new_repo 1.0.0); cd "$w"
git tag -a v1.0.0 -m "release(1.0.0): first" >/dev/null 2>&1
git push --quiet origin v1.0.0 2>/dev/null
echo 'changed' >> src/Engine.cs
set_version 1.1.0
git commit --quiet -am "engine(1.1.0): a shipped change on a fresh version"
check "a bumped version pushes" allowed "$(try_push main)"

# --- 5. a side branch may sit at a released version: ALLOWED ----------------------------------------
w=$(new_repo 1.0.0); cd "$w"
git tag -a v1.0.0 -m "release(1.0.0): first" >/dev/null 2>&1
git push --quiet origin v1.0.0 2>/dev/null
git checkout --quiet -b fix/control-harness
echo 'changed' >> src/Engine.cs
git commit --quiet -am "engine: a pinned control harness"
check "a side branch at a released version pushes" allowed "$(try_push fix/control-harness)"

echo "---"
echo "version-push-guard.test: $pass passed, $fail failed"
[ "$fail" -eq 0 ] || exit 1
