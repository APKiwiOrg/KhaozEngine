#!/usr/bin/env bash
# Publish a KhaozEngine delta update. COPY this into your game repo and fill in the CONFIG block.
# Flow: build -> generate manifest (ke-updater) -> sign (ke-updater) -> upload -> update latest-{platform}.json
#
# Prereqs: the `ke-updater` dotnet tool (dotnet tool install --global KhaozEngine.Updates.Tool) and,
# for the default Azure Blob backend, the `az` CLI authenticated to the target storage account.
set -euo pipefail

# ---- CONFIG: edit these for your game --------------------------------------
STORAGE_ACCOUNT="yourgameupdates"                          # Azure Blob storage account
CONTAINER="releases"                                       # blob container
PRIVATE_KEY="${UPDATE_PRIVATE_KEY:-secrets/private.pem}"   # RSA private key (keep secret; supply via CI secret)

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

runtime_id="${1:?usage: publish-update.sh <runtime-id> <version>}"
version="${2:?usage: publish-update.sh <runtime-id> <version>}"

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

# Point latest-{platform}.json at this version.
tmp="$(mktemp)"; printf '{"version":"%s"}' "$version" > "$tmp"
az storage blob upload \
  --account-name "$STORAGE_ACCOUNT" --container-name "$CONTAINER" \
  --name "latest-$runtime_id.json" --file "$tmp" --content-type application/json --overwrite
rm -f "$tmp"

echo "Published $version for $runtime_id." >&2
