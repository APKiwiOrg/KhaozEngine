#pragma once

#include "memory.hpp"
#include "rendering.hpp"

#include <memory>

namespace khaoz::vulkan {
struct recorded_set {
    const khaoz_gpu_resource_set* set{};
    uint32_t dynamic_offset{};
};
} // namespace khaoz::vulkan

struct khaoz_gpu_command_list : khaoz::vulkan::device_child {
    enum class phase : uint8_t { initial, recording, executable, pending };

    VkCommandBuffer handle{};
    phase state{};
    khaoz::vulkan::rendering_scope rendering;
    const khaoz_gpu_pipeline* pipeline{};
    std::vector<khaoz::vulkan::recorded_set> sets;
    std::vector<uint32_t> dynamic_offsets;
    std::vector<uint8_t> vertices;
    std::vector<std::unique_ptr<khaoz::vulkan::allocated_buffer>> uploads;
    uint32_t index_capacity{};
    bool index_bound{};

    explicit khaoz_gpu_command_list(khaoz_gpu_device& owner) noexcept : device_child(owner) {}
    ~khaoz_gpu_command_list() override;
};

namespace khaoz::vulkan {

khaoz_result require_recording(const khaoz_gpu_command_list* commands);
khaoz_result allocate_commands(khaoz_gpu_device& device, VkCommandBuffer& out);
khaoz_result begin_commands(khaoz_gpu_device& device, VkCommandBuffer commands);
khaoz_result end_commands(khaoz_gpu_device& device, VkCommandBuffer commands);
khaoz_result submit(khaoz_gpu_device& device, VkCommandBuffer commands, VkFence fence);
void record_buffer_copy(const khaoz_gpu_device& device, VkCommandBuffer commands, VkBuffer source, VkBuffer destination,
                        const VkBufferCopy& copy);

template <class Record> khaoz_result transfer_now(std::unique_ptr<khaoz_gpu_command_list> commands, Record record) {
    auto& device = commands->device;
    auto result = allocate_commands(device, commands->handle);
    if (result != KHAOZ_OK) {
        return result;
    }
    result = begin_commands(device, commands->handle);
    if (result != KHAOZ_OK) {
        return result;
    }

    record(commands->handle);
    result = end_commands(device, commands->handle);
    if (result != KHAOZ_OK) {
        return result;
    }
    result = submit(device, commands->handle, VK_NULL_HANDLE);
    if (result != KHAOZ_OK) {
        return result;
    }
    result = device.wait_for_idle();
    if (device.work_pending) {
        // The failed wait did not prove completion. The device takes ownership until it can drain.
        retain_for_teardown(commands.release());
    }
    return result;
}

struct staged_transfer final : khaoz_gpu_command_list {
    allocated_buffer upload;

    explicit staged_transfer(khaoz_gpu_device& owner) noexcept : khaoz_gpu_command_list(owner) {}
};

template <class Record>
khaoz_result transfer_upload(khaoz_gpu_device& device, const void* data, size_t size, Record record) {
    auto commands = std::make_unique<staged_transfer>(device);
    auto result = commands->upload.create(device.allocator, {size, VK_BUFFER_USAGE_TRANSFER_SRC_BIT,
                                                             VMA_ALLOCATION_CREATE_HOST_ACCESS_SEQUENTIAL_WRITE_BIT});
    if (result != KHAOZ_OK) {
        return result;
    }
    result = commands->upload.write(0, data, size);
    if (result != KHAOZ_OK) {
        return result;
    }

    const auto upload = commands->upload.handle;
    return transfer_now(std::move(commands), [&](VkCommandBuffer command_buffer) { record(command_buffer, upload); });
}

} // namespace khaoz::vulkan
