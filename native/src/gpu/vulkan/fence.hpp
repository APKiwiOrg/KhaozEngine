#pragma once

#include "device.hpp"

struct khaoz_gpu_fence : khaoz::vulkan::device_child {
    VkFence handle{};
    bool submitted{};

    explicit khaoz_gpu_fence(khaoz_gpu_device& owner) noexcept : device_child(owner) {}
    ~khaoz_gpu_fence() override;
};
