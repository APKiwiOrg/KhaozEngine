#!/bin/sh
# pack-local-feed.sh - the finishing ritual's pack step, with the released-version guard in front of it.
# This is the sanctioned way to run the ritual's pack, and AGENTS.md names it instead of the bare
# dotnet command it wraps:
#
#   scripts/pack-local-feed.sh              # guard, pack fresh, then replace generated files in <feed>
#   scripts/pack-local-feed.sh --dry-run    # guard only, print the command it would run
#   PACK_RELEASED_OK=1 scripts/pack-local-feed.sh    # pack anyway over a released version
#   KHAOZENGINE_FEED=DIR scripts/pack-local-feed.sh  # pack into DIR instead
#
# It packs the CURRENT tree, worktree or not. <feed> is the MAIN checkout's local-feed, the one
# consumers' scripts/refresh-engine.sh read, so a pack from a linked worktree reaches them (#1063).
# That shared feed accepts only clean commits already on origin/main (#1135). A KHAOZENGINE_FEED that
# resolves outside it is private and may carry an unmerged branch. Relative values use this tree's root.
# Extra arguments after the options are forwarded to dotnet pack unchanged.
#
# The rule it enforces, and why, is scripts/pack-standard.sh (issue #492). Short version: the ritual
# packs whatever <KhaozEngineVersion> currently says, that version does not move until someone bumps it,
# and so every finish between a tag and the next bump quietly re-packs an already-released number.
# scripts/hooks/pack-release-guard.sh is the other half, catching the raw dotnet command an agent types
# out of habit and pointing it here.
set -eu
script_path=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)/$(basename -- "$0")
cd "$(git rev-parse --show-toplevel)"
. scripts/tag-standard.sh
. scripts/pack-standard.sh

dry=0
while [ $# -gt 0 ]; do
  case "$1" in
    --dry-run|-n) dry=1; shift ;;
    --help|-h)
      sed -n '2,14p' "$script_path" | sed 's/^# \{0,1\}//'
      exit 0 ;;
    --) shift; break ;;
    *) break ;;
  esac
done

feed=$(pack_feed_dir) || {
  echo "pack-local-feed: cannot locate the main checkout's local-feed from this repository layout." >&2
  echo "pack-local-feed: set KHAOZENGINE_FEED to the feed directory and run this again." >&2
  exit 1
}
echo "pack-local-feed: feed is $feed"

ver=$(tag_props_version < Directory.Build.props 2>/dev/null || true)
[ -n "${ver:-}" ] || { echo "pack-local-feed: could not read <KhaozEngineVersion> from Directory.Build.props." >&2; exit 1; }

state=$(pack_release_state "$ver")
pack_message=''
pack_message_stderr=0
if pack_state_allows "$state"; then
  case "$state" in
    staged) pack_message="pack-local-feed: $ver is staged (no v$ver tag yet), packing." ;;
    at-tag) pack_message="pack-local-feed: HEAD is v$ver with a clean tree, so this re-pack reproduces the released bytes." ;;
  esac
elif pack_override_set; then
  pack_message="pack-local-feed: v$ver is released ($state) and PACK_RELEASED_OK=1 is set, packing anyway."
  pack_message_stderr=1
else
  pack_refusal_lines "$ver" "$state" | sed 's/^/pack-local-feed: /' >&2
  exit 1
fi

feed_scope=$(pack_feed_scope "$feed")
case "$feed_scope" in
  shared)
    headcommit=$(git rev-parse -q --verify 'HEAD^{commit}' 2>/dev/null || true)
    maincommit=$(pack_origin_main_commit)
    if [ -z "${headcommit:-}" ] || [ -z "${maincommit:-}" ] || ! pack_commit_on_origin_main "$headcommit"; then
      pack_shared_feed_refusal_lines "$headcommit" "$maincommit" | sed 's/^/pack-local-feed: /' >&2
      exit 1
    fi
    if ! pack_tree_clean; then
      pack_shared_feed_dirty_refusal_lines | sed 's/^/pack-local-feed: /' >&2
      exit 1
    fi
    ;;
  private) ;;
  *)
    echo "pack-local-feed: refusing to pack because feed path '$feed' cannot be resolved safely." >&2
    echo "pack-local-feed: use an existing parent directory or a simpler private KHAOZENGINE_FEED." >&2
    exit 1
    ;;
esac

if [ "$pack_message_stderr" = 1 ]; then echo "$pack_message" >&2; else echo "$pack_message"; fi

# This tree's local-feed is gitignored, so a fresh checkout or worktree has none, and the nuget.config
# source the pack's restore reads would not resolve. It is needed even when the output goes elsewhere.
mkdir -p local-feed
echo "pack-local-feed: dotnet pack -c Release -o $feed${*:+ $*}"
if [ "$dry" = 1 ]; then
  echo "pack-local-feed: --dry-run, nothing packed."
  exit 0
fi
mkdir -p "$feed"

# Pack through an empty sibling directory. The SDK's Pack target is incremental over PackageOutputPath,
# so handing it a feed that already carries this version can leave the old nupkg and snupkg untouched while
# returning success. An empty output forces fresh package bytes, and a sibling keeps the final moves on the
# same filesystem. Only the guarded current version's generated artifacts are promoted. Obsolete artifacts
# of that version are removed after promotion. Every other version remains byte for byte as it stood.
pack_parent=$(dirname "$feed")
pack_name=$(basename "$feed")
pack_stage=$(mktemp -d "$pack_parent/.$pack_name.pack.XXXXXX") || {
  echo "pack-local-feed: could not create a fresh package output beside $feed." >&2
  exit 1
}
cleanup_pack_stage() { rm -rf "$pack_stage"; }
trap cleanup_pack_stage EXIT
trap 'exit 1' HUP INT TERM

echo "pack-local-feed: building through fresh output $pack_stage"
dotnet pack -c Release "$@" -o "$pack_stage"

pack_count=0
pack_manifest="$pack_stage/.package-names"
: > "$pack_manifest"
for package in "$pack_stage"/*.nupkg "$pack_stage"/*.snupkg; do
  [ -f "$package" ] || continue
  package_name=$(basename "$package")
  case "$package_name" in
    *."$ver".nupkg|*."$ver".snupkg) ;;
    *)
      echo "pack-local-feed: refusing unexpected package '$package_name' because it is not version $ver." >&2
      exit 1
      ;;
  esac
  printf '%s\n' "$package_name" >> "$pack_manifest"
  pack_count=$((pack_count + 1))
done
[ "$pack_count" -gt 0 ] || {
  echo "pack-local-feed: dotnet pack produced no nupkg or snupkg for version $ver." >&2
  exit 1
}

for package in "$pack_stage"/*.nupkg "$pack_stage"/*.snupkg; do
  [ -f "$package" ] || continue
  mv -f "$package" "$feed/$(basename "$package")"
done
for package in "$feed"/*."$ver".nupkg "$feed"/*."$ver".snupkg; do
  if [ ! -e "$package" ] && [ ! -L "$package" ]; then continue; fi
  package_name=$(basename "$package")
  if ! grep -Fxq "$package_name" "$pack_manifest"; then
    rm -f "$package"
  fi
done
echo "pack-local-feed: refreshed $pack_count package file(s) for $ver in $feed"
