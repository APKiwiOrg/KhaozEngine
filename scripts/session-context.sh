#!/usr/bin/env bash
# Emits the SessionStart additionalContext payload for Claude Code.
#
# Injects two things every session needs before it decides what its task is:
#   1. concurrent work (worktrees + current branch), so a new chat sees what else is in flight
#   2. the discovered-work ledger (open follow-up count + every handoff line in docs/TODO.md)
#
# (2) is deliberately informational and never blocks. There is no signal for "you noticed
# something and did not write it down", so any hook aiming at that fires on non-violations and
# trains people to write junk entries to silence it. Putting the ledger in front of the session
# instead has no false positives and nothing to bypass.
#
# Prints a single JSON object on stdout. Safe to run anywhere: outside a git repo, with no
# docs/TODO.md, or with no jq, it degrades rather than failing the session start.

set -uo pipefail

worktrees=$(git worktree list 2>/dev/null)
branch=$(git status -sb 2>/dev/null | head -1)

todo="docs/TODO.md"
if [ -f "$todo" ]; then
  # An open item is a plain "- " bullet or "- [ ]" or "- [~]" (in progress). "- [x]" is resolved
  # and is awaiting the release sweep, so it is not open. Nested bullets are not counted.
  open_count=$(grep -cE '^- (\[ \]|\[~\]|[^[])' "$todo" 2>/dev/null || printf '0')
  handoffs=$(grep -hE '\*\*(Handed off|Blocked on):\*\*' "$todo" 2>/dev/null | sed 's/^[[:space:]]*/  /' | head -20)
  if [ -n "$handoffs" ]; then
    ledger=$(printf 'open follow-ups in docs/TODO.md: %s\nhandoffs and blockers:\n%s' "$open_count" "$handoffs")
  else
    ledger=$(printf 'open follow-ups in docs/TODO.md: %s' "$open_count")
  fi
else
  ledger="(no docs/TODO.md in this repo)"
fi

ctx=$(printf 'git worktrees:\n%s\n\nbranch:\n%s\n\n%s' "$worktrees" "$branch" "$ledger")

if command -v jq >/dev/null 2>&1; then
  printf '{"hookSpecificOutput":{"hookEventName":"SessionStart","additionalContext":%s}}' \
    "$(printf '%s' "$ctx" | jq -Rs .)"
else
  # jq absent: emit nothing rather than malformed JSON. A broken payload is worse than none.
  exit 0
fi
