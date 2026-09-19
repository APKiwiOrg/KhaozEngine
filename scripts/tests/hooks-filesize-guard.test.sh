#!/bin/sh
# Pin the Bash guard that distinguishes baseline reads from writes.
set -eu
here=$(CDPATH='' cd -- "$(dirname -- "$0")" && pwd)
ROOT=${1:-$here/../..}
ROOT=$(CDPATH='' cd -- "$ROOT" && pwd)
CLAUDE_HOOKS="$ROOT/.claude/settings.json"
CODEX_HOOKS="$ROOT/.codex/hooks.json"
command -v jq >/dev/null 2>&1 || { echo "hooks-filesize-guard.test: jq is required" >&2; exit 2; }

extract() {
  jq -r '.hooks.PreToolUse[] | select(.matcher == "Bash") | .hooks[]
         | select(.command | test("filesize-baseline")) | .command' "$1"
}

[ -f "$CLAUDE_HOOKS" ] || { echo "missing $CLAUDE_HOOKS" >&2; exit 2; }
[ -f "$CODEX_HOOKS" ] || { echo "missing $CODEX_HOOKS" >&2; exit 2; }
GUARD=$(extract "$CLAUDE_HOOKS")
CODEX_GUARD=$(extract "$CODEX_HOOKS")
[ "$GUARD" = "$CODEX_GUARD" ] || { echo "FAIL: baseline Bash guards differ" >&2; exit 1; }

pass=0
fail=0
decision() {
  printf '%s' "$1" | jq -Rs '{tool_name:"Bash",tool_input:{command:.}}' | sh -c "$GUARD" \
    | jq -r '.hookSpecificOutput.permissionDecision // empty' 2>/dev/null
}
reads() {
  got=$(decision "$1")
  if [ -z "$got" ]; then pass=$((pass + 1)); else echo "FAIL: read prompted: $1"; fail=$((fail + 1)); fi
}
writes() {
  got=$(decision "$1")
  if [ "$got" = ask ]; then pass=$((pass + 1)); else echo "FAIL: write stayed quiet: $1"; fail=$((fail + 1)); fi
}

reads 'cat .filesize-baseline'
reads 'cat .filesize-baseline 2>/dev/null'
reads 'git diff HEAD -- .filesize-baseline'
reads "sed -n '1,5p' .filesize-baseline"
reads 'cat .filesize-baseline > /tmp/baseline-copy'
reads 'sh scripts/check-file-size.sh --update'
writes 'echo "Foo.cs 100" >> .filesize-baseline'
writes 'echo x > ./.filesize-baseline'
writes "printf '%s\n' x | tee -a .filesize-baseline"
writes "sed -i '' 's/a/b/' .filesize-baseline"
writes 'rm .filesize-baseline'
writes 'python3 -c "open('"'"'.filesize-baseline'"'"', '"'"'w'"'"').write(s)"'
# shellcheck disable=SC2016
writes 'perl -e '"'"'open(my $f, q{>}, q{.filesize-baseline})'"'"''

echo "hooks-filesize-guard.test: $pass passed, $fail failed"
[ "$fail" -eq 0 ]
