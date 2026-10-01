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
  _test_packable=${2:-false}
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
  printf '<Project><PropertyGroup><IsPackable>true</IsPackable></PropertyGroup></Project>\n' > src/Engine.csproj
  echo 'shipped' > src/Engine.cs
  echo 'notes' > docs/NOTES.md
  echo '{}' > tools/kit/package-lock.json
  mkdir -p KhaozEngine.TileWorld.Netcode.Tests/TileNetcode KhaozEngine.Tests
  case "$_test_packable" in
    unknown) _test_property='' ;;
    comment) _test_property='<!-- <IsPackable>false</IsPackable> -->' ;;
    multiline-comment) _test_property='<!--
<IsPackable>false</IsPackable>
-->' ;;
    conditional) _test_property='<IsPackable Condition="false">false</IsPackable>' ;;
    conditional-group) _test_property='</PropertyGroup><PropertyGroup Condition="false"><IsPackable>false</IsPackable>' ;;
    conflicting) _test_property='<IsPackable>false</IsPackable><IsPackable>true</IsPackable>' ;;
    choose) _test_property='</PropertyGroup><Choose><When Condition="&apos;$(Configuration)&apos; == &apos;Debug&apos;"><PropertyGroup><IsPackable>false</IsPackable></PropertyGroup></When></Choose><PropertyGroup>' ;;
    cdata) _test_property='<Description><![CDATA[<IsPackable>false</IsPackable>]]></Description>' ;;
    target) _test_property='</PropertyGroup><Target Name="SetPackability"><PropertyGroup><IsPackable>false</IsPackable></PropertyGroup></Target><PropertyGroup>' ;;
    import) _test_property='<IsPackable>false</IsPackable></PropertyGroup><Import Project="packable.props" /><PropertyGroup>' ;;
    root) _test_property='</PropertyGroup><IsPackable>false</IsPackable><PropertyGroup>' ;;
    item) _test_property='</PropertyGroup><ItemGroup><IsPackable>false</IsPackable></ItemGroup><PropertyGroup>' ;;
    *) _test_property="<IsPackable>$_test_packable</IsPackable>" ;;
  esac
  if [ "$_test_packable" != absent ]; then
    printf '<Project><PropertyGroup>%s</PropertyGroup></Project>\n' "$_test_property" > KhaozEngine.TileWorld.Netcode.Tests/KhaozEngine.TileWorld.Netcode.Tests.csproj
  fi
  printf '<Project><PropertyGroup><IsPackable>true</IsPackable></PropertyGroup></Project>\n' > KhaozEngine.TileWorld.Netcode.Tests/packable.props
  printf '<Project><PropertyGroup><IsPackable>false</IsPackable></PropertyGroup></Project>\n' > KhaozEngine.Tests/KhaozEngine.Tests.csproj
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

# --- 2c. main past the tag with a .gitignore edit: ALLOWED ------------------------------------------
# .gitignore decides what is TRACKED, never what a package contains. This case exists because the
# guard refused its own author pushing exactly this, one commit after the tools/ carve-out.
w=$(new_repo 1.0.0); cd "$w"
git tag -a v1.0.0 -m "release(1.0.0): first" >/dev/null 2>&1
git push --quiet origin v1.0.0 2>/dev/null
printf '.worktrees/\n' >> .gitignore
git add -A
git commit --quiet -m "governance: ignore the worktree root"
check "a gitignore edit rides a release" allowed "$(try_push main)"

# --- 2d. non-packable area and rump tests ride a release --------------------------------------------
for test_source in KhaozEngine.TileWorld.Netcode.Tests/TileNetcode/Example.cs KhaozEngine.Tests/Example.cs; do
  w=$(new_repo 1.0.0); cd "$w"
  git tag -a v1.0.0 -m "release(1.0.0): first" >/dev/null 2>&1
  git push --quiet origin v1.0.0 2>/dev/null
  echo 'test' > "$test_source"
  git add "$test_source"
  git commit --quiet -m "test: add a regression"
  check "$test_source rides a released version" allowed "$(try_push main)"
done

# A test-looking name alone proves nothing. Missing, conditional and commented properties do not
# prove that the project is non-packable either.
for test_packable in true unknown comment multiline-comment conditional conditional-group conflicting choose cdata target import root item; do
  w=$(new_repo 1.0.0 "$test_packable"); cd "$w"
  git tag -a v1.0.0 -m "release(1.0.0): first" >/dev/null 2>&1
  git push --quiet origin v1.0.0 2>/dev/null
  echo 'test' > KhaozEngine.TileWorld.Netcode.Tests/TileNetcode/Example.cs
  git add KhaozEngine.TileWorld.Netcode.Tests/TileNetcode/Example.cs
  git commit --quiet -m "test: add a regression"
  check "test-looking project with $test_packable packability is refused" refused "$(try_push main)"
done

# Turning a previously packable or unknown project into tests still changes shipped packages.
for test_packable in true unknown; do
  w=$(new_repo 1.0.0 "$test_packable"); cd "$w"
  git tag -a v1.0.0 -m "release(1.0.0): first" >/dev/null 2>&1
  git push --quiet origin v1.0.0 2>/dev/null
  printf '<Project><PropertyGroup><IsPackable>false</IsPackable></PropertyGroup></Project>\n' > KhaozEngine.TileWorld.Netcode.Tests/KhaozEngine.TileWorld.Netcode.Tests.csproj
  git commit --quiet -am "build: retire a package"
  check "changing $test_packable packability to false is refused" refused "$(try_push main)"
done

# A genuinely new non-packable test project has no old package to remove.
w=$(new_repo 1.0.0 absent); cd "$w"
git tag -a v1.0.0 -m "release(1.0.0): first" >/dev/null 2>&1
git push --quiet origin v1.0.0 2>/dev/null
printf '<Project><PropertyGroup><IsPackable>false</IsPackable></PropertyGroup></Project>\n' > KhaozEngine.TileWorld.Netcode.Tests/KhaozEngine.TileWorld.Netcode.Tests.csproj
echo 'test' > KhaozEngine.TileWorld.Netcode.Tests/TileNetcode/Example.cs
git add KhaozEngine.TileWorld.Netcode.Tests/KhaozEngine.TileWorld.Netcode.Tests.csproj KhaozEngine.TileWorld.Netcode.Tests/TileNetcode/Example.cs
git commit --quiet -m "test: add a non-packable test project"
check "a new non-packable test project rides a release" allowed "$(try_push main)"

# The push names a commit. A dirty project declaration cannot change that commit's classification.
for test_packable in false true; do
  w=$(new_repo 1.0.0 "$test_packable"); cd "$w"
  git tag -a v1.0.0 -m "release(1.0.0): first" >/dev/null 2>&1
  git push --quiet origin v1.0.0 2>/dev/null
  echo 'test' > KhaozEngine.TileWorld.Netcode.Tests/TileNetcode/Example.cs
  git add KhaozEngine.TileWorld.Netcode.Tests/TileNetcode/Example.cs
  git commit --quiet -m "test: add a regression"
  case "$test_packable" in
    false) dirty_packable=true; expected=allowed ;;
    true) dirty_packable=false; expected=refused ;;
  esac
  printf '<Project><PropertyGroup><IsPackable>%s</IsPackable></PropertyGroup></Project>\n' "$dirty_packable" > KhaozEngine.TileWorld.Netcode.Tests/KhaozEngine.TileWorld.Netcode.Tests.csproj
  check "committed $test_packable packability overrides dirty $dirty_packable" "$expected" "$(try_push main)"
done

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
