#!/bin/sh
# Release test setup shared by local development and both ordinary CI routes.
# The verifier remains outside the solution and depends on Render.Tests, so build it only after
# its owning test assembly. No reverse project reference or test-time build is needed.
set -eu
cd "$(dirname "$0")/.."
target=${1:-KhaozEngine.slnx}
if [ "$#" -gt 0 ]; then shift; fi
dotnet build "$target" -c Release "$@"
case "$target" in
  KhaozEngine.slnx|./KhaozEngine.slnx|"$PWD/KhaozEngine.slnx"|KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj|./KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj|"$PWD/KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj")
    dotnet build tools/Renderer.Perf.Verify/Renderer.Perf.Verify.csproj -c Release
    ;;
esac
