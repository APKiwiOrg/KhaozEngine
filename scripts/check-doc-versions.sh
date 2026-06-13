#!/usr/bin/env bash
#
# Guards against the docs drifting behind the engine version.
#
# Source of truth: <Version> in Directory.Build.props. A few docs declare the
# *engine current version* in prose; those must equal the source of truth. This
# script checks exactly those declarations and nothing else.
#
# NOTE: consumer *pins* (e.g. "Hardpoint ... On 4.0.0") legitimately lag the
# engine version and are deliberately NOT checked here — a game adopts a release
# on its own schedule. Only the "this is the current release" claims are enforced.
#
# Run locally: ./scripts/check-doc-versions.sh   (also runs in CI)

set -euo pipefail
cd "$(dirname "$0")/.."

ver=$(grep -oE '<Version>[^<]+</Version>' Directory.Build.props | head -1 | sed -E 's#</?Version>##g')
if [ -z "$ver" ]; then
  echo "could not read <Version> from Directory.Build.props" >&2
  exit 1
fi
echo "engine version (Directory.Build.props): $ver"

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

# docs/CONSUMERS.md  ->  **Engine current version:** `X.Y.Z`
c=$(grep -oE 'Engine current version:\*\* `[^`]+`' docs/CONSUMERS.md | grep -oE '`[^`]+`' | tr -d '`' | head -1 || true)
expect docs/CONSUMERS.md "engine current version" "$c"

# docs/ROADMAP.md  ->  Current released version: **X.Y.Z**
r=$(grep -oE 'Current released version: \*\*[^*]+\*\*' docs/ROADMAP.md | grep -oE '\*\*[^*]+\*\*' | tr -d '*' | head -1 || true)
expect docs/ROADMAP.md "current released version" "$r"

# README.md  ->  the copy-paste <PackageReference> example (shows the current release)
while read -r v; do
  expect README.md "PackageReference example version" "$v"
done < <(grep -E 'PackageReference Include="KhaozEngine' README.md | grep -oE 'Version="[^"]+"' | sed -E 's/Version="([^"]+)"/\1/')

if [ "$fail" -ne 0 ]; then
  echo
  echo "Doc version drift detected. Bump the declarations above to $ver (or fix Directory.Build.props)." >&2
  exit 1
fi
echo "all engine-version declarations match $ver"
