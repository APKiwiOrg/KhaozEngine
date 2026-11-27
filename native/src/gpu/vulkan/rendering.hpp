#pragma once

#include "framebuffer.hpp"

struct khaoz_gpu_command_list;

namespace khaoz::vulkan {

struct rendering_scope {
    const khaoz_gpu_framebuffer* framebuffer{};
    std::vector<VkRenderingAttachmentInfo> colors;
    VkRenderingAttachmentInfo depth{};
    VkRect2D scissor{};
    bool active{};
    bool has_clear{};
    bool wrote_storage{};

    void set_framebuffer(khaoz_gpu_command_list& commands, const khaoz_gpu_framebuffer& target);
    void clear_color(khaoz_gpu_command_list& commands, uint32_t index, const VkClearValue& clear);
    void clear_depth(khaoz_gpu_command_list& commands, const VkClearValue& clear);
    void begin(khaoz_gpu_command_list& commands);
    void finish(khaoz_gpu_command_list& commands);
    void prepare_draw(khaoz_gpu_command_list& commands);
};

} // namespace khaoz::vulkan
