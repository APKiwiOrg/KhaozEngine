#!/bin/sh
# tag-release.sh - create the annotated release tag v<version> with a canonical
# 'area(<version>): summary' message, validated against the shared tag standard so releases
# stop drifting in format. area+summary default from the HEAD commit subject (already
# conventional-commit) when HEAD is a plain commit, or from the version-bump commit / newest
# non-merge commit when HEAD is a merge (see below). Override either with positional args.
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

# THE authoritative collision check (issue #261). It belongs here rather than in the PreToolUse hook,
# because a hook runs BEFORE the command it judges: for the ritual's own chained
# `git merge <branch> && scripts/tag-release.sh` the bump arrives with the merge, so the hook could only
# ever read the PREVIOUS version out of Directory.Build.props and deny the whole chain over the previous
# release's tag. Here the version has already been read at the moment it is true, and origin counts as
# well as local, because the collision this catches is a concurrent release that tagged first.
if tag_taken . "$tag"; then
  echo "tag-release: $tag already exists ($TAG_TAKEN_WHERE). A concurrent release likely took it." >&2
  echo "             Re-read the current version and tags, bump <$knob> to the next free version," >&2
  echo "             rebase the CHANGELOG entry onto it, then tag." >&2
  exit 1
fi

# Defaults from the HEAD subject when it is already 'area[(scope)]: summary'. When HEAD is a merge
# commit (the release ritual's own integrate-then-tag flow produces one: merge main into the
# feature branch, then merge that branch back), git's own synthetic 'Merge ...' subject is not a
# conventional one and leaks into the tag. Prefer, in order: the commit that bumped the version
# knob to $ver (wherever it lives in the merged history - that commit's subject already IS the
# canonical one per the release-commit convention), then the newest non-merge commit on HEAD's
# first-parent chain, then HEAD's own subject as before.
hs=''
if git rev-parse -q --verify HEAD^2 >/dev/null 2>&1; then
  needle="<$knob>$ver</$knob>"
  hs=$(git log -S"$needle" --format='%s' -- Directory.Build.props 2>/dev/null | head -1 || true)
  [ -n "${hs:-}" ] || hs=$(git log --first-parent --no-merges -1 --format='%s' 2>/dev/null || true)
fi
[ -n "${hs:-}" ] || hs=$(git log -1 --format='%s' 2>/dev/null || true)
harea=''; hsum=''
if printf '%s' "$hs" | grep -Eq '^[a-z0-9][a-z0-9._-]*(\([^)]*\))?: .+$'; then
  harea=$(printf '%s' "$hs" | sed -E 's/^([a-z0-9][a-z0-9._-]*).*$/\1/')
  hsum=$(printf '%s' "$hs" | sed -E 's/^[^:]*: //')
fi

area=${1:-}
# area is one bare conventional-commit word; this script assembles 'area(<version>): summary' itself.
# Passing that whole canonical string as the single area arg is the obvious mistake (it reads like the
# thing the docs tell you to produce) and it used to assemble into a doubled message. Reject it here,
# at the source, where the message can name the right form. Empty is fine: it defaults from HEAD below.
case "$area" in
  *'('* | *')'* | *:* | *[[:space:]]*)
    echo "tag-release: bad area '$area'." >&2
    echo "             area is one bare word and summary is separate; the 'area($ver): summary' message" >&2
    echo "             is assembled for you. Do not pass it pre-assembled." >&2
    echo "             usage: scripts/tag-release.sh <area> <summary...>" >&2
    echo "             e.g.   scripts/tag-release.sh render \"frustum-slice shadow cascades\"" >&2
    exit 1 ;;
esac
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
