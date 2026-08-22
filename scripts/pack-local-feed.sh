#!/bin/sh
# pack-local-feed.sh - the finishing ritual's pack step, with the released-version guard in front of it.
# This is the sanctioned way to run the ritual's pack, and AGENTS.md names it instead of the bare
# dotnet command it wraps:
#
#   scripts/pack-local-feed.sh              # guard, then dotnet pack -c Release -o ./local-feed
#   scripts/pack-local-feed.sh --dry-run    # guard only, print the command it would run
#   PACK_RELEASED_OK=1 scripts/pack-local-feed.sh    # pack anyway over a released version
#
# Extra arguments after the options are forwarded to dotnet pack unchanged.
#
# The rule it enforces, and why, is scripts/pack-standard.sh (issue #492). Short version: the ritual
# packs whatever <KhaozEngineVersion> currently says, that version does not move until someone bumps it,
# and so every finish between a tag and the next bump quietly re-packs an already-released number.
# scripts/hooks/pack-release-guard.sh is the other half, catching the raw dotnet command an agent types
# out of habit and pointing it here.
set -eu
cd "$(git rev-parse --show-toplevel)"
. scripts/tag-standard.sh
. scripts/pack-standard.sh

dry=0
while [ $# -gt 0 ]; do
  case "$1" in
    --dry-run|-n) dry=1; shift ;;
    --help|-h)
      sed -n '2,12p' "$0" | sed 's/^# \{0,1\}//'
      exit 0 ;;
    --) shift; break ;;
    *) break ;;
  esac
done

ver=$(tag_props_version < Directory.Build.props 2>/dev/null || true)
[ -n "${ver:-}" ] || { echo "pack-local-feed: could not read <KhaozEngineVersion> from Directory.Build.props." >&2; exit 1; }

state=$(pack_release_state "$ver")
if pack_state_allows "$state"; then
  case "$state" in
    staged) echo "pack-local-feed: $ver is staged (no v$ver tag yet), packing." ;;
    at-tag) echo "pack-local-feed: HEAD is v$ver with a clean tree, so this re-pack reproduces the released bytes." ;;
  esac
elif pack_override_set; then
  echo "pack-local-feed: v$ver is released ($state) and PACK_RELEASED_OK=1 is set, packing anyway." >&2
else
  pack_refusal_lines "$ver" "$state" | sed 's/^/pack-local-feed: /' >&2
  exit 1
fi

# local-feed is gitignored, so a fresh checkout has none and the nuget.config source would not resolve.
mkdir -p local-feed
echo "pack-local-feed: dotnet pack -c Release -o ./local-feed${*:+ $*}"
if [ "$dry" = 1 ]; then
  echo "pack-local-feed: --dry-run, nothing packed."
  exit 0
fi
exec dotnet pack -c Release -o ./local-feed "$@"
