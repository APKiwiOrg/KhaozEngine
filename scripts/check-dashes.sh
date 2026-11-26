#!/bin/sh
# Template-managed: canonical copy is game-template/scripts/check-dashes.sh. Do not hand-edit in a game repo:
# change it in the template and re-propagate (scaffold-game-repo skill). Commits are gated by the pre-commit template-sync check.
# check-dashes.sh - enforce the em/en-dash ban (global CLAUDE.md writing style, restated in this
# repo's AGENTS.md) in Markdown, C#, native source and CMake files. Two modes so the hook and CI agree on what
# counts as a violation:
#
#   (default)   staged mode - checks only ADDED lines in the currently staged diff. What
#               .githooks/pre-commit runs at commit time: pre-existing dashes elsewhere in a touched
#               file are ignored, only what you are adding is checked.
#   --tree      whole-tree mode - checks all tracked lines in the covered files. What CI runs, so a
#               commit that bypassed the hook (--no-verify, another IDE, the GitHub web UI) still gets
#               caught.
set -eu

mode=${1:-staged}

dash_pattern=$(printf '\342\200\224|\342\200\223')

case "$mode" in
  --tree)
    hit=$(git ls-files -z -- '*.md' '*.cs' '*.h' '*.hpp' '*.cpp' '*.slang' '*.cmake' '*CMakeLists.txt' \
      | LC_ALL=C xargs -0 grep -lE -- "$dash_pattern" 2>/dev/null || true)
    if [ -n "$hit" ]; then
      echo "check-dashes: em-dash or en-dash found in tracked files (banned in shipped text):" >&2
      printf '%s\n' "$hit" | sed 's/^/  /' >&2
      exit 1
    fi
    ;;
  staged)
    added=$(git diff --cached --unified=0 -- '*.md' '*.cs' '*.h' '*.hpp' '*.cpp' '*.slang' '*.cmake' '*CMakeLists.txt' \
      2>/dev/null | grep -E '^\+' | grep -vE '^\+\+\+' || true)
    if printf '%s' "$added" | LC_ALL=C grep -qE "$dash_pattern"; then
      echo "check-dashes: staged additions contain an em-dash or en-dash (banned in shipped text)." >&2
      exit 1
    fi
    ;;
  *)
    echo "check-dashes: unknown mode '$mode' (expected: staged, --tree)" >&2
    exit 2
    ;;
esac
