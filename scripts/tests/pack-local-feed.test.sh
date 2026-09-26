#!/bin/sh
# Exercises the local-feed pack guard (issue #492) against throwaway fixture repos in a temp dir: the
# wrapper scripts/pack-local-feed.sh in --dry-run, the report scripts/check-local-feed.sh, and the
# PreToolUse deny scripts/hooks/pack-release-guard.sh.
#
# The regression it pins down is the #492 window: the ritual packs whatever <KhaozEngineVersion> says,
# that version does not move until someone bumps it, so every finish between a tag and the next bump
# re-packs an already-released number and the feed's copy stops matching the tag everybody reads.
#
# It also pins where the feed is (#1063): a pack from a linked worktree lands in the MAIN checkout's
# local-feed, which is what consumers read, and KHAOZENGINE_FEED moves the pack and the report together.
#
# Fixture-only by construction: every repo, tag and package below is scratch, made with git init and
# touch under mktemp. Nothing here reads or writes the real checkout, the real local-feed, or a real
# tag, and --dry-run means dotnet is never invoked. Each fixture is its own main checkout, and every run
# clears an inherited KHAOZENGINE_FEED, so the resolved feed is always a scratch one.
#
# Run it from anywhere:  sh scripts/tests/pack-local-feed.test.sh
set -eu

here=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
SRC=$here/..

for f in pack-standard.sh tag-standard.sh pack-local-feed.sh check-local-feed.sh hooks/pack-release-guard.sh; do
  [ -f "$SRC/$f" ] || { echo "pack-local-feed.test: missing script under test: $SRC/$f" >&2; exit 2; }
done

TMPROOT=$(mktemp -d)
trap 'rm -rf "$TMPROOT"' EXIT
OUTFILE="$TMPROOT/out"
GIT="git -c user.email=t@example.invalid -c user.name=t -c commit.gpgsign=false -c tag.gpgsign=false"

pass=0
fail=0
check() { # name, expected, actual
  if [ "$2" = "$3" ]; then pass=$((pass+1)); echo "  ok    $1"
  else fail=$((fail+1)); echo "  FAIL  $1 (expected $2, got $3)"; echo "  ----- output -----"; sed 's/^/  | /' "$OUTFILE"; fi
}
says() { grep -qF -- "$1" "$OUTFILE" && r=0 || r=1; }
absent_says() { grep -qF -- "$1" "$OUTFILE" && r=1 || r=0; }

# newfixture <name> <version> -> a scratch repo carrying the scripts under test and that engine
# version, with one commit and a clean tree. Sets REPO, FEED, REAL (REPO with symlinks resolved, which
# is how the scripts print it) and AT (where packrun and feedrun run, the fixture root by default).
newfixture() {
  REPO="$TMPROOT/$1"
  FEED="$REPO/local-feed"
  AT=$REPO
  mkdir -p "$REPO/scripts/hooks" "$FEED"
  for _f in pack-standard.sh tag-standard.sh pack-local-feed.sh check-local-feed.sh; do
    cp "$SRC/$_f" "$REPO/scripts/$_f"
  done
  cp "$SRC/hooks/pack-release-guard.sh" "$REPO/scripts/hooks/pack-release-guard.sh"
  # check-doc-versions.sh is the engine marker tag-standard.sh keys on to pick KhaozEngineVersion over
  # Version, so the fixture carries a stub of it. Presence is the whole contract.
  echo '#!/bin/sh' > "$REPO/scripts/check-doc-versions.sh"
  cat > "$REPO/Directory.Build.props" <<EOF
<Project>
  <PropertyGroup>
    <KhaozEngineVersion>$2</KhaozEngineVersion>
  </PropertyGroup>
</Project>
EOF
  # local-feed and .worktrees are gitignored in the real repo, and the fixture needs the same, or a
  # packed file or a linked worktree would make the tree dirty and turn every at-tag case into
  # at-tag-dirty by accident.
  printf 'local-feed\n.worktrees/\n' > "$REPO/.gitignore"
  ( cd "$REPO" && $GIT init -q . >/dev/null 2>&1 && $GIT add -A && $GIT commit -q --no-verify -m "chore: fixture" )
  ( cd "$REPO" && git update-ref refs/remotes/origin/main HEAD )
  REAL=$(cd "$REPO" && pwd -P)
}

# addworktree -> a linked worktree of the fixture at .worktrees/wt on a new branch, the layout the
# contributor rules prescribe. Sets WT and points AT at it.
addworktree() {
  WT="$REPO/.worktrees/wt"
  ( cd "$REPO" && $GIT worktree add -b wt "$WT" >/dev/null 2>&1 )
  AT=$WT
}

# tagit <version> -> annotated tag v<version> at the fixture's HEAD.
tagit() { ( cd "$REPO" && $GIT tag -a "v$1" -m "release($1): fixture" ); }

# advance -> one more commit on top, leaving the tree clean and HEAD off the tag.
advance() {
  ( cd "$REPO" && echo "more" >> README.md && $GIT add -A \
    && $GIT commit -q --no-verify -m "chore: more" \
    && git update-ref refs/remotes/origin/main HEAD )
}

# package <id> <version> <commit> [feed] -> a minimal nupkg carrying the same repository commit metadata
# the .NET SDK writes into real packages. The fixture's shared feed is the default destination.
package() {
  _id=$1; _version=$2; _commit=$3; _feed=${4:-$FEED}
  _pkgdir="$TMPROOT/package-$_id-$_version"
  mkdir -p "$_pkgdir" "$_feed"
  cat > "$_pkgdir/$_id.nuspec" <<EOF
<?xml version="1.0" encoding="utf-8"?>
<package>
  <metadata>
    <id>$_id</id>
    <version>$_version</version>
    <repository type="git" url="https://example.invalid/repo" commit="$_commit" />
  </metadata>
</package>
EOF
  ( cd "$_pkgdir" && zip -q "$_feed/$_id.$_version.nupkg" "$_id.nuspec" )
}

# packrun [VAR=VALUE ...] -> the fixture's wrapper in --dry-run from $AT, never invoking dotnet.
packrun() {
  set +e
  ( cd "$AT" && env -u PACK_RELEASED_OK -u KHAOZENGINE_FEED "$@" sh scripts/pack-local-feed.sh --dry-run ) >"$OUTFILE" 2>&1
  rc=$?
  set -e
}

# feedrun [VAR=VALUE ...] [args...] -> the fixture's feed report from $AT.
feedrun() {
  set +e
  (
    cd "$AT" || exit 2
    unset KHAOZENGINE_FEED
    while [ $# -gt 0 ]; do
      case "$1" in *=*) export "${1?}"; shift ;; *) break ;; esac
    done
    sh scripts/check-local-feed.sh "$@"
  ) >"$OUTFILE" 2>&1
  rc=$?
  set -e
}

# hookrun <command-string> -> the PreToolUse guard fed the hook JSON on stdin. rc is the hook's exit
# code (always 0) and the deny, if any, is the JSON on stdout in $OUTFILE.
hookrun() {
  set +e
  printf '%s' "$1" | jq -Rs '{tool_input: {command: .}}' \
    | ( cd "$REPO" && sh scripts/hooks/pack-release-guard.sh ) >"$OUTFILE" 2>&1
  rc=$?
  set -e
}
# hookrun_feed <feed> <command-string> -> hookrun with the configured feed visible to the hook.
hookrun_feed() {
  set +e
  printf '%s' "$2" | jq -Rs '{tool_input: {command: .}}' \
    | ( cd "$REPO" && KHAOZENGINE_FEED=$1 sh scripts/hooks/pack-release-guard.sh ) >"$OUTFILE" 2>&1
  rc=$?
  set -e
}
denied() { grep -q '"permissionDecision":"deny"' "$OUTFILE" && r=0 || r=1; }
allowed() { [ -s "$OUTFILE" ] && r=1 || r=0; }

echo "== staged: the version carries no tag, so the ritual pack is the ordinary case =="
newfixture staged 2.0.0
packrun
check "wrapper succeeds" 0 "$rc"
says "2.0.0 is staged";        check "  says the version is staged" 0 "$r"
says "dotnet pack -c Release -o $REAL/local-feed"
check "  prints the command it would run, into the checkout's own feed" 0 "$r"
says "nothing packed";         check "  and did not run it" 0 "$r"

echo "== released: the tag exists and HEAD moved past it, which is the #492 window =="
newfixture released 2.0.0
tagit 2.0.0
advance
packrun
check "wrapper refuses" 1 "$rc"
says "v2.0.0 is already a released tag";    check "  names the released tag" 0 "$r"
says "Bump <KhaozEngineVersion>";           check "  says to bump first" 0 "$r"
says "PACK_RELEASED_OK=1";                  check "  names the override" 0 "$r"
absent_says "dotnet pack";                  check "  and never reaches the pack command" 0 "$r"

echo "== override: the same refused state packs when PACK_RELEASED_OK=1 is set =="
packrun PACK_RELEASED_OK=1
check "wrapper succeeds" 0 "$rc"
says "PACK_RELEASED_OK=1 is set, packing anyway"
check "  says it is overriding rather than staying silent" 0 "$r"
says "dotnet pack -c Release -o $REAL/local-feed"
check "  reaches the pack command" 0 "$r"

echo "== at-tag: HEAD IS the tag with a clean tree, so a re-pack reproduces the released bytes =="
newfixture attag 2.0.0
tagit 2.0.0
packrun
check "wrapper succeeds with no override" 0 "$rc"
says "reproduces the released bytes";  check "  says why it is allowed" 0 "$r"
says "dotnet pack";                    check "  reaches the pack command" 0 "$r"

echo "== at-tag-dirty: same commit, dirty tree, so the pack would not reproduce those bytes =="
( cd "$REPO" && echo "uncommitted" >> README.md )
packrun
check "wrapper refuses" 1 "$rc"
says "the tree is not clean";  check "  names the dirty tree as the reason" 0 "$r"
packrun PACK_RELEASED_OK=1
check "  and the release override does not bypass shared-feed cleanliness" 1 "$rc"
ATTAG_PRIVATE="$TMPROOT/attag-private"
packrun PACK_RELEASED_OK=1 KHAOZENGINE_FEED="$ATTAG_PRIVATE"
check "  while the release override plus a private feed gets through" 0 "$rc"

echo "== check-local-feed: tagged versions are classified by their nuspec commit stamps =="
newfixture feed 3.0.0
tagit 1.0.0
tagit 1.1.0
TAG_COMMIT=$(cd "$REPO" && git rev-parse 'refs/tags/v1.0.0^{commit}')
advance
WRONG_COMMIT=$(cd "$REPO" && git rev-parse HEAD)
package KhaozEngine.App 1.0.0 "$TAG_COMMIT"
package KhaozEngine.Gui 1.0.0 "$TAG_COMMIT"
package KhaozEngine.App 1.1.0 "$TAG_COMMIT"
package KhaozEngine.Gui 1.1.0 "$WRONG_COMMIT"
package KhaozEngine.Gpu.D3D11 3.0.0 "$WRONG_COMMIT"
# Times deliberately contradict the commit stamps. The matching 1.0.0 files look newer than the tag,
# while the mismatched 1.1.0 files look older. Commit identity must decide both classifications.
touch -t 209901010000 "$FEED/KhaozEngine.App.1.0.0.nupkg" "$FEED/KhaozEngine.Gui.1.0.0.nupkg"
touch -t 200001010000 "$FEED/KhaozEngine.App.1.1.0.nupkg" "$FEED/KhaozEngine.Gui.1.1.0.nupkg"
feedrun
check "report succeeds without --strict" 0 "$rc"
says "RELEASED   1.0.0  commit $TAG_COMMIT";  check "  matching package commits are RELEASED" 0 "$r"
says "DRIFTED    1.1.0  tag commit $TAG_COMMIT";  check "  any wrong package commit is DRIFTED" 0 "$r"
says "KhaozEngine.Gui.1.1.0.nupkg: commit $WRONG_COMMIT";  check "  names the wrong package and stamp" 0 "$r"
says "STAGED     3.0.0  commit $WRONG_COMMIT on origin/main";  check "  a main commit is safely STAGED" 0 "$r"
says "1 staged, 0 unsafe, 1 released, 1 drifted"
check "  the summary counts all three" 0 "$r"
feedrun --strict
check "--strict fails on a drifted version" 1 "$rc"

echo "== check-local-feed: a clean feed and a missing feed are both quiet successes =="
newfixture cleanfeed 3.0.0
tagit 1.0.0
CLEAN_TAG_COMMIT=$(cd "$REPO" && git rev-parse 'refs/tags/v1.0.0^{commit}')
package KhaozEngine.App 1.0.0 "$CLEAN_TAG_COMMIT"
feedrun --strict
check "--strict passes when every released package matches its tag" 0 "$rc"
rm -rf "$FEED"
feedrun --strict
check "no feed at all is not a failure" 0 "$rc"
says "nothing to check";  check "  and says so" 0 "$r"

echo "== worktree: a pack from a linked worktree lands in the MAIN checkout's feed (#1063) =="
newfixture wtmain 2.0.0
addworktree
WTREAL=$(cd "$WT" && pwd -P)
packrun
check "wrapper succeeds from the worktree" 0 "$rc"
says "feed is $REAL/local-feed";                    check "  resolves the main checkout's feed" 0 "$r"
says "dotnet pack -c Release -o $REAL/local-feed";  check "  and packs into it" 0 "$r"
absent_says "$WTREAL/local-feed";                   check "  never into the worktree's own feed" 0 "$r"
absent_says "-o ./local-feed";                      check "  nor into a cwd-relative one" 0 "$r"
[ -d "$WT/local-feed" ] && r=0 || r=1
check "  the worktree still gets the local-feed its nuget.config source needs" 0 "$r"
WT_COMMIT=$(cd "$WT" && git rev-parse HEAD)
package KhaozEngine.App 2.0.0 "$WT_COMMIT"
feedrun
check "report succeeds from the worktree" 0 "$rc"
says "STAGED     2.0.0  commit $WT_COMMIT on origin/main"
check "  reads the safe package in the main checkout's feed" 0 "$r"
says "(feed: $REAL/local-feed)";   check "  and names that feed" 0 "$r"

echo "== worktree: the released-version guard still runs in front of the resolved feed =="
tagit 2.0.0
( cd "$WT" && echo "more" >> README.md && $GIT add -A && $GIT commit -q --no-verify -m "chore: more" )
packrun
check "wrapper refuses from the worktree" 1 "$rc"
says "feed is $REAL/local-feed";            check "  names the feed it refused to write" 0 "$r"
says "v2.0.0 is already a released tag";    check "  names the released tag" 0 "$r"
absent_says "dotnet pack";                  check "  and never reaches the pack command" 0 "$r"
packrun PACK_RELEASED_OK=1
check "  the release override does not bypass the shared-feed boundary" 1 "$rc"
says "not an ancestor of origin/main";  check "  names the remaining refusal" 0 "$r"
RELEASED_PRIVATE="$TMPROOT/released-private"
packrun PACK_RELEASED_OK=1 KHAOZENGINE_FEED="$RELEASED_PRIVATE"
check "  release override plus a private feed gets through" 0 "$rc"
says "dotnet pack -c Release -o $RELEASED_PRIVATE";  check "  into only the private feed" 0 "$r"

echo "== unmerged worktree: shared feed refuses it, private feed allows it (#1135) =="
newfixture unmerged 4.0.0
addworktree
MAIN_COMMIT=$(cd "$REPO" && git rev-parse refs/remotes/origin/main)
( cd "$WT" && echo "branch" >> README.md && $GIT add -A && $GIT commit -q --no-verify -m "chore: branch" )
BRANCH_COMMIT=$(cd "$WT" && git rev-parse HEAD)
packrun
check "shared-feed pack refuses the unmerged HEAD" 1 "$rc"
says "Refusing to pack the shared feed";  check "  names the shared-feed boundary" 0 "$r"
says "HEAD $BRANCH_COMMIT is not an ancestor of origin/main $MAIN_COMMIT"
check "  prints both commits" 0 "$r"
says "KHAOZENGINE_FEED";  check "  points to the private-feed escape path" 0 "$r"
absent_says "dotnet pack";  check "  never reaches the shared pack command" 0 "$r"
packrun KHAOZENGINE_FEED="$REAL/local-feed"
check "an absolute override naming the shared feed is still refused" 1 "$rc"
says "not an ancestor of origin/main";  check "  keeps the ancestry guard" 0 "$r"
packrun KHAOZENGINE_FEED=../../local-feed
check "a relative override resolving to the shared feed is still refused" 1 "$rc"
says "not an ancestor of origin/main";  check "  canonicalizes before deciding" 0 "$r"
packrun KHAOZENGINE_FEED="$REAL/missing/../local-feed"
check "a shared alias behind a missing component is still refused" 1 "$rc"
says "not an ancestor of origin/main";  check "  normalizes missing components before deciding" 0 "$r"
ALT="$TMPROOT/unmerged-private"
packrun KHAOZENGINE_FEED="$ALT"
check "explicit private-feed pack succeeds" 0 "$rc"
says "dotnet pack -c Release -o $ALT";  check "  reaches only the private feed" 0 "$r"

echo "== dirty tree: shared feed refuses it, private feed allows it (#1135) =="
UNMERGED_REPO=$REPO
UNMERGED_FEED=$FEED
UNMERGED_WT=$WT
newfixture dirtyshared 6.0.0
echo "dirty" >> "$REPO/README.md"
packrun
check "shared-feed pack refuses a dirty tree" 1 "$rc"
says "the worktree is not clean";  check "  names uncommitted package bytes" 0 "$r"
packrun KHAOZENGINE_FEED="$REAL/missing/../local-feed"
check "a dirty tree cannot use a missing-component shared alias" 1 "$rc"
says "the worktree is not clean";  check "  retains the cleanliness guard" 0 "$r"
DIRTY_PRIVATE="$TMPROOT/dirty-private"
packrun KHAOZENGINE_FEED="$DIRTY_PRIVATE"
check "dirty private-feed pack succeeds" 0 "$rc"
says "dotnet pack -c Release -o $DIRTY_PRIVATE";  check "  reaches only the private feed" 0 "$r"
NESTED_PRIVATE="$TMPROOT/private-missing/nested/feed"
packrun KHAOZENGINE_FEED="$NESTED_PRIVATE"
check "a nonexistent nested private feed remains allowed" 0 "$rc"
says "dotnet pack -c Release -o $NESTED_PRIVATE";  check "  keeps the distinct private path" 0 "$r"
packrun KHAOZENGINE_FEED="$REPO/Directory.Build.props/feed"
check "an unresolvable feed path fails closed" 1 "$rc"
says "cannot be resolved safely";  check "  explains the path refusal" 0 "$r"

echo "== staged report: package commits outside origin/main are unsafe (#1135) =="
REPO=$UNMERGED_REPO
FEED=$UNMERGED_FEED
WT=$UNMERGED_WT
AT=$WT
package KhaozEngine.App 4.0.0 "$BRANCH_COMMIT"
feedrun
check "unsafe staged report succeeds without --strict" 0 "$rc"
says "UNSAFE     4.0.0";  check "  flags the staged version" 0 "$r"
says "KhaozEngine.App.4.0.0.nupkg: commit $BRANCH_COMMIT not on origin/main"
check "  names the package and unmerged commit" 0 "$r"
says "0 staged, 1 unsafe, 0 released, 0 drifted";  check "  counts unsafe separately" 0 "$r"
feedrun --strict
check "--strict fails on an unsafe staged version" 1 "$rc"

echo "== staged report: a package commit on origin/main remains safe =="
newfixture stagedmain 5.0.0
MAIN_COMMIT=$(cd "$REPO" && git rev-parse refs/remotes/origin/main)
package KhaozEngine.App 5.0.0 "$MAIN_COMMIT"
feedrun --strict
check "safe staged report passes --strict" 0 "$rc"
says "STAGED     5.0.0  commit $MAIN_COMMIT on origin/main"
check "  prints the package commit and ancestry" 0 "$r"

echo "== KHAOZENGINE_FEED: one override moves the pack and the report together =="
newfixture wtoverride 2.0.0
addworktree
ALT="$TMPROOT/altfeed"
mkdir -p "$ALT"
ALT_COMMIT=$(cd "$WT" && git rev-parse HEAD)
package KhaozEngine.App 9.9.9 "$ALT_COMMIT" "$ALT"
packrun KHAOZENGINE_FEED="$ALT"
check "wrapper succeeds with the override" 0 "$rc"
says "dotnet pack -c Release -o $ALT";  check "  packs into the override" 0 "$r"
absent_says "$REAL/local-feed";         check "  instead of the main checkout's feed" 0 "$r"
feedrun KHAOZENGINE_FEED="$ALT"
check "report succeeds with the override" 0 "$rc"
says "STAGED     9.9.9  commit $ALT_COMMIT on origin/main";  check "  reads the same safe override" 0 "$r"
feedrun KHAOZENGINE_FEED="$ALT" --feed "$FEED"
says "nothing to check";   check "  and --feed still beats it" 0 "$r"
absent_says "9.9.9";       check "  without reading the override" 0 "$r"

echo "== KHAOZENGINE_FEED: a relative override resolves against the toplevel the scripts cd to =="
WTREAL=$(cd "$WT" && pwd -P)
mkdir -p "$WT/relfeed"
package KhaozEngine.App 8.8.8 "$ALT_COMMIT" "$WT/relfeed"
packrun KHAOZENGINE_FEED=relfeed
check "wrapper succeeds with a relative override" 0 "$rc"
says "dotnet pack -c Release -o $WTREAL/relfeed";  check "  packs into it, made absolute" 0 "$r"
feedrun KHAOZENGINE_FEED=relfeed
says "STAGED     8.8.8  commit $ALT_COMMIT on origin/main";  check "  the report reads the same place" 0 "$r"
mkdir -p "$WT/sub"
set +e
( cd "$WT/sub" && env -u PACK_RELEASED_OK KHAOZENGINE_FEED=relfeed sh ../scripts/pack-local-feed.sh --dry-run ) >"$OUTFILE" 2>&1
rc=$?
set -e
check "  from a subdirectory too" 0 "$rc"
says "dotnet pack -c Release -o $WTREAL/relfeed";  check "  the toplevel is the base" 0 "$r"
absent_says "$WTREAL/sub/relfeed";                  check "  not the directory it ran from" 0 "$r"

echo "== a layout with no main checkout to find refuses rather than guessing a feed =="
newfixture sepdir 2.0.0
( cd "$REPO" && $GIT init -q --separate-git-dir "$TMPROOT/sepdir-gitdir" . >/dev/null 2>&1 )
packrun
check "wrapper refuses under a separate git dir" 1 "$rc"
says "set KHAOZENGINE_FEED";  check "  and asks for the override" 0 "$r"
absent_says "dotnet pack";    check "  never reaching the pack command" 0 "$r"
packrun KHAOZENGINE_FEED="$TMPROOT/sepfeed"
check "  the override gets it through" 0 "$rc"
feedrun
check "report refuses the same layout as a usage error" 2 "$rc"
feedrun KHAOZENGINE_FEED="$TMPROOT/sepfeed"
check "  and the override gets it through" 0 "$rc"

if ! command -v jq >/dev/null 2>&1; then
  echo "== hook cases SKIPPED (no jq on PATH) =="
else
  echo "== hook: denies the remembered raw command in the released state =="
  newfixture hookreleased 2.0.0
  tagit 2.0.0
  advance
  hookrun "cd $REPO && dotnet pack -c Release -o ./local-feed"
  check "hook exits 0 (it decides by output, never by status)" 0 "$rc"
  denied;  check "  the raw ritual command is denied" 0 "$r"
  says "already a released tag";   check "  the deny reason carries the explanation" 0 "$r"
  says "pack-local-feed.sh";       check "  and points at the wrapper" 0 "$r"

  PRIVATE_FEED="$TMPROOT/hook-private"
  hookrun_feed "$PRIVATE_FEED" "cd $REPO && dotnet pack -c Release -o \"\$KHAOZENGINE_FEED\""
  denied;  check "  a literal KHAOZENGINE_FEED output is denied" 0 "$r"
  hookrun_feed "$PRIVATE_FEED" "cd $REPO && dotnet pack -c Release --output \"\$KHAOZENGINE_FEED\""
  denied;  check "  the long output form with a separate value is denied" 0 "$r"
  hookrun_feed "$PRIVATE_FEED" "cd $REPO && dotnet pack -c Release -o=\"\$KHAOZENGINE_FEED\""
  denied;  check "  the short output equals form is denied" 0 "$r"
  hookrun_feed "$PRIVATE_FEED" "cd $REPO && dotnet pack -c Release --output=\"\$KHAOZENGINE_FEED\""
  denied;  check "  the long output form with a literal KHAOZENGINE_FEED is denied" 0 "$r"
  hookrun_feed "$PRIVATE_FEED" "cd $REPO && dotnet pack -c Release -o $PRIVATE_FEED"
  denied;  check "  an expanded KHAOZENGINE_FEED output is denied" 0 "$r"
  hookrun_feed "$PRIVATE_FEED" "cd $REPO && dotnet pack -c Release -o \"$PRIVATE_FEED\""
  denied;  check "  a quoted expanded KHAOZENGINE_FEED output is denied" 0 "$r"

  echo "== hook: the allow paths =="
  hookrun "cd $REPO && PACK_RELEASED_OK=1 dotnet pack -c Release -o ./local-feed"
  allowed;  check "an inline override is allowed through" 0 "$r"
  hookrun "cd $REPO && dotnet pack -c Release -o ./artifacts --no-build"
  allowed;  check "a pack that misses local-feed entirely is not this hook's business" 0 "$r"
  hookrun_feed "$PRIVATE_FEED" "cd $REPO && dotnet pack -c Release -p:RestoreSources=\"\$KHAOZENGINE_FEED\" -o ./artifacts"
  allowed;  check "a feed mentioned outside the output option does not redirect the pack" 0 "$r"
  hookrun "cd $REPO && sh scripts/pack-local-feed.sh"
  allowed;  check "the wrapper is left to speak for itself" 0 "$r"
  hookrun "cd $REPO && git commit -m 'chore: dotnet pack into local-feed later'"
  allowed;  check "a quoted mention of the command is not the command" 0 "$r"
  hookrun_feed "$REAL/local-feed" "cd $REPO && git commit -m 'chore: dotnet pack -o \"\$KHAOZENGINE_FEED\" later'"
  allowed;  check "a quoted KHAOZENGINE_FEED mention is not the command" 0 "$r"
  hookrun_feed "$PRIVATE_FEED" "cd $REPO && git commit -m 'chore: dotnet pack -o \"$PRIVATE_FEED\" later'"
  allowed;  check "a quoted expanded feed mention is not the command" 0 "$r"
  hookrun_feed "$PRIVATE_FEED" "cd $REPO && PACK_RELEASED_OK=1 dotnet pack -c Release -o \"\$KHAOZENGINE_FEED\""
  allowed;  check "the release override allows a configured private feed" 0 "$r"
  hookrun "cd $REPO && dotnet build"
  allowed;  check "an unrelated dotnet command is untouched" 0 "$r"

  echo "== hook: a raw pack from a linked worktree into the main checkout's feed is still caught =="
  newfixture hookworktree 2.0.0
  addworktree
  tagit 2.0.0
  ( cd "$WT" && echo "more" >> README.md && $GIT add -A && $GIT commit -q --no-verify -m "chore: more" )
  hookrun "cd $WT && dotnet pack -c Release -o $REAL/local-feed"
  denied;  check "the raw command aimed at the resolved feed is denied" 0 "$r"

  echo "== hook: an untagged unmerged HEAD cannot write the shared feed =="
  newfixture hookunmerged 2.0.0
  addworktree
  ( cd "$WT" && echo "branch" >> README.md && $GIT add -A && $GIT commit -q --no-verify -m "chore: branch" )
  hookrun "cd $WT && dotnet pack -c Release -o $REAL/local-feed"
  denied;  check "the raw staged pack from an unmerged HEAD is denied" 0 "$r"
  says "not an ancestor of origin/main";  check "  the deny names the ancestry failure" 0 "$r"
  hookrun "cd $WT && PACK_RELEASED_OK=1 dotnet pack -c Release -o $REAL/local-feed"
  denied;  check "  PACK_RELEASED_OK does not bypass the shared-feed boundary" 0 "$r"
  hookrun_feed "$REAL/local-feed" "cd $WT && dotnet pack -c Release -o \"\$KHAOZENGINE_FEED\""
  denied;  check "  a configured shared feed keeps the ancestry guard" 0 "$r"
  UNMERGED_PRIVATE="$TMPROOT/hook-unmerged-private"
  hookrun_feed "$UNMERGED_PRIVATE" "cd $WT && dotnet pack -c Release -o \"\$KHAOZENGINE_FEED\""
  allowed;  check "  a configured private feed allows the unmerged HEAD" 0 "$r"

  echo "== hook: an untagged dirty tree cannot write the shared feed =="
  newfixture hookdirty 2.0.0
  echo "dirty" >> "$REPO/README.md"
  hookrun "cd $REPO && dotnet pack -c Release -o $REAL/local-feed"
  denied;  check "the raw staged pack from a dirty tree is denied" 0 "$r"
  says "the worktree is not clean";  check "  the deny names uncommitted package bytes" 0 "$r"
  hookrun_feed "$REAL/local-feed" "cd $REPO && dotnet pack -c Release -o \"\$KHAOZENGINE_FEED\""
  denied;  check "  a configured shared feed keeps the cleanliness guard" 0 "$r"
  DIRTY_HOOK_PRIVATE="$TMPROOT/hook-dirty-private"
  hookrun_feed "$DIRTY_HOOK_PRIVATE" "cd $REPO && dotnet pack -c Release -o \"\$KHAOZENGINE_FEED\""
  allowed;  check "  a configured private feed allows the dirty tree" 0 "$r"

  echo "== hook: stays silent while the version is staged =="
  newfixture hookstaged 2.0.0
  hookrun "cd $REPO && dotnet pack -c Release -o ./local-feed"
  allowed;  check "the ordinary ritual pack is never denied" 0 "$r"
fi

echo ""
echo "passed: $pass   failed: $fail"
[ "$fail" -eq 0 ]
