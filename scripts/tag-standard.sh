# tag-standard.sh - shared release-tag standard. POSIX sh, meant to be SOURCED (not executed)
# by .githooks/pre-push (validation) and scripts/tag-release.sh (generation), so the single
# definition of "what a release tag looks like" cannot drift between the two.
#
# The standard, uniform across the engine and every game:
#   name     : vMAJOR.MINOR.PATCH
#   object   : annotated, never lightweight (a lightweight tag shows the commit subject, which is
#              how merge-commit messages leaked into release tags)
#   subject  : area(<version>): summary   (lowercase conventional area, scope == the version,
#              non-empty summary, no em/en dashes)
#   coupling : <version> == the repo's version knob in Directory.Build.props at the tagged commit

# The repo's version knob: the engine ships <KhaozEngineVersion>, a game ships <Version>.
# scripts/check-doc-versions.sh is the engine-only marker the other hooks already key on.
tag_version_knob() {
  if [ -f scripts/check-doc-versions.sh ]; then printf '%s' KhaozEngineVersion; else printf '%s' Version; fi
}

# Read a versioned knob's value from a Directory.Build.props stream on stdin (empty if absent).
# THE single version reader for the whole repo standard: publish/deploy workflows and refresh-engine.sh
# source this file and call it instead of hand-rolling a grep (that is how the readers drifted and how
# an un-anchored capture once matched a <Version> in a COMMENT and broke a GITHUB_OUTPUT write). Anchored
# on a leading digit AND the closing tag, first match only, so a knob mentioned in a comment cannot match.
# Knob defaults to Version (the game version); pass KhaozEngineVersion for the engine pin.
props_version() {
  _pv_knob=${1:-Version}
  grep -oE "<$_pv_knob>[0-9][^<]*</$_pv_knob>" | head -1 | sed -E "s#</?$_pv_knob>##g"
}

# Read the repo's release version from a Directory.Build.props stream on stdin (empty if absent).
tag_props_version() { props_version "$(tag_version_knob)"; }

# tag_name_ok <name>  -> 0 when it is vX.Y.Z
tag_name_ok() { printf '%s' "$1" | grep -Eq '^v[0-9]+\.[0-9]+\.[0-9]+$'; }

# tag_taken <repo-dir> <tag> [scope]  -> 0 when that release tag is already taken, and sets
# TAG_TAKEN_WHERE to 'origin' or 'local' so the caller can say which. Fetches tags first, because the
# collision this exists to catch is a CONCURRENT release: the tag is usually on origin before it is
# here. scope 'any' (the default) counts a tag that exists locally OR on origin, which is the creation
# collision. scope 'origin' counts only origin, for a push, where the local tag is expected to exist.
#
# THE single collision reader for the repo standard (issue #261): scripts/tag-release.sh refuses on it
# right before it tags, and scripts/hooks/tag-collision-guard.sh denies an explicit `git tag vX.Y.Z` or
# tag push on it, so the two cannot drift on what "already taken" means. A repo with no reachable
# origin degrades to the local half rather than failing.
tag_taken() {
  _tt_repo=$1; _tt_tag=$2; _tt_scope=${3:-any}
  TAG_TAKEN_WHERE=''
  git -C "$_tt_repo" fetch --tags --quiet 2>/dev/null
  if git -C "$_tt_repo" ls-remote --tags origin "$_tt_tag" 2>/dev/null | grep -q "refs/tags/${_tt_tag}$"; then
    TAG_TAKEN_WHERE=origin
    return 0
  fi
  if [ "$_tt_scope" = any ] && git -C "$_tt_repo" rev-parse -q --verify "refs/tags/$_tt_tag" >/dev/null 2>&1; then
    TAG_TAKEN_WHERE=local
    return 0
  fi
  return 1
}

# tag_msg_ok <subject> <version>  -> 0 when subject is 'area(<version>): summary' with no dash
tag_msg_ok() {
  _subj=$1; _ver=$2
  printf '%s' "$_subj" | LC_ALL=C grep -qF -e '—' -e '–' && return 1
  _re=$(printf '%s' "$_ver" | sed 's/\./\\./g')
  printf '%s' "$_subj" | grep -Eq "^[a-z0-9][a-z0-9._-]*\\(${_re}\\): .+$" || return 1
  # The summary must not carry a SECOND '(<version>): ' segment. That is the signature of the whole
  # canonical 'area(version): summary' string passed as tag-release.sh's single area arg, which
  # assembles into 'area(ver): summary(ver): head-subject'. The prefix match above passes it, because
  # '.+' happily swallows the doubled tail. Pinned to the actual version, not any '(...): ', so a
  # summary like 'fix the thing (again)' stays legal.
  _rest=${_subj#*"($_ver): "}
  printf '%s' "$_rest" | grep -qF "($_ver): " && return 1
  return 0
}
