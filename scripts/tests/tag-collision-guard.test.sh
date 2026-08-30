#!/bin/sh
# Exercises the release-tag collision rule (issue #261) against throwaway fixture repos in a temp dir:
# the PreToolUse deny scripts/hooks/tag-collision-guard.sh and the refusal inside scripts/tag-release.sh,
# both reading the one rule in scripts/tag-standard.sh.
#
# The regression it pins down is #261's third mode, the timing bug. A PreToolUse hook necessarily runs
# BEFORE the command it is judging, so for the ritual's own chained
# `git merge <branch> && scripts/tag-release.sh` it read <KhaozEngineVersion> from the PRE-merge tree,
# found the PREVIOUS version's tag legitimately present, and denied the whole chain. The check now lives
# in tag-release.sh, which reads the version at the moment it is true, and the hook only fires on an
# explicit literal vX.Y.Z in the command text.
#
# Fixture-only by construction: every repo, remote and tag below is scratch, made with git init under
# mktemp. Nothing here reads or writes the real checkout or a real tag, and no fixture has a network
# remote (the "origin" cases point at a bare repo in the same temp dir).
#
# Run it from anywhere:  sh scripts/tests/tag-collision-guard.test.sh
set -eu

here=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
SRC=$here/..

for f in tag-standard.sh tag-release.sh hooks/tag-collision-guard.sh; do
  [ -f "$SRC/$f" ] || { echo "tag-collision-guard.test: missing script under test: $SRC/$f" >&2; exit 2; }
done
command -v jq >/dev/null 2>&1 || { echo "tag-collision-guard.test: jq is required (the hook reads its JSON with it)" >&2; exit 2; }

TMPROOT=$(mktemp -d)
trap 'rm -rf "$TMPROOT"' EXIT
OUTFILE="$TMPROOT/out"

pass=0
fail=0
check() { # name, expected, actual
  if [ "$2" = "$3" ]; then pass=$((pass+1)); echo "  ok    $1"
  else fail=$((fail+1)); echo "  FAIL  $1 (expected $2, got $3)"; echo "  ----- output -----"; sed 's/^/  | /' "$OUTFILE"; fi
}
says() { grep -qF "$1" "$OUTFILE" && r=0 || r=1; }
denied() { grep -q '"permissionDecision":"deny"' "$OUTFILE" && r=0 || r=1; }
allowed() { [ -s "$OUTFILE" ] && r=1 || r=0; }

# props <version> -> the fixture's Directory.Build.props at that engine version.
props() {
  cat > "$REPO/Directory.Build.props" <<EOF
<Project>
  <PropertyGroup>
    <KhaozEngineVersion>$1</KhaozEngineVersion>
  </PropertyGroup>
</Project>
EOF
}

# newfixture <name> <version> -> a scratch repo on main carrying the scripts under test and that engine
# version, with one conventional commit and a clean tree. Sets REPO.
newfixture() {
  REPO="$TMPROOT/$1"
  mkdir -p "$REPO/scripts/hooks"
  cp "$SRC/tag-standard.sh" "$SRC/tag-release.sh" "$REPO/scripts/"
  cp "$SRC/hooks/tag-collision-guard.sh" "$REPO/scripts/hooks/tag-collision-guard.sh"
  # check-doc-versions.sh is the engine marker tag-standard.sh keys on to pick KhaozEngineVersion over
  # Version, so the fixture carries a stub of it. Presence is the whole contract.
  echo '#!/bin/sh' > "$REPO/scripts/check-doc-versions.sh"
  props "$2"
  (
    cd "$REPO"
    git init -q -b main . >/dev/null 2>&1
    git config user.email t@example.invalid
    git config user.name t
    git config commit.gpgsign false
    git config tag.gpgsign false
    git add -A
    git commit -q --no-verify -m "chore($2): fixture"
  )
}

# tagit <version> -> annotated tag v<version> at the fixture's HEAD.
tagit() { ( cd "$REPO" && git tag -a "v$1" -m "release($1): fixture" ); }

# bumpbranch <branch> <version> -> a branch off main carrying the version bump, main left checked out.
# This is the shape the ritual produces: the bump arrives via the merge, not before it.
bumpbranch() {
  (
    cd "$REPO"
    git checkout -q -b "$1"
    props "$2"
    git add -A
    git commit -q --no-verify -m "release($2): the bump rides the merge"
    git checkout -q main
  )
}

# addorigin -> a bare repo alongside the fixture, wired as origin, with main and every tag pushed.
addorigin() {
  ( cd "$TMPROOT" && git init -q --bare "$(basename "$REPO").git" >/dev/null 2>&1 )
  ( cd "$REPO" && git remote add origin "$TMPROOT/$(basename "$REPO").git" && git push -q --no-verify origin main --tags )
}

# hookrun <command-string> -> the PreToolUse guard fed the hook JSON on stdin. rc is the hook's exit
# code (always 0) and the deny, if any, is the JSON on stdout in $OUTFILE. The command is only ever
# PARSED here, never executed.
hookrun() {
  set +e
  printf '%s' "$1" | jq -Rs '{tool_input: {command: .}}' \
    | ( cd "$REPO" && sh scripts/hooks/tag-collision-guard.sh ) >"$OUTFILE" 2>&1
  rc=$?
  set -e
}

# relrun [area summary...] -> the fixture's tag-release.sh, for real.
relrun() {
  set +e
  ( cd "$REPO" && sh scripts/tag-release.sh "$@" ) >"$OUTFILE" 2>&1
  rc=$?
  set -e
}

# hastag <version> -> 0 when the fixture holds that annotated tag.
hastag() { ( cd "$REPO" && git rev-parse -q --verify "refs/tags/v$1" >/dev/null 2>&1 ) && r=0 || r=1; }

echo "== #261 mode 3: the chained merge-then-release is not denied on the PRE-merge version =="
newfixture chained 1.0.0
tagit 1.0.0
bumpbranch feature/x 1.0.1
hookrun "cd $REPO && git checkout -q main && git merge --ff-only feature/x && sh scripts/tag-release.sh"
check "hook exits 0 (it decides by output, never by status)" 0 "$rc"
allowed;  check "  the documented release chain is allowed through" 0 "$r"

echo "== and the chain, actually run, tags the version the merge brought in =="
( cd "$REPO" && git merge -q --ff-only feature/x )
relrun
check "tag-release.sh succeeds" 0 "$rc"
says "created annotated v1.0.1";  check "  names the tag it created" 0 "$r"
hastag 1.0.1;  check "  and the annotated tag is there" 0 "$r"
hastag 1.0.0;  check "  the previous release is untouched" 0 "$r"

echo "== the collision is caught by tag-release.sh, where the version is read at the right moment =="
newfixture takenlocal 2.0.0
tagit 2.0.0
relrun
check "tag-release.sh refuses" 1 "$rc"
says "v2.0.0 already exists (local)";  check "  names the taken tag and where it found it" 0 "$r"
says "next free version";             check "  says to bump" 0 "$r"
says "CHANGELOG";                     check "  and to rebase the changelog entry" 0 "$r"
hookrun "cd $REPO && sh scripts/tag-release.sh"
allowed;  check "the hook leaves the wrapper to speak for itself" 0 "$r"

echo "== a tag that exists only on origin is a collision too, which is the authoritative half =="
newfixture takenorigin 2.0.0
tagit 2.0.0
addorigin
( cd "$REPO" && git tag -d v2.0.0 >/dev/null )
relrun
check "tag-release.sh refuses on an origin-only tag" 1 "$rc"
says "v2.0.0 already exists (origin)"
check "  names the taken tag and that it came from origin" 0 "$r"

echo "== the hook still denies an explicit literal vX.Y.Z that is already taken =="
newfixture literal 3.0.0
tagit 3.0.0
hookrun "cd $REPO && git tag v3.0.0"
denied;  check "a bare create of the taken tag is denied" 0 "$r"
says "Tag v3.0.0 already exists";  check "  the deny reason names it" 0 "$r"
hookrun "cd $REPO && git tag -a v3.0.0 -m fixture"
denied;  check "an annotated create of the taken tag is denied" 0 "$r"
hookrun "cd $REPO && git tag v3.1.0"
allowed;  check "a free version is allowed" 0 "$r"

echo "== the hook still denies a push of a tag origin already holds =="
newfixture pushtaken 4.0.0
tagit 4.0.0
addorigin
hookrun "cd $REPO && git push origin v4.0.0"
denied;  check "pushing a tag origin already holds is denied" 0 "$r"
hookrun "cd $REPO && git push origin refs/tags/v4.0.0"
denied;  check "  the refs/tags/ spelling too" 0 "$r"
hookrun "cd $REPO && git push origin --delete v4.0.0"
allowed;  check "a tag DELETE is not a collision" 0 "$r"
hookrun "cd $REPO && git push origin main"
allowed;  check "an ordinary branch push is not this hook's business" 0 "$r"

echo "== the read-only and quoted-mention allow paths from #261 modes 1 and 2 stay allowed =="
hookrun "cd $REPO && git tag --sort=-v:refname | head -5"
allowed;  check "a read-only listing is allowed" 0 "$r"
hookrun "cd $REPO && git tag --format='%(refname:short)'"
allowed;  check "  whatever formatting flag it carries" 0 "$r"
hookrun "cd $REPO && gh issue create --body \"the fix for git tag v4.0.0 and scripts/tag-release.sh\""
allowed;  check "a command that merely TALKS about tagging is allowed" 0 "$r"
hookrun "cd $REPO && git tag -d v4.0.0"
allowed;  check "deleting a local tag is allowed" 0 "$r"

echo ""
echo "passed: $pass   failed: $fail"
[ "$fail" -eq 0 ]
