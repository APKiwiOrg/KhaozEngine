#include "framebuffer.hpp"

#include <atomic>
#include <memory>

using namespace khaoz::vulkan;

namespace {

std::atomic<uint64_t> next_framebuffer_id{1};

bool valid_attachment(const khaoz_gpu_framebuffer& framebuffer, const khaoz_gpu_texture* texture,
                      VkImageAspectFlags aspect, uint32_t usage) {
    const auto& targets = framebuffer.targets;
    return texture != nullptr && &texture->device == &framebuffer.device && texture->attachment != VK_NULL_HANDLE &&
           (texture->format.aspects & aspect) != 0 && (texture->desc.usage & usage) != 0 &&
           texture->desc.width == targets.extent.width && texture->desc.height == targets.extent.height &&
           texture->desc.sample_count == targets.samples;
}

framebuffer_attachment attachment(const khaoz_gpu_texture& texture) {
    return {texture.attachment, texture.format.vk, texture.image, texture.format.aspects};
}

khaoz_result build(khaoz_gpu_framebuffer& framebuffer, const khaoz_gpu_texture* depth, khaoz_gpu_texture* const* colors,
                   uint32_t count) {
    const auto* first = count == 0 ? depth : colors[0];
    if (first == nullptr)
        return error(KHAOZ_ERROR_INVALID_ARGUMENT, "A Vulkan attachment is missing");

    auto& targets = framebuffer.targets;
    targets.extent = {first->desc.width, first->desc.height};
    targets.samples = first->desc.sample_count;
    if (depth != nullptr) {
        if (!valid_attachment(framebuffer, depth, VK_IMAGE_ASPECT_DEPTH_BIT, KHAOZ_GPU_TEXTURE_DEPTH_STENCIL))
            return error(KHAOZ_ERROR_INVALID_ARGUMENT, "Invalid Vulkan depth attachment");
        framebuffer.textures.add(*depth, &framebuffer);
        targets.depth = attachment(*depth);
    }

    for (uint32_t i = 0; i < count; ++i) {
        const auto* color = colors[i];
        if (!valid_attachment(framebuffer, color, VK_IMAGE_ASPECT_COLOR_BIT, KHAOZ_GPU_TEXTURE_RENDER_TARGET))
            return error(KHAOZ_ERROR_INVALID_ARGUMENT, "Invalid Vulkan colour attachment");
        framebuffer.textures.add(*color, &framebuffer);
        targets.colors.push_back(attachment(*color));
    }
    targets.id = next_framebuffer_id.fetch_add(1, std::memory_order_relaxed);
    return KHAOZ_OK;
}

} // namespace

khaoz_gpu_framebuffer::~khaoz_gpu_framebuffer() {
    if (!device.closing)
        textures.release(this);
}

khaoz_result khaoz_gpu_framebuffer_create(khaoz_gpu_device* device, khaoz_gpu_texture* depth,
                                          khaoz_gpu_texture* const* colors, uint32_t count,
                                          khaoz_gpu_framebuffer** out_framebuffer) {
    return khaoz::abi::guard([&] {
        if (out_framebuffer == nullptr)
            return error(KHAOZ_ERROR_INVALID_ARGUMENT, "A framebuffer output is required");
        *out_framebuffer = nullptr;
        if (device == nullptr || (count == 0 ? depth == nullptr : colors == nullptr) ||
            count > device->capabilities.max_color_attachments)
            return error(KHAOZ_ERROR_INVALID_ARGUMENT, "A device and valid framebuffer attachments are required");

        const auto usable = device->require_usable();
        if (usable != KHAOZ_OK)
            return usable;
        auto framebuffer = std::make_unique<khaoz_gpu_framebuffer>(*device);
        const auto result = build(*framebuffer, depth, colors, count);
        if (result == KHAOZ_OK)
            *out_framebuffer = framebuffer.release();
        return result;
    });
}

khaoz_result khaoz_gpu_framebuffer_destroy(khaoz_gpu_framebuffer* framebuffer) {
    return khaoz::abi::guard([&] { return destroy(framebuffer); });
}
