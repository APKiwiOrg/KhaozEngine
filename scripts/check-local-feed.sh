#!/bin/sh
# check-local-feed.sh - report which versions sitting in local-feed are RELEASED-AND-RE-PACKED, so a
# consumer about to vendor from the feed can see the hazard before it ships. POSIX sh.
#
#   scripts/check-local-feed.sh            # report, always exits 0 (informational, like ledger.sh)
#   scripts/check-local-feed.sh --strict   # exit 1 when any version is RE-PACKED
#   scripts/check-local-feed.sh --feed DIR # read DIR instead of ./local-feed (or set KHAOZENGINE_FEED)
#
# Statuses, per version present in the feed:
#   STAGED     no v<version> tag yet. The ordinary in-flight state, nothing to see.
#   RELEASED   tagged, and the feed's newest package for it predates the tag. The feed and the tag agree.
#   RE-PACKED  tagged, and the feed's newest package for it was written AFTER the tag. The feed holds a
#              build the tag does not describe (#492). GitHub Packages still has the published copy, so
#              the recovery is to re-vendor from there or from a checkout of the tag, never to trust
#              these bytes as "what vX.Y.Z is".
#
# The pack time is the nupkg's mtime and the release time is the annotated tag's own taggerdate, so this
# reads the two events the hazard is actually made of rather than inferring anything from contents.
# scripts/pack-local-feed.sh is the prevention. This is the detection, for a feed that already drifted.
set -eu
cd "$(git rev-parse --show-toplevel)"
. scripts/pack-standard.sh

strict=0
feed=${KHAOZENGINE_FEED:-local-feed}
while [ $# -gt 0 ]; do
  case "$1" in
    --strict) strict=1; shift ;;
    --feed) feed=${2:-}; shift 2 2>/dev/null || { echo "check-local-feed: --feed needs a directory." >&2; exit 2; } ;;
    --help|-h) sed -n '2,20p' "$0" | sed 's/^# \{0,1\}//'; exit 0 ;;
    *) echo "check-local-feed: unknown argument '$1'." >&2; exit 2 ;;
  esac
done

if [ ! -d "$feed" ]; then
  echo "check-local-feed: no feed at '$feed' (set KHAOZENGINE_FEED or pass --feed); nothing to check."
  exit 0
fi

# Human-readable stamp for an epoch. BSD date takes -r, GNU date takes -d @.
stamp() {
  date -r "$1" '+%Y-%m-%d %H:%M' 2>/dev/null || date -d "@$1" '+%Y-%m-%d %H:%M' 2>/dev/null || printf '%s' "$1"
}

# Every X.Y.Z carried by a KhaozEngine package file in the feed, deduplicated, newest first. The
# greedy prefix is what makes an id with digits in it (KhaozEngine.Gpu.D3D11) split at the right dot.
versions=$(ls -1 "$feed" 2>/dev/null \
  | grep -E '^KhaozEngine\..*\.[0-9]+\.[0-9]+\.[0-9]+\.s?nupkg$' \
  | sed -E 's/\.s?nupkg$//' \
  | sed -E 's/.*\.([0-9]+\.[0-9]+\.[0-9]+)$/\1/' \
  | sort -u -V -r || true)

if [ -z "${versions:-}" ]; then
  echo "check-local-feed: $feed holds no KhaozEngine X.Y.Z packages; nothing to check."
  exit 0
fi

staged=0; released=0; repacked=0
for v in $versions; do
  # Newest pack time across every file of this version, so a partial re-pack of a single package still
  # shows up rather than being averaged away by its untouched siblings.
  newest=0
  newest_file=''
  for f in "$feed"/KhaozEngine.*."$v".nupkg "$feed"/KhaozEngine.*."$v".snupkg; do
    [ -f "$f" ] || continue
    m=$(pack_file_mtime "$f")
    [ -n "${m:-}" ] || continue
    if [ "$m" -gt "$newest" ]; then newest=$m; newest_file=$f; fi
  done
  tagtime=$(pack_tag_time "v$v")
  if [ -z "${tagtime:-}" ]; then
    echo "  STAGED     $v  packed $(stamp "$newest")  (no v$v tag)"
    staged=$((staged + 1))
  elif [ "$newest" -gt "$tagtime" ]; then
    echo "  RE-PACKED  $v  packed $(stamp "$newest")  AFTER  v$v tagged $(stamp "$tagtime")"
    echo "             newest: $(basename "$newest_file")"
    repacked=$((repacked + 1))
  else
    echo "  RELEASED   $v  packed $(stamp "$newest")  before v$v tagged $(stamp "$tagtime")"
    released=$((released + 1))
  fi
done

echo "check-local-feed: $staged staged, $released released, $repacked re-packed (feed: $feed)."
if [ "$repacked" -ne 0 ]; then
  echo "check-local-feed: a RE-PACKED version is a build its tag does not describe (#492). Do not vendor" >&2
  echo "                  it into a consumer: re-pack from a checkout of the tag, or restore the" >&2
  echo "                  published copy from GitHub Packages. Prevention is scripts/pack-local-feed.sh." >&2
  if [ "$strict" = 1 ]; then exit 1; fi
fi
exit 0
