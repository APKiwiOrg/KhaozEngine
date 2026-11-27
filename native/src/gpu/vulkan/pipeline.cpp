#include "pipeline.hpp"
#include "resource_layout.hpp"
#include "shaders.hpp"
#include "texture.hpp"

#include <bit>
#include <memory>

using namespace khaoz::vulkan;

namespace {

bool valid_desc(const khaoz_gpu_device& device, const khaoz_gpu_pipeline_desc& desc) {
    const auto& limits = device.adapter.properties.limits;
    return desc.shaders != nullptr && &desc.shaders->device == &device &&
           (desc.layout_count == 0 || desc.layouts != nullptr) && desc.layout_count <= limits.maxBoundDescriptorSets &&
           (desc.vertex_layout_count == 0 || desc.vertex_layouts != nullptr) &&
           desc.vertex_layout_count <= limits.maxVertexInputBindings &&
           (desc.color_count == 0 || (desc.color_formats != nullptr && desc.blends != nullptr)) &&
           desc.color_count <= device.capabilities.max_color_attachments &&
           (desc.color_count != 0 || desc.depth_format != nullptr) && std::has_single_bit(desc.sample_count) &&
           desc.sample_count <= 64;
}

khaoz_result set_outputs(khaoz_gpu_pipeline& pipeline, const khaoz_gpu_pipeline_desc& desc) {
    for (uint32_t i = 0; i < desc.color_count; ++i) {
        const auto format = image_format(desc.color_formats[i]);
        if ((format.aspects & VK_IMAGE_ASPECT_COLOR_BIT) == 0)
            return error(KHAOZ_ERROR_INVALID_ARGUMENT, "A Vulkan pipeline color output needs a colour format");
        pipeline.color_formats.push_back(format.vk);
    }
    if (desc.depth_format != nullptr) {
        const auto format = image_format(*desc.depth_format, true);
        if ((format.aspects & VK_IMAGE_ASPECT_DEPTH_BIT) == 0)
            return error(KHAOZ_ERROR_INVALID_ARGUMENT, "A Vulkan pipeline depth output needs a depth format");
        pipeline.depth_format = format.vk;
        if ((format.aspects & VK_IMAGE_ASPECT_STENCIL_BIT) != 0)
            pipeline.stencil_format = format.vk;
    }
    pipeline.samples = desc.sample_count;
    return KHAOZ_OK;
}

khaoz_result create_layout(khaoz_gpu_pipeline& pipeline, const khaoz_gpu_pipeline_desc& desc) {
    uint64_t uniforms = 0;
    std::vector<VkDescriptorSetLayout> set_layouts;
    auto& device = pipeline.device;
    for (uint32_t i = 0; i < desc.layout_count; ++i) {
        const auto* layout = desc.layouts[i];
        if (layout == nullptr || &layout->device != &device)
            return error(KHAOZ_ERROR_INVALID_ARGUMENT, "A Vulkan pipeline needs layouts from its own device");
        set_layouts.push_back(layout->handle);
        pipeline.set_bindings.push_back(layout->bindings);
        uniforms += layout->uniform_count;
    }
    if (uniforms > device.adapter.properties.limits.maxDescriptorSetUniformBuffersDynamic)
        return error(KHAOZ_ERROR_UNSUPPORTED, "The Vulkan pipeline exceeds the dynamic uniform limit");

    auto layout = info<VkPipelineLayoutCreateInfo>(VK_STRUCTURE_TYPE_PIPELINE_LAYOUT_CREATE_INFO);
    layout.setLayoutCount = desc.layout_count;
    layout.pSetLayouts = set_layouts.data();
    return check(create_handle(pipeline.layout,
                               [&](auto* pending) {
                                   return device.api.vkCreatePipelineLayout(device.handle, &layout, nullptr, pending);
                               }),
                 "Could not create a Vulkan pipeline layout");
}

} // namespace

khaoz_gpu_pipeline::~khaoz_gpu_pipeline() {
    if (handle != VK_NULL_HANDLE)
        device.api.vkDestroyPipeline(device.handle, handle, nullptr);
    if (layout != VK_NULL_HANDLE)
        device.api.vkDestroyPipelineLayout(device.handle, layout, nullptr);
}

khaoz_result khaoz_gpu_pipeline_create(khaoz_gpu_device* device, const khaoz_gpu_pipeline_desc* desc,
                                       khaoz_gpu_pipeline** out_pipeline) {
    return khaoz::abi::guard([&] {
        if (out_pipeline == nullptr)
            return error(KHAOZ_ERROR_INVALID_ARGUMENT, "A pipeline output is required");
        *out_pipeline = nullptr;
        if (device == nullptr || desc == nullptr || !valid_desc(*device, *desc))
            return error(KHAOZ_ERROR_INVALID_ARGUMENT, "A device and valid graphics pipeline description are required");
        const auto usable = device->require_usable();
        if (usable != KHAOZ_OK)
            return usable;

        auto pipeline = std::make_unique<khaoz_gpu_pipeline>(*device);
        auto result = set_outputs(*pipeline, *desc);
        if (result == KHAOZ_OK)
            result = create_layout(*pipeline, *desc);
        if (result == KHAOZ_OK)
            result = create_graphics_pipeline(*pipeline, *desc);
        if (result == KHAOZ_OK)
            *out_pipeline = pipeline.release();
        return result;
    });
}

khaoz_result khaoz_gpu_pipeline_destroy(khaoz_gpu_pipeline* pipeline) {
    return khaoz::abi::guard([&] { return destroy(pipeline); });
}
