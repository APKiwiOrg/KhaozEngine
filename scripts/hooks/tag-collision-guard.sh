#!/bin/sh
# tag-collision-guard.sh - PreToolUse Bash guard. Reads the hook JSON on stdin, parses the proposed
# command for a release tag named LITERALLY in it (git tag vX.Y.Z, git push of a vX.Y.Z tag), and
# denies it when that tag already exists locally or on origin, so a concurrent release cannot have its
# version taken twice. Silent (exit 0, no output) for anything else, which is the allow path.
#
# It does NOT gate scripts/tag-release.sh, deliberately (issue #261). A PreToolUse hook runs BEFORE the
# command it judges, so for the ritual's own chained `git merge <branch> && scripts/tag-release.sh` it
# could only read the PRE-merge <KhaozEngineVersion> out of Directory.Build.props, which is the
# PREVIOUS release's version, whose tag legitimately exists. It denied the documented release chain
# every time, and the deny killed the merge with it. The authoritative check lives in tag-release.sh
# instead, which reads the version at the moment it is true. What is left here is the half a hook can
# actually judge: a version the command spells out itself.
#
# Lived inline in .claude/settings.json and .codex/settings.json as one JSON string until it was moved
# here verbatim, and both settings files now invoke this file. The rule for what "already taken" means
# is scripts/tag-standard.sh (tag_taken), shared with tag-release.sh so the two cannot drift.
here=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
[ -f "$here/../tag-standard.sh" ] || exit 0
. "$here/../tag-standard.sh"
data=$(cat)
cmd=$(printf '%s' "$data" | jq -r '.tool_input.command // ""')
case "$cmd" in *git*) ;; *) exit 0 ;; esac
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
  # Creating the tag collides with either copy of it. Pushing one is expected to have a local copy
  # already, so only origin counts there.
  case "$kind" in
    mk) scope=any ;;
    *) scope=origin ;;
  esac
  if tag_taken "$repo" "$cand" "$scope"; then
    printf '{"hookSpecificOutput":{"hookEventName":"PreToolUse","permissionDecision":"deny","permissionDecisionReason":"Tag %s already exists in %s (%s). A concurrent release likely took it. Re-read the current version and tags, bump to the next free version, rebase the CHANGELOG entry, then tag."}}' "$cand" "$repo" "$TAG_TAKEN_WHERE"
    exit 0
  fi
done
exit 0
