#!/bin/sh
# Template-managed: canonical copy is game-template/scripts/check-prose.sh. Do not hand-edit in a game repo:
# change it in the template and re-propagate (scaffold-game-repo skill). Commits are gated by the pre-commit template-sync check.
# check-prose.sh - enforce the prose-semicolon ban (global CLAUDE.md writing style, restated in this
# repo's AGENTS.md) in .md files. Semicolons in CODE are fine, so the checker strips fenced code
# blocks (``` / ~~~), indented code blocks, inline code spans, and HTML comments before looking, and
# only ever reads .md. Same two modes as check-dashes.sh:
#
#   (default)   staged mode - checks only ADDED lines in the currently staged diff. What
#               .githooks/pre-commit runs at commit time: pre-existing semicolons elsewhere in a
#               touched file are ignored, only what you are adding is checked.
#   --tree      whole-tree mode - checks every tracked .md file as it stands. A manual sweep, NOT a CI
#               step yet, which is the one place this differs from check-dashes.sh: the rule postdates
#               the docs, so the fleet still carries ~1100 pre-existing prose semicolons (33 here, 73
#               Hardpoint, 169 SpaceGame, 400 Nullwake, 460 Ruinborne, measured for #35). Wiring
#               --tree into build-test.yml reds every repo until those are swept, so it waits for the
#               sweep. Accuracy is not the blocker: of 1135 tree hits, 5 were false positives (4 are
#               one unbackticked `net8.0;net10.0` in docs/SERVER-STATUS.md, replicated per repo).
set -eu

mode=${1:-staged}

# stdin = markdown, stdout = "<lineno><TAB><line>" for each prose line carrying a semicolon.
scan() {
  awk '
    NR == 1 && $0 == "---" { fm = 1; next }
    fm == 1 { if ($0 == "---" || $0 == "...") fm = 0; next }

    {
      line = $0
      t = line
      sub(/^[ \t]+/, "", t)
      m = ""
      if (substr(t, 1, 3) == "```") m = "```"
      else if (substr(t, 1, 3) == "~~~") m = "~~~"

      if (infence) { if (m == marker) infence = 0; next }
      if (m != "") { infence = 1; marker = m; span = 0; next }
    }

    # a code span cannot contain a blank line, so a paragraph break always closes one (this bounds
    # the damage a stray unpaired backtick can do to one paragraph instead of the rest of the file)
    /^[ \t]*$/ { span = 0; next }

    # indented code block (4+ spaces)
    /^(    |\t)/ { next }

    {
      line = $0
      gsub(/<!--.*-->/, " ", line)
      # tail of an inline code span opened on an earlier line: drop everything up to its close
      if (span) {
        p = index(line, "`")
        if (p == 0) next
        line = substr(line, p + 1)
        span = 0
      }
      while (match(line, /`[^`]*`/)) line = substr(line, 1, RSTART - 1) " " substr(line, RSTART + RLENGTH)
      # an unclosed backtick opens a span that continues on the next line: drop the tail, do not guess
      p = index(line, "`")
      if (p > 0) { line = substr(line, 1, p - 1); span = 1 }
      if (index(line, ";") > 0) printf "%d\t%s\n", NR, $0
    }
  '
}

report() {
  echo "check-prose: prose semicolon found in .md (banned in shipped text: split the sentence, or use a comma, colon, or parentheses):" >&2
  cat >&2
  return 1
}

case "$mode" in
  --tree)
    fail=0
    out=$(
      git ls-files '*.md' | while IFS= read -r f; do
        [ -f "$f" ] || continue
        scan < "$f" | awk -F'\t' -v f="$f" '{ printf "  %s:%s: %s\n", f, $1, substr($0, index($0, "\t") + 1) }'
      done
    )
    if [ -n "$out" ]; then
      printf '%s\n' "$out" | report || fail=1
    fi
    exit $fail
    ;;
  staged)
    fail=0
    files=$(git diff --cached --name-only --diff-filter=ACMR -- '*.md' 2>/dev/null || true)
    [ -n "$files" ] || exit 0
    out=$(
      printf '%s\n' "$files" | while IFS= read -r f; do
        [ -n "$f" ] || continue
        added=$(git diff --cached --unified=0 -- "$f" 2>/dev/null | awk '
          /^@@ / {
            n = $3
            sub(/^\+/, "", n)
            c = n
            len = 1
            if (index(n, ",")) { split(n, a, ","); c = a[1]; len = a[2] }
            for (i = 0; i < len; i++) print c + i
          }')
        [ -n "$added" ] || continue
        git show ":$f" 2>/dev/null | scan | awk -F'\t' -v add=" $(printf '%s' "$added" | tr '\n' ' ') " -v f="$f" '
          { if (index(add, " " $1 " ")) printf "  %s:%s: %s\n", f, $1, substr($0, index($0, "\t") + 1) }'
      done
    )
    if [ -n "$out" ]; then
      printf '%s\n' "$out" | report || fail=1
    fi
    exit $fail
    ;;
  *)
    echo "check-prose: unknown mode '$mode' (expected: staged, --tree)" >&2
    exit 2
    ;;
esac
