#!/bin/sh
# Keep personal absolute home paths out of tracked text.
# Default: inspect added staged lines, like check-dashes.sh. --tree: inspect all tracked text for CI.
# Allow the hosted runner accounts (runner on Linux and macOS, runneradmin on Windows), Shared and
# angle-bracket placeholders. Published renderer evidence records runner paths verbatim.
# .home-paths-ignore holds exceptional pathspecs, one per line, with blank lines and # comments ignored.
# Staged mode reads that list from the index.
# Exit 0 clean, 1 on a violation, 2 on bad usage or a git error.
set -eu

mode=${1:-staged}
case "$mode" in
  staged|--tree) ;;
  *) echo "check-home-paths: unknown mode '$mode' (expected: staged, --tree)" >&2; exit 2 ;;
esac
top=$(git rev-parse --show-toplevel 2>/dev/null) || {
  echo 'check-home-paths: not inside a git work tree' >&2
  exit 2
}
cd "$top"
g() { git -c core.quotePath=false "$@"; }

ignore=
if [ "$mode" = --tree ]; then
  if [ -f .home-paths-ignore ]; then ignore=$(cat .home-paths-ignore); fi
elif g cat-file -e :.home-paths-ignore 2>/dev/null; then
  ignore=$(g show :.home-paths-ignore)
fi
set -- .
while IFS= read -r spec || [ -n "$spec" ]; do
  spec=$(printf '%s' "$spec" | tr -d '\r' | sed 's/^[[:space:]]*//; s/[[:space:]]*$//')
  case "$spec" in ''|'#'*) continue ;; esac
  set -- "$@" ":(exclude)$spec"
done <<EOF
$ignore
EOF

# Windows separators may be escaped in source or JSON. The memory form uses a dash as its separator.
home_pattern='/Users/([-A-Za-z0-9._+@ ]+|<[^>]+>)/|/home/([-A-Za-z0-9._+@ ]+|<[^>]+>)/|[A-Za-z]:\\+[Uu][Ss][Ee][Rr][Ss]\\+([-A-Za-z0-9._+@ ]+|<[^>]+>)\\+|-Users-([A-Za-z0-9._+@ ]+|<[^>]+>)-'
classify='
BEGIN { pattern = ENVIRON["CHECK_HOME_PATTERN"] }
{
  if (!match($0, /:[0-9]+:/)) next
  file = substr($0, 1, RSTART - 1)
  line = substr($0, RSTART + 1, RLENGTH - 2)
  text = substr($0, RSTART + RLENGTH)
  while (match(text, pattern)) {
    path = substr(text, RSTART, RLENGTH)
    text = substr(text, RSTART + RLENGTH)
    name = path
    if (name ~ /^-Users-/) {
      sub(/^-Users-/, "", name)
      sub(/-$/, "", name)
    } else {
      sub(/^\/(Users|home)\//, "", name)
      sub(/^[A-Za-z]:\\+[Uu][Ss][Ee][Rr][Ss]\\+/, "", name)
      sub(/[\/\\]+$/, "", name)
    }
    if (name == "runner" || name == "runneradmin" || name == "Shared" || name ~ /^<[^>]+>$/) continue
    printf "check-home-paths: %s:%s: absolute home path %s\n", file, line, path
  }
}'

# Keep new-file line numbers even when a user configures diff.interHunkContext.
added_lines='
/^diff --git / { hdr = 1; next }
hdr && /^\+\+\+ / { file = substr($0, 7); sub(/\t$/, "", file); next }
/^@@ / {
  hdr = 0
  h = $0
  sub(/^@@ -[0-9,]+ \+/, "", h)
  sub(/[^0-9].*$/, "", h)
  ln = h + 0
  next
}
hdr { next }
/^\+/ { print file ":" ln ":" substr($0, 2); ln++; next }
/^ / { ln++ }'

if [ "$mode" = --tree ]; then
  rc=0
  refs=$(g -c grep.column=false --no-pager grep -I -n -E --no-color -e "$home_pattern" -- "$@") || rc=$?
  if [ "$rc" -gt 1 ]; then echo 'check-home-paths: git grep failed' >&2; exit 2; fi
else
  diff=$(g diff --cached --no-color --no-ext-diff --no-textconv --unified=0 --inter-hunk-context=0 \
    --src-prefix=a/ --dst-prefix=b/ --diff-filter=d -- "$@") || {
    echo 'check-home-paths: git diff failed' >&2
    exit 2
  }
  refs=$(printf '%s\n' "$diff" | awk "$added_lines")
fi
# Environment transport preserves regex backslashes without awk -v decoding them a second time.
out=$(printf '%s\n' "$refs" | CHECK_HOME_PATTERN="$home_pattern" awk "$classify")
if [ -n "$out" ]; then printf '%s\n' "$out" >&2; exit 1; fi
