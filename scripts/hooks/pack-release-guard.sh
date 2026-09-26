#!/bin/sh
# pack-release-guard.sh - PreToolUse Bash guard. Reads the hook JSON on stdin, looks for a proposed
# `dotnet pack` whose output lands in the engine feed, and denies it when <KhaozEngineVersion> names a
# version that is already released (issue #492) or HEAD is not on origin/main (issue #1135). Silent
# (exit 0, no output) for everything else, which is the allow path. Same shape and the same stdin
# contract as scripts/hooks/tag-collision-guard.sh, which both agent hook configs already invoke.
#
# WHY A HOOK AND NOT JUST A SCRIPT. The prevention is scripts/pack-local-feed.sh, which carries the
# guard inline. But the ritual line has read `dotnet pack -c Release -o ./local-feed` for a very long
# time, it is quoted in AGENTS.md, docs/INDEX.md, USING-KHAOZENGINE.md and two design docs, and an agent
# types what it remembers. This is the leg that catches the remembered command and points at the
# wrapper, so the guard is not merely available but reached.
#
# The rule itself lives in scripts/pack-standard.sh and is shared with the wrapper and the feed report,
# so all three cannot drift apart on what a safe pack is.
data=$(cat)
cmd=$(printf '%s' "$data" | jq -r '.tool_input.command // ""')
configured_feed=${KHAOZENGINE_FEED:-}

# Cheap rejections first: this hook runs on every Bash call.
case "$cmd" in *pack*) ;; *) exit 0 ;; esac
feed_text_hit=0
case "$cmd" in
  *local-feed*|*'$KHAOZENGINE_FEED'*|*'${KHAOZENGINE_FEED}'*) feed_text_hit=1 ;;
esac
if [ "$feed_text_hit" = 0 ] && [ -n "$configured_feed" ]; then
  case "$cmd" in *"$configured_feed"*) feed_text_hit=1 ;; esac
fi
[ "$feed_text_hit" = 1 ] || exit 0
# The wrapper carries the same guard, so let it speak for itself rather than denying it from out here.
case "$cmd" in *pack-local-feed.sh*) exit 0 ;; esac
# An inline PACK_RELEASED_OK=1 bypasses only the released-version guard. The shared-feed ancestry
# boundary still applies, matching the wrapper.
release_override=0
printf '%s' "$cmd" | grep -q 'PACK_RELEASED_OK=1' && release_override=1

# Strip heredoc bodies and quoted spans before parsing, so a command that merely TALKS about packing
# (a commit message, an echo, a doc edit) cannot be read as one. Lifted from tag-collision-guard.sh,
# which needs the identical treatment for the identical reason.
nohd=$(printf '%s\n' "$cmd" | awk 'skip==1 { t=$0; sub(/^[ \t]*/,"",t); if (t==term) skip=0; next } { line=$0; p=index(line,"<<"); if (p>0) { rest=substr(line,p+2); sub(/^-?[ \t]*/,"",rest); q=sprintf("%c",39); f=substr(rest,1,1); if (f=="\"" || f==q) rest=substr(rest,2); if (match(rest,/^[A-Za-z_][A-Za-z0-9_]*/)) { term=substr(rest,RSTART,RLENGTH); skip=1 } } print line }')
# Preserve a quoted feed variable or its exact expanded value before removing quoted prose. The marker
# is resolved only after a real dotnet pack statement is found.
protected=$(printf '%s\n' "$nohd" | awk '
  function replace_literal(line, needle, replacement, at, result) {
    if (needle == "") return line
    result = ""
    while ((at = index(line, needle)) > 0) {
      result = result substr(line, 1, at - 1) replacement
      line = substr(line, at + length(needle))
    }
    return result line
  }
  {
    line = replace_literal($0, "\"$KHAOZENGINE_FEED\"", "__KHAOZENGINE_FEED__")
    line = replace_literal(line, "\"${KHAOZENGINE_FEED}\"", "__KHAOZENGINE_FEED__")
    feed = ENVIRON["KHAOZENGINE_FEED"]
    if (feed != "") line = replace_literal(line, "\"" feed "\"", "__KHAOZENGINE_FEED__")
    print line
  }')
stripped=$(printf '%s\n' "$protected" | sed -e 's/"[^"]*"//g' -e "s/'[^']*'//g")
norm=$(printf '%s\n' "$stripped" | tr ';&|(){}`' '\n')
NL='
'
TAB=$(printf '\t')
set -f
dir=''
IFS=$NL
for stmt in $norm; do
  IFS=" $TAB"
  set -- $stmt
  # Skip the wrappers and env assignments a real command can be prefixed with.
  while [ $# -gt 0 ]; do
    case "$1" in
      command|exec|env|nohup|time|sh|bash|dash|zsh|.) shift ;;
      [A-Za-z_]*=*) shift ;;
      *) break ;;
    esac
  done
  [ $# -gt 0 ] || continue
  # Track cd, because the pack is nearly always written as `cd <worktree> && dotnet pack ...`: this
  # repo's Bash cwd does not persist between calls, so every command carries its own cd.
  if [ "$1" = cd ] || [ "$1" = pushd ]; then
    if [ $# -ge 2 ]; then
      case "$2" in
        /*) dir=$2 ;;
        -*) dir='//unknown' ;;
        *) if [ "$dir" = '//unknown' ]; then :; elif [ -n "$dir" ]; then dir="$dir/$2"; else dir="./$2"; fi ;;
      esac
    else
      dir='//unknown'
    fi
    continue
  fi
  [ "$1" = dotnet ] || continue
  shift
  [ "${1:-}" = pack ] || continue
  # Only a pack whose output targets the engine feed is this hook's business. A pack to ./artifacts
  # (what ci.yml does) or a bare pack to bin/ overwrites nothing anybody vendors from.
  feedhit=0
  configured_hit=0
  for a in "$@"; do
    case "$a" in
      *local-feed*) feedhit=1 ;;
      *__KHAOZENGINE_FEED__*|*'$KHAOZENGINE_FEED'*|*'${KHAOZENGINE_FEED}'*)
        if [ -n "$configured_feed" ]; then feedhit=1; configured_hit=1; fi
        ;;
      *)
        if [ -n "$configured_feed" ]; then
          case "$a" in "$configured_feed"|*="$configured_feed") feedhit=1; configured_hit=1 ;; esac
        fi
        ;;
    esac
  done
  [ "$feedhit" = 1 ] || continue

  if [ "$dir" = '//unknown' ]; then continue
  elif [ -n "$dir" ]; then base=$dir
  else base=${CLAUDE_PROJECT_DIR:-.}
  fi
  repo=$(git -C "$base" rev-parse --show-toplevel 2>/dev/null) || continue
  [ -f "$repo/scripts/pack-standard.sh" ] || continue
  [ -f "$repo/scripts/tag-standard.sh" ] || continue

  decision=$(
    cd "$repo" || exit 0
    . ./scripts/tag-standard.sh
    . ./scripts/pack-standard.sh
    v=$(tag_props_version < Directory.Build.props 2>/dev/null || true)
    [ -n "${v:-}" ] || exit 0
    feed_scope=shared
    if [ "$configured_hit" = 1 ]; then
      feed=$(pack_feed_dir) || exit 0
      feed_scope=$(pack_feed_scope "$feed")
    fi
    s=$(pack_release_state "$v")
    if ! pack_state_allows "$s" && [ "$release_override" != 1 ]; then
      pack_refusal_lines "$v" "$s"
      exit 0
    fi
    [ "$feed_scope" = shared ] || exit 0
    h=$(git rev-parse -q --verify 'HEAD^{commit}' 2>/dev/null || true)
    m=$(pack_origin_main_commit)
    if [ -z "${h:-}" ] || [ -z "${m:-}" ] || ! pack_commit_on_origin_main "$h"; then
      pack_shared_feed_refusal_lines "$h" "$m"
      exit 0
    fi
    pack_tree_clean && exit 0
    pack_shared_feed_dirty_refusal_lines
  )
  [ -n "${decision:-}" ] || continue
  reason=$(printf '%s\n%s\n' "$decision" "Use scripts/pack-local-feed.sh, which applies the shared feed guards." | jq -Rs .)
  printf '{"hookSpecificOutput":{"hookEventName":"PreToolUse","permissionDecision":"deny","permissionDecisionReason":%s}}' "$reason"
  exit 0
done
exit 0
