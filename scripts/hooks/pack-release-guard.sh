#!/bin/sh
# pack-release-guard.sh - PreToolUse Bash guard. Reads the hook JSON on stdin, looks for a proposed
# `dotnet pack` whose output lands in local-feed, and denies it when <KhaozEngineVersion> names a
# version that is already released (issue #492). Silent (exit 0, no output) for everything else, which
# is the allow path. Same shape and the same stdin contract as scripts/hooks/tag-collision-guard.sh,
# which both settings files already invoke.
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

# Cheap rejections first: this hook runs on every Bash call.
case "$cmd" in *pack*) ;; *) exit 0 ;; esac
case "$cmd" in *local-feed*) ;; *) exit 0 ;; esac
# The wrapper carries the same guard, so let it speak for itself rather than denying it from out here.
case "$cmd" in *pack-local-feed.sh*) exit 0 ;; esac
# An inline PACK_RELEASED_OK=1 is the sanctioned override and is honoured here exactly as the wrapper
# honours it from the environment.
printf '%s' "$cmd" | grep -q 'PACK_RELEASED_OK=1' && exit 0

# Strip heredoc bodies and quoted spans before parsing, so a command that merely TALKS about packing
# (a commit message, an echo, a doc edit) cannot be read as one. Lifted from tag-collision-guard.sh,
# which needs the identical treatment for the identical reason.
nohd=$(printf '%s\n' "$cmd" | awk 'skip==1 { t=$0; sub(/^[ \t]*/,"",t); if (t==term) skip=0; next } { line=$0; p=index(line,"<<"); if (p>0) { rest=substr(line,p+2); sub(/^-?[ \t]*/,"",rest); q=sprintf("%c",39); f=substr(rest,1,1); if (f=="\"" || f==q) rest=substr(rest,2); if (match(rest,/^[A-Za-z_][A-Za-z0-9_]*/)) { term=substr(rest,RSTART,RLENGTH); skip=1 } } print line }')
stripped=$(printf '%s\n' "$nohd" | sed -e 's/"[^"]*"//g' -e "s/'[^']*'//g")
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
  # Only a pack whose output actually lands in local-feed is this hook's business. A pack to ./artifacts
  # (what ci.yml does) or a bare pack to bin/ overwrites nothing anybody vendors from.
  feedhit=0
  for a in "$@"; do
    case "$a" in *local-feed*) feedhit=1 ;; esac
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
    s=$(pack_release_state "$v")
    pack_state_allows "$s" && exit 0
    pack_refusal_lines "$v" "$s"
  )
  [ -n "${decision:-}" ] || continue
  reason=$(printf '%s\n%s\n' "$decision" "Use scripts/pack-local-feed.sh, which carries this guard, once the version is bumped." | jq -Rs .)
  printf '{"hookSpecificOutput":{"hookEventName":"PreToolUse","permissionDecision":"deny","permissionDecisionReason":%s}}' "$reason"
  exit 0
done
exit 0
