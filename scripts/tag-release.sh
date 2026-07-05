#!/bin/sh
# tag-release.sh - create the annotated release tag v<version> with a canonical
# 'area(<version>): summary' message, validated against the shared tag standard so releases
# stop drifting in format. area+summary default from the HEAD commit subject (already
# conventional-commit); override either with positional args.
#
#   scripts/tag-release.sh                       # mirror the HEAD subject
#   scripts/tag-release.sh audio "loads .ogg"    # explicit area + summary
#
# It creates the tag only. Push it yourself (the tag push is what triggers the release).
set -eu
cd "$(git rev-parse --show-toplevel)"
. scripts/tag-standard.sh

knob=$(tag_version_knob)
ver=$(tag_props_version < Directory.Build.props 2>/dev/null || true)
[ -n "${ver:-}" ] || { echo "tag-release: could not read <$knob> from Directory.Build.props." >&2; exit 1; }
tag="v$ver"

if git rev-parse -q --verify "refs/tags/$tag" >/dev/null 2>&1; then
  echo "tag-release: $tag already exists locally. Bump <$knob> to a free version first." >&2
  exit 1
fi

# Defaults from the HEAD subject when it is already 'area[(scope)]: summary'.
hs=$(git log -1 --format='%s' 2>/dev/null || true)
harea=''; hsum=''
if printf '%s' "$hs" | grep -Eq '^[a-z0-9][a-z0-9._-]*(\([^)]*\))?: .+$'; then
  harea=$(printf '%s' "$hs" | sed -E 's/^([a-z0-9][a-z0-9._-]*).*$/\1/')
  hsum=$(printf '%s' "$hs" | sed -E 's/^[^:]*: //')
fi

area=${1:-}
[ $# -gt 0 ] && shift
summary=${*:-}
[ -n "$area" ] || area=$harea
[ -n "$area" ] || area=release
[ -n "$summary" ] || summary=$hsum
[ -n "$summary" ] || { echo "tag-release: no summary from HEAD; pass one: scripts/tag-release.sh <area> <summary...>" >&2; exit 1; }

msg="$area($ver): $summary"
if ! tag_msg_ok "$msg" "$ver"; then
  echo "tag-release: assembled message fails the standard: $msg" >&2
  echo "             need 'area($ver): summary' (lowercase area, no em/en dashes)." >&2
  exit 1
fi

git tag -a "$tag" -m "$msg"
echo "tag-release: created annotated $tag"
echo "             $msg"
echo "next: push main, then  git push origin $tag   (the tag push triggers the release)"
