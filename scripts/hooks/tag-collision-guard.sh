#!/bin/sh
# tag-collision-guard.sh - PreToolUse Bash guard. Reads the hook JSON on stdin, parses the proposed
# command for a release-tag action (git tag vX.Y.Z, git push of a vX.Y.Z tag, scripts/tag-release.sh),
# and denies it when that tag already exists locally or on origin, so a concurrent release cannot have
# its version taken twice. Silent (exit 0, no output) for anything else, which is the allow path.
#
# Lived inline in .claude/settings.json and .codex/settings.json as one JSON string until it was moved
# here verbatim, and both settings files now invoke this file. The body is the ENGINE's own copy, not
# the game-template one: it reads the version through scripts/tag-standard.sh (tag_props_version)
# rather than a raw sed, and its deny message also asks for the CHANGELOG entry to be rebased. Keep
# edits surgical.
data=$(cat)
cmd=$(printf '%s' "$data" | jq -r '.tool_input.command // ""')
case "$cmd" in *tag-release.sh*|*git*) ;; *) exit 0 ;; esac
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
  while [ $# -gt 0 ]; do
    case "$1" in
      command|exec|env|nohup|time|sh|bash|dash|zsh|.) shift ;;
      [A-Za-z_]*=*) shift ;;
      *) break ;;
    esac
  done
  [ $# -gt 0 ] || continue
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
  kind=''
  cand=''
  gd=''
  case "$1" in
    tag-release.sh|*/tag-release.sh)
      kind=rel
      ;;
    git)
      shift
      while [ $# -gt 0 ]; do
        case "$1" in
          -C) gd=${2:-}; shift 2 2>/dev/null || break ;;
          -c|--git-dir|--work-tree) shift 2 2>/dev/null || break ;;
          --git-dir=*|--work-tree=*|--namespace=*|-P|--no-pager|--paginate) shift ;;
          *) break ;;
        esac
      done
      [ $# -gt 0 ] || continue
      sub=$1
      shift
      if [ "$sub" = tag ]; then
        create=1
        name=''
        while [ $# -gt 0 ]; do
          case "$1" in
            -d|--delete|-l|--list|-v|--verify|--contains|--no-contains|--points-at|--merged|--no-merged|-n*) create=0; break ;;
            -m|-F|-u|--format|--sort|--color) shift 2 2>/dev/null || break ;;
            -*) shift ;;
            *) name=$1; break ;;
          esac
        done
        if [ "$create" = 1 ] && [ -n "$name" ] && printf '%s' "$name" | grep -qE '^v[0-9]+\.[0-9]+\.[0-9]+$'; then
          kind=mk
          cand=$name
        fi
      elif [ "$sub" = push ]; then
        del=0
        expect=0
        for a in "$@"; do
          if [ "$expect" = 1 ]; then
            expect=0
            if [ -z "$cand" ] && printf '%s' "$a" | grep -qE '^v[0-9]+\.[0-9]+\.[0-9]+$'; then cand=$a; fi
            continue
          fi
          case "$a" in
            -d|--delete|-n|--dry-run) del=1 ;;
            tag) expect=1 ;;
            *)
              b=${a#+}
              case "$b" in refs/tags/*) b=${b#refs/tags/}; b=${b%%:*} ;; esac
              if [ -z "$cand" ] && printf '%s' "$b" | grep -qE '^v[0-9]+\.[0-9]+\.[0-9]+$'; then cand=$b; fi
              ;;
          esac
        done
        if [ "$del" = 0 ] && [ -n "$cand" ]; then kind=push; fi
      fi
      ;;
  esac
  [ -n "$kind" ] || continue
  if [ -n "$gd" ]; then base=$gd
  elif [ "$dir" = '//unknown' ]; then continue
  elif [ -n "$dir" ]; then base=$dir
  else base=.
  fi
  repo=$(git -C "$base" rev-parse --show-toplevel 2>/dev/null) || continue
  mk=0
  case "$kind" in
    rel)
      mk=1
      cand=$( cd "$repo" 2>/dev/null && [ -f scripts/tag-standard.sh ] && . scripts/tag-standard.sh && tag_props_version < Directory.Build.props 2>/dev/null )
      [ -n "$cand" ] || continue
      cand="v$cand"
      ;;
    mk)
      mk=1
      ;;
  esac
  git -C "$repo" fetch --tags --quiet 2>/dev/null
  hit=0
  if git -C "$repo" ls-remote --tags origin "$cand" 2>/dev/null | grep -q "refs/tags/$cand$"; then hit=1; fi
  if [ "$hit" = 0 ] && [ "$mk" = 1 ] && git -C "$repo" rev-parse -q --verify "refs/tags/$cand" >/dev/null 2>&1; then hit=1; fi
  if [ "$hit" = 1 ]; then
    printf '{"hookSpecificOutput":{"hookEventName":"PreToolUse","permissionDecision":"deny","permissionDecisionReason":"Tag %s already exists in %s (local or origin). A concurrent release likely took it. Re-read the current version and tags, bump to the next free version, rebase the CHANGELOG entry, then tag."}}' "$cand" "$repo"
    exit 0
  fi
done
exit 0
