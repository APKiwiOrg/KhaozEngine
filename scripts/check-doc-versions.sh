#!/usr/bin/env bash
#
# Guards against the docs drifting behind the engine version.
#
# Source of truth: <KhaozEngineVersion> in Directory.Build.props. This single version line IS
# the engine, so the "engine current version" the docs declare must equal that. A few docs declare
# that version in prose; this script checks exactly those declarations and nothing else.
#
# NOTE: consumer *pins* (e.g. "Hardpoint ... On 5.38.0") legitimately lag the engine version and
# are deliberately NOT checked - a game adopts a release on its own schedule. Only the "this is the
# current release" claims are enforced.
#
# Run locally: ./scripts/check-doc-versions.sh   (also runs in CI)

case "${BASH_VERSION:-}:${SHELLOPTS:-}" in
  :*|*:posix|*:posix:*)
    printf '%s\n' 'Run this script with bash: bash scripts/check-doc-versions.sh' >&2
    exit 2
    ;;
esac

set -euo pipefail
cd "$(dirname "$0")/.."

# The single version reader lives in tag-standard.sh (props_version); never hand-roll the grep here.
. scripts/tag-standard.sh

ver=$(props_version KhaozEngineVersion < Directory.Build.props)
if [ -z "$ver" ]; then
  echo "could not read <KhaozEngineVersion> from Directory.Build.props" >&2
  exit 1
fi
echo "engine version (Directory.Build.props KhaozEngineVersion): $ver"

fail=0
expect() {
  # $1 = file, $2 = human label, $3 = version the doc currently claims (may be empty)
  local file="$1" label="$2" claimed="$3"
  if [ -z "$claimed" ]; then
    echo "FAIL  $file: could not find the '$label' declaration"
    fail=1
  elif [ "$claimed" != "$ver" ]; then
    echo "FAIL  $file: $label says '$claimed', engine is '$ver'"
    fail=1
  else
    echo "ok    $file: $label = $ver"
  fi
}

# docs/ROADMAP.md used to declare "Current released version: **X.Y.Z**" and was checked here. The
# roadmap is now GitHub issues (label kind/roadmap) and the file is gone, so there is no longer a
# prose copy of the version to drift. Deliberately NOT repointed at Directory.Build.props: that is
# where $ver is already read from, so the check would compare the file to itself and pass forever.
#
# README.md + docs/USING-KHAOZENGINE.md  ->  the copy-paste <PackageReference> examples (show the current
# release). Both carry the same paste-ready snippet and drift the same way, so check every such line in each.
for doc in README.md docs/USING-KHAOZENGINE.md; do
  while read -r v; do
    expect "$doc" "PackageReference example version" "$v"
  done < <(grep -E 'PackageReference Include="KhaozEngine' "$doc" | grep -oE 'Version="[^"]+"' | sed -E 's/Version="([^"]+)"/\1/')
done

# CHANGELOG.md must actually DOCUMENT this version: its newest "## X.Y.Z" heading has to be the current one.
# The pre-commit and PostToolUse hooks only check the file was touched, not that it names the new version, so
# a bump with an unrelated changelog edit sails through them. This closes that gap.
newest=$(grep -m1 -E '^## ' CHANGELOG.md | sed -E 's/^##[[:space:]]+//; s/[[:space:]]+$//' || true)
if [ "$newest" = "$ver" ]; then
  echo "ok    CHANGELOG.md: newest entry heading = ## $ver"
else
  echo "FAIL  CHANGELOG.md: newest entry heading is '## $newest', engine is '$ver' (add a '## $ver' section at the top)"
  fail=1
fi

# --- Package inventory guard -------------------------------------------------
# Every packable KhaozEngine.* project must (a) have a row in the docs/PACKAGES.md catalog
# whose first cell is [<PackageId>](../<project dir>/README.md) and (b) ship its own README.md
# as the nupkg PackageReadmeFile. This is what catches a new package landing undocumented
# (8.x shipped Imaging/Snapshot/Snapshot.Render3D invisible for several releases). The reverse
# check catches a row left behind for a package that was deleted or made non-packable, and a
# row whose label names one package while its link points at another. Rows are table lines
# outside HTML comments, so a commented-out row does not count.
catalog=docs/PACKAGES.md
catalog_rows=$(awk '
  /<!--/ { in_comment=1 }
  !in_comment && /^\| \[KhaozEngine\.[A-Za-z0-9.]+\]\(\.\.\/KhaozEngine\.[A-Za-z0-9.]+\/README\.md\) \|/ {
    match($0, /^\| \[[^]]+\]\(\.\.\/[^)]+\)/)
    cell = substr($0, 4, RLENGTH - 3)
    label = cell; sub(/\].*/, "", label)
    target = cell; sub(/^[^(]*\(\.\.\//, "", target); sub(/\/README\.md\)$/, "", target)
    print label "|" target
  }
  /-->/ { in_comment=0 }
' "$catalog" | sort)
packable_rows=""
for csproj in KhaozEngine.*/KhaozEngine.*.csproj; do
  dir=$(dirname "$csproj")
  # skip non-packable projects (tests, probes, the bundled validator)
  if grep -q '<IsPackable>false</IsPackable>' "$csproj"; then continue; fi
  pkgid=$(grep -oE '<PackageId>[^<]+</PackageId>' "$csproj" | sed -E 's#</?PackageId>##g' | head -1)
  [ -z "$pkgid" ] && pkgid=$(basename "$dir")
  packable_rows="$packable_rows$pkgid|$dir"$'\n'
  if ! grep -Fxq "$pkgid|$dir" <<< "$catalog_rows"; then
    echo "FAIL  $catalog: packable package $pkgid has no catalog row [$pkgid](../$dir/README.md)"
    fail=1
  fi
  if [ ! -f "$dir/README.md" ] || ! grep -q '<PackageReadmeFile>' "$csproj"; then
    echo "FAIL  $dir: packable package $pkgid is missing README.md / <PackageReadmeFile>"
    fail=1
  fi
done
while IFS= read -r row; do
  [ -z "$row" ] && continue
  if ! grep -Fxq "$row" <<< "$packable_rows"; then
    echo "FAIL  $catalog: row [${row%%|*}](../${row#*|}/README.md) does not name a packable package and its own directory"
    fail=1
  fi
done <<< "$catalog_rows"
while IFS= read -r dup; do
  [ -z "$dup" ] && continue
  echo "FAIL  $catalog: ${dup%%|*} has more than one catalog row"
  fail=1
done < <(uniq -d <<< "$catalog_rows")

# --- Umbrella membership guard ---------------------------------------------
# Each umbrella csproj is the authority on the packages it carries. Keep every
# direct reference visible in the Pulls in list shipped inside that package.
check_umbrella_membership() {
  local umbrella="$1"
  local project="KhaozEngine.$umbrella/KhaozEngine.$umbrella.csproj"
  local readme="KhaozEngine.$umbrella/README.md"
  local pulls_in package needle

  pulls_in=$(awk '
    /^Pulls in:$/ { found=1; next }
    found && /^- / { started=1; print; next }
    found && started && /^  / { print; next }
    found && started { exit }
  ' "$readme")

  while IFS= read -r reference; do
    package=$(basename "$(dirname "$reference")")
    needle="\`$package\`"
    if ! grep -Fq "$needle" <<< "$pulls_in"; then
      echo "FAIL  $readme: Pulls in list is missing $package from $project"
      fail=1
    fi
  done < <(sed -n 's#.*ProjectReference Include="\([^"]*\)".*#\1#p' "$project")
}

for umbrella in Foundation Game2D Game3D Server; do
  check_umbrella_membership "$umbrella"
done

if [ "$fail" -ne 0 ]; then
  echo
  echo "Documentation drift detected. Fix the failures above." >&2
  exit 1
fi
echo "all engine-version declarations match $ver, and package inventory plus umbrella membership are documented"
