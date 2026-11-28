#!/bin/sh
# CI only: installs Mesa lavapipe, the Vulkan loader and the Khronos validation layer on an Ubuntu runner,
# then points the loader at lavapipe through GITHUB_ENV. The ICD manifest is found in the package's own
# file list, as cross-platform-gpu.yml does, because its name drifts across runner images.
set -eux

sudo apt-get update
sudo apt-get install -y mesa-vulkan-drivers vulkan-tools libvulkan1 vulkan-validationlayers ninja-build

# Validation is the point of these runs, so a missing layer fails here instead of running unvalidated.
layer=$(grep -rl VK_LAYER_KHRONOS_validation /usr/share/vulkan /etc/vulkan 2>/dev/null | head -1)
if [ -z "$layer" ]; then
    echo "::error::VK_LAYER_KHRONOS_validation is missing after installing vulkan-validationlayers"
    exit 1
fi
echo "Khronos validation layer manifest: $layer"

icd=$(dpkg -L mesa-vulkan-drivers | grep -E '/lvp_icd[^/]*\.json$' | head -1)
if [ -z "$icd" ] || [ ! -f "$icd" ]; then
    echo "::error::lavapipe ICD manifest not found in the mesa-vulkan-drivers file list"
    dpkg -L mesa-vulkan-drivers | grep -E '_icd[^/]*\.json$' || true
    exit 1
fi
echo "lavapipe ICD: $icd"
{ echo "VK_ICD_FILENAMES=$icd"; echo "VK_DRIVER_FILES=$icd"; } >> "$GITHUB_ENV"
