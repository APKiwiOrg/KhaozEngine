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
    mixed-conflict) _test_property='<IsPackable>false</IsPackable><ispackable>true</ispackable>' ;;
    casefold) _test_property='<ispackable>FALSE</ispackable>' ;;
    casefold-root) _test_property='</PropertyGroup><ispackable>FALSE</ispackable><PropertyGroup>' ;;
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

# --- 2c2. .gitattributes edits ride a release only when no changed pattern can reach shipped files ---
# Checkout attributes change the bytes of the files they match. The 21.19.0 LF pin on shader sources
# changed package content that way, so a pattern that can reach a packable project still needs a bump.
# This case exists because the guard refused an LF pin on a non-packable test fixture folder.
gitattributes_case() { # name, expected, baseline attributes line or '', changed attributes text
  w=$(new_repo 1.0.0 "${5:-false}"); cd "$w"
  if [ -n "$3" ]; then
    printf '%s\n' "$3" > .gitattributes
    git add .gitattributes
    git commit --quiet -m "governance: baseline attributes"
    git push --quiet origin main 2>/dev/null
  fi
  git tag -a v1.0.0 -m "release(1.0.0): first" >/dev/null 2>&1
  git push --quiet origin v1.0.0 2>/dev/null
  printf '%s' "$4" > .gitattributes
  git add .gitattributes
  git commit --quiet -m "governance: change attributes"
  check "$1" "$2" "$(try_push main)"
}
gitattributes_case "an LF pin on a non-packable test folder rides a release" allowed '' \
  '# Fixture goldens keep exact bytes.
KhaozEngine.TileWorld.Netcode.Tests/Fixtures/*.txt text eol=lf
'
gitattributes_case "an anchored pin on a carved-out folder rides a release" allowed '' '/docs/** text eol=lf
'
gitattributes_case "a comment-only attributes edit rides a release" allowed '# keep' '# keep, now explained
'
gitattributes_case "an LF pin on a packable project is refused" refused '' 'src/*.cs text eol=lf
'
gitattributes_case "a pattern without a folder is refused" refused '' '*.cs text eol=lf
'
gitattributes_case "a glob in the first folder is refused" refused '' '**/Fixtures/*.txt text eol=lf
'
gitattributes_case "a quoted pattern is refused" refused '' '"src/a b.cs" text eol=lf
'
gitattributes_case "removing a pin on a packable project is refused" refused 'src/*.cs text eol=lf' ''
gitattributes_case "a pin on a packable test-looking project is refused" refused '' \
  'KhaozEngine.TileWorld.Netcode.Tests/Fixtures/*.txt text eol=lf
' true

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
for test_packable in true unknown comment multiline-comment conditional conditional-group conflicting mixed-conflict choose cdata target import root item casefold-root; do
  w=$(new_repo 1.0.0 "$test_packable"); cd "$w"
  git tag -a v1.0.0 -m "release(1.0.0): first" >/dev/null 2>&1
  git push --quiet origin v1.0.0 2>/dev/null
  echo 'test' > KhaozEngine.TileWorld.Netcode.Tests/TileNetcode/Example.cs
  git add KhaozEngine.TileWorld.Netcode.Tests/TileNetcode/Example.cs
  git commit --quiet -m "test: add a regression"
  check "test-looking project with $test_packable packability is refused" refused "$(try_push main)"
done

# MSBuild property names and boolean values ignore case. XML structural names still retain it.
w=$(new_repo 1.0.0 casefold); cd "$w"
git tag -a v1.0.0 -m "release(1.0.0): first" >/dev/null 2>&1
git push --quiet origin v1.0.0 2>/dev/null
echo 'test' > KhaozEngine.TileWorld.Netcode.Tests/TileNetcode/Example.cs
git add KhaozEngine.TileWorld.Netcode.Tests/TileNetcode/Example.cs
git commit --quiet -m "test: add a regression"
check "lowercase property with uppercase false rides a release" allowed "$(try_push main)"

# Shared test imports must be proved at the same commit as the owning project. The safe fixture uses
# the actual Catalog project and allocation props that exposed the false positive in PR #1548.
new_shared_repo() {
  _shared_case=$1
  new_repo 1.0.0 > /dev/null
  mkdir -p KhaozEngine.Catalog.Tests/Upgrade test-shared outside
  cp "$SRC/../KhaozEngine.Catalog.Tests/KhaozEngine.Catalog.Tests.csproj" KhaozEngine.Catalog.Tests/
  cp "$SRC/../test-shared/AllocationMeasurement.props" test-shared/
  _shared_props=test-shared/AllocationMeasurement.props
  case "$_shared_case" in
    safe) ;;
    false) printf '<Project><PropertyGroup><IsPackable>false</IsPackable></PropertyGroup></Project>\n' > "$_shared_props" ;;
    true) printf '<Project><PropertyGroup><IsPackable>true</IsPackable></PropertyGroup></Project>\n' > "$_shared_props" ;;
    conditional) printf '<Project><ItemGroup Condition="false"><Compile Include="test.cs" /></ItemGroup></Project>\n' > "$_shared_props" ;;
    conditional-property) printf '<Project><PropertyGroup><IsPackable Condition="false">false</IsPackable></PropertyGroup></Project>\n' > "$_shared_props" ;;
    nested-import) printf '<Project><Import Project="safe.props" /></Project>\n' > "$_shared_props" ;;
    choose) printf '<Project><Choose><When Condition="false"><PropertyGroup><IsPackable>false</IsPackable></PropertyGroup></When></Choose></Project>\n' > "$_shared_props" ;;
    target) printf '<Project><Target Name="Pack" /></Project>\n' > "$_shared_props" ;;
    cdata) printf '<Project><PropertyGroup><IsPackable><![CDATA[false]]></IsPackable></PropertyGroup></Project>\n' > "$_shared_props" ;;
    sdk) printf '<Project><Sdk Name="Example.Sdk" /></Project>\n' > "$_shared_props" ;;
    sdk-attribute) printf '<Project Sdk="Example.Sdk" />\n' > "$_shared_props" ;;
    package-property) printf '<Project><PropertyGroup><GeneratePackageOnBuild>true</GeneratePackageOnBuild></PropertyGroup></Project>\n' > "$_shared_props" ;;
    package-content) printf '<Project><ItemGroup><Content Include="payload" Pack="true" /></ItemGroup></Project>\n' > "$_shared_props" ;;
    compile-pack) printf '<Project><ItemGroup><Compile Include="test.cs" Pack="true" /></ItemGroup></Project>\n' > "$_shared_props" ;;
    pack-metadata) printf '<Project><ItemGroup><Compile Include="test.cs"><Pack>true</Pack></Compile></ItemGroup></Project>\n' > "$_shared_props" ;;
    malformed) printf '<Project><ItemGroup><Compile Include="test.cs" /></Project>\n' > "$_shared_props" ;;
    missing) rm "$_shared_props" ;;
    outside|escape)
      cp "$_shared_props" outside/safe.props
      case "$_shared_case" in outside) _shared_path='../outside/safe.props' ;; escape) _shared_path='../test-shared/../outside/safe.props' ;; esac
      sed "s#../test-shared/AllocationMeasurement.props#$_shared_path#" KhaozEngine.Catalog.Tests/KhaozEngine.Catalog.Tests.csproj > "$TMPROOT/project"
      mv "$TMPROOT/project" KhaozEngine.Catalog.Tests/KhaozEngine.Catalog.Tests.csproj ;;
    normalized)
      sed 's#../test-shared/AllocationMeasurement.props#../test-shared/./AllocationMeasurement.props#' KhaozEngine.Catalog.Tests/KhaozEngine.Catalog.Tests.csproj > "$TMPROOT/project"
      mv "$TMPROOT/project" KhaozEngine.Catalog.Tests/KhaozEngine.Catalog.Tests.csproj ;;
    conditional-import)
      sed 's#<Import Project=#<Import Condition="false" Project=#' KhaozEngine.Catalog.Tests/KhaozEngine.Catalog.Tests.csproj > "$TMPROOT/project"
      mv "$TMPROOT/project" KhaozEngine.Catalog.Tests/KhaozEngine.Catalog.Tests.csproj ;;
    symlink)
      cp "$_shared_props" outside/safe.props
      rm "$_shared_props"
      ln -s ../outside/safe.props "$_shared_props" ;;
  esac
  printf '<Project />\n' > test-shared/safe.props
  git add -A
  git commit --quiet -m "test: configure shared test imports"
  printf '%s' "$(pwd)"
}

for shared_case in safe false normalized true conditional conditional-property nested-import choose target cdata sdk sdk-attribute package-property package-content compile-pack pack-metadata malformed missing outside escape conditional-import symlink; do
  w=$(new_shared_repo "$shared_case"); cd "$w"
  git tag -a v1.0.0 -m "release(1.0.0): first" >/dev/null 2>&1
  echo 'test' > KhaozEngine.Catalog.Tests/Upgrade/Example.cs
  git add KhaozEngine.Catalog.Tests/Upgrade/Example.cs
  git commit --quiet -m "test: add a catalog regression"
  case "$shared_case" in safe|false|normalized) expected=allowed ;; *) expected=refused ;; esac
  check "Catalog tests with $shared_case shared props are $expected" "$expected" "$(try_push main)"
done

# Neither a dirty import nor a safer import at the new endpoint may hide the committed packability.
for shared_case in safe true; do
  w=$(new_shared_repo "$shared_case"); cd "$w"
  git tag -a v1.0.0 -m "release(1.0.0): first" >/dev/null 2>&1
  echo 'test' > KhaozEngine.Catalog.Tests/Upgrade/Example.cs
  git add KhaozEngine.Catalog.Tests/Upgrade/Example.cs
  git commit --quiet -m "test: add a catalog regression"
  case "$shared_case" in
    safe) dirty_packable=true; expected=allowed ;;
    true) dirty_packable=false; expected=refused ;;
  esac
  printf '<Project><PropertyGroup><IsPackable>%s</IsPackable></PropertyGroup></Project>\n' "$dirty_packable" > test-shared/AllocationMeasurement.props
  check "committed $shared_case props override dirty $dirty_packable" "$expected" "$(try_push main)"
  if [ "$shared_case" = true ]; then
    sed 's#AllocationMeasurement.props#safe.props#' KhaozEngine.Catalog.Tests/KhaozEngine.Catalog.Tests.csproj > "$TMPROOT/project"
    mv "$TMPROOT/project" KhaozEngine.Catalog.Tests/KhaozEngine.Catalog.Tests.csproj
    git add KhaozEngine.Catalog.Tests/KhaozEngine.Catalog.Tests.csproj
    git commit --quiet -m "test: switch to safe shared props"
    check "previously packable shared imports remain version-bearing" refused "$(try_push main)"
  fi
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
