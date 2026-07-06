#!/usr/bin/env bash
#
# Guards against the docs drifting behind the engine version.
#
# Source of truth: <KhaozEngineVersion> in Directory.Build.props. This single version line IS
# the engine (the MonoGame-free foundation packages graduated onto it at 5.46.0),
# so the "engine current version" the docs declare must equal that. A few docs declare
# that version in prose; this script checks exactly those declarations and nothing else.
#
# NOTE: the legacy 4.x <Version> (the genuinely-MonoGame packages only) is NOT checked
# here - it is frozen-ish and lags like a consumer pin. Consumer *pins* (e.g. "Hardpoint
# ... On 5.38.0") also legitimately lag the engine version and are deliberately NOT
# checked - a game adopts a release on its own schedule. Only the "this is the current
# release" claims are enforced.
#
# Run locally: ./scripts/check-doc-versions.sh   (also runs in CI)

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

# docs/ROADMAP.md  ->  Current released version: **X.Y.Z**
r=$(grep -oE 'Current released version: \*\*[^*]+\*\*' docs/ROADMAP.md | grep -oE '\*\*[^*]+\*\*' | tr -d '*' | head -1 || true)
expect docs/ROADMAP.md "current released version" "$r"

# README.md  ->  the copy-paste <PackageReference> example (shows the current release)
while read -r v; do
  expect README.md "PackageReference example version" "$v"
done < <(grep -E 'PackageReference Include="KhaozEngine' README.md | grep -oE 'Version="[^"]+"' | sed -E 's/Version="([^"]+)"/\1/')

# --- Package inventory guard -------------------------------------------------
# Every packable KhaozEngine.* project must (a) have a row in the README.md catalog
# (the package table or the umbrella table) and (b) ship its own README.md as the
# nupkg PackageReadmeFile. This is what catches a new package landing undocumented
# (8.x shipped Imaging/Snapshot/Snapshot.Render3D invisible for several releases).
for csproj in KhaozEngine.*/KhaozEngine.*.csproj; do
  dir=$(dirname "$csproj")
  # skip non-packable projects (tests, probes, the bundled validator)
  if grep -q '<IsPackable>false</IsPackable>' "$csproj"; then continue; fi
  pkgid=$(grep -oE '<PackageId>[^<]+</PackageId>' "$csproj" | sed -E 's#</?PackageId>##g' | head -1)
  [ -z "$pkgid" ] && pkgid=$(basename "$dir")
  if ! grep -q "\*\*$pkgid\*\*" README.md; then
    echo "FAIL  README.md: packable package $pkgid has no catalog row"
    fail=1
  fi
  if [ ! -f "$dir/README.md" ] || ! grep -q '<PackageReadmeFile>' "$csproj"; then
    echo "FAIL  $dir: packable package $pkgid is missing README.md / <PackageReadmeFile>"
    fail=1
  fi
done

if [ "$fail" -ne 0 ]; then
  echo
  echo "Doc version drift detected. Bump the declarations above to $ver (or fix Directory.Build.props)." >&2
  exit 1
fi
echo "all engine-version declarations match $ver; package inventory is documented"
