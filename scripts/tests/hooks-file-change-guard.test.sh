#!/bin/sh
# Exercise the engine Write, Edit, and apply_patch guards plus every custom hook registration.
set -eu

here=$(CDPATH='' cd -- "$(dirname -- "$0")" && pwd)
ROOT=${1:-$here/../..}
ROOT=$(CDPATH='' cd -- "$ROOT" && pwd)
HELPER="$ROOT/scripts/hooks/file-change-guard.pl"
CLAUDE_HOOKS="$ROOT/.claude/settings.json"
CODEX_HOOKS="$ROOT/.codex/hooks.json"

command -v jq >/dev/null 2>&1 || { echo "hooks-file-change-guard.test: jq is required" >&2; exit 2; }

TMPROOT=$(mktemp -d)
trap 'rm -rf "$TMPROOT"' EXIT HUP INT TERM
FIXTURE="$TMPROOT/repo"
OTHER="$TMPROOT/other repo"
mkdir -p "$FIXTURE/scripts" "$FIXTURE/docs" "$FIXTURE/nested dir" "$OTHER/scripts" "$OTHER/docs"
cp "$ROOT/scripts/check-file-size.sh" "$FIXTURE/scripts/check-file-size.sh"
cp "$ROOT/scripts/check-file-size.sh" "$OTHER/scripts/check-file-size.sh"
printf '# fixture baseline\n# reason: root-only fixture\nexempt Foo.cs\n' > "$FIXTURE/.filesize-baseline"
printf '<Project><PropertyGroup><KhaozEngineVersion>1.2.3</KhaozEngineVersion></PropertyGroup></Project>\n' > "$FIXTURE/Directory.Build.props"
printf '# Changelog\n\n## 1.2.3\n' > "$FIXTURE/CHANGELOG.md"
printf 'old\n' > "$FIXTURE/docs/Note.md"
printf 'note\n' > "$FIXTURE/notes.md"
printf 'class RootFoo {}\n' > "$FIXTURE/Foo.cs"
awk 'BEGIN { for (i = 1; i <= 800; i++) print "line " i }' > "$FIXTURE/nested dir/Path With Spaces.cs"
awk 'BEGIN { for (i = 1; i <= 800; i++) print "local move " i }' > "$FIXTURE/nested dir/Move Local.cs"
awk 'BEGIN { for (i = 1; i <= 800; i++) { if (i < 800) print "tail " i; else printf "tail %d", i } }' > "$FIXTURE/No Newline.cs"
git -C "$FIXTURE" init -q
git -C "$FIXTURE" config user.email fixture@example.invalid
git -C "$FIXTURE" config user.name Fixture
git -C "$FIXTURE" add .
git -C "$FIXTURE" commit -qm fixture

printf '# fixture baseline\n# reason: external fixture\nexempt External.cs\n' > "$OTHER/.filesize-baseline"
awk 'BEGIN { for (i = 1; i <= 900; i++) print "external " i }' > "$OTHER/External.cs"
printf '<Project><PropertyGroup><KhaozEngineVersion>1.2.3</KhaozEngineVersion></PropertyGroup></Project>\n' > "$OTHER/Directory.Build.props"
printf '# Changelog\n\n## 1.2.3\n' > "$OTHER/CHANGELOG.md"
git -C "$OTHER" init -q
git -C "$OTHER" config user.email fixture@example.invalid
git -C "$OTHER" config user.name Fixture
git -C "$OTHER" add .
git -C "$OTHER" commit -qm fixture

pass=0
fail=0

ok() { pass=$((pass + 1)); }
bad() { echo "FAIL: $1"; fail=$((fail + 1)); }

if [ -f "$CODEX_HOOKS" ]; then ok; else bad "missing $CODEX_HOOKS"; fi
if [ ! -e "$ROOT/.codex/settings.json" ]; then ok; else bad "legacy .codex/settings.json still exists"; fi
if [ -f "$CODEX_HOOKS" ] && cmp -s "$CLAUDE_HOOKS" "$CODEX_HOOKS"; then ok; else bad "Claude and Codex hook configs differ"; fi

hook_count() {
  event=$1
  matcher=$2
  needle=$3
  jq --arg event "$event" --arg matcher "$matcher" --arg needle "$needle" '
    [.hooks[$event][]
      | select(($matcher == "") or ((.matcher // "") == $matcher))
      | .hooks[] | select(.command | contains($needle))] | length' "$CLAUDE_HOOKS"
}

check_registration() {
  label=$1
  event=$2
  matcher=$3
  needle=$4
  if [ "$(hook_count "$event" "$matcher" "$needle")" -eq 1 ]; then ok; else bad "$label registration"; fi
}

check_registration "session local-feed and ledger" SessionStart '' 'local-feed'
check_registration "file pre guard" PreToolUse 'Write|Edit' 'file-change-guard.pl" pre'
check_registration "baseline Bash guard" PreToolUse Bash '.filesize-baseline'
check_registration "tag collision guard" PreToolUse Bash 'tag-collision-guard.sh'
check_registration "pack release guard" PreToolUse Bash 'pack-release-guard.sh'
check_registration "engine version reminder" PostToolUse 'Write|Edit' 'file-change-guard.pl" post'
check_registration "doc version Stop guard" Stop '' 'check-doc-versions.sh'

if grep -q 'check-agent-instructions.sh' "$ROOT/.githooks/pre-commit"; then ok; else bad "pre-commit instruction budget registration"; fi
for test_name in check-agent-instructions hooks-file-change-guard hooks-filesize-guard pack-local-feed tag-collision-guard; do
  if grep -q "scripts/tests/$test_name.test.sh" "$ROOT/.github/workflows/ci.yml"; then ok; else bad "CI $test_name test registration"; fi
done

payload_codex() {
  jq -n --arg command "$1" '{tool_name:"apply_patch",tool_input:{command:$command}}'
}

payload_claude() {
  tool=$1 path=$2 content=${3:-} old=${4:-} new=${5:-} all=${6:-false}
  jq -n --arg tool "$tool" --arg path "$path" --arg content "$content" --arg old "$old" --arg new "$new" --argjson all "$all" \
    '{tool_name:$tool,tool_input:{file_path:$path,content:$content,old_string:$old,new_string:$new,replace_all:$all}}'
}

run_hook() {
  phase=$1 cwd=$2 payload=$3
  (cd "$cwd" && printf '%s' "$payload" | perl "$HELPER" "$phase")
}

decision_of() { printf '%s' "$1" | jq -r '.hookSpecificOutput.permissionDecision // empty' 2>/dev/null || true; }

expect_decision() {
  label=$1 expected=$2 cwd=$3 payload=$4
  out=$(run_hook pre "$cwd" "$payload" 2>&1) || { bad "$label hook execution: $out"; return; }
  got=$(decision_of "$out")
  if [ "$got" = "$expected" ]; then ok; else bad "$label expected '${expected:-quiet}', got '${got:-quiet}': $out"; fi
}

expect_message() {
  label=$1 cwd=$2 payload=$3
  out=$(run_hook post "$cwd" "$payload" 2>&1) || { bad "$label hook execution: $out"; return; }
  got=$(printf '%s' "$out" | jq -r '.systemMessage // empty' 2>/dev/null || true)
  if printf '%s' "$got" | grep -q 'CHANGELOG.md'; then ok; else bad "$label expected CHANGELOG.md message: $out"; fi
}

expect_quiet_post() {
  label=$1 cwd=$2 payload=$3
  out=$(run_hook post "$cwd" "$payload" 2>&1) || { bad "$label hook execution: $out"; return; }
  if [ -z "$out" ]; then ok; else bad "$label expected no message: $out"; fi
}

dash='bad — dash'
expect_decision "Claude blocks a markdown dash" deny "$FIXTURE" "$(payload_claude Write "$FIXTURE/docs/New.md" "$dash")"
expect_decision "Claude blocks a C# comment dash" deny "$FIXTURE" "$(payload_claude Write "$FIXTURE/New.cs" "// $dash")"
expect_decision "Claude allows clean markdown" '' "$FIXTURE" "$(payload_claude Write "$FIXTURE/docs/New.md" clean)"
expect_decision "Claude blocks a retired document" deny "$FIXTURE" "$(payload_claude Edit "$FIXTURE/docs/TODO.md" '' old new)"
expect_decision "Claude baseline edits ask" ask "$FIXTURE" "$(payload_claude Edit "$FIXTURE/.filesize-baseline" '' old new)"

large=$(awk 'BEGIN { for (i = 1; i <= 802; i++) print "line " i }')
expect_decision "Claude checks final source content" deny "$FIXTURE" "$(payload_claude Write "$FIXTURE/Large File.cs" "$large")"
expect_decision "Claude resolves an external repo baseline" '' "$FIXTURE" "$(payload_claude Edit "$OTHER/External.cs" '' 'external 1' 'external 1
extra')"

patch='*** Begin Patch
*** Add File: docs/Codex Note.md
+bad — dash
*** End Patch'
expect_decision "Codex blocks an added markdown dash" deny "$FIXTURE" "$(payload_codex "$patch")"

patch='*** Begin Patch
*** Add File: first.cs
+class First {}
*** Add File: docs/second.md
+bad — dash
*** End Patch'
expect_decision "Codex checks every file in one patch" deny "$FIXTURE" "$(payload_codex "$patch")"

patch='*** Begin Patch
*** Add File: docs/ROADMAP.md
+work
*** End Patch'
expect_decision "Codex blocks a retired document" deny "$FIXTURE" "$(payload_codex "$patch")"

patch='*** Begin Patch
*** Update File: notes.md
*** Move to: docs/TODO.md
@@
 note
*** End Patch'
expect_decision "Codex blocks a move into a retired document" deny "$FIXTURE" "$(payload_codex "$patch")"

patch='*** Begin Patch
*** Delete File: docs/TODO.md
*** End Patch'
expect_decision "Codex allows retired document deletion" '' "$FIXTURE" "$(payload_codex "$patch")"

patch='*** Begin Patch
*** Delete File: .filesize-baseline
*** End Patch'
expect_decision "Codex blocks baseline deletion" deny "$FIXTURE" "$(payload_codex "$patch")"

patch='*** Begin Patch
*** Update File: Path With Spaces.cs
@@
 line 1
+extra
 line 2
*** End Patch'
expect_decision "Codex checks a nested path with spaces" deny "$FIXTURE/nested dir" "$(payload_codex "$patch")"

patch=$(awk 'BEGIN { print "*** Begin Patch"; print "*** Add File: Foo.cs"; for (i = 1; i <= 801; i++) print "+nested " i; print "*** End Patch" }')
expect_decision "Codex keeps nested add paths cwd relative" deny "$FIXTURE/nested dir" "$(payload_codex "$patch")"

patch='*** Begin Patch
*** Update File: Move Local.cs
*** Move to: Foo.cs
@@
 local move 1
+extra
 local move 2
*** End Patch'
expect_decision "Codex keeps nested move paths cwd relative" deny "$FIXTURE/nested dir" "$(payload_codex "$patch")"

patch='*** Begin Patch
*** Update File: No Newline.cs
@@
 tail 1
+extra
 tail 2
*** End Patch'
expect_decision "Codex counts native newline normalization" deny "$FIXTURE" "$(payload_codex "$patch")"

printf '<Project><PropertyGroup><KhaozEngineVersion>1.2.4</KhaozEngineVersion></PropertyGroup></Project>\n' > "$FIXTURE/Directory.Build.props"
patch='*** Begin Patch
*** Update File: ../Directory.Build.props
@@
-<KhaozEngineVersion>1.2.3</KhaozEngineVersion>
+<KhaozEngineVersion>1.2.4</KhaozEngineVersion>
*** End Patch'
expect_message "Codex engine version reminder" "$FIXTURE/nested dir" "$(payload_codex "$patch")"
expect_message "Claude engine version reminder" "$FIXTURE" "$(payload_claude Edit "$FIXTURE/Directory.Build.props" '' 1.2.3 1.2.4)"

printf '# Changelog\n\n## 1.2.4\n' > "$FIXTURE/CHANGELOG.md"
expect_quiet_post "Codex stays quiet with a changelog edit" "$FIXTURE/nested dir" "$(payload_codex "$patch")"

echo "hooks-file-change-guard.test: $pass passed, $fail failed"
[ "$fail" -eq 0 ]
