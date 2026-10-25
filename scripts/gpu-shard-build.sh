#!/bin/sh
# Build a reduced GPU shard solution on a fresh CI checkout, keeping every reference in Release.
set -eu
: "${1:?usage: gpu-shard-build.sh <shard.slnx>}"

# MSBuild otherwise unsets Configuration on references absent from the solution, choosing Debug.
# https://learn.microsoft.com/dotnet/api/microsoft.build.tasks.assignprojectconfiguration.shouldunsetparentconfigurationandplatform
# The shared setup runs from the repository root, so retain a caller-relative solution's location.
case "$1" in
  /*|[A-Za-z]:*) target=$1 ;;
  *) target="$PWD/$1" ;;
esac
script_dir=$(CDPATH='' cd -- "$(dirname -- "$0")" && pwd)
sh "$script_dir/build-release-tests.sh" "$target" --no-restore -p:ShouldUnsetParentConfigurationAndPlatform=false

debug_outputs=$(find . -type d -path '*/bin/Debug' -prune -print)
if [ -n "$debug_outputs" ]; then
  echo '::error::A Release shard produced Debug output' >&2
  printf '%s\n' "$debug_outputs" >&2
  exit 1
fi
