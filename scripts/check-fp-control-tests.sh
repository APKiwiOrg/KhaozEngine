#!/bin/sh
# Keep every source that can change a floating point rounding or flush control in a floating point test assembly,
# one whose assembly name ends in .FloatingPoint.Tests. Machine code the JIT optimizes while a control is changed,
# and threads created then, outlive the test that changed it, so a test that changes a control runs in its own test
# process (docs/CONTRIBUTOR-RULES.md). Three rules:
#   1. Only a file on the allow-list below declares a native control setter: a DllImport or LibraryImport of one of
#      SETTERS, by entry point or method name, or the name passed to a native export lookup.
#   2. A partial half of WaterFloatingPointEnvironment other than its core half lies inside a floating point test
#      project. Such a half can pass nonzero bits to the core half's writer.
#   3. No project or .props/.targets file other than a floating point test project compiles a control-setting
#      source, by its own directory or by a Compile item: a mode half from rule 2, or an allow-list entry marked fp.
# Default: read the index. --tree: read tracked files in the work tree, for CI.
# Limits: Compile items whose path needs an MSBuild property other than the project directory are not resolved,
# and machine code a test writes and runs itself is not recognised.
# Exit 0 clean, 1 on a violation, 2 on bad usage or a git error.
set -eu

mode=${1:-staged}
case "$mode" in
  staged|--tree) ;;
  *) echo "check-fp-control-tests: unknown mode '$mode' (expected: staged, --tree)" >&2; exit 2 ;;
esac
top=$(git rev-parse --show-toplevel 2>/dev/null) || {
  echo 'check-fp-control-tests: not inside a git work tree' >&2
  exit 2
}
cd "$top"
g() { git -c core.quotePath=false "$@"; }

# The native calls that write a rounding, flush or trap control.
SETTERS='fesetenv|fesetround|feholdexcept|feupdateenv|fesetmode|feenableexcept|fedisableexcept|_controlfp|_controlfp_s|_control87|__control87_2|_set_controlfp'

# path|scope|reason. Scope any: may be compiled anywhere, because it only returns a thread to the default or to the
# controls it found. Scope fp: sets a non-default control, so only a floating point test project compiles it.
ALLOW='KhaozEngine.Determinism/FpNative.cs|any|DeterministicFpScope installs the canonical environment and restores the one it found.
KhaozEngine.Physics/PhysicsFloatingPointControls.cs|any|It reads the controls. Its _controlfp_s call passes a zero mask, which writes nothing.
KhaozEngine.TileWorld.Physics.Tests/Fixtures/WaterFloatingPointEnvironment.cs|any|The fixture core half restores the captured controls and returns to the default. Other bits come only from a mode half.
KhaozEngine.FloatingPoint.Tests/Fixtures/FpPoke.cs|fp|Sets round toward zero for the determinism scope tests.'
CORE=KhaozEngine.TileWorld.Physics.Tests/Fixtures/WaterFloatingPointEnvironment.cs

TMP=$(mktemp -d)
trap 'rm -rf "$TMP"' EXIT
fail=0
violation() { echo "check-fp-control-tests: $1" >&2; fail=1; }
if [ "$mode" = --tree ]; then
  grep_() { g grep "$@"; }
  read_() { if [ -f "$1" ]; then cat "$1"; fi; }
else
  grep_() { g grep --cached "$@"; }
  read_() { g show ":$1" 2>/dev/null || true; }
fi

# Lines that declare a setter: a quoted entry or export name, or an extern or partial method named for it.
grep_ -lE "\"($SETTERS)\"|(extern|partial)[^;=]*[^A-Za-z0-9_]($SETTERS)[[:space:]]*\\(" -- '*.cs' > "$TMP/setters" || true
grep_ -lE 'partial[[:space:]]+class[[:space:]]+WaterFloatingPointEnvironment([^A-Za-z0-9_]|$)' -- '*.cs' > "$TMP/halves" || true
g ls-files -- '*.csproj' > "$TMP/csprojs"
grep_ -l '<AssemblyName>' -- '*.csproj' > "$TMP/renamed" || true
grep_ -l '<Compile' -- '*.csproj' '*.props' '*.targets' > "$TMP/compiling" || true
dir_of() { case "$1" in */*) printf '%s\n' "${1%/*}" ;; *) printf '.\n' ;; esac; }

# Floating point test project directories, named by assembly name: the AssemblyName property, else the file name.
renamed=$(cat "$TMP/renamed")
: > "$TMP/fpdirs"
while IFS= read -r project; do
  case "
$renamed
" in *"
$project
"*) continue ;; esac
  case "$project" in */*.FloatingPoint.Tests.csproj) dir_of "$project" >> "$TMP/fpdirs" ;; esac
done < "$TMP/csprojs"
while IFS= read -r project; do
  name=$(read_ "$project" | tr '\n' ' ' | sed -n 's/.*<AssemblyName>[[:space:]]*\([^<[:space:]]*\)[[:space:]]*<\/AssemblyName>.*/\1/p')
  case "$name" in *.FloatingPoint.Tests) dir_of "$project" >> "$TMP/fpdirs" ;; esac
done < "$TMP/renamed"
in_fp_project() {
  while IFS= read -r dir; do
    case "$1" in "$dir"/*) return 0 ;; esac
  done < "$TMP/fpdirs"
  return 1
}

# Rule 1, and the stale-entry check that keeps the allow-list short.
: > "$TMP/fponly"
while IFS='|' read -r path scope reason; do
  if ! grep -qxF "$path" "$TMP/setters"; then violation "$path: allow-list entry declares no control setter, remove it"; fi
  if [ "$scope" = fp ]; then printf '%s\n' "$path" >> "$TMP/fponly"; fi
done <<EOF
$ALLOW
EOF
while IFS= read -r file; do
  if ! printf '%s\n' "$ALLOW" | cut -d'|' -f1 | grep -qxF "$file"; then
    violation "$file: declares a native floating point control setter outside the allow-list"
  fi
done < "$TMP/setters"
while IFS='|' read -r path scope reason; do
  if [ "$scope" = fp ] && ! in_fp_project "$path"; then
    violation "$path: sets a non-default control, so it belongs in a floating point test project"
  fi
done <<EOF
$ALLOW
EOF

# Rule 2.
while IFS= read -r file; do
  if [ "$file" = "$CORE" ]; then continue; fi
  if in_fp_project "$file"; then printf '%s\n' "$file" >> "$TMP/fponly"
  else violation "$file: a WaterFloatingPointEnvironment half outside the floating point test projects"
  fi
done < "$TMP/halves"

# Rule 3. A project compiles every source under its own directory, and each Compile item, resolved against its
# file's directory with .. and . folded and matched as a shell pattern, so * and ** both match across directories.
# That can only widen a match.
compiles() { violation "$1: compiles $2, which can set a non-default floating point control"; }
while IFS= read -r project; do
  if in_fp_project "$project"; then continue; fi
  dir=$(dir_of "$project")
  while IFS= read -r source; do
    case "$source" in "$dir"/*) compiles "$project" "$source" ;; esac
  done < "$TMP/fponly"
done < "$TMP/csprojs"
while IFS= read -r project; do
  if in_fp_project "$project"; then case "$project" in *.csproj) continue ;; esac; fi
  read_ "$project" | tr '\n' ' ' | grep -oE '<Compile[^>]*>' | awk -v dir="$(dir_of "$project")" '
    match($0, /Include="[^"]*"/) {
      count = split(substr($0, RSTART + 9, RLENGTH - 10), items, ";")
      for (k = 1; k <= count; k++) {
        item = items[k]
        gsub(/\\/, "/", item); sub(/^[ \t]+/, "", item); sub(/[ \t]+$/, "", item)
        sub(/^\$\(MSBuildThisFileDirectory\)/, "", item); sub(/^\$\(MSBuildProjectDirectory\)\//, "", item)
        if (item == "") continue
        n = split(dir "/" item, part, "/"); m = 0
        for (i = 1; i <= n; i++) {
          if (part[i] == "" || part[i] == ".") continue
          if (part[i] == "..") { if (m > 0) m--; continue }
          kept[++m] = part[i]
        }
        out = ""
        for (i = 1; i <= m; i++) out = out (i > 1 ? "/" : "") kept[i]
        print out
      }
    }' > "$TMP/items" || true
  while IFS= read -r source; do
    while IFS= read -r pattern; do
      # shellcheck disable=SC2254 # the item is a pattern on purpose
      case "$source" in $pattern) compiles "$project" "$source" ;; esac
    done < "$TMP/items"
  done < "$TMP/fponly"
done < "$TMP/compiling"

if [ "$fail" -ne 0 ]; then
  echo 'check-fp-control-tests: a test that changes a floating point control runs in a floating point test assembly. See docs/CONTRIBUTOR-RULES.md.' >&2
fi
exit "$fail"
