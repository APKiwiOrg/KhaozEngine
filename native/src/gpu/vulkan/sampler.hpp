#pragma once

#include "device.hpp"

struct khaoz_gpu_sampler final : khaoz::vulkan::device_child {
    VkSampler handle{};

    explicit khaoz_gpu_sampler(khaoz_gpu_device& owner) noexcept : device_child(owner) {}
    ~khaoz_gpu_sampler() override;
};
