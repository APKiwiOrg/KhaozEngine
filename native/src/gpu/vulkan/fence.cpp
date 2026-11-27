#include "fence.hpp"

#include <memory>

using namespace khaoz::vulkan;

khaoz_gpu_fence::~khaoz_gpu_fence() {
    if (handle != VK_NULL_HANDLE) {
        device.api.vkDestroyFence(device.handle, handle, nullptr);
    }
}

khaoz_result khaoz_gpu_fence_create(khaoz_gpu_device* device, khaoz_gpu_fence** out_fence) {
    return khaoz::abi::guard([&] {
        if (out_fence == nullptr) {
            return error(KHAOZ_ERROR_INVALID_ARGUMENT, "A fence output is required");
        }
        *out_fence = nullptr;
        if (device == nullptr) {
            return error(KHAOZ_ERROR_INVALID_ARGUMENT, "A device is required");
        }
        auto fence = std::make_unique<khaoz_gpu_fence>(*device);
        const auto desc = info<VkFenceCreateInfo>(VK_STRUCTURE_TYPE_FENCE_CREATE_INFO);
        const auto created = check(device->api.vkCreateFence(device->handle, &desc, nullptr, &fence->handle),
                                   "Could not create a Vulkan fence");
        if (created == KHAOZ_OK) {
            *out_fence = fence.release();
        }
        return created;
    });
}

khaoz_result khaoz_gpu_fence_destroy(khaoz_gpu_fence* fence) {
    return khaoz::abi::guard([&] { return destroy(fence); });
}

khaoz_result khaoz_gpu_fence_signaled(khaoz_gpu_fence* fence, uint32_t* out_signaled) {
    return khaoz::abi::guard([&] {
        if (fence == nullptr || out_signaled == nullptr) {
            return error(KHAOZ_ERROR_INVALID_ARGUMENT, "A fence and status output are required");
        }
        const auto status = fence->device.api.vkGetFenceStatus(fence->device.handle, fence->handle);
        *out_signaled = status == VK_SUCCESS ? 1U : 0U;
        return status == VK_NOT_READY ? KHAOZ_OK : check(status, "Could not read Vulkan fence status");
    });
}

khaoz_result khaoz_gpu_fence_reset(khaoz_gpu_fence* fence) {
    return khaoz::abi::guard([&] {
        if (fence == nullptr) {
            return error(KHAOZ_ERROR_INVALID_ARGUMENT, "A fence is required");
        }
        auto& device = fence->device;
        if (fence->submitted) {
            const auto status = device.api.vkGetFenceStatus(device.handle, fence->handle);
            if (status == VK_NOT_READY) {
                return error(KHAOZ_ERROR_INVALID_ARGUMENT, "A pending Vulkan fence cannot be reset");
            }
            if (status != VK_SUCCESS) {
                return check(status, "Could not read Vulkan fence status");
            }
        }
        const auto result =
            check(device.api.vkResetFences(device.handle, 1, &fence->handle), "Could not reset the Vulkan fence");
        if (result == KHAOZ_OK) {
            fence->submitted = false;
        }
        return result;
    });
}
