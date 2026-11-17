#!/bin/sh
# Exercise every rule of check-fp-control-tests.sh against throwaway repos that mirror the real allow-list paths.
# Each red case breaks one rule beside a clean control. Fixture setter names are assembled at runtime so this file
# does not itself declare one.
set -eu

here=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
SCRIPT=${1:-$here/../check-fp-control-tests.sh}
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
put() { mkdir -p "$(dirname -- "$1")"; printf '%s\n' "$2" > "$1"; }

q='"'
set_env=fe$(printf setenv)
set_round=fe$(printf setround)
control=_control$(printf fp_s)
import() { printf '[DllImport("libm.so.6", EntryPoint = %s%s%s)] static extern int Set(int value);' "$q" "$1" "$q"; }
CORE=KhaozEngine.TileWorld.Physics.Tests/Fixtures/WaterFloatingPointEnvironment.cs
MODES=KhaozEngine.TileWorld.Physics.FloatingPoint.Tests/Fixtures/WaterFloatingPointEnvironment.Modes.cs
POKE=KhaozEngine.FloatingPoint.Tests/Fixtures/FpPoke.cs
project() { put "$1" "<Project Sdk=\"Microsoft.NET.Sdk\"><ItemGroup>$2</ItemGroup></Project>"; }
link() { printf '<Compile Include="%s" Link="L/%s" />' "$1" "$(basename -- "$1")"; }

# The real layout in miniature: both production setters, the core half, the mode half in the water floating point
# assembly, the rounding poke in the other one, and default projects that link the core half.
fresh() {
  case_no=$((case_no+1))
  mkdir "$TMPROOT/case$case_no"
  cd "$TMPROOT/case$case_no"
  git init -q .
  put KhaozEngine.Determinism/FpNative.cs "$(import "$set_env")"
  put KhaozEngine.Physics/PhysicsFloatingPointControls.cs "$(import "$control")"
  put "$CORE" "internal sealed partial class WaterFloatingPointEnvironment { $(import "$set_env") }"
  put "$MODES" 'internal sealed partial class WaterFloatingPointEnvironment { }'
  put "$POKE" "$(import "$set_round")"
  project KhaozEngine.TileWorld.Physics.Tests/KhaozEngine.TileWorld.Physics.Tests.csproj ''
  project KhaozEngine.TileWorld.Physics.FloatingPoint.Tests/KhaozEngine.TileWorld.Physics.FloatingPoint.Tests.csproj \
    "$(link "../$CORE")"
  project KhaozEngine.FloatingPoint.Tests/KhaozEngine.FloatingPoint.Tests.csproj "$(link "../$CORE")$(link "../$MODES")"
  project KhaozEngine.Game.Tests/KhaozEngine.Game.Tests.csproj "$(link "../$CORE")"
  put KhaozEngine.Game.Tests/Physics/Reader.cs '[DllImport("libm.so.6", EntryPoint = "fegetenv")] static extern int Get(ref long env);'
  git add -A
  commit
}
stage() { git add -A; }

fresh; run --tree; check tree_clean 0
run; check staged_clean 0

# Rule 1: a setter outside the allow-list, by entry point, by method name, by LibraryImport and by export lookup.
fresh; put KhaozEngine.Game.Tests/Poke.cs "$(import "$set_round")"; stage
run --tree; said 'KhaozEngine.Game.Tests/Poke.cs: declares a native floating point control setter'; check entry_point 1
run; said 'KhaozEngine.Game.Tests/Poke.cs'; check entry_point_staged 1
fresh; put KhaozEngine.Server.Tests/Poke.cs "[DllImport(\"libm.so.6\")] static extern int $set_round(int mode);"; stage
run --tree; said 'KhaozEngine.Server.Tests/Poke.cs'; check method_name 1
fresh; put KhaozEngine.Foundation.Tests/Poke.cs "[LibraryImport(\"libm.so.6\")] private static partial int $set_env(ref long env);"; stage
run --tree; said 'KhaozEngine.Foundation.Tests/Poke.cs'; check library_import 1
fresh; put KhaozEngine.Game.Tests/Poke.cs "NativeLibrary.GetExport(handle, $q$control$q);"; stage
run --tree; said 'KhaozEngine.Game.Tests/Poke.cs'; check export_lookup 1
# The water default assembly may carry no import beside its core half.
fresh; put KhaozEngine.TileWorld.Physics.Tests/Fixtures/OtherEnvironment.cs "$(import "$set_env")"; stage
run --tree; said 'KhaozEngine.TileWorld.Physics.Tests/Fixtures/OtherEnvironment.cs'; check water_default_import 1
# An entry that no longer declares a setter, or a poke placed outside the floating point projects.
fresh; put KhaozEngine.Physics/PhysicsFloatingPointControls.cs 'static class Gone { }'; stage
run --tree; said 'allow-list entry declares no control setter'; check stale_entry 1
fresh; git rm -q "$POKE"; put KhaozEngine.FloatingPoint.Tests/Fixtures/Other.cs 'class Other { }'; stage
run --tree; said "$POKE: allow-list entry declares no control setter"; check missing_poke 1

# Rule 2: another half of the fixture outside the floating point projects, beside a half inside one.
fresh; put KhaozEngine.TileWorld.Physics.Tests/Fixtures/WaterFloatingPointEnvironment.Sneak.cs \
  'internal sealed partial class WaterFloatingPointEnvironment { bool Up() => Apply(0, 0x400000, 0, 0); }'; stage
run --tree; said 'WaterFloatingPointEnvironment.Sneak.cs: a WaterFloatingPointEnvironment half outside'; check sneak_half 1
fresh; put KhaozEngine.FloatingPoint.Tests/Fixtures/WaterFloatingPointEnvironment.More.cs \
  'internal sealed partial class WaterFloatingPointEnvironment { }'; stage
run --tree; check half_inside_floating_point_project 0

# Rule 3: a default project or an imported props compiles a control-setting source.
fresh; project KhaozEngine.Game.Tests/KhaozEngine.Game.Tests.csproj "$(link "../$CORE")$(link "../$MODES")"; stage
run --tree; said "KhaozEngine.Game.Tests/KhaozEngine.Game.Tests.csproj: compiles $MODES"; check links_mode_half 1
fresh; project KhaozEngine.Foundation.Tests/KhaozEngine.Foundation.Tests.csproj "$(link "..\\$POKE" | tr '/' '\\')"; stage
run --tree; said "compiles $POKE"; check links_poke_windows_separators 1
fresh; project KhaozEngine.Server.Tests/KhaozEngine.Server.Tests.csproj \
  '<Compile Include="../KhaozEngine.TileWorld.Physics.FloatingPoint.Tests/**/*.cs" />'; stage
run --tree; said "compiles $MODES"; check glob_link 1
fresh; project KhaozEngine.TileWorld.Physics.Tests/KhaozEngine.TileWorld.Physics.Tests.csproj \
  '<Compile Include="$(MSBuildThisFileDirectory)../KhaozEngine.FloatingPoint.Tests/Fixtures/FpPoke.cs" />'; stage
run --tree; said "compiles $POKE"; check property_rooted_link 1
fresh; put test-shared/Modes.props "<Project><ItemGroup>$(link "../$MODES")</ItemGroup></Project>"; stage
run --tree; said "test-shared/Modes.props: compiles $MODES"; check props_link 1
# The name that counts is the assembly name, not the folder.
fresh; put KhaozEngine.FloatingPoint.Tests/KhaozEngine.FloatingPoint.Tests.csproj \
  "<Project><PropertyGroup><AssemblyName>KhaozEngine.Other.Tests</AssemblyName></PropertyGroup><ItemGroup>$(link "../$MODES")</ItemGroup></Project>"
stage; run --tree; said "compiles $MODES"; check renamed_assembly 1

echo "check-fp-control-tests.test: $pass passed, $fail failed"
[ "$fail" -eq 0 ]
