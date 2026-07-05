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

# Read the repo's release version from a Directory.Build.props stream on stdin (empty if absent).
tag_props_version() {
  _knob=$(tag_version_knob)
  grep -oE "<$_knob>[0-9][^<]*</$_knob>" | head -1 | sed -E "s#</?$_knob>##g"
}

# tag_name_ok <name>  -> 0 when it is vX.Y.Z
tag_name_ok() { printf '%s' "$1" | grep -Eq '^v[0-9]+\.[0-9]+\.[0-9]+$'; }

# tag_msg_ok <subject> <version>  -> 0 when subject is 'area(<version>): summary' with no dash
tag_msg_ok() {
  _subj=$1; _ver=$2
  printf '%s' "$_subj" | LC_ALL=C grep -qF -e '—' -e '–' && return 1
  _re=$(printf '%s' "$_ver" | sed 's/\./\\./g')
  printf '%s' "$_subj" | grep -Eq "^[a-z0-9][a-z0-9._-]*\\(${_re}\\): .+$"
}
