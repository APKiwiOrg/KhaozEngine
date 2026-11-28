#pragma once

#include "device.hpp"

struct khaoz_gpu_pipeline final : khaoz::vulkan::device_child {
    VkPipeline handle{};
    VkPipelineLayout layout{};
    std::vector<std::vector<VkDescriptorSetLayoutBinding>> set_bindings;
    std::vector<VkFormat> color_formats;
    VkFormat depth_format{};
    VkFormat stencil_format{};
    uint32_t samples{};
    uint32_t vertex_bindings{};

    explicit khaoz_gpu_pipeline(khaoz_gpu_device& owner) noexcept : device_child(owner) {}
    ~khaoz_gpu_pipeline() override;
};

namespace khaoz::vulkan {
khaoz_result create_graphics_pipeline(khaoz_gpu_pipeline& pipeline, const khaoz_gpu_pipeline_desc& desc);
} // namespace khaoz::vulkan
