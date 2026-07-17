#!/usr/bin/env bash
# Reciprocity guard for the discovered-work ledger (docs/TODO.md).
#
# A cross-repo handoff is only real when BOTH sides carry an entry. This line:
#
#   **Handed off:** KhaozEngine `docs/TODO.md` "Void/plane fallback for ground decals" (2026-07-17)
#
# requires a matching entry titled "Void/plane fallback for ground decals" in
# KhaozEngine's docs/TODO.md. Without it, that repo never learns it is blocking
# anyone, which is exactly how a scoped ask gets silently dropped.
#
# A pointer that cannot answer back (a branch, a chat, a person) uses
# **Blocked on:** instead and is never checked here.
#
# Policy, deliberately asymmetric:
#   - BLOCKS a push that ADDS a one-sided **Handed off:** line. That is your bug,
#     written in this push, and you are the one who can fix it.
#   - WARNS on a pre-existing one-sided line. A sibling repo renaming its entry
#     must not block your unrelated push. Noise that blocks gets bypassed, and a
#     hook that gets bypassed protects nothing.
#   - WARNS when the target repo is not checked out locally. Cannot verify is not
#     the same as broken.
#
# Fleet root: $KE_FLEET_DIR, default the parent directory of this repo.
# Override the whole check with HANDOFF_CHECK_OK=1 for a deliberate exception.

set -uo pipefail

[ "${HANDOFF_CHECK_OK:-0}" = "1" ] && exit 0

root=$(git rev-parse --show-toplevel 2>/dev/null) || exit 0
todo="$root/docs/TODO.md"
[ -f "$todo" ] || exit 0

# Resolve the fleet root from the MAIN checkout, never the current worktree. Inside
# .claude/worktrees/<name> the toplevel IS the worktree, so its parent is the worktrees
# dir and every sibling repo looks absent. That silently downgrades a block to a warning
# in the one place the guard matters most, because all real work happens in a worktree.
# --porcelain because a repo path may contain spaces.
main_root=$(git worktree list --porcelain 2>/dev/null | sed -n 's/^worktree //p' | head -1)
[ -n "$main_root" ] || main_root="$root"
fleet="${KE_FLEET_DIR:-$(dirname "$main_root")}"

# Pull the handoff target repo and the quoted item title out of one ledger line.
# Tolerates optional backticks around both the repo name and the docs/TODO.md path.
parse_repo() {
  printf '%s' "$1" | sed -nE 's/.*\*\*Handed off:\*\*[[:space:]]*`?([A-Za-z0-9_.-]+)`?.*/\1/p'
}
parse_title() {
  printf '%s' "$1" | sed -nE 's/.*\*\*Handed off:\*\*[^"]*"([^"]+)".*/\1/p'
}

# One handoff line in, verdict out: ok | missing | unresolvable
verify_line() {
  local line="$1" repo title target
  repo=$(parse_repo "$line")
  title=$(parse_title "$line")
  [ -z "$repo" ] && { printf 'unresolvable\tunparseable target'; return; }
  [ -z "$title" ] && { printf 'unresolvable\tno quoted item title'; return; }

  target="$fleet/$repo/docs/TODO.md"
  if [ ! -f "$target" ]; then
    printf 'unresolvable\t%s not checked out at %s' "$repo" "$target"
    return
  fi
  if grep -Fq "$title" "$target"; then
    printf 'ok\t'
  else
    printf 'missing\t%s has no entry titled "%s"' "$repo" "$title"
  fi
}

# Lines this push ADDS to any TODO.md, across every ref being pushed.
added_handoffs() {
  local local_ref local_sha remote_ref remote_sha range
  local zero="0000000000000000000000000000000000000000"
  while read -r local_ref local_sha remote_ref remote_sha; do
    [ "$local_sha" = "$zero" ] && continue          # branch deletion
    if [ "$remote_sha" = "$zero" ]; then            # new branch, compare to the default
      range=$(git merge-base "$local_sha" origin/main 2>/dev/null)
      [ -z "$range" ] && range=$(git merge-base "$local_sha" main 2>/dev/null)
      [ -z "$range" ] && continue
      range="$range..$local_sha"
    else
      range="$remote_sha..$local_sha"
    fi
    git diff "$range" -- '*TODO.md' 2>/dev/null \
      | grep '^+' | grep -v '^+++' | sed 's/^+//' \
      | grep -F '**Handed off:**'
  done
}

fail=0
new_lines=$(added_handoffs)

if [ -n "$new_lines" ]; then
  while IFS= read -r line; do
    [ -z "$line" ] && continue
    result=$(verify_line "$line")
    verdict=${result%%$'\t'*}
    detail=${result#*$'\t'}
    case "$verdict" in
      missing)
        printf 'BLOCKED: this push adds a one-sided handoff.\n' >&2
        printf '  %s\n' "$(printf '%s' "$line" | sed 's/^[[:space:]]*//')" >&2
        printf '  %s\n' "$detail" >&2
        printf '  A handoff writes both sides. Add the reciprocal entry there, or use\n' >&2
        printf '  **Blocked on:** if the target cannot write back (a branch or a chat).\n' >&2
        fail=1
        ;;
      unresolvable)
        printf 'warning: cannot verify a handoff added by this push (%s)\n' "$detail" >&2
        ;;
    esac
  done <<< "$new_lines"
fi

# Pre-existing lines warn only. Someone else's rename is not your push's problem.
while IFS= read -r line; do
  [ -z "$line" ] && continue
  printf '%s' "$new_lines" | grep -Fq "$line" && continue
  result=$(verify_line "$line")
  case "${result%%$'\t'*}" in
    missing)
      printf 'warning: existing one-sided handoff in docs/TODO.md (%s)\n' "${result#*$'\t'}" >&2
      ;;
  esac
done < <(grep -F '**Handed off:**' "$todo" 2>/dev/null)

if [ "$fail" = 1 ]; then
  printf '\nOverride with HANDOFF_CHECK_OK=1 git push (only for a deliberate exception).\n' >&2
  exit 1
fi
exit 0
