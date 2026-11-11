#!/bin/sh
# Standard package inventory. The consumer verifier stays outside normal solution builds and test discovery.
set -eu
cd "$(dirname "$0")/.."
dotnet pack KhaozEngine.slnx "$@"
dotnet pack tools/Coverage.Verify/Coverage.Verify.csproj "$@"
