#include "buffer.hpp"
#include "commands.hpp"
#include "texture.hpp"

#include <array>

using namespace khaoz::vulkan;

namespace {

struct access_dependency {
    VkPipelineStageFlags2 source_stage;
    VkAccessFlags2 source_access;
    VkPipelineStageFlags2 destination_stage;
    VkAccessFlags2 destination_access;
};

void memory_barrier(const khaoz_gpu_device& device, VkCommandBuffer commands, const access_dependency& access) {
    auto barrier = info<VkMemoryBarrier2>(VK_STRUCTURE_TYPE_MEMORY_BARRIER_2);
    barrier.srcStageMask = access.source_stage;
    barrier.srcAccessMask = access.source_access;
    barrier.dstStageMask = access.destination_stage;
    barrier.dstAccessMask = access.destination_access;
    auto dependency = info<VkDependencyInfo>(VK_STRUCTURE_TYPE_DEPENDENCY_INFO);
    dependency.memoryBarrierCount = 1;
    dependency.pMemoryBarriers = &barrier;
    device.api.vkCmdPipelineBarrier2(commands, &dependency);
}

void before_transfer(const khaoz_gpu_device& device, VkCommandBuffer commands) {
    memory_barrier(device, commands,
                   {
                       .source_stage = VK_PIPELINE_STAGE_2_ALL_COMMANDS_BIT,
                       .source_access = VK_ACCESS_2_MEMORY_WRITE_BIT,
                       .destination_stage = VK_PIPELINE_STAGE_2_ALL_TRANSFER_BIT,
                       .destination_access = VK_ACCESS_2_TRANSFER_READ_BIT | VK_ACCESS_2_TRANSFER_WRITE_BIT,
                   });
}

void after_transfer(const khaoz_gpu_device& device, VkCommandBuffer commands) {
    memory_barrier(device, commands,
                   {
                       .source_stage = VK_PIPELINE_STAGE_2_ALL_TRANSFER_BIT,
                       .source_access = VK_ACCESS_2_TRANSFER_WRITE_BIT,
                       .destination_stage = VK_PIPELINE_STAGE_2_ALL_COMMANDS_BIT | VK_PIPELINE_STAGE_2_HOST_BIT,
                       .destination_access = VK_ACCESS_2_MEMORY_READ_BIT | VK_ACCESS_2_MEMORY_WRITE_BIT |
                                             VK_ACCESS_2_HOST_READ_BIT | VK_ACCESS_2_HOST_WRITE_BIT,
                   });
}

bool has_region(const khaoz_gpu_texture& texture, const khaoz_gpu_texture_region& region) {
    return region.mip_level < texture.desc.mip_levels && region.array_layer < texture.desc.array_layers &&
           region.width > 0 && region.height > 0 &&
           fits(mip_extent(texture.desc.width, region.mip_level), region.x, region.width) &&
           fits(mip_extent(texture.desc.height, region.mip_level), region.y, region.height);
}

struct buffer_image_layout {
    VkDeviceSize offset;
    uint32_t row_length;
};

VkBufferImageCopy buffer_image_region(const buffer_image_layout& layout, const khaoz_gpu_texture_region& region) {
    VkBufferImageCopy copy{};
    copy.bufferOffset = layout.offset;
    copy.bufferRowLength = layout.row_length;
    copy.imageSubresource = {VK_IMAGE_ASPECT_COLOR_BIT, region.mip_level, region.array_layer, 1};
    copy.imageOffset = {static_cast<int32_t>(region.x), static_cast<int32_t>(region.y), 0};
    copy.imageExtent = {region.width, region.height, 1};
    return copy;
}

void copy_staging(VkCommandBuffer commands, const khaoz_gpu_texture& source, const khaoz_gpu_texture& destination,
                  const khaoz_gpu_texture_copy& copy) {
    const auto src = source.subresource({copy.src_mip_level, copy.src_array_layer});
    const auto dst = destination.subresource({copy.dst_mip_level, copy.dst_array_layer});
    const uint32_t bytes = copy.width * source.format.bytes_per_texel;
    if (bytes == src.row_pitch && bytes == dst.row_pitch) {
        const VkBufferCopy region{src.offset, dst.offset, uint64_t{bytes} * copy.height};
        source.device.api.vkCmdCopyBuffer(commands, source.staging.handle, destination.staging.handle, 1, &region);
        return;
    }
    for (uint32_t y = 0; y < copy.height; ++y) {
        const VkBufferCopy row{src.offset + uint64_t{src.row_pitch} * y, dst.offset + uint64_t{dst.row_pitch} * y,
                               bytes};
        source.device.api.vkCmdCopyBuffer(commands, source.staging.handle, destination.staging.handle, 1, &row);
    }
}

void copy_images(VkCommandBuffer commands, const khaoz_gpu_texture& source, const khaoz_gpu_texture& destination,
                 const khaoz_gpu_texture_copy& copy) {
    constexpr std::array aspects{VK_IMAGE_ASPECT_COLOR_BIT, VK_IMAGE_ASPECT_DEPTH_BIT, VK_IMAGE_ASPECT_STENCIL_BIT};
    std::array<VkImageCopy, 2> regions{};
    uint32_t count = 0;
    for (auto aspect : aspects) {
        if ((source.format.aspects & aspect) == 0) {
            continue;
        }
        auto& region = regions.at(count++);
        region.srcSubresource = {aspect, copy.src_mip_level, copy.src_array_layer, 1};
        region.dstSubresource = {aspect, copy.dst_mip_level, copy.dst_array_layer, 1};
        region.extent = {copy.width, copy.height, 1};
    }
    source.device.api.vkCmdCopyImage(commands, source.image, VK_IMAGE_LAYOUT_GENERAL, destination.image,
                                     VK_IMAGE_LAYOUT_GENERAL, count, regions.data());
}

void copy_texture(VkCommandBuffer commands, const khaoz_gpu_texture& src, const khaoz_gpu_texture& dst,
                  const khaoz_gpu_texture_copy& copy) {
    if (src.is_staging() && dst.is_staging()) {
        copy_staging(commands, src, dst, copy);
    } else if (src.is_staging()) {
        const auto subresource = src.subresource({copy.src_mip_level, copy.src_array_layer});
        const auto region =
            buffer_image_region({subresource.offset, mip_extent(src.desc.width, copy.src_mip_level)},
                                {0, 0, copy.width, copy.height, copy.dst_mip_level, copy.dst_array_layer});
        src.device.api.vkCmdCopyBufferToImage(commands, src.staging.handle, dst.image, VK_IMAGE_LAYOUT_GENERAL, 1,
                                              &region);
    } else if (dst.is_staging()) {
        const auto subresource = dst.subresource({copy.dst_mip_level, copy.dst_array_layer});
        const auto region =
            buffer_image_region({subresource.offset, mip_extent(dst.desc.width, copy.dst_mip_level)},
                                {0, 0, copy.width, copy.height, copy.src_mip_level, copy.src_array_layer});
        src.device.api.vkCmdCopyImageToBuffer(commands, src.image, VK_IMAGE_LAYOUT_GENERAL, dst.staging.handle, 1,
                                              &region);
    } else {
        copy_images(commands, src, dst, copy);
    }
}

bool matching_shape(const khaoz_gpu_texture_desc& src, const khaoz_gpu_texture_desc& dst) {
    return src.width == dst.width && src.height == dst.height && src.mip_levels == dst.mip_levels &&
           src.array_layers == dst.array_layers;
}

khaoz_result write_staging(khaoz_gpu_texture& texture, const khaoz_gpu_texture_region& region, const void* data) {
    const auto drained = texture.device.wait_for_idle();
    if (drained != KHAOZ_OK) {
        return drained;
    }
    const uint32_t bytes_per_texel = texture.format.bytes_per_texel;
    const auto subresource = texture.subresource({region.mip_level, region.array_layer});
    const auto row_size = size_t{region.width} * bytes_per_texel;
    const auto offset =
        subresource.offset + uint64_t{region.y} * subresource.row_pitch + uint64_t{region.x} * bytes_per_texel;
    for (uint32_t y = 0; y < region.height; ++y) {
        const auto written = texture.staging.write(offset + uint64_t{y} * subresource.row_pitch,
                                                   static_cast<const char*>(data) + y * row_size, row_size);
        if (written != KHAOZ_OK) {
            return written;
        }
    }
    return KHAOZ_OK;
}

} // namespace

void khaoz::vulkan::record_buffer_copy(const khaoz_gpu_device& device, VkCommandBuffer commands, VkBuffer source,
                                       VkBuffer destination, const VkBufferCopy& copy) {
    before_transfer(device, commands);
    device.api.vkCmdCopyBuffer(commands, source, destination, 1, &copy);
    after_transfer(device, commands);
}

khaoz_result khaoz_gpu_command_list_copy_buffer(khaoz_gpu_command_list* commands, khaoz_gpu_buffer* src,
                                                uint32_t src_offset, khaoz_gpu_buffer* dst, uint32_t dst_offset,
                                                uint32_t size) {
    return khaoz::abi::guard([&] {
        if (commands == nullptr || commands->state != khaoz_gpu_command_list::phase::recording || src == nullptr ||
            dst == nullptr || &src->device != &commands->device || &dst->device != &commands->device ||
            src->storage.mapped || dst->storage.mapped) {
            return error(KHAOZ_ERROR_INVALID_ARGUMENT,
                         "A recording and two unmapped buffers from its device are required");
        }
        const auto usable = commands->device.require_usable();
        if (usable != KHAOZ_OK) {
            return usable;
        }
        if ((src_offset % 4) != 0 || (dst_offset % 4) != 0 || !fits(src->size, src_offset, size) ||
            !fits(dst->size, dst_offset, size) ||
            (src == dst && std::max(src_offset, dst_offset) - std::min(src_offset, dst_offset) < size)) {
            return error(KHAOZ_ERROR_INVALID_ARGUMENT, "A buffer copy must fit, use aligned offsets and avoid overlap");
        }
        if (size != 0) {
            const VkBufferCopy copy{src_offset, dst_offset, size};
            commands->rendering.finish(*commands);
            record_buffer_copy(commands->device, commands->handle, src->storage.handle, dst->storage.handle, copy);
        }
        return KHAOZ_OK;
    });
}

khaoz_result khaoz_gpu_command_list_copy_texture(khaoz_gpu_command_list* commands, khaoz_gpu_texture* src,
                                                 khaoz_gpu_texture* dst, const khaoz_gpu_texture_copy* copy) {
    return khaoz::abi::guard([&] {
        if (commands == nullptr || commands->state != khaoz_gpu_command_list::phase::recording || src == nullptr ||
            dst == nullptr || &src->device != &commands->device || &dst->device != &commands->device ||
            src->staging.mapped || dst->staging.mapped) {
            return error(KHAOZ_ERROR_INVALID_ARGUMENT,
                         "A recording and two unmapped textures from its device are required");
        }
        const auto usable = commands->device.require_usable();
        if (usable != KHAOZ_OK) {
            return usable;
        }
        if (src->desc.format != dst->desc.format || src->desc.sample_count != dst->desc.sample_count) {
            return error(KHAOZ_ERROR_INVALID_ARGUMENT, "Texture copies require matching formats and sample counts");
        }
        if (copy != nullptr) {
            if (!has_region(*src, {0, 0, copy->width, copy->height, copy->src_mip_level, copy->src_array_layer}) ||
                !has_region(*dst, {0, 0, copy->width, copy->height, copy->dst_mip_level, copy->dst_array_layer}) ||
                (src == dst && copy->src_mip_level == copy->dst_mip_level &&
                 copy->src_array_layer == copy->dst_array_layer)) {
                return error(KHAOZ_ERROR_INVALID_ARGUMENT, "The texture copy exceeds a subresource or overlaps itself");
            }
            commands->rendering.finish(*commands);
            before_transfer(commands->device, commands->handle);
            copy_texture(commands->handle, *src, *dst, *copy);
        } else {
            if (src == dst || !matching_shape(src->desc, dst->desc)) {
                return error(KHAOZ_ERROR_INVALID_ARGUMENT,
                             "A whole texture copy needs distinct textures of the same shape");
            }
            commands->rendering.finish(*commands);
            before_transfer(commands->device, commands->handle);
            for (uint32_t mip = 0; mip < src->desc.mip_levels; ++mip) {
                const uint32_t width = mip_extent(src->desc.width, mip);
                const uint32_t height = mip_extent(src->desc.height, mip);
                for (uint32_t layer = 0; layer < src->desc.array_layers; ++layer) {
                    copy_texture(commands->handle, *src, *dst, {mip, layer, mip, layer, width, height});
                }
            }
        }
        after_transfer(commands->device, commands->handle);
        return KHAOZ_OK;
    });
}

khaoz_result khaoz_gpu_command_list_update_buffer(khaoz_gpu_command_list* commands, khaoz_gpu_buffer* buffer,
                                                  uint32_t offset, const void* data, uint32_t size) {
    return khaoz::abi::guard([&] {
        const auto usable = require_recording(commands);
        if (usable != KHAOZ_OK)
            return usable;
        if (buffer == nullptr || &buffer->device != &commands->device || buffer->storage.mapped ||
            !fits(buffer->size, offset, size) || (size != 0 && data == nullptr))
            return error(KHAOZ_ERROR_INVALID_ARGUMENT, "The Vulkan buffer upload has an invalid destination or source");
        if (size == 0)
            return KHAOZ_OK;

        auto upload = std::make_unique<allocated_buffer>();
        auto result =
            upload->create(commands->device.allocator, {size, VK_BUFFER_USAGE_TRANSFER_SRC_BIT,
                                                        VMA_ALLOCATION_CREATE_HOST_ACCESS_SEQUENTIAL_WRITE_BIT});
        if (result != KHAOZ_OK)
            return result;
        result = upload->write(0, data, size);
        if (result != KHAOZ_OK)
            return result;

        // A new recording releases its old uploads only after the command buffer can be reset.
        const auto source = upload->handle;
        commands->uploads.push_back(std::move(upload));
        commands->rendering.finish(*commands);
        record_buffer_copy(commands->device, commands->handle, source, buffer->storage.handle, {0, offset, size});
        return KHAOZ_OK;
    });
}

khaoz_result khaoz_gpu_texture_update(khaoz_gpu_texture* texture, const khaoz_gpu_texture_region* region,
                                      const void* data, uint32_t size) {
    return khaoz::abi::guard([&] {
        if (texture == nullptr || region == nullptr || data == nullptr || !has_region(*texture, *region)) {
            return error(KHAOZ_ERROR_INVALID_ARGUMENT, "A texture upload requires data and a region in the texture");
        }
        if (texture->format.bytes_per_texel == 0 || texture->desc.sample_count != 1) {
            return error(KHAOZ_ERROR_UNSUPPORTED, "Texture uploads require a single-sample colour format");
        }
        const uint64_t required = uint64_t{region->width} * region->height * texture->format.bytes_per_texel;
        if (required > size) {
            return error(KHAOZ_ERROR_INVALID_ARGUMENT, "The upload data is smaller than the texture region");
        }
        auto& device = texture->device;
        const auto usable = device.require_usable();
        if (usable != KHAOZ_OK) {
            return usable;
        }
        if (texture->is_staging()) {
            return write_staging(*texture, *region, data);
        }
        return transfer_upload(
            device, data, static_cast<size_t>(required), [&](VkCommandBuffer commands, VkBuffer upload) {
                const auto copy = buffer_image_region({0, 0}, *region);
                before_transfer(device, commands);
                device.api.vkCmdCopyBufferToImage(commands, upload, texture->image, VK_IMAGE_LAYOUT_GENERAL, 1, &copy);
                after_transfer(device, commands);
            });
    });
}
