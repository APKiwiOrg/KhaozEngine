#pragma once

#include "texture.hpp"

namespace khaoz::vulkan {

struct framebuffer_attachment {
    VkImageView view;
    VkFormat format;
    VkImage image;
    VkImageAspectFlags aspects;
};

} // namespace khaoz::vulkan

struct khaoz_gpu_framebuffer final : khaoz::vulkan::device_child {
    std::vector<khaoz::vulkan::framebuffer_attachment> colors;
    khaoz::vulkan::framebuffer_attachment depth{};
    VkExtent2D extent{};
    uint32_t samples{};

    explicit khaoz_gpu_framebuffer(khaoz_gpu_device& owner) noexcept : device_child(owner) {}
};
