#include "commands.hpp"

#include <limits>

using namespace khaoz::vulkan;

namespace {

VkRenderingAttachmentInfo render_attachment(VkImageView view) {
    auto result = info<VkRenderingAttachmentInfo>(VK_STRUCTURE_TYPE_RENDERING_ATTACHMENT_INFO);
    result.imageView = view;
    result.imageLayout = VK_IMAGE_LAYOUT_GENERAL;
    result.loadOp = VK_ATTACHMENT_LOAD_OP_LOAD;
    result.storeOp = VK_ATTACHMENT_STORE_OP_STORE;
    return result;
}

void attachment_barrier(const khaoz_gpu_command_list& commands) {
    // Images stay in GENERAL. Order preceding transfers, target writes and shader accesses before this pass.
    auto memory = info<VkMemoryBarrier2>(VK_STRUCTURE_TYPE_MEMORY_BARRIER_2);
    memory.srcStageMask = VK_PIPELINE_STAGE_2_ALL_COMMANDS_BIT;
    memory.srcAccessMask = VK_ACCESS_2_MEMORY_WRITE_BIT;
    memory.dstStageMask = VK_PIPELINE_STAGE_2_ALL_COMMANDS_BIT;
    memory.dstAccessMask = VK_ACCESS_2_MEMORY_READ_BIT | VK_ACCESS_2_MEMORY_WRITE_BIT;
    auto dependency = info<VkDependencyInfo>(VK_STRUCTURE_TYPE_DEPENDENCY_INFO);
    dependency.memoryBarrierCount = 1;
    dependency.pMemoryBarriers = &memory;
    commands.device.api.vkCmdPipelineBarrier2(commands.handle, &dependency);
}

void clear_attachment(const khaoz_gpu_command_list& commands, VkImageAspectFlags aspects, uint32_t index,
                      const VkClearValue& value) {
    const VkClearAttachment attachment{aspects, index, value};
    const VkClearRect rect{{{0, 0}, commands.rendering.framebuffer.extent}, 0, 1};
    commands.device.api.vkCmdClearAttachments(commands.handle, 1, &attachment, 1, &rect);
}

} // namespace

void rendering_scope::set_framebuffer(khaoz_gpu_command_list& commands, const framebuffer_targets& target) {
    if (framebuffer.id == target.id)
        return;

    // Copy and allocate before changing the existing recording. A failed allocation leaves it intact.
    auto snapshot = target;
    std::vector<VkRenderingAttachmentInfo> attachments;
    attachments.reserve(snapshot.colors.size());
    for (const auto& color : snapshot.colors)
        attachments.push_back(render_attachment(color.view));

    finish(commands);
    framebuffer = std::move(snapshot);
    colors = std::move(attachments);
    depth = render_attachment(framebuffer.depth.view);
    scissor = {{0, 0}, framebuffer.extent};
}

void rendering_scope::clear_color(khaoz_gpu_command_list& commands, uint32_t index, const VkClearValue& clear) {
    if (active) {
        clear_attachment(commands, VK_IMAGE_ASPECT_COLOR_BIT, index, clear);
        return;
    }
    auto& attachment = colors.at(index);
    attachment.loadOp = VK_ATTACHMENT_LOAD_OP_CLEAR;
    attachment.clearValue = clear;
    has_clear = true;
}

void rendering_scope::clear_depth(khaoz_gpu_command_list& commands, const VkClearValue& clear) {
    if (active) {
        clear_attachment(commands, framebuffer.depth.aspects, 0, clear);
        return;
    }
    depth.loadOp = VK_ATTACHMENT_LOAD_OP_CLEAR;
    depth.clearValue = clear;
    has_clear = true;
}

void rendering_scope::begin(khaoz_gpu_command_list& commands) {
    auto desc = info<VkRenderingInfo>(VK_STRUCTURE_TYPE_RENDERING_INFO);
    desc.renderArea = {{0, 0}, framebuffer.extent};
    desc.layerCount = 1;
    desc.colorAttachmentCount = static_cast<uint32_t>(colors.size());
    desc.pColorAttachments = colors.data();
    if ((framebuffer.depth.aspects & VK_IMAGE_ASPECT_DEPTH_BIT) != 0)
        desc.pDepthAttachment = &depth;
    if ((framebuffer.depth.aspects & VK_IMAGE_ASPECT_STENCIL_BIT) != 0)
        desc.pStencilAttachment = &depth;

    attachment_barrier(commands);
    commands.device.api.vkCmdBeginRendering(commands.handle, &desc);
    for (auto& color : colors)
        color.loadOp = VK_ATTACHMENT_LOAD_OP_LOAD;
    depth.loadOp = VK_ATTACHMENT_LOAD_OP_LOAD;
    has_clear = false;
    active = true;
}

void rendering_scope::finish(khaoz_gpu_command_list& commands) {
    if (has_clear)
        begin(commands);
    if (active)
        commands.device.api.vkCmdEndRendering(commands.handle);
    active = false;
    wrote_storage = false;
}

void rendering_scope::prepare_draw(khaoz_gpu_command_list& commands) {
    if (!active)
        begin(commands);
    const auto width = static_cast<float>(framebuffer.extent.width);
    const auto height = static_cast<float>(framebuffer.extent.height);
    // Match the managed Vulkan viewport's Y flip. Scissors are already in framebuffer coordinates.
    const VkViewport viewport{0.0F, height, width, -height, 0.0F, 1.0F};
    commands.device.api.vkCmdSetViewport(commands.handle, 0, 1, &viewport);
    commands.device.api.vkCmdSetScissor(commands.handle, 0, 1, &scissor);
}

khaoz_result khaoz_gpu_command_list_set_framebuffer(khaoz_gpu_command_list* commands,
                                                    khaoz_gpu_framebuffer* framebuffer) {
    return khaoz::abi::guard([&] {
        const auto usable = require_recording(commands);
        if (usable != KHAOZ_OK)
            return usable;
        if (framebuffer == nullptr || &framebuffer->device != &commands->device)
            return error(KHAOZ_ERROR_INVALID_ARGUMENT, "A framebuffer from this device is required");
        commands->rendering.set_framebuffer(*commands, framebuffer->targets);
        return KHAOZ_OK;
    });
}

khaoz_result khaoz_gpu_command_list_clear_color(khaoz_gpu_command_list* commands, uint32_t index,
                                                const khaoz_gpu_color* color) {
    return khaoz::abi::guard([&] {
        const auto usable = require_recording(commands);
        if (usable != KHAOZ_OK)
            return usable;
        if (color == nullptr || index >= commands->rendering.colors.size())
            return error(KHAOZ_ERROR_INVALID_ARGUMENT, "The color attachment is not bound");
        const VkClearValue value{.color = {.float32 = {color->r, color->g, color->b, color->a}}};
        commands->rendering.clear_color(*commands, index, value);
        return KHAOZ_OK;
    });
}

khaoz_result khaoz_gpu_command_list_clear_depth(khaoz_gpu_command_list* commands, float depth) {
    return khaoz::abi::guard([&] {
        const auto usable = require_recording(commands);
        if (usable != KHAOZ_OK)
            return usable;
        if (!commands->rendering.bound() || commands->rendering.framebuffer.depth.view == VK_NULL_HANDLE)
            return error(KHAOZ_ERROR_INVALID_ARGUMENT, "The depth attachment is not bound");
        if (!(depth >= 0.0F && depth <= 1.0F))
            return error(KHAOZ_ERROR_INVALID_ARGUMENT, "Vulkan clear depth must be between zero and one");
        commands->rendering.clear_depth(*commands, {.depthStencil = {depth, 0}});
        return KHAOZ_OK;
    });
}

khaoz_result khaoz_gpu_command_list_set_scissor(khaoz_gpu_command_list* commands, int32_t x, int32_t y, uint32_t width,
                                                uint32_t height) {
    return khaoz::abi::guard([&] {
        const auto usable = require_recording(commands);
        if (usable != KHAOZ_OK)
            return usable;
        constexpr auto max = std::numeric_limits<int32_t>::max();
        if (x < 0 || y < 0 || width > static_cast<uint32_t>(max - x) || height > static_cast<uint32_t>(max - y))
            return error(KHAOZ_ERROR_INVALID_ARGUMENT, "The scissor rectangle exceeds Vulkan's signed bounds");
        commands->rendering.scissor = {{x, y}, {width, height}};
        return KHAOZ_OK;
    });
}
