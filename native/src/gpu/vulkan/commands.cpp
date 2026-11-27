#include "commands.hpp"
#include "fence.hpp"

#include <memory>

using namespace khaoz::vulkan;

namespace khaoz::vulkan {

khaoz_result require_recording(const khaoz_gpu_command_list* commands) {
    if (commands == nullptr || commands->state != khaoz_gpu_command_list::phase::recording)
        return error(KHAOZ_ERROR_INVALID_ARGUMENT, "The Vulkan command list is not recording");
    return commands->device.require_usable();
}

khaoz_result allocate_commands(khaoz_gpu_device& device, VkCommandBuffer& out) {
    const auto usable = device.require_usable();
    if (usable != KHAOZ_OK) {
        return usable;
    }
    auto desc = info<VkCommandBufferAllocateInfo>(VK_STRUCTURE_TYPE_COMMAND_BUFFER_ALLOCATE_INFO);
    desc.commandPool = device.command_pool;
    desc.level = VK_COMMAND_BUFFER_LEVEL_PRIMARY;
    desc.commandBufferCount = 1;
    return check(
        create_handle(
            out, [&](auto* pending) { return device.api.vkAllocateCommandBuffers(device.handle, &desc, pending); }),
        "Could not allocate a Vulkan command buffer");
}

khaoz_result begin_commands(khaoz_gpu_device& device, VkCommandBuffer commands) {
    const auto usable = device.require_usable();
    if (usable != KHAOZ_OK) {
        return usable;
    }
    auto desc = info<VkCommandBufferBeginInfo>(VK_STRUCTURE_TYPE_COMMAND_BUFFER_BEGIN_INFO);
    desc.flags = VK_COMMAND_BUFFER_USAGE_ONE_TIME_SUBMIT_BIT;
    return check(device.api.vkBeginCommandBuffer(commands, &desc), "Could not begin Vulkan recording");
}

khaoz_result end_commands(khaoz_gpu_device& device, VkCommandBuffer commands) {
    return check(device.api.vkEndCommandBuffer(commands), "Could not end Vulkan recording");
}

khaoz_result submit(khaoz_gpu_device& device, VkCommandBuffer commands, VkFence fence) {
    const auto usable = device.require_usable();
    if (usable != KHAOZ_OK) {
        return usable;
    }
    auto desc = info<VkSubmitInfo>(VK_STRUCTURE_TYPE_SUBMIT_INFO);
    desc.commandBufferCount = 1;
    desc.pCommandBuffers = &commands;
    const auto result =
        check(device.api.vkQueueSubmit(device.queue, 1, &desc, fence), "Could not submit Vulkan commands");
    if (result == KHAOZ_OK) {
        device.work_pending = true;
    } else if (result == KHAOZ_ERROR_DEVICE_LOST) {
        device.queue_failure = result;
        device.work_pending = false;
    }
    return result;
}

} // namespace khaoz::vulkan

khaoz_gpu_command_list::~khaoz_gpu_command_list() {
    if (handle != VK_NULL_HANDLE) {
        device.api.vkFreeCommandBuffers(device.handle, device.command_pool, 1, &handle);
    }
}

khaoz_result khaoz_gpu_command_list_create(khaoz_gpu_device* device, khaoz_gpu_command_list** out_commands) {
    return khaoz::abi::guard([&] {
        if (out_commands == nullptr) {
            return error(KHAOZ_ERROR_INVALID_ARGUMENT, "A command list output is required");
        }
        *out_commands = nullptr;
        if (device == nullptr) {
            return error(KHAOZ_ERROR_INVALID_ARGUMENT, "A device is required");
        }
        auto commands = std::make_unique<khaoz_gpu_command_list>(*device);
        const auto result = allocate_commands(*device, commands->handle);
        if (result == KHAOZ_OK) {
            *out_commands = commands.release();
        }
        return result;
    });
}

khaoz_result khaoz_gpu_command_list_destroy(khaoz_gpu_command_list* commands) {
    return khaoz::abi::guard([&] { return destroy(commands); });
}

khaoz_result khaoz_gpu_command_list_begin(khaoz_gpu_command_list* commands) {
    return khaoz::abi::guard([&] {
        using phase = khaoz_gpu_command_list::phase;
        if (commands == nullptr || commands->state == phase::recording) {
            return error(KHAOZ_ERROR_INVALID_ARGUMENT, "The command list is missing or already recording");
        }
        if (commands->state == phase::pending) {
            const auto drained = commands->device.wait_for_idle();
            if (drained != KHAOZ_OK) {
                return drained;
            }
        }
        // vkBeginCommandBuffer implicitly resets an executable buffer from this resettable pool.
        const auto result = begin_commands(commands->device, commands->handle);
        if (result == KHAOZ_OK) {
            commands->rendering = {};
            commands->pipeline = nullptr;
            commands->sets.clear();
            commands->vertices.clear();
            commands->uploads.clear();
            commands->index_bound = false;
        }
        commands->state = result == KHAOZ_OK ? phase::recording : phase::initial;
        return result;
    });
}

khaoz_result khaoz_gpu_command_list_end(khaoz_gpu_command_list* commands) {
    return khaoz::abi::guard([&] {
        using phase = khaoz_gpu_command_list::phase;
        if (commands == nullptr || commands->state != phase::recording) {
            return error(KHAOZ_ERROR_INVALID_ARGUMENT, "The command list is not recording");
        }
        const auto usable = commands->device.require_usable();
        if (usable != KHAOZ_OK)
            return usable;
        commands->rendering.finish(*commands);
        const auto result = end_commands(commands->device, commands->handle);
        commands->state = result == KHAOZ_OK ? phase::executable : phase::initial;
        return result;
    });
}

khaoz_result khaoz_gpu_device_submit(khaoz_gpu_device* device, khaoz_gpu_command_list* commands,
                                     khaoz_gpu_fence* completion) {
    return khaoz::abi::guard([&] {
        using phase = khaoz_gpu_command_list::phase;
        if (device == nullptr || commands == nullptr || &commands->device != device ||
            commands->state != phase::executable) {
            return error(KHAOZ_ERROR_INVALID_ARGUMENT, "Submission requires a finished command list from this device");
        }
        if (completion != nullptr && (&completion->device != device || completion->submitted)) {
            return error(KHAOZ_ERROR_INVALID_ARGUMENT, "Submission requires a fresh or reset fence from this device");
        }
        const auto result =
            submit(*device, commands->handle, completion != nullptr ? completion->handle : VK_NULL_HANDLE);
        if (result == KHAOZ_OK) {
            commands->state = phase::pending;
            if (completion != nullptr) {
                completion->submitted = true;
            }
        }
        return result;
    });
}
