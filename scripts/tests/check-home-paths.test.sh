#!/bin/sh
# Exercise home-path detection against real staged diffs and tracked text in temporary repos.
# Fixture paths are assembled at runtime so this test can pass the guard it exercises.
set -eu

here=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
SCRIPT=${1:-$here/../check-home-paths.sh}
case "$SCRIPT" in /*) ;; *) SCRIPT=$(CDPATH= cd -- "$(dirname -- "$SCRIPT")" && pwd)/$(basename -- "$SCRIPT") ;; esac
TMPROOT=$(mktemp -d)
trap 'rm -rf "$TMPROOT"' EXIT
OUTFILE="$TMPROOT/out"
pass=0
fail=0
case_no=0
check() {
  if [ "$2" = "$rc" ]; then pass=$((pass+1)); echo "  ok    $1"
  else fail=$((fail+1)); echo "  FAIL  $1 (expected exit $2, got $rc)"; sed 's/^/        /' "$OUTFILE"; fi
}
run() { set +e; sh "$SCRIPT" "$@" >"$OUTFILE" 2>&1; rc=$?; set -e; }
said() { if ! grep -qF -- "$1" "$OUTFILE"; then rc="$rc, without '$1'"; fi; }
commit() { git -c user.email=t@t.t -c user.name=t -c commit.gpgsign=false commit -q --no-verify -m fixture; }
fresh() {
  case_no=$((case_no+1))
  mkdir "$TMPROOT/case$case_no"
  cd "$TMPROOT/case$case_no"
  git init -q .
  printf 'Clean text.\n' > README.md
  git add README.md
  commit
}
put() { mkdir -p "$(dirname -- "$1")"; printf '%s\n' "$2" > "$1"; }

# Removing detection for any form must fail these cases. No expected result uses the scanner.
person=fixture-person
mac=$(printf '/%s/%s/' Users "$person")
linux=$(printf '/%s/%s/' home "$person")
windows=$(printf '%s\\%s\\%s\\' 'C:' Users "$person")
escaped=$(printf '%s\\\\%s\\\\%s\\\\' 'D:' Users "$person")
windows_space=$(printf '%s\\%s\\%s\\' 'C:' Users 'fixture person')
mac_symbol=$(printf '/%s/%s/' Users 'fixture+person')
memory=$(printf -- '-%s-%s-' Users fixture)

fresh; run --tree; check tree_clean 0
run; check staged_clean 0

for form in mac linux windows escaped windows_space mac_symbol memory; do
  case "$form" in
    mac) path=$mac ;; linux) path=$linux ;; windows) path=$windows ;;
    escaped) path=$escaped ;; memory) path=$memory ;;
    windows_space) path=$windows_space ;; mac_symbol) path=$mac_symbol ;;
  esac
  fresh; put paths.txt "prefix ${path}project/file suffix"; git add paths.txt
  run --tree; said 'check-home-paths: paths.txt:1:'; check "tree_$form" 1
  run; said 'check-home-paths: paths.txt:1:'; check "staged_$form" 1
done

fresh
{
  printf '/%s/%s/work\n' Users runner Users Shared Users '<you>' home runner home '<name>'
  printf '%s\\%s\\%s\\work\n' 'C:' Users runner 'C:' Users Shared 'C:' Users '<name>'
  printf '%s\\%s\\%s\\work\n' 'C:' Users runneradmin
  printf -- '-%s-%s-project\n' Users runner Users Shared Users '<name>'
  printf '~/project/file\nrelative/project/file\n'
} > allowed.txt
git add allowed.txt
run --tree; check tree_allowed_names_and_relative_paths 0
run; check staged_allowed_names_and_relative_paths 0

fresh; put paths.txt "$(printf '/%s/%s/work' Users runner) ${mac}project"
git add paths.txt; run --tree; check allowed_path_does_not_hide_private_path 1

fresh; put paths.txt "$(printf '/%s/%s/work' Users runner-private)"
git add paths.txt; run --tree; check allow_list_matches_entire_name 1

fresh; put paths.txt "${mac}old"; git add paths.txt; commit
printf 'New clean line.\n' >> paths.txt; git add paths.txt
run; check staged_existing_violation_is_ignored 0
run --tree; check tree_existing_violation_is_caught 1

fresh; put paths.txt "${linux}staged"; git add paths.txt; put paths.txt clean
run; check staged_reads_index_not_unstaged_repair 1

fresh; put paths.txt clean; git add paths.txt; put paths.txt "${linux}unstaged"
run; check staged_ignores_unstaged_violation 0
run --tree; check tree_reads_working_copy 1

fresh; put paths.txt "${mac}deleted"; git add paths.txt; commit; git rm -q paths.txt
run; check staged_deletion 0

fresh; put untracked.txt "${mac}untracked"; run --tree; check tree_ignores_untracked_files 0

fresh; printf '\000%s\n' "${mac}binary" > binary.dat; git add binary.dat
run --tree; check tree_ignores_binary_files 0
run; check staged_ignores_binary_files 0

fresh; put 'docs/path with spaces.txt' "clean
${mac}project"; git add docs
run; said 'check-home-paths: docs/path with spaces.txt:2:'; check staged_space_in_filename_and_line_number 1
run --tree; said 'check-home-paths: docs/path with spaces.txt:2:'; check tree_space_in_filename_and_line_number 1

fresh; put fixtures/redaction.txt "${mac}synthetic"; put docs/private.txt "${linux}private"
printf '# Synthetic fixture only.\n\nfixtures/*.txt\n' > .home-paths-ignore
git add -A
run --tree; said 'docs/private.txt:1:'; check ignore_pathspec_does_not_hide_other_files 1
git rm -q --cached docs/private.txt; rm docs/private.txt
run --tree; check tree_ignore_pathspec 0
run; check staged_ignore_pathspec 0

fresh; put fixtures/path.txt "${mac}synthetic"; git add fixtures
printf 'fixtures/*.txt\n' > .home-paths-ignore
run; check unstaged_ignore_cannot_bypass_staged_guard 1

fresh; put fixtures/path.txt "${mac}synthetic"
printf 'fixtures/*.txt\n' > .home-paths-ignore; git add -A; commit
git rm -q --cached .home-paths-ignore
printf 'new %s\n' "${mac}synthetic" >> fixtures/path.txt; git add fixtures/path.txt
run; check staged_ignore_removal_uses_index 1

fresh; printf '../outside\n' > .home-paths-ignore; git add .home-paths-ignore
run --tree; check tree_invalid_ignore_pathspec 2
run; check staged_invalid_ignore_pathspec 2

fresh; run --unknown; check unknown_mode 2
cd "$TMPROOT"; run --tree; check outside_git_repository 2

echo "passed: $pass   failed: $fail"
[ "$fail" -eq 0 ]
