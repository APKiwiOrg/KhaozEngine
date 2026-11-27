#include "buffer.hpp"
#include "commands.hpp"

#include <memory>

using namespace khaoz::vulkan;

namespace {

VkBufferUsageFlags buffer_usage(uint32_t usage) {
    auto flags = transfer_buffer;
    if ((usage & KHAOZ_GPU_BUFFER_VERTEX) != 0)
        flags |= VK_BUFFER_USAGE_VERTEX_BUFFER_BIT;
    if ((usage & KHAOZ_GPU_BUFFER_INDEX) != 0)
        flags |= VK_BUFFER_USAGE_INDEX_BUFFER_BIT;
    if ((usage & KHAOZ_GPU_BUFFER_UNIFORM) != 0)
        flags |= VK_BUFFER_USAGE_UNIFORM_BUFFER_BIT;
    if ((usage & (uint32_t{KHAOZ_GPU_BUFFER_STORAGE_READ_ONLY} | KHAOZ_GPU_BUFFER_STORAGE_READ_WRITE)) != 0) {
        flags |= VK_BUFFER_USAGE_STORAGE_BUFFER_BIT;
    }
    if ((usage & KHAOZ_GPU_BUFFER_INDIRECT) != 0)
        flags |= VK_BUFFER_USAGE_INDIRECT_BUFFER_BIT;
    return flags;
}

} // namespace

khaoz_result khaoz_gpu_buffer_create(khaoz_gpu_device* device, uint32_t size_in_bytes, uint32_t usage,
                                     khaoz_gpu_buffer** out_buffer) {
    return khaoz::abi::guard([&] {
        if (out_buffer == nullptr) {
            return error(KHAOZ_ERROR_INVALID_ARGUMENT, "A buffer output is required");
        }
        *out_buffer = nullptr;
        constexpr uint32_t all_usage = (KHAOZ_GPU_BUFFER_STAGING << 1U) - 1U;
        if (device == nullptr || size_in_bytes == 0 || (usage & ~all_usage) != 0) {
            return error(KHAOZ_ERROR_INVALID_ARGUMENT, "A device, nonzero buffer size and known usage are required");
        }

        const auto usable = device->require_usable();
        if (usable != KHAOZ_OK) {
            return usable;
        }
        auto buffer = std::make_unique<khaoz_gpu_buffer>(*device);
        buffer->size = size_in_bytes;
        buffer->staging = (usage & KHAOZ_GPU_BUFFER_STAGING) != 0;
        VmaAllocationCreateFlags access = 0;
        if (buffer->staging) {
            access = VMA_ALLOCATION_CREATE_HOST_ACCESS_RANDOM_BIT;
        } else if ((usage & KHAOZ_GPU_BUFFER_DYNAMIC) != 0) {
            access = VMA_ALLOCATION_CREATE_HOST_ACCESS_SEQUENTIAL_WRITE_BIT;
        }
        const auto result = buffer->storage.create(device->allocator, {size_in_bytes, buffer_usage(usage), access});
        if (result == KHAOZ_OK) {
            *out_buffer = buffer.release();
        }
        return result;
    });
}

khaoz_result khaoz_gpu_buffer_destroy(khaoz_gpu_buffer* buffer) {
    return khaoz::abi::guard([&] { return destroy(buffer); });
}

khaoz_result khaoz_gpu_buffer_update(khaoz_gpu_buffer* buffer, uint32_t offset, const void* data, uint32_t size) {
    return khaoz::abi::guard([&] {
        if (buffer == nullptr || !fits(buffer->size, offset, size) || (size != 0 && data == nullptr)) {
            return error(KHAOZ_ERROR_INVALID_ARGUMENT, "The upload exceeds the Vulkan buffer or has no source");
        }
        if (size == 0) {
            return KHAOZ_OK;
        }

        auto& device = buffer->device;
        const auto usable = device.require_usable();
        if (usable != KHAOZ_OK) {
            return usable;
        }
        if (buffer->storage.host_visible) {
            const auto drained = device.wait_for_idle();
            return drained == KHAOZ_OK ? buffer->storage.write(offset, data, size) : drained;
        }

        return transfer_upload(device, data, size, [&](VkCommandBuffer commands, VkBuffer upload) {
            const VkBufferCopy copy{0, offset, size};
            record_buffer_copy(device, commands, upload, buffer->storage.handle, copy);
        });
    });
}

khaoz_result khaoz_gpu_buffer_map(khaoz_gpu_buffer* buffer, uint32_t mode, khaoz_gpu_mapped_data* out_data) {
    return khaoz::abi::guard([&] {
        if (buffer == nullptr || !buffer->staging || out_data == nullptr) {
            return error(KHAOZ_ERROR_INVALID_ARGUMENT, "A staging buffer and mapping output are required");
        }
        const auto usable = buffer->device.require_usable();
        if (usable != KHAOZ_OK) {
            return usable;
        }
        const auto drained = buffer->device.wait_for_idle();
        return drained == KHAOZ_OK ? buffer->storage.map({.offset = 0, .size = buffer->size, .row_pitch = buffer->size},
                                                         mode, *out_data)
                                   : drained;
    });
}

khaoz_result khaoz_gpu_buffer_unmap(khaoz_gpu_buffer* buffer) {
    return khaoz::abi::guard([&] {
        return buffer != nullptr ? buffer->storage.unmap()
                                 : error(KHAOZ_ERROR_INVALID_ARGUMENT, "A buffer is required");
    });
}
