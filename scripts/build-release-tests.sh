#!/bin/sh
# Release test setup shared by local development, ordinary CI and native GPU CI.
# Only the outside-solution renderer tooling suite owns verifier process tests.
# Build its executable prerequisite after that suite, without a reverse project reference.
set -eu
cd "$(dirname "$0")/.."
target=${1:-KhaozEngine.slnx}
if [ "$#" -gt 0 ]; then shift; fi
dotnet build "$target" -c Release "$@"
normalized_target=$target
case "${OS:-}" in
  Windows_NT) normalized_target=$(printf '%s\n' "$target" | tr '\\' '/') ;;
esac
needs_verifier=false
# Only this checkout's actual canonical files can bypass solution-root inspection.
if [ "$normalized_target" -ef "$PWD/KhaozEngine.slnx" ]; then
  # The architecture guard keeps tooling outside this solution. Retain its shell-only setup path.
  needs_verifier=false
elif [ "$normalized_target" -ef "$PWD/tools/Renderer.Perf.Tooling.Tests/Renderer.Perf.Tooling.Tests.csproj" ]; then
  needs_verifier=true
else
  case "$normalized_target" in
    *.slnx)
      # GPU shards use reduced solutions. Inspect their actual roots rather than widening every shard.
      needs_verifier=$(python3 - "$normalized_target" "$PWD" <<'PY'
import pathlib, sys, xml.etree.ElementTree as ET
solution = pathlib.Path(sys.argv[1]).resolve()
tooling_tests = (pathlib.Path(sys.argv[2]) / 'tools/Renderer.Perf.Tooling.Tests/Renderer.Perf.Tooling.Tests.csproj').resolve()
projects = ET.parse(solution).iter('Project')
print(str(any((solution.parent / project.attrib['Path'].replace('\\', '/')).resolve() == tooling_tests
              for project in projects)).lower())
PY
      )
      ;;
  esac
fi
if [ "$needs_verifier" = true ]; then
  dotnet build tools/Renderer.Perf.Verify/Renderer.Perf.Verify.csproj -c Release
fi
