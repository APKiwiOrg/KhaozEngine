#pragma once

#include "memory.hpp"

struct khaoz_gpu_buffer : khaoz::vulkan::device_child {
    khaoz::vulkan::allocated_buffer storage;
    uint32_t size{};
    bool staging{};

    explicit khaoz_gpu_buffer(khaoz_gpu_device& owner) noexcept : device_child(owner) {}
};

namespace khaoz::vulkan {

inline bool fits(uint32_t capacity, uint32_t offset, uint32_t size) noexcept {
    return offset <= capacity && size <= capacity - offset;
}

} // namespace khaoz::vulkan
