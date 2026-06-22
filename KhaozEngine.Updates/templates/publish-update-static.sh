#!/usr/bin/env bash
# Publish a KhaozEngine delta update to a SERVER-LESS static-blob feed. COPY this into your game repo
# and fill in the CONFIG block. Use this when no dynamic API serves the "latest version" response: the
# client reads the full LatestVersionInfo straight out of a static blob (HttpUpdateSource +
# LatestVersionPath = "<container>/latest-{platform}.json"). For a feed fronted by a dynamic API that
# synthesizes that response, use publish-update.sh instead.
#
# Flow: build -> generate manifest (ke-updater) -> sign (ke-updater) -> upload ->
#       write the FULL latest-{platform}.json (version + buildVersion + manifestUrl + required).
#
# Prereqs: the `ke-updater` dotnet tool (dotnet tool install --global KhaozEngine.Updates.Tool) and,
# for the default Azure Blob backend, the `az` CLI authenticated to the target storage account.
set -euo pipefail

# ---- CONFIG: edit these for your game --------------------------------------
STORAGE_ACCOUNT="yourgameupdates"                          # Azure Blob storage account
CONTAINER="releases"                                       # blob container (must allow public/anon blob read)
PRIVATE_KEY="${UPDATE_PRIVATE_KEY:-secrets/private.pem}"   # RSA private key (keep secret; supply via CI secret)

# Public base URL the client reads from. Must be the SAME host the client's ServerBaseUrl points at
# (HttpUpdateSource enforces https + same-host). Default is the standard Azure Blob endpoint; override
# for a custom domain or CDN in front of the container.
PUBLIC_BASE_URL="${PUBLIC_BASE_URL:-https://${STORAGE_ACCOUNT}.blob.core.windows.net}"

# Whether this update is mandatory (the client cannot skip it). Override per-publish via UPDATE_REQUIRED=true.
REQUIRED="${UPDATE_REQUIRED:-false}"

# Map a runtime id to the directory holding the built game files to hash.
# Replace with your build output; handle the macOS .app bundle here if needed, e.g.:
#   osx-*) echo "package/$1/MyGame.app/Contents/MacOS" ;;
resolve_build_dir() {  # $1 = runtime id
  echo "artifacts/$1"
}

# Build the game for a runtime id (replace with your build command).
build_game() {         # $1 = runtime id
  echo "TODO: build $1" >&2
}
# ---------------------------------------------------------------------------

runtime_id="${1:?usage: publish-update-static.sh <runtime-id> <version> [build-version]}"
version="${2:?usage: publish-update-static.sh <runtime-id> <version> [build-version]}"
# buildVersion is an opaque display label the engine does NOT compare against (only `version` drives the
# IsNewer check). Defaults to `version`; pass a 3rd arg for a separate informational/display string.
build_version="${3:-$version}"

build_game "$runtime_id"
build_dir="$(resolve_build_dir "$runtime_id")"
[ -d "$build_dir" ] || { echo "build dir not found: $build_dir" >&2; exit 1; }

manifest="$build_dir/manifest.json"
ke-updater manifest --dir "$build_dir" --platform "$runtime_id" --version "$version" --output "$manifest"
ke-updater sign --manifest "$manifest" --key "$PRIVATE_KEY"

# Upload the whole build dir (game files + manifest.json + manifest.json.sig).
az storage blob upload-batch \
  --account-name "$STORAGE_ACCOUNT" \
  --destination "$CONTAINER/$version/$runtime_id" \
  --source "$build_dir" --overwrite

# Write the FULL LatestVersionInfo the client deserializes directly. manifestUrl is the absolute blob
# URL of THIS build's manifest.json; the client resolves every other file as its sibling.
manifest_url="$PUBLIC_BASE_URL/$CONTAINER/$version/$runtime_id/manifest.json"
tmp="$(mktemp)"
printf '{"version":"%s","buildVersion":"%s","manifestUrl":"%s","required":%s}' \
  "$version" "$build_version" "$manifest_url" "$REQUIRED" > "$tmp"
az storage blob upload \
  --account-name "$STORAGE_ACCOUNT" --container-name "$CONTAINER" \
  --name "latest-$runtime_id.json" --file "$tmp" --content-type application/json --overwrite
rm -f "$tmp"

echo "Published $version for $runtime_id (static feed, manifest at $manifest_url)." >&2
