#pragma once

#include "device.hpp"

struct khaoz_gpu_command_list : khaoz::vulkan::device_child {
    enum class phase : uint8_t { initial, recording, executable, pending };

    VkCommandBuffer handle{};
    phase state{};

    explicit khaoz_gpu_command_list(khaoz_gpu_device& owner) noexcept : device_child(owner) {}
    ~khaoz_gpu_command_list() override;
};

namespace khaoz::vulkan {

khaoz_result allocate_commands(khaoz_gpu_device& device, VkCommandBuffer& out);
khaoz_result begin_commands(khaoz_gpu_device& device, VkCommandBuffer commands);
khaoz_result end_commands(khaoz_gpu_device& device, VkCommandBuffer commands);
khaoz_result submit(khaoz_gpu_device& device, VkCommandBuffer commands, VkFence fence);
void record_buffer_copy(const khaoz_gpu_device& device, VkCommandBuffer commands, VkBuffer source, VkBuffer destination,
                        const VkBufferCopy& copy);

template <class Record> khaoz_result transfer_now(khaoz_gpu_device& device, Record record) {
    khaoz_gpu_command_list commands{device};
    auto result = allocate_commands(device, commands.handle);
    if (result != KHAOZ_OK) {
        return result;
    }
    result = begin_commands(device, commands.handle);
    if (result != KHAOZ_OK) {
        return result;
    }

    record(commands.handle);
    result = end_commands(device, commands.handle);
    if (result != KHAOZ_OK) {
        return result;
    }
    result = submit(device, commands.handle, VK_NULL_HANDLE);
    return result == KHAOZ_OK ? device.wait_for_idle() : result;
}

} // namespace khaoz::vulkan
