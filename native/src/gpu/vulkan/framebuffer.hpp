#pragma once

#include "texture.hpp"

namespace khaoz::vulkan {

struct framebuffer_attachment {
    VkImageView view;
    VkFormat format;
    VkImage image;
    VkImageAspectFlags aspects;
};

// A recording binds a copy of these, so destroying the framebuffer never frees memory a live recording reads.
// The id is unique per framebuffer, which keeps rebind detection exact after an address is reused.
struct framebuffer_targets {
    uint64_t id{};
    std::vector<framebuffer_attachment> colors;
    framebuffer_attachment depth{};
    VkExtent2D extent{};
    uint32_t samples{};
};

} // namespace khaoz::vulkan

struct khaoz_gpu_framebuffer final : khaoz::vulkan::device_child {
    khaoz::vulkan::framebuffer_targets targets;

    explicit khaoz_gpu_framebuffer(khaoz_gpu_device& owner) noexcept : device_child(owner) {}
};
