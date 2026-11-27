#pragma once

#include "resource_layout.hpp"

namespace khaoz::vulkan {

struct uniform_window {
    uint32_t offset;
    uint32_t range;
    uint32_t capacity;
    bool applies_caller_offset;
};

} // namespace khaoz::vulkan

struct khaoz_gpu_resource_set final : khaoz::vulkan::device_child {
    VkDescriptorPool pool{};
    VkDescriptorSet handle{};
    std::vector<VkDescriptorSetLayoutBinding> bindings;
    std::vector<khaoz::vulkan::uniform_window> uniforms;
    std::vector<VkImage> images;
    bool writable_storage{};

    explicit khaoz_gpu_resource_set(khaoz_gpu_device& owner) noexcept : device_child(owner) {}
    ~khaoz_gpu_resource_set() override;
};
