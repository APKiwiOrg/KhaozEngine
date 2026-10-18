#!/bin/sh
# check-local-feed.sh - report whether tagged versions in local-feed were built from their tag commit,
# so a consumer about to vendor from the feed can see drift before it ships. POSIX sh.
#
#   scripts/check-local-feed.sh                    # every version, always exits 0 (informational)
#   scripts/check-local-feed.sh --strict           # exit 1 when any checked version is UNSAFE or DRIFTED
#   scripts/check-local-feed.sh --version X.Y.Z    # check only X.Y.Z, the version a consumer vendors
#   scripts/check-local-feed.sh --current          # check only this tree's <KhaozEngineVersion>
#   scripts/check-local-feed.sh --all              # every version, the default, spelled out
#   scripts/check-local-feed.sh --feed DIR         # read DIR instead of the default feed
#
# A release checks the version it ships with --current or --version (#1442). A consumer checks the
# version it is about to vendor, which may be older than the engine's current one. With no scope the
# report walks every version in the feed. Pass at most one of --all, --current and --version.
#
# The default feed is the one scripts/pack-local-feed.sh writes: KHAOZENGINE_FEED when set, otherwise the
# MAIN checkout's local-feed, from a linked worktree too (pack_feed_dir in scripts/pack-standard.sh).
# A relative DIR or KHAOZENGINE_FEED resolves against this tree's toplevel, not the directory you ran from.
#
# Statuses, per checked version present in the feed:
#   STAGED     no v<version> tag yet, and every package commit is on current origin/main.
#   UNSAFE     no tag yet, and at least one package commit is missing or not on current origin/main.
#   RELEASED   tagged, and every package's nuspec commit matches the tag commit.
#   DRIFTED    tagged, and at least one package has a missing or different nuspec commit. The feed holds
#              a build the tag does not describe (#492, #1136). GitHub Packages still has the published
#              copy, so recover from there or from a checkout of the tag.
#
# The package stamp is the repository commit written by the .NET SDK into the nuspec. Every package of a
# checked version is read. The release stamp is the annotated tag peeled to its commit. Each distinct
# package commit is tested against origin/main once. Package modification times remain informational.
# scripts/pack-local-feed.sh is the prevention. This is detection for a feed that already drifted.
set -eu
script_path=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)/$(basename -- "$0")
cd "$(git rev-parse --show-toplevel)"
. scripts/tag-standard.sh
. scripts/pack-standard.sh

strict=0
feed=''
scope=''
only=''
set_scope() {
  if [ -n "$scope" ]; then
    echo "check-local-feed: pass at most one of --all, --current and --version." >&2
    exit 2
  fi
  scope=$1
}
while [ $# -gt 0 ]; do
  case "$1" in
    --strict) strict=1; shift ;;
    --feed) feed=${2:-}; shift 2 2>/dev/null || { echo "check-local-feed: --feed needs a directory." >&2; exit 2; } ;;
    --all) set_scope all; shift ;;
    --current) set_scope current; shift ;;
    --version)
      set_scope version
      only=${2:-}
      if ! printf '%s' "$only" | grep -Eq '^[0-9]+\.[0-9]+\.[0-9]+$'; then
        echo "check-local-feed: --version needs an X.Y.Z version." >&2
        exit 2
      fi
      shift 2 ;;
    --help|-h)
      awk 'NR == 1 { next } /^#/ { sub(/^# ?/, ""); print; next } { exit }' "$script_path"
      exit 0 ;;
    *) echo "check-local-feed: unknown argument '$1'." >&2; exit 2 ;;
  esac
done
if [ "$scope" = current ]; then
  only=$(tag_props_version < Directory.Build.props 2>/dev/null || true)
  if [ -z "${only:-}" ]; then
    echo "check-local-feed: --current cannot read <KhaozEngineVersion> from Directory.Build.props." >&2
    exit 2
  fi
fi
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

tab=$(printf '\t')
mark='@@check-local-feed-package@@'

# scan_version <version> -> sets newest (the newest pack time across every file of the version, 0 when
# none can be read) and pairs (one "<file><TAB><nuspec commit>" line per package file, in the order the
# per-file glob gives: every nupkg, then every snupkg). The commit is empty when the nuspec has none.
# One unzip reads every package of the version through its own archive wildcard, so the process count
# no longer grows with the package count (#1442). The sed expression is the per-package reader this
# script always used. The awk files each hit under the "Archive:" header unzip prints, keeps the first
# hit per package and restores the glob order, since unzip walks its wildcard in directory order.
scan_version() {
  _sv_v=$1
  set --
  for _sv_f in "$feed"/KhaozEngine.*."$_sv_v".nupkg "$feed"/KhaozEngine.*."$_sv_v".snupkg; do
    if [ -f "$_sv_f" ]; then set -- "$@" "$_sv_f"; fi
  done
  newest=0
  pairs=''
  [ $# -gt 0 ] || return 0
  newest=$(pack_newest_mtime "$@")
  pairs=$(
    {
      for _sv_f in "$@"; do printf '%s%s\n' "$mark" "${_sv_f##*/}"; done
      ( CDPATH='' cd -- "$feed" && unzip -c "KhaozEngine.*.$_sv_v.*nupkg" '*.nuspec' 2>/dev/null ) || true
    } \
    | sed -n -e "/^$mark/p" -e '/Archive:  /p' \
        -e '/<repository[[:space:]][^>]*>/s/.*commit="\([^"]*\)".*/\1/p' \
    | awk -v mark="$mark" '
        index($0, mark) == 1 { name = substr($0, length(mark) + 1); order[++n] = name; want[name] = 1; next }
        (i = index($0, "Archive:  ")) > 0 {
          archive = substr($0, i + 10)
          if (archive in want) { cur = archive; next }
          if (i == 1) cur = ""
          next
        }
        cur != "" && !(cur in commit) { commit[cur] = $0 }
        END { for (k = 1; k <= n; k++) print order[k] "\t" commit[order[k]] }')
}

# on_main <commit> -> pack_commit_on_origin_main, asked once per distinct commit for the whole run.
on_main_yes=''
on_main_no=''
on_main() {
  case "
$on_main_yes
" in *"
$1
"*) return 0 ;; esac
  case "
$on_main_no
" in *"
$1
"*) return 1 ;; esac
  if pack_commit_on_origin_main "$1"; then
    on_main_yes="${on_main_yes}${on_main_yes:+
}$1"
    return 0
  fi
  on_main_no="${on_main_no}${on_main_no:+
}$1"
  return 1
}

if [ -n "$only" ]; then
  versions=''
  for f in "$feed"/KhaozEngine.*."$only".nupkg "$feed"/KhaozEngine.*."$only".snupkg; do
    if [ -f "$f" ]; then versions=$only; break; fi
  done
  if [ -z "$versions" ]; then
    echo "check-local-feed: $feed holds no KhaozEngine $only packages; nothing to check."
    exit 0
  fi
else
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
fi

originmain=$(pack_origin_main_commit)
staged=0; unsafe=0; released=0; drifted=0
for v in $versions; do
  scan_version "$v"
  tagcommit=$(git rev-parse -q --verify "refs/tags/v$v^{commit}" 2>/dev/null || true)
  if [ -z "${tagcommit:-}" ]; then
    staged_unsafe=0
    staged_commits=''
    unsafe_lines=''
    while IFS= read -r line; do
      [ -n "$line" ] || continue
      f=${line%%"$tab"*}
      packagecommit=${line#*"$tab"}
      if [ -z "${packagecommit:-}" ]; then
        staged_unsafe=1
        detail="${f##*/}: no repository commit in nuspec"
        unsafe_lines="${unsafe_lines}${unsafe_lines:+
}$detail"
        continue
      fi
      case "
$staged_commits
" in
        *"
$packagecommit
"*) ;;
        *) staged_commits="${staged_commits}${staged_commits:+
}$packagecommit" ;;
      esac
      if [ -z "${originmain:-}" ] || ! on_main "$packagecommit"; then
        staged_unsafe=1
        detail="${f##*/}: commit $packagecommit not on origin/main"
        unsafe_lines="${unsafe_lines}${unsafe_lines:+
}$detail"
      fi
    done <<EOF
$pairs
EOF
    if [ "$staged_unsafe" = 1 ]; then
      echo "  UNSAFE     $v  packed $(stamp "$newest")  (no v$v tag)"
      printf '%s\n' "$unsafe_lines" | sed 's/^/             /'
      unsafe=$((unsafe + 1))
    else
      firstcommit=$(printf '%s\n' "$staged_commits" | head -1)
      echo "  STAGED     $v  commit $firstcommit on origin/main  packed $(stamp "$newest")"
      staged=$((staged + 1))
    fi
  else
    version_drifted=0
    drift_lines=''
    while IFS= read -r line; do
      [ -n "$line" ] || continue
      f=${line%%"$tab"*}
      packagecommit=${line#*"$tab"}
      if [ "${packagecommit:-}" != "$tagcommit" ]; then
        version_drifted=1
        if [ -n "${packagecommit:-}" ]; then
          detail="${f##*/}: commit $packagecommit"
        else
          detail="${f##*/}: no repository commit in nuspec"
        fi
        drift_lines="${drift_lines}${drift_lines:+
}$detail"
      fi
    done <<EOF
$pairs
EOF
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

echo "check-local-feed: $staged staged, $unsafe unsafe, $released released, $drifted drifted (feed: $feed)."
if [ "$unsafe" -ne 0 ]; then
  echo "check-local-feed: an UNSAFE staged version contains a package commit not on origin/main (#1135)." >&2
  echo "                  Do not vendor it from the shared feed. Re-pack from origin/main, or use an" >&2
  echo "                  explicit private KHAOZENGINE_FEED for intentional branch builds." >&2
fi
if [ "$drifted" -ne 0 ]; then
  echo "check-local-feed: a DRIFTED version contains a build its tag does not describe (#492, #1136)." >&2
  echo "                  Do not vendor it into a consumer. Re-pack from a checkout of the tag or" >&2
  echo "                  restore the published copy from GitHub Packages." >&2
fi
if [ "$strict" = 1 ] && { [ "$unsafe" -ne 0 ] || [ "$drifted" -ne 0 ]; }; then exit 1; fi
exit 0
