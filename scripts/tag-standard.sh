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

# tag_release_window <version> <commit> -> one word naming where <commit> sits relative to v<version>:
#   staged    no v<version> tag exists. The version is in flight and the commit may carry anything.
#   at-tag    v<version> IS this commit. The release itself.
#   past-tag  v<version> exists at some OTHER commit, so this commit claims a version already released.
#
# 'past-tag' is the state that cost two repair commits on 2026-09-20 (issue #1033): a branch merged to
# the integration branch while a concurrent session tagged the version it was staged into, leaving the
# version knob naming a released version and that version's changelog entry describing content its tag
# does not carry. It is the same window the pack guard refuses (#492), asked at push time instead.
tag_release_window() {
  _rw_ver=$1; _rw_commit=${2:-HEAD}
  git rev-parse -q --verify "refs/tags/v$_rw_ver" >/dev/null 2>&1 || { printf staged; return 0; }
  _rw_tagc=$(git rev-parse -q --verify "refs/tags/v$_rw_ver^{commit}" 2>/dev/null || true)
  _rw_headc=$(git rev-parse -q --verify "$_rw_commit^{commit}" 2>/dev/null || true)
  if [ -n "${_rw_tagc:-}" ] && [ "${_rw_tagc:-x}" = "${_rw_headc:-y}" ]; then printf at-tag; return 0; fi
  printf past-tag
}

# Parse the small XML subset that proves a test project non-packable. Project mode emits only
# literal imports resolved within test-shared/. Shared mode forbids further imports and admits only
# false IsPackable properties, Compile items and AssemblyAttribute items with their safe metadata.
# Unknown properties, items or attributes cannot acquire an exclusion by hiding in shared props.
tag_nonpackable_test_xml() {
  awk -v shared="$1" -v project_dir="${2:-}" '
    function import_path(path, count, pieces, parts, n, i, result) {
      if (path !~ /^[A-Za-z0-9_.\/-]+[.]props$/ || substr(path, 1, 1) == "/") return ""
      n = split(project_dir "/" path, pieces, "/")
      for (i = 1; i <= n; i++) {
        if (pieces[i] == "" || pieces[i] == ".") continue
        if (pieces[i] == "..") { if (!count) return ""; count-- }
        else parts[++count] = pieces[i]
      }
      if (count < 2 || parts[1] != "test-shared") return ""
      result = parts[1]
      for (i = 2; i <= count; i++) result = result "/" parts[i]
      return result
    }
    function safe_attributes(tag, allowed, rest, entry, key, value, seen, quote, pattern) {
      for (key in attributes) delete attributes[key]
      rest = tag
      sub(/^<[A-Za-z_][A-Za-z0-9_.-]*/, "", rest)
      sub(/\/?>$/, "", rest)
      quote = sprintf("%c", 39)
      pattern = "^[[:space:]]+[A-Za-z_][A-Za-z0-9_.-]*[[:space:]]*=[[:space:]]*(\"[^\"]*\"|" quote "[^" quote "]*" quote ")"
      while (rest !~ /^[[:space:]]*$/) {
        if (!match(rest, pattern)) return 0
        entry = substr(rest, RSTART, RLENGTH)
        rest = substr(rest, RSTART + RLENGTH)
        key = entry
        sub(/^[[:space:]]*/, "", key)
        sub(/[[:space:]=].*$/, "", key)
        if (!index("|" allowed "|", "|" key "|") || index(seen, "|" key "|")) return 0
        seen = seen "|" key "|"
        value = entry
        sub(/^[^=]+=[[:space:]]*/, "", value)
        attributes[key] = substr(value, 2, length(value) - 2)
      }
      return 1
    }
    { xml = xml "\n" $0 }
    END {
      while ((start = index(xml, "<!--")) > 0) {
        tail = substr(xml, start + 4)
        finish = index(tail, "-->")
        if (!finish) exit 1
        xml = substr(xml, 1, start - 1) substr(tail, finish + 3)
      }
      if (xml ~ /<!\[CDATA\[/) exit 1
      if (xml ~ /<(Choose|When|Otherwise|Target|ImportGroup|UsingTask|Sdk)[[:space:]\/>]/) exit 1
      if (shared && xml ~ /<Import[[:space:]\/>]/) exit 1
      if (xml ~ /<(Project|PropertyGroup)[^>]*Condition[[:space:]]*=/) exit 1
      tree = xml
      while (match(tree, /<[^>]+>/)) {
        content = substr(tree, 1, RSTART - 1)
        if (shared && content !~ /^[[:space:]]*$/ && tolower(parents[depth]) != "ispackable" &&
            parents[depth] !~ /^(Link|_Parameter[1-9][0-9]*)$/) exit 1
        tag = substr(tree, RSTART, RLENGTH)
        tree = substr(tree, RSTART + RLENGTH)
        if (tag ~ /^<\?xml[[:space:]]/) continue
        name = tag
        sub(/^<\/?/, "", name)
        sub(/[[:space:]\/>].*$/, "", name)
        if (name !~ /^[A-Za-z_][A-Za-z0-9_.-]*$/) exit 1
        if (tag ~ /^<\//) {
          if (shared && tag !~ /^<\/[A-Za-z_][A-Za-z0-9_.-]*[[:space:]]*>$/) exit 1
          if (depth < 1 || parents[depth] != name) exit 1
          depth--
        } else {
          if (!depth && (name != "Project" || ++roots != 1)) exit 1
          if (depth == 1 && name != "PropertyGroup" && name != "ItemGroup" && name != "Import") exit 1
          if (name == "Import") {
            if (shared || depth != 1 || tag !~ /\/>$/ || !safe_attributes(tag, "Project")) exit 1
            path = import_path(attributes["Project"])
            if (!path) exit 1
            imports[++import_count] = path
          }
          if (tolower(name) == "ispackable" && (depth != 2 || parents[1] != "Project" || parents[2] != "PropertyGroup")) exit 1
          if (shared) {
            allowed = ""
            if (depth == 2) {
              if (parents[2] == "PropertyGroup") {
                if (tolower(name) != "ispackable") exit 1
              } else if (name == "Compile") allowed = "Include|Link"
              else if (name == "AssemblyAttribute") allowed = "Include"
              else exit 1
            } else if (depth == 3) {
              if (parents[3] == "Compile") { if (name != "Link") exit 1 }
              else if (parents[3] != "AssemblyAttribute" || name !~ /^_Parameter[1-9][0-9]*$/) exit 1
            } else if (depth > 3) exit 1
            if (!safe_attributes(tag, allowed)) exit 1
          }
          if (tag !~ /\/>$/) parents[++depth] = name
        }
      }
      if (depth || roots != 1 || (shared && tree !~ /^[[:space:]]*$/)) exit 1
      properties = tolower(xml)
      if (shared) {
        while (sub(/<ispackable>[[:space:]]*false[[:space:]]*<\/ispackable>/, "", properties)) {}
      } else if (!sub(/<ispackable>[[:space:]]*false[[:space:]]*<\/ispackable>/, "", properties)) exit 1
      if (properties ~ /<ispackable[[:space:]\/>]/) exit 1
      for (i = 1; i <= import_count; i++) print imports[i]
      exit 0
    }'
}

# tag_nonpackable_test <commit> <path> -> 0 for a path owned by a non-packable test project.
# Read the project AND imports at the pushed commit, never from the worktree. Require one literal
# false in a direct unconditional PropertyGroup. Imports must be regular tracked props files inside
# test-shared/, proved separately by the same parser. Unsupported XML stays version-bearing.
tag_nonpackable_test() (
  _nt_commit=$1
  _nt_dir=${2%%/*}
  case "$_nt_dir" in KhaozEngine.*Tests) ;; *) exit 1 ;; esac
  _nt_project=$_nt_dir/$_nt_dir.csproj
  _nt_imports=$(git show "$_nt_commit:$_nt_project" 2>/dev/null | tag_nonpackable_test_xml 0 "$_nt_dir") || exit 1
  printf '%s\n' "$_nt_imports" | while IFS= read -r _nt_import; do
    [ -n "$_nt_import" ] || continue
    git ls-tree "$_nt_commit" -- "$_nt_import" | grep -Eq "^100(644|755) blob " || exit 1
    git show "$_nt_commit:$_nt_import" 2>/dev/null | tag_nonpackable_test_xml 1 > /dev/null || exit 1
  done
)

# tag_version_bearing <from> <to> -> 0 when the range changes something that ships inside a package,
# 1 when every change is tests, documentation, tooling or repository governance.
#
# The carve-out is not leniency, it is the contributor rule: tooling, documentation and repository
# governance do not bump the version. Without it, every docs or hook commit landing on top of a release
# would be refused for failing to bump a version it is forbidden to bump, and a guard that cries wolf is
# a guard people learn to pass --no-verify to. Markdown is excluded even though package READMEs are
# packed: a README edit changes package BYTES but not package BEHAVIOUR, and the same rule says it rides.
#
# tools/ is carved out on evidence rather than by category, and the first use of this guard is what
# found it: a dependabot bump to tools/kit-bake's npm lockfile would have been refused for not bumping
# a version that lockfile cannot reach. Every csproj under tools/ carries IsPackable=false, no packable
# project takes a ProjectReference on one, and the `tools/` path that DOES appear in a nupkg is
# KhaozEngine.Content's PackagePath for its own validator output, not this directory. If a packable
# project ever moves under tools/, this line is the one to revisit.
#
# Test projects are also non-packable, including the rump KhaozEngine.Tests project. No packable
# project references them. Check their declaration at both commits before excluding a path. A new
# non-packable test project is safe, but retiring a packable or unknown project still needs a bump.
#
# .gitignore and .filesize-baseline are governance in the same sense: they decide what is TRACKED and
# how large a file may be, never what a package contains. .gitignore joined the list the third time
# this guard refused its own author, which is worth stating plainly. The list is derived by reasoning
# about what ships, and reasoning keeps missing entries, so expect to add more.
#
# That asymmetry is why the list EXCLUDES rather than includes. A missing exclusion costs a refusal
# someone reads and fixes, as happened three times here. A missing inclusion would silently let
# through the exact push this guard exists to stop, and nobody would ever learn it was wrong.
#
# The root .gitattributes is judged by the lines it changes, not by its name, because checkout
# attributes change the bytes of the files they match. See tag_gitattributes_version_bearing.
tag_version_bearing() {
  _vb_from=$1; _vb_to=$2
  git diff --name-only "$_vb_from" "$_vb_to" -- \
    ':(exclude)docs/**' ':(exclude)*.md' ':(exclude)scripts/**' ':(exclude)tools/**' \
    ':(exclude).github/**' ':(exclude).githooks/**' ':(exclude).claude/**' ':(exclude).codex/**' \
    ':(exclude).filesize-baseline' ':(exclude).gitignore' 2>/dev/null | (
      while IFS= read -r _vb_path; do
        if [ "$_vb_path" = .gitattributes ]; then
          tag_gitattributes_version_bearing "$_vb_from" "$_vb_to" && exit 0
          continue
        fi
        tag_path_nonpackable "$_vb_from" "$_vb_to" "$_vb_path" || exit 0
      done
      exit 1
    )
}

# tag_path_nonpackable <from> <to> <path> -> 0 when <path> sits in a test project that is non-packable
# at <to>, and also at <from> when the project already existed there.
tag_path_nonpackable() (
  tag_nonpackable_test "$2" "$3" || exit 1
  _pn_dir=${3%%/*}
  if git cat-file -e "$1:$_pn_dir/$_pn_dir.csproj" 2>/dev/null; then
    tag_nonpackable_test "$1" "$3" || exit 1
  fi
  exit 0
)

# tag_gitattributes_version_bearing <from> <to> -> 0 when an added or removed line of the root
# .gitattributes can match a file that ships.
#
# Checkout attributes change the bytes of the files they match. The 21.19.0 LF pin on shader sources
# changed embedded package content exactly that way, so such a line needs a bump like the files it
# pins. A line confined to a carved-out folder or a non-packable test project rides, as those files
# do. A pattern this cannot place counts as shipped: one without a folder matches at any depth, and a
# glob, quote, escape or macro in its first folder could reach anything.
tag_gitattributes_version_bearing() (
  git diff -U0 "$1" "$2" -- .gitattributes 2>/dev/null \
    | awk 'hunk && /^[-+]/ { print substr($0, 2) } /^@@/ { hunk = 1 }' | (
      while IFS= read -r _ga_line; do
        _ga_line=$(printf '%s' "$_ga_line" | sed 's/^[[:space:]]*//')
        case "$_ga_line" in ''|'#'*) continue ;; esac
        _ga_pattern=${_ga_line%%[ 	]*}
        _ga_pattern=${_ga_pattern#/}
        case "$_ga_pattern" in */*) ;; *) exit 0 ;; esac
        _ga_dir=${_ga_pattern%%/*}
        case "$_ga_dir" in ''|*[\*\?\[\]\\\"\!]*) exit 0 ;; esac
        case "$_ga_dir" in docs|scripts|tools|.github|.githooks|.claude|.codex) continue ;; esac
        tag_path_nonpackable "$1" "$2" "$_ga_pattern" || exit 0
      done
      exit 1
    )
)
