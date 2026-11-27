#pragma once

#include "memory.hpp"

namespace khaoz::vulkan {

struct texture_format {
    VkFormat vk{};
    VkImageAspectFlags aspects{};
    uint32_t bytes_per_texel{};
};

struct texture_subresource {
    uint32_t mip;
    uint32_t layer;
};

texture_format image_format(uint32_t format, bool depth = false);
uint32_t mip_extent(uint32_t base, uint32_t mip) noexcept;

} // namespace khaoz::vulkan

struct khaoz_gpu_texture : khaoz::vulkan::device_child {
    khaoz_gpu_texture_desc desc{};
    khaoz::vulkan::texture_format format{};
    VkImage image{};
    VkImageView sampled{};
    VkImageView storage{};
    VkImageView attachment{};
    VmaAllocation image_memory{};
    khaoz::vulkan::allocated_buffer staging;
    VkDeviceSize layer_stride{};

    explicit khaoz_gpu_texture(khaoz_gpu_device& owner) noexcept : device_child(owner) {}
    ~khaoz_gpu_texture() override;

    [[nodiscard]] bool is_staging() const noexcept { return (desc.usage & KHAOZ_GPU_TEXTURE_STAGING) != 0; }
    [[nodiscard]] khaoz::vulkan::buffer_window subresource(khaoz::vulkan::texture_subresource location) const noexcept;
};
