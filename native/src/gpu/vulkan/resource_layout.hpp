#pragma once

#include "device.hpp"

struct khaoz_gpu_resource_layout final : khaoz::vulkan::device_child {
    VkDescriptorSetLayout handle{};
    std::vector<khaoz_gpu_resource_element> elements;
    std::vector<VkDescriptorSetLayoutBinding> bindings;
    uint32_t uniform_count{};

    explicit khaoz_gpu_resource_layout(khaoz_gpu_device& owner) noexcept : device_child(owner) {}
    ~khaoz_gpu_resource_layout() override;
};
