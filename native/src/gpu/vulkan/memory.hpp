#pragma once

#include "device.hpp"

namespace khaoz::vulkan {

struct buffer_allocation {
    VkDeviceSize size;
    VkBufferUsageFlags usage;
    VmaAllocationCreateFlags access{};
};

struct buffer_window {
    VkDeviceSize offset;
    uint32_t size;
    uint32_t row_pitch;
};

struct allocated_buffer {
    VmaAllocator allocator{};
    VmaAllocation memory{};
    VkBuffer handle{};
    bool host_visible{};
    bool mapped{};
    uint32_t mode{};

    allocated_buffer() = default;
    ~allocated_buffer();
    allocated_buffer(const allocated_buffer&) = delete;
    allocated_buffer& operator=(const allocated_buffer&) = delete;

    khaoz_result create(VmaAllocator owner, const buffer_allocation& desc);
    khaoz_result write(VkDeviceSize offset, const void* data, size_t size) const;
    khaoz_result map(const buffer_window& window, uint32_t access, khaoz_gpu_mapped_data& out);
    khaoz_result unmap();
};

inline constexpr VkBufferUsageFlags transfer_buffer =
    VK_BUFFER_USAGE_TRANSFER_SRC_BIT | VK_BUFFER_USAGE_TRANSFER_DST_BIT;

} // namespace khaoz::vulkan
