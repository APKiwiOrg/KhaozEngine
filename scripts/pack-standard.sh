# pack-standard.sh - shared "may this version be packed into local-feed" standard. POSIX sh, meant to
# be SOURCED (not executed) by scripts/pack-local-feed.sh (the ritual pack), scripts/check-local-feed.sh
# (the feed report) and scripts/hooks/pack-release-guard.sh (the agent-side deny), so the single
# definition of a safe pack cannot drift between the three. Same shape and same reason as
# scripts/tag-standard.sh, which is the tag half of the same standard and which callers source too
# (this file deliberately sources nothing, so a caller controls its own cwd).
#
# THE HAZARD (issue #492). The finishing ritual packs the CURRENT <KhaozEngineVersion> to local-feed on
# every finish, and the version stays put until someone bumps it. So after v17.30.0 is tagged, the next
# finish still reads 17.30.0, packs it again, and local-feed's 17.30.0 silently becomes a second,
# larger build of a released version number. A consumer vendoring from the feed in that window ships an
# engine build no tag describes, its tag-to-tag adopt sweep reads the wrong release, and "which engine
# build is this?" stops having an answer. GitHub Packages holds the immutable published copy, so nothing
# is lost, but the two disagree and the tag is the one everybody reads.
#
# THE RULE. Packing v<version> into local-feed is allowed when the version is STAGED (no such tag yet,
# which is the ordinary case and the whole point of the ritual), or when HEAD is exactly that tag's
# commit with a clean tree (re-packing then reproduces the released bytes, which is how a pruned or lost
# local-feed is rebuilt). Anything else is a re-pack OVER a release and is refused. The override for a
# deliberate exception is PACK_RELEASED_OK=1, matching the repo's other *_OK escape hatches
# (BACKLOG_FILE_OK, FILESIZE_OK).
#
# Note on the release pack itself: scripts/tag-release.sh creates the tag AFTER the ritual's pack and
# commit, so at tag time no v<version> tag exists yet and the state is plain "staged". The guard is
# silent through a normal release. It only speaks in the window this issue is about, which is the one
# between a tag and the next bump.

# pack_file_mtime <path> -> modification time in unix seconds (empty when it cannot be read).
# GNU and BSD stat disagree on the flag, and picking wrong is not a clean failure: GNU's -f is
# --file-system and its %m is the MOUNT POINT, so `stat -f %m file` succeeds on Linux and returns
# nonsense. Probe for GNU coreutils by its --version, which BSD stat does not have.
pack_file_mtime() {
  if stat --version >/dev/null 2>&1; then
    stat -c %Y "$1" 2>/dev/null
  else
    stat -f %m "$1" 2>/dev/null
  fi
}

# pack_tag_time <tag> -> when the tag was CREATED, in unix seconds (empty when the tag does not exist).
# An annotated tag carries its own taggerdate, which is the number that matters: the release moment. A
# lightweight tag has none, so it falls back to the commit date. Run from anywhere inside the repo.
pack_tag_time() {
  _pt=$(git for-each-ref --format='%(taggerdate:unix)' "refs/tags/$1" 2>/dev/null | head -1)
  [ -n "${_pt:-}" ] || _pt=$(git for-each-ref --format='%(committerdate:unix)' "refs/tags/$1" 2>/dev/null | head -1)
  printf '%s' "${_pt:-}"
}

# pack_release_state <version> -> one word on stdout. Run with cwd anywhere inside the repo.
#   staged        no v<version> tag exists. The ordinary case: packing is the ritual. ALLOWED.
#   at-tag        the tag exists, HEAD is its commit, and the tree is clean. Re-packing reproduces the
#                 released bytes, so it is a rebuild rather than an overwrite. ALLOWED.
#   at-tag-dirty  the tag exists and HEAD is its commit, but the tree carries changes, so the pack would
#                 NOT reproduce the released bytes. REFUSED without the override.
#   released      the tag exists and HEAD is somewhere else. This is the #492 window exactly. REFUSED.
pack_release_state() {
  _pv=$1
  _ptag="v$_pv"
  git rev-parse -q --verify "refs/tags/$_ptag" >/dev/null 2>&1 || { printf staged; return 0; }
  _ptc=$(git rev-parse -q --verify "refs/tags/$_ptag^{commit}" 2>/dev/null || true)
  _phc=$(git rev-parse -q --verify 'HEAD^{commit}' 2>/dev/null || true)
  if [ -n "${_ptc:-}" ] && [ "${_ptc:-x}" = "${_phc:-y}" ]; then
    # Untracked files count: an untracked .cs is compiled into the package just like a modified one, so
    # the plain porcelain listing (which already skips gitignored paths, local-feed included) is the
    # right question to ask.
    if [ -z "$(git status --porcelain 2>/dev/null)" ]; then printf at-tag; else printf at-tag-dirty; fi
    return 0
  fi
  printf released
}

# pack_state_allows <state> -> 0 when that state may pack, 1 when it must be refused.
pack_state_allows() {
  case "$1" in
    staged|at-tag) return 0 ;;
    *) return 1 ;;
  esac
}

# pack_override_set -> 0 when PACK_RELEASED_OK=1 is in the environment.
pack_override_set() { [ "${PACK_RELEASED_OK:-}" = 1 ]; }

# pack_refusal_lines <version> <state> -> the explanation, one line per line of output, shared by the
# wrapper (which prints it) and the PreToolUse hook (which folds it into its deny reason), so the two
# cannot come to say different things.
pack_refusal_lines() {
  _rv=$1; _rs=$2
  echo "Refusing to pack $_rv into local-feed: v$_rv is already a released tag."
  if [ "$_rs" = at-tag-dirty ]; then
    echo "HEAD is v$_rv but the tree is not clean, so this pack would not reproduce the released bytes."
  else
    echo "HEAD is not v$_rv, so this pack would overwrite the released $_rv in the feed with a larger"
    echo "build of the same version number. A consumer vendoring from the feed would then ship an engine"
    echo "build no tag describes, and its tag-to-tag adopt sweep would read the wrong release (#492)."
  fi
  echo "Bump <KhaozEngineVersion> in Directory.Build.props first, add the CHANGELOG entry, then pack."
  echo "Deliberate exception (rebuilding a pruned feed, a known-good re-pack): PACK_RELEASED_OK=1."
}
