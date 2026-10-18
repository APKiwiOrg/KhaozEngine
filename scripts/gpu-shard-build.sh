#!/bin/sh
# Build a reduced GPU shard solution on a fresh CI checkout, keeping every reference in Release.
set -eu
: "${1:?usage: gpu-shard-build.sh <shard.slnx>}"

# MSBuild otherwise unsets Configuration on references absent from the solution, choosing Debug.
# https://learn.microsoft.com/dotnet/api/microsoft.build.tasks.assignprojectconfiguration.shouldunsetparentconfigurationandplatform
dotnet build "$1" -c Release --no-restore -p:ShouldUnsetParentConfigurationAndPlatform=false

debug_outputs=$(find . -type d -path '*/bin/Debug' -prune -print)
if [ -n "$debug_outputs" ]; then
  echo '::error::A Release shard produced Debug output' >&2
  printf '%s\n' "$debug_outputs" >&2
  exit 1
fi
