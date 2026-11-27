#include "texture.hpp"
#include "commands.hpp"

#include <bit>
#include <limits>
#include <memory>

using namespace khaoz::vulkan;

namespace {

constexpr VkImageAspectFlags color = VK_IMAGE_ASPECT_COLOR_BIT;
constexpr VkImageAspectFlags depth_stencil = VK_IMAGE_ASPECT_DEPTH_BIT | VK_IMAGE_ASPECT_STENCIL_BIT;

texture_format image_format(uint32_t format) {
    switch (format) {
    case KHAOZ_GPU_RGBA8_UNORM:
        return {VK_FORMAT_R8G8B8A8_UNORM, color, 4};
    case KHAOZ_GPU_R32_FLOAT:
        return {VK_FORMAT_R32_SFLOAT, color, 4};
    case KHAOZ_GPU_D32_FLOAT_S8_UINT:
        return {VK_FORMAT_D32_SFLOAT_S8_UINT, depth_stencil, 0};
    case KHAOZ_GPU_D24_UNORM_S8_UINT:
        return {VK_FORMAT_D24_UNORM_S8_UINT, depth_stencil, 0};
    case KHAOZ_GPU_R8_UNORM:
        return {VK_FORMAT_R8_UNORM, color, 1};
    case KHAOZ_GPU_BGRA8_UNORM:
        return {VK_FORMAT_B8G8R8A8_UNORM, color, 4};
    case KHAOZ_GPU_RGBA16_FLOAT:
        return {VK_FORMAT_R16G16B16A16_SFLOAT, color, 8};
    case KHAOZ_GPU_RG16_FLOAT:
        return {VK_FORMAT_R16G16_SFLOAT, color, 4};
    case KHAOZ_GPU_RGBA32_FLOAT:
        return {VK_FORMAT_R32G32B32A32_SFLOAT, color, 16};
    default:
        return {};
    }
}

VkImageUsageFlags image_usage(uint32_t usage) {
    VkImageUsageFlags flags = VK_IMAGE_USAGE_TRANSFER_SRC_BIT | VK_IMAGE_USAGE_TRANSFER_DST_BIT;
    if ((usage & KHAOZ_GPU_TEXTURE_SAMPLED) != 0)
        flags |= VK_IMAGE_USAGE_SAMPLED_BIT;
    if ((usage & KHAOZ_GPU_TEXTURE_RENDER_TARGET) != 0)
        flags |= VK_IMAGE_USAGE_COLOR_ATTACHMENT_BIT;
    if ((usage & KHAOZ_GPU_TEXTURE_DEPTH_STENCIL) != 0)
        flags |= VK_IMAGE_USAGE_DEPTH_STENCIL_ATTACHMENT_BIT;
    if ((usage & KHAOZ_GPU_TEXTURE_STORAGE) != 0)
        flags |= VK_IMAGE_USAGE_STORAGE_BIT;
    return flags;
}

uint64_t level_bytes(const khaoz_gpu_texture& texture, uint32_t mip) noexcept {
    return uint64_t{mip_extent(texture.desc.width, mip)} * mip_extent(texture.desc.height, mip) *
           texture.format.bytes_per_texel;
}

VkDeviceSize level_stride(const khaoz_gpu_texture& texture, uint32_t mip) noexcept {
    // Colour formats have power-of-two texel sizes, so this aligns R8 levels without disturbing the others.
    return (level_bytes(texture, mip) + 3U) & ~VkDeviceSize{3};
}

khaoz_result create_staging(khaoz_gpu_texture& texture) {
    const auto& desc = texture.desc;
    if (texture.format.aspects != color || desc.sample_count != 1 || desc.usage != KHAOZ_GPU_TEXTURE_STAGING) {
        return error(KHAOZ_ERROR_UNSUPPORTED,
                     "Staging textures require a single-sample colour format and staging usage");
    }
    if (level_bytes(texture, 0) > std::numeric_limits<uint32_t>::max()) {
        return error(KHAOZ_ERROR_UNSUPPORTED, "The texture mapping exceeds the ABI size limit");
    }
    for (uint32_t mip = 0; mip < desc.mip_levels; ++mip) {
        texture.layer_stride += level_stride(texture, mip);
    }
    return texture.staging.create(texture.device.allocator, {texture.layer_stride * desc.array_layers, transfer_buffer,
                                                             VMA_ALLOCATION_CREATE_HOST_ACCESS_RANDOM_BIT});
}

khaoz_result create_image(khaoz_gpu_texture& texture) {
    const auto& desc = texture.desc;
    VkImageCreateInfo image{
        .sType = VK_STRUCTURE_TYPE_IMAGE_CREATE_INFO,
        .pNext = nullptr,
        .flags = 0,
        .imageType = VK_IMAGE_TYPE_2D,
        .format = texture.format.vk,
        .extent = {desc.width, desc.height, 1},
        .mipLevels = desc.mip_levels,
        .arrayLayers = desc.array_layers,
        .samples = static_cast<VkSampleCountFlagBits>(desc.sample_count),
        .tiling = VK_IMAGE_TILING_OPTIMAL,
        .usage = image_usage(desc.usage),
        .sharingMode = VK_SHARING_MODE_EXCLUSIVE,
        .queueFamilyIndexCount = 0,
        .pQueueFamilyIndices = nullptr,
        .initialLayout = VK_IMAGE_LAYOUT_UNDEFINED,
    };
    if ((desc.usage & KHAOZ_GPU_TEXTURE_CUBEMAP) != 0) {
        if (desc.width != desc.height || desc.sample_count != 1) {
            return error(KHAOZ_ERROR_INVALID_ARGUMENT, "Cube textures require square single-sample faces");
        }
        image.flags = VK_IMAGE_CREATE_CUBE_COMPATIBLE_BIT;
    }

    auto& device = texture.device;
    VkImageFormatProperties supported{};
    const auto queried = device.instance_api.vkGetPhysicalDeviceImageFormatProperties(
        device.adapter.handle, image.format, image.imageType, image.tiling, image.usage, image.flags, &supported);
    if (queried != VK_SUCCESS || desc.width > supported.maxExtent.width || desc.height > supported.maxExtent.height ||
        desc.mip_levels > supported.maxMipLevels || desc.array_layers > supported.maxArrayLayers ||
        (supported.sampleCounts & desc.sample_count) == 0) {
        return error(KHAOZ_ERROR_UNSUPPORTED, "The device does not support the requested texture shape or usage");
    }

    VmaAllocationCreateInfo allocation{};
    allocation.usage = VMA_MEMORY_USAGE_AUTO_PREFER_DEVICE;
    VkImage pending_image{};
    VmaAllocation pending_memory{};
    const auto result =
        check(vmaCreateImage(device.allocator, &image, &allocation, &pending_image, &pending_memory, nullptr),
              "Could not allocate a Vulkan image");
    if (result != KHAOZ_OK) {
        return result;
    }

    texture.image = pending_image;
    texture.image_memory = pending_memory;

    // Establish a resting layout before the image can be recorded in any list. Abandoning or reordering lists
    // then cannot leave a transfer using the wrong old layout.
    return transfer_now(std::make_unique<khaoz_gpu_command_list>(device), [&](VkCommandBuffer commands) {
        auto barrier = info<VkImageMemoryBarrier2>(VK_STRUCTURE_TYPE_IMAGE_MEMORY_BARRIER_2);
        barrier.dstStageMask = VK_PIPELINE_STAGE_2_ALL_COMMANDS_BIT;
        barrier.dstAccessMask = VK_ACCESS_2_MEMORY_READ_BIT | VK_ACCESS_2_MEMORY_WRITE_BIT;
        barrier.oldLayout = VK_IMAGE_LAYOUT_UNDEFINED;
        barrier.newLayout = VK_IMAGE_LAYOUT_GENERAL;
        barrier.srcQueueFamilyIndex = VK_QUEUE_FAMILY_IGNORED;
        barrier.dstQueueFamilyIndex = VK_QUEUE_FAMILY_IGNORED;
        barrier.image = texture.image;
        barrier.subresourceRange = {texture.format.aspects, 0, desc.mip_levels, 0, desc.array_layers};
        auto dependency = info<VkDependencyInfo>(VK_STRUCTURE_TYPE_DEPENDENCY_INFO);
        dependency.imageMemoryBarrierCount = 1;
        dependency.pImageMemoryBarriers = &barrier;
        device.api.vkCmdPipelineBarrier2(commands, &dependency);
    });
}

} // namespace

uint32_t khaoz::vulkan::mip_extent(uint32_t base, uint32_t mip) noexcept { return std::max(1U, base >> mip); }

buffer_window khaoz_gpu_texture::subresource(texture_subresource location) const noexcept {
    const auto [mip, layer] = location;
    VkDeviceSize offset = layer_stride * layer;
    for (uint32_t level = 0; level < mip; ++level) {
        offset += level_stride(*this, level);
    }
    return {
        .offset = offset,
        .size = static_cast<uint32_t>(level_bytes(*this, mip)),
        .row_pitch = mip_extent(desc.width, mip) * format.bytes_per_texel,
    };
}

khaoz_gpu_texture::~khaoz_gpu_texture() {
    if (image != VK_NULL_HANDLE) {
        vmaDestroyImage(device.allocator, image, image_memory);
    }
}

khaoz_result khaoz_gpu_texture_create(khaoz_gpu_device* device, const khaoz_gpu_texture_desc* desc,
                                      khaoz_gpu_texture** out_texture) {
    return khaoz::abi::guard([&] {
        if (out_texture == nullptr) {
            return error(KHAOZ_ERROR_INVALID_ARGUMENT, "A texture output is required");
        }
        *out_texture = nullptr;
        if (device == nullptr || desc == nullptr) {
            return error(KHAOZ_ERROR_INVALID_ARGUMENT, "A device and texture description are required");
        }
        const auto& limits = device->adapter.properties.limits;
        const auto max_axis = std::max(desc->width, desc->height);
        const uint32_t faces = (desc->usage & KHAOZ_GPU_TEXTURE_CUBEMAP) != 0 ? 6U : 1U;
        constexpr uint32_t all_usage = (KHAOZ_GPU_TEXTURE_STORAGE << 1U) - 1U;
        if (desc->width == 0 || desc->height == 0 || max_axis > limits.maxImageDimension2D || desc->mip_levels == 0 ||
            desc->mip_levels > static_cast<uint32_t>(std::bit_width(max_axis)) || desc->array_layers == 0 ||
            desc->array_layers > limits.maxImageArrayLayers / faces || (desc->usage & ~all_usage) != 0 ||
            !std::has_single_bit(desc->sample_count) || desc->sample_count > 64 ||
            (desc->sample_count > 1 && (desc->mip_levels > 1 || desc->array_layers > 1 || desc->is_array))) {
            return error(KHAOZ_ERROR_INVALID_ARGUMENT, "The Vulkan texture description has an invalid shape or usage");
        }

        const auto usable = device->require_usable();
        if (usable != KHAOZ_OK) {
            return usable;
        }
        auto texture = std::make_unique<khaoz_gpu_texture>(*device);
        texture->desc = *desc;
        texture->desc.array_layers *= faces;
        texture->format = image_format(desc->format);
        if (texture->format.vk == VK_FORMAT_UNDEFINED) {
            return error(KHAOZ_ERROR_UNSUPPORTED, "The texture format is unknown to the native Vulkan backend");
        }
        const auto result = texture->is_staging() ? create_staging(*texture) : create_image(*texture);
        if (result == KHAOZ_OK) {
            *out_texture = texture.release();
        } else if (device->work_pending && device->queue_failure != KHAOZ_OK) {
            // The first layout transition may still reference this image.
            retain_for_teardown(texture.release());
        }
        return result;
    });
}

khaoz_result khaoz_gpu_texture_destroy(khaoz_gpu_texture* texture) {
    return khaoz::abi::guard([&] { return destroy(texture); });
}

khaoz_result khaoz_gpu_texture_map(khaoz_gpu_texture* texture, uint32_t mode, khaoz_gpu_mapped_data* out_data) {
    return khaoz::abi::guard([&] {
        if (texture == nullptr || !texture->is_staging() || out_data == nullptr) {
            return error(KHAOZ_ERROR_INVALID_ARGUMENT, "A staging texture and mapping output are required");
        }
        const auto usable = texture->device.require_usable();
        if (usable != KHAOZ_OK) {
            return usable;
        }
        const auto result = texture->device.wait_for_idle();
        if (result != KHAOZ_OK) {
            return result;
        }
        const auto base = texture->subresource({0, 0});
        return texture->staging.map(base, mode, *out_data);
    });
}

khaoz_result khaoz_gpu_texture_unmap(khaoz_gpu_texture* texture) {
    return khaoz::abi::guard([&] {
        return texture != nullptr && texture->is_staging()
                   ? texture->staging.unmap()
                   : error(KHAOZ_ERROR_INVALID_ARGUMENT, "A staging texture is required");
    });
}
