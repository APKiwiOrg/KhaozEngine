#!/bin/sh
# check-local-feed.sh - report whether tagged versions in local-feed were built from their tag commit,
# so a consumer about to vendor from the feed can see drift before it ships. POSIX sh.
#
#   scripts/check-local-feed.sh            # report, always exits 0 (informational, like ledger.sh)
#   scripts/check-local-feed.sh --strict   # exit 1 when any tagged version is DRIFTED
#   scripts/check-local-feed.sh --feed DIR # read DIR instead of the default feed
#
# The default feed is the one scripts/pack-local-feed.sh writes: KHAOZENGINE_FEED when set, otherwise the
# MAIN checkout's local-feed, from a linked worktree too (pack_feed_dir in scripts/pack-standard.sh).
# A relative DIR or KHAOZENGINE_FEED resolves against this tree's toplevel, not the directory you ran from.
#
# Statuses, per version present in the feed:
#   STAGED     no v<version> tag yet. The ordinary in-flight state, nothing to see.
#   RELEASED   tagged, and every package's nuspec commit matches the tag commit.
#   DRIFTED    tagged, and at least one package has a missing or different nuspec commit. The feed holds
#              a build the tag does not describe (#492, #1136). GitHub Packages still has the published
#              copy, so recover from there or from a checkout of the tag.
#
# The package stamp is the repository commit written by the .NET SDK into the nuspec. The release stamp
# is the annotated tag peeled to its commit. Package modification times remain informational only.
# scripts/pack-local-feed.sh is the prevention. This is detection for a feed that already drifted.
set -eu
cd "$(git rev-parse --show-toplevel)"
. scripts/pack-standard.sh

strict=0
feed=''
while [ $# -gt 0 ]; do
  case "$1" in
    --strict) strict=1; shift ;;
    --feed) feed=${2:-}; shift 2 2>/dev/null || { echo "check-local-feed: --feed needs a directory." >&2; exit 2; } ;;
    --help|-h) sed -n '2,22p' "$0" | sed 's/^# \{0,1\}//'; exit 0 ;;
    *) echo "check-local-feed: unknown argument '$1'." >&2; exit 2 ;;
  esac
done
if [ -z "$feed" ]; then
  feed=$(pack_feed_dir) || {
    echo "check-local-feed: cannot locate the main checkout's local-feed from this repository layout." >&2
    echo "check-local-feed: set KHAOZENGINE_FEED or pass --feed." >&2
    exit 2
  }
fi

if [ ! -d "$feed" ]; then
  echo "check-local-feed: no feed at '$feed' (set KHAOZENGINE_FEED or pass --feed); nothing to check."
  exit 0
fi

# Human-readable stamp for an epoch. BSD date takes -r, GNU date takes -d @.
stamp() {
  date -r "$1" '+%Y-%m-%d %H:%M' 2>/dev/null || date -d "@$1" '+%Y-%m-%d %H:%M' 2>/dev/null || printf '%s' "$1"
}

# package_commit <nupkg> -> the repository commit from the package's nuspec, or empty when absent.
package_commit() {
  _pc_file=$1
  unzip -p "$_pc_file" '*.nuspec' 2>/dev/null \
    | sed -n '/<repository[[:space:]][^>]*>/s/.*commit="\([^"]*\)".*/\1/p' \
    | head -1
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

staged=0; released=0; drifted=0
for v in $versions; do
  # Newest pack time across every file of this version, so a partial re-pack of a single package still
  # shows up rather than being averaged away by its untouched siblings.
  newest=0
  for f in "$feed"/KhaozEngine.*."$v".nupkg "$feed"/KhaozEngine.*."$v".snupkg; do
    [ -f "$f" ] || continue
    m=$(pack_file_mtime "$f")
    [ -n "${m:-}" ] || continue
    if [ "$m" -gt "$newest" ]; then newest=$m; fi
  done
  tagcommit=$(git rev-parse -q --verify "refs/tags/v$v^{commit}" 2>/dev/null || true)
  if [ -z "${tagcommit:-}" ]; then
    echo "  STAGED     $v  packed $(stamp "$newest")  (no v$v tag)"
    staged=$((staged + 1))
  else
    version_drifted=0
    drift_lines=''
    for f in "$feed"/KhaozEngine.*."$v".nupkg "$feed"/KhaozEngine.*."$v".snupkg; do
      [ -f "$f" ] || continue
      packagecommit=$(package_commit "$f")
      if [ "${packagecommit:-}" != "$tagcommit" ]; then
        version_drifted=1
        if [ -n "${packagecommit:-}" ]; then
          detail="$(basename "$f"): commit $packagecommit"
        else
          detail="$(basename "$f"): no repository commit in nuspec"
        fi
        drift_lines="${drift_lines}${drift_lines:+
}$detail"
      fi
    done
    if [ "$version_drifted" = 1 ]; then
      echo "  DRIFTED    $v  tag commit $tagcommit  packed $(stamp "$newest")"
      printf '%s\n' "$drift_lines" | sed 's/^/             /'
      drifted=$((drifted + 1))
    else
      echo "  RELEASED   $v  commit $tagcommit  packed $(stamp "$newest")"
      released=$((released + 1))
    fi
  fi
done

echo "check-local-feed: $staged staged, $released released, $drifted drifted (feed: $feed)."
if [ "$drifted" -ne 0 ]; then
  echo "check-local-feed: a DRIFTED version contains a build its tag does not describe (#492, #1136)." >&2
  echo "                  Do not vendor it into a consumer. Re-pack from a checkout of the tag or" >&2
  echo "                  restore the published copy from GitHub Packages." >&2
  if [ "$strict" = 1 ]; then exit 1; fi
fi
exit 0
