#!/bin/sh
# Exercises the local-feed pack guard (issue #492) against throwaway fixture repos in a temp dir: the
# wrapper scripts/pack-local-feed.sh in --dry-run, the report scripts/check-local-feed.sh, and the
# PreToolUse deny scripts/hooks/pack-release-guard.sh.
#
# The regression it pins down is the #492 window: the ritual packs whatever <KhaozEngineVersion> says,
# that version does not move until someone bumps it, so every finish between a tag and the next bump
# re-packs an already-released number and the feed's copy stops matching the tag everybody reads.
#
# Fixture-only by construction: every repo, tag and package below is scratch, made with git init and
# touch under mktemp. Nothing here reads or writes the real checkout, the real local-feed, or a real
# tag, and --dry-run means dotnet is never invoked.
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
says() { grep -qF "$1" "$OUTFILE" && r=0 || r=1; }
absent_says() { grep -qF "$1" "$OUTFILE" && r=1 || r=0; }

# newfixture <name> <version> -> a scratch repo carrying the scripts under test and that engine
# version, with one commit and a clean tree. Sets REPO and FEED.
newfixture() {
  REPO="$TMPROOT/$1"
  FEED="$REPO/local-feed"
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
  # local-feed is gitignored in the real repo, and the fixture needs the same, or a packed file would
  # make the tree dirty and turn every at-tag case into at-tag-dirty by accident.
  echo 'local-feed/' > "$REPO/.gitignore"
  ( cd "$REPO" && $GIT init -q . >/dev/null 2>&1 && $GIT add -A && $GIT commit -q --no-verify -m "chore: fixture" )
}

# tagit <version> -> annotated tag v<version> at the fixture's HEAD.
tagit() { ( cd "$REPO" && $GIT tag -a "v$1" -m "release($1): fixture" ); }

# advance -> one more commit on top, leaving the tree clean and HEAD off the tag.
advance() { ( cd "$REPO" && echo "more" >> README.md && $GIT add -A && $GIT commit -q --no-verify -m "chore: more" ); }

# packrun [VAR=VALUE ...] -> the fixture's wrapper in --dry-run, never invoking dotnet.
packrun() {
  set +e
  ( cd "$REPO" && env -u PACK_RELEASED_OK "$@" sh scripts/pack-local-feed.sh --dry-run ) >"$OUTFILE" 2>&1
  rc=$?
  set -e
}

# feedrun [args...] -> the fixture's feed report.
feedrun() {
  set +e
  ( cd "$REPO" && sh scripts/check-local-feed.sh "$@" ) >"$OUTFILE" 2>&1
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
denied() { grep -q '"permissionDecision":"deny"' "$OUTFILE" && r=0 || r=1; }
allowed() { [ -s "$OUTFILE" ] && r=1 || r=0; }

echo "== staged: the version carries no tag, so the ritual pack is the ordinary case =="
newfixture staged 2.0.0
packrun
check "wrapper succeeds" 0 "$rc"
says "2.0.0 is staged";        check "  says the version is staged" 0 "$r"
says "dotnet pack -c Release -o ./local-feed"
check "  prints the command it would run" 0 "$r"
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
says "dotnet pack -c Release -o ./local-feed"
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
check "  and the override still gets through" 0 "$rc"

echo "== check-local-feed: classifies staged, released and re-packed versions =="
newfixture feed 3.0.0
tagit 1.0.0
tagit 1.1.0
touch "$FEED/KhaozEngine.App.1.0.0.nupkg" "$FEED/KhaozEngine.Gui.1.0.0.nupkg"
touch "$FEED/KhaozEngine.App.1.1.0.nupkg"
touch "$FEED/KhaozEngine.Gpu.D3D11.3.0.0.nupkg" "$FEED/KhaozEngine.Gpu.D3D11.3.0.0.snupkg"
# The two tags were just created, so a package stamped in 2000 predates its release and one stamped in
# 2099 was written after it. Absolute stamps rather than relative ones, so the case does not depend on
# clock resolution.
touch -t 200001010000 "$FEED/KhaozEngine.App.1.0.0.nupkg" "$FEED/KhaozEngine.Gui.1.0.0.nupkg"
touch -t 209901010000 "$FEED/KhaozEngine.App.1.1.0.nupkg"
feedrun
check "report succeeds without --strict" 0 "$rc"
says "RELEASED   1.0.0";   check "  a version packed before its tag is RELEASED" 0 "$r"
says "RE-PACKED  1.1.0";   check "  a version packed after its tag is RE-PACKED" 0 "$r"
says "STAGED     3.0.0";   check "  an untagged version is STAGED" 0 "$r"
says "1 staged, 1 released, 1 re-packed"
check "  the summary counts all three" 0 "$r"
says "KhaozEngine.App.1.1.0.nupkg"
check "  and names the offending file" 0 "$r"
feedrun --strict
check "--strict fails on a re-packed version" 1 "$rc"

echo "== check-local-feed: a clean feed and a missing feed are both quiet successes =="
newfixture cleanfeed 3.0.0
tagit 1.0.0
touch "$FEED/KhaozEngine.App.1.0.0.nupkg"
touch -t 200001010000 "$FEED/KhaozEngine.App.1.0.0.nupkg"
feedrun --strict
check "--strict passes when nothing was re-packed" 0 "$rc"
rm -rf "$FEED"
feedrun --strict
check "no feed at all is not a failure" 0 "$rc"
says "nothing to check";  check "  and says so" 0 "$r"

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

  echo "== hook: the allow paths =="
  hookrun "cd $REPO && PACK_RELEASED_OK=1 dotnet pack -c Release -o ./local-feed"
  allowed;  check "an inline override is allowed through" 0 "$r"
  hookrun "cd $REPO && dotnet pack -c Release -o ./artifacts --no-build"
  allowed;  check "a pack that misses local-feed entirely is not this hook's business" 0 "$r"
  hookrun "cd $REPO && sh scripts/pack-local-feed.sh"
  allowed;  check "the wrapper is left to speak for itself" 0 "$r"
  hookrun "cd $REPO && git commit -m 'chore: dotnet pack into local-feed later'"
  allowed;  check "a quoted mention of the command is not the command" 0 "$r"
  hookrun "cd $REPO && dotnet build"
  allowed;  check "an unrelated dotnet command is untouched" 0 "$r"

  echo "== hook: stays silent while the version is staged =="
  newfixture hookstaged 2.0.0
  hookrun "cd $REPO && dotnet pack -c Release -o ./local-feed"
  allowed;  check "the ordinary ritual pack is never denied" 0 "$r"
fi

echo ""
echo "passed: $pass   failed: $fail"
[ "$fail" -eq 0 ]
