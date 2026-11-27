#pragma once

#include "device.hpp"

struct khaoz_gpu_shader_set final : khaoz::vulkan::device_child {
    VkShaderModule vertex{};
    VkShaderModule fragment{};

    explicit khaoz_gpu_shader_set(khaoz_gpu_device& owner) noexcept : device_child(owner) {}
    ~khaoz_gpu_shader_set() override;
};
