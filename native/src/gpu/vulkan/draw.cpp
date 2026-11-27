#include "buffer.hpp"
#include "commands.hpp"
#include "pipeline.hpp"
#include "resource_set.hpp"

using namespace khaoz::vulkan;

namespace {

bool match_outputs(const khaoz_gpu_pipeline& pipeline, const framebuffer_targets& target) {
    return pipeline.samples == target.samples && pipeline.depth_format == target.depth.format &&
           std::equal(pipeline.color_formats.begin(), pipeline.color_formats.end(), target.colors.begin(),
                      target.colors.end(), [](VkFormat format, const auto& color) { return color.format == format; });
}

bool has_attachment(const framebuffer_targets& target, VkImage image) {
    return target.depth.image == image || std::any_of(target.colors.begin(), target.colors.end(),
                                                      [image](const auto& color) { return color.image == image; });
}

bool valid_uniform_offset(const uniform_window& uniform, uint32_t caller, const VkPhysicalDeviceLimits& limits) {
    const auto alignment = limits.minUniformBufferOffsetAlignment;
    const auto offset = uint64_t{uniform.offset} + (uniform.applies_caller_offset ? caller : 0U);
    return offset + uniform.range <= uniform.capacity && (alignment == 0 || offset % alignment == 0);
}

bool matches_layout(const recorded_set& binding, const std::vector<VkDescriptorSetLayoutBinding>& expected) {
    if (binding.set == nullptr)
        return false;
    const auto& actual = binding.set->bindings;
    return std::equal(actual.begin(), actual.end(), expected.begin(), expected.end(),
                      [](const auto& left, const auto& right) {
                          return left.descriptorType == right.descriptorType && left.stageFlags == right.stageFlags;
                      });
}

khaoz_result prepare_draw(khaoz_gpu_command_list& commands) {
    const auto* pipeline = commands.pipeline;
    const auto& target = commands.rendering.framebuffer;
    if (pipeline == nullptr || !commands.rendering.bound() || !match_outputs(*pipeline, target))
        return error(KHAOZ_ERROR_INVALID_ARGUMENT, "A graphics draw needs matching pipeline and framebuffer outputs");

    if (commands.vertices.size() < pipeline->vertex_bindings ||
        std::find(commands.vertices.begin(), commands.vertices.begin() + pipeline->vertex_bindings, uint8_t{0}) !=
            commands.vertices.begin() + pipeline->vertex_bindings)
        return error(KHAOZ_ERROR_INVALID_ARGUMENT, "The draw is missing a vertex buffer");

    const auto slots = std::min(commands.sets.size(), pipeline->set_bindings.size());
    bool writable_storage = false;
    size_t offset_count = 0;
    const auto& limits = commands.device.adapter.properties.limits;
    for (size_t i = 0; i < slots; ++i) {
        const auto& binding = commands.sets.at(i);
        if (!matches_layout(binding, pipeline->set_bindings.at(i)))
            continue;

        const auto& set = *binding.set;
        if (std::any_of(set.images.begin(), set.images.end(),
                        [&](VkImage image) { return has_attachment(target, image); }))
            return error(KHAOZ_ERROR_UNSUPPORTED, "A draw cannot use its attachment as a shader resource");
        for (const auto& uniform : set.uniforms) {
            if (!valid_uniform_offset(uniform, binding.dynamic_offset, limits))
                return error(KHAOZ_ERROR_INVALID_ARGUMENT, "A dynamic uniform offset is out of bounds or misaligned");
        }
        offset_count = std::max(offset_count, set.uniforms.size());
        writable_storage |= set.writable_storage;
    }
    commands.dynamic_offsets.reserve(offset_count);

    if (commands.rendering.wrote_storage)
        commands.rendering.finish(commands);
    commands.rendering.prepare_draw(commands);
    commands.rendering.wrote_storage = writable_storage;
    for (size_t i = 0; i < slots; ++i) {
        const auto& binding = commands.sets.at(i);
        if (!matches_layout(binding, pipeline->set_bindings.at(i)))
            continue;

        commands.dynamic_offsets.clear();
        for (const auto& uniform : binding.set->uniforms)
            commands.dynamic_offsets.push_back(uniform.offset +
                                               (uniform.applies_caller_offset ? binding.dynamic_offset : 0U));
        commands.device.api.vkCmdBindDescriptorSets(commands.handle, VK_PIPELINE_BIND_POINT_GRAPHICS, pipeline->layout,
                                                    static_cast<uint32_t>(i), 1, &binding.set->handle,
                                                    static_cast<uint32_t>(commands.dynamic_offsets.size()),
                                                    commands.dynamic_offsets.data());
    }
    return KHAOZ_OK;
}

} // namespace

khaoz_result khaoz_gpu_command_list_set_pipeline(khaoz_gpu_command_list* commands, khaoz_gpu_pipeline* pipeline) {
    return khaoz::abi::guard([&] {
        const auto usable = require_recording(commands);
        if (usable != KHAOZ_OK)
            return usable;
        if (pipeline == nullptr || &pipeline->device != &commands->device)
            return error(KHAOZ_ERROR_INVALID_ARGUMENT, "A graphics pipeline from this device is required");
        commands->device.api.vkCmdBindPipeline(commands->handle, VK_PIPELINE_BIND_POINT_GRAPHICS, pipeline->handle);
        commands->pipeline = pipeline;
        return KHAOZ_OK;
    });
}

khaoz_result khaoz_gpu_command_list_set_resource_set(khaoz_gpu_command_list* commands, uint32_t slot,
                                                     khaoz_gpu_resource_set* set, uint32_t dynamic_offset) {
    return khaoz::abi::guard([&] {
        const auto usable = require_recording(commands);
        if (usable != KHAOZ_OK)
            return usable;
        if (slot >= commands->device.adapter.properties.limits.maxBoundDescriptorSets ||
            (set != nullptr && &set->device != &commands->device))
            return error(KHAOZ_ERROR_INVALID_ARGUMENT,
                         "A valid set slot and a resource set from this device are required");
        if (set != nullptr && dynamic_offset != 0 &&
            std::none_of(set->uniforms.begin(), set->uniforms.end(),
                         [](const auto& uniform) { return uniform.applies_caller_offset; }))
            return error(KHAOZ_ERROR_INVALID_ARGUMENT, "The resource set has no declared dynamic element");
        if (slot >= commands->sets.size())
            commands->sets.resize(size_t{slot} + 1);
        commands->sets.at(slot) = {set, dynamic_offset};
        return KHAOZ_OK;
    });
}

khaoz_result khaoz_gpu_command_list_set_vertex_buffer(khaoz_gpu_command_list* commands, uint32_t slot,
                                                      khaoz_gpu_buffer* buffer, uint32_t offset) {
    return khaoz::abi::guard([&] {
        const auto usable = require_recording(commands);
        if (usable != KHAOZ_OK)
            return usable;
        if (buffer == nullptr || &buffer->device != &commands->device || buffer->staging ||
            (buffer->usage & KHAOZ_GPU_BUFFER_VERTEX) == 0 || offset >= buffer->size ||
            slot >= commands->device.adapter.properties.limits.maxVertexInputBindings)
            return error(KHAOZ_ERROR_INVALID_ARGUMENT,
                         "The Vulkan vertex binding has an invalid buffer, slot or offset");
        if (slot >= commands->vertices.size())
            commands->vertices.resize(size_t{slot} + 1);
        commands->vertices.at(slot) = 1;
        const VkDeviceSize vk_offset = offset;
        commands->device.api.vkCmdBindVertexBuffers(commands->handle, slot, 1, &buffer->storage.handle, &vk_offset);
        return KHAOZ_OK;
    });
}

khaoz_result khaoz_gpu_command_list_set_index_buffer(khaoz_gpu_command_list* commands, khaoz_gpu_buffer* buffer,
                                                     uint32_t format) {
    return khaoz::abi::guard([&] {
        const auto usable = require_recording(commands);
        if (usable != KHAOZ_OK)
            return usable;
        if (buffer == nullptr || &buffer->device != &commands->device || buffer->staging ||
            (buffer->usage & KHAOZ_GPU_BUFFER_INDEX) == 0 || format > 1)
            return error(KHAOZ_ERROR_INVALID_ARGUMENT, "The Vulkan index binding has an invalid buffer or format");
        const auto type = format == 0 ? VK_INDEX_TYPE_UINT16 : VK_INDEX_TYPE_UINT32;
        commands->device.api.vkCmdBindIndexBuffer(commands->handle, buffer->storage.handle, 0, type);
        commands->index_bound = true;
        commands->index_capacity = buffer->size / (format == 0 ? 2U : 4U);
        return KHAOZ_OK;
    });
}

khaoz_result khaoz_gpu_command_list_draw(khaoz_gpu_command_list* commands, uint32_t vertex_count,
                                         uint32_t instance_count, uint32_t vertex_start, uint32_t instance_start) {
    return khaoz::abi::guard([&] {
        const auto usable = require_recording(commands);
        if (usable != KHAOZ_OK)
            return usable;
        const auto result = prepare_draw(*commands);
        if (result == KHAOZ_OK)
            commands->device.api.vkCmdDraw(commands->handle, vertex_count, instance_count, vertex_start,
                                           instance_start);
        return result;
    });
}

khaoz_result khaoz_gpu_command_list_draw_indexed(khaoz_gpu_command_list* commands, uint32_t index_count,
                                                 uint32_t instance_count, uint32_t index_start, int32_t vertex_offset,
                                                 uint32_t instance_start) {
    return khaoz::abi::guard([&] {
        const auto usable = require_recording(commands);
        if (usable != KHAOZ_OK)
            return usable;
        if (!commands->index_bound || !fits(commands->index_capacity, index_start, index_count))
            return error(KHAOZ_ERROR_INVALID_ARGUMENT, "The indexed draw needs a bound buffer holding its index range");
        const auto result = prepare_draw(*commands);
        if (result == KHAOZ_OK)
            commands->device.api.vkCmdDrawIndexed(commands->handle, index_count, instance_count, index_start,
                                                  vertex_offset, instance_start);
        return result;
    });
}
