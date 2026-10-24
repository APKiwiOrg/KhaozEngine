#!/bin/sh
# Release test setup shared by local development, ordinary CI and native GPU CI.
# The verifier remains outside the solution and depends on Render.Tests, so build it only after
# its owning test assembly. No reverse project reference or test-time build is needed.
set -eu
cd "$(dirname "$0")/.."
target=${1:-KhaozEngine.slnx}
if [ "$#" -gt 0 ]; then shift; fi
dotnet build "$target" -c Release "$@"
normalized_target=$(printf '%s\n' "$target" | tr '\\' '/')
needs_verifier=false
case "$normalized_target" in
  KhaozEngine.slnx|*/KhaozEngine.slnx|KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj|*/KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj)
    needs_verifier=true
    ;;
  *.slnx)
    # GPU shards use reduced solutions. Inspect their actual roots rather than widening every shard.
    needs_verifier=$(python3 - "$target" "$PWD" <<'PY'
import pathlib, sys, xml.etree.ElementTree as ET
solution = pathlib.Path(sys.argv[1]).resolve()
render_tests = (pathlib.Path(sys.argv[2]) / 'KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj').resolve()
projects = ET.parse(solution).iter('Project')
print(str(any((solution.parent / project.attrib['Path'].replace('\\', '/')).resolve() == render_tests
              for project in projects)).lower())
PY
)
    ;;
esac
if [ "$needs_verifier" = true ]; then
  dotnet build tools/Renderer.Perf.Verify/Renderer.Perf.Verify.csproj -c Release
fi
