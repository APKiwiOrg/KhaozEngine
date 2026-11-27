#pragma once

#include "framebuffer.hpp"

struct khaoz_gpu_command_list;

namespace khaoz::vulkan {

struct rendering_scope {
    // A value copy of the bound framebuffer. Its id is zero while nothing is bound.
    framebuffer_targets framebuffer;
    std::vector<VkRenderingAttachmentInfo> colors;
    VkRenderingAttachmentInfo depth{};
    VkRect2D scissor{};
    bool active{};
    bool has_clear{};
    bool wrote_storage{};

    [[nodiscard]] bool bound() const noexcept { return framebuffer.id != 0; }
    void set_framebuffer(khaoz_gpu_command_list& commands, const framebuffer_targets& target);
    void clear_color(khaoz_gpu_command_list& commands, uint32_t index, const VkClearValue& clear);
    void clear_depth(khaoz_gpu_command_list& commands, const VkClearValue& clear);
    void begin(khaoz_gpu_command_list& commands);
    void finish(khaoz_gpu_command_list& commands);
    void prepare_draw(khaoz_gpu_command_list& commands);
};

} // namespace khaoz::vulkan
