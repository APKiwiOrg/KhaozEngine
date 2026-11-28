#include "device.hpp"

#include <bit>
#include <cmath>
#include <limits>

namespace khaoz::vulkan {
namespace {

VkSampleCountFlags format_counts(const khaoz_gpu_device& device, VkFormat format, VkImageUsageFlags attachment) {
    VkImageFormatProperties properties{};
    const auto result = device.instance_api.vkGetPhysicalDeviceImageFormatProperties(
        device.adapter.handle, format, VK_IMAGE_TYPE_2D, VK_IMAGE_TILING_OPTIMAL,
        VK_IMAGE_USAGE_SAMPLED_BIT | attachment, 0, &properties);
    return result == VK_SUCCESS ? properties.sampleCounts : static_cast<VkSampleCountFlags>(VK_SAMPLE_COUNT_1_BIT);
}

uint32_t sample_counts(const khaoz_gpu_device& device) {
    constexpr VkSampleCountFlags known = VkSampleCountFlags{VK_SAMPLE_COUNT_1_BIT} | VK_SAMPLE_COUNT_2_BIT |
                                         VK_SAMPLE_COUNT_4_BIT | VK_SAMPLE_COUNT_8_BIT | VK_SAMPLE_COUNT_16_BIT |
                                         VK_SAMPLE_COUNT_32_BIT;
    const auto& limits = device.adapter.properties.limits;
    const auto common =
        known & limits.framebufferColorSampleCounts & limits.framebufferDepthSampleCounts &
        limits.framebufferStencilSampleCounts &
        format_counts(device, VK_FORMAT_R8G8B8A8_UNORM, VK_IMAGE_USAGE_COLOR_ATTACHMENT_BIT) &
        format_counts(device, VK_FORMAT_R32_SFLOAT, VK_IMAGE_USAGE_COLOR_ATTACHMENT_BIT) &
        format_counts(device, VK_FORMAT_D32_SFLOAT_S8_UINT, VK_IMAGE_USAGE_DEPTH_STENCIL_ATTACHMENT_BIT);
    return common | VK_SAMPLE_COUNT_1_BIT;
}

} // namespace

khaoz_gpu_capabilities read_capabilities(const khaoz_gpu_device& device) {
    const auto& adapter = device.adapter;
    const auto& limits = adapter.properties.limits;
    const auto& api = device.instance_api;
    VkFormatProperties shadow{};
    api.vkGetPhysicalDeviceFormatProperties(adapter.handle, VK_FORMAT_R32_SFLOAT, &shadow);
    constexpr VkFormatFeatureFlags shadow_flags =
        VK_FORMAT_FEATURE_COLOR_ATTACHMENT_BIT | VK_FORMAT_FEATURE_SAMPLED_IMAGE_BIT;

    auto floats =
        info<VkPhysicalDeviceFloatControlsProperties>(VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_FLOAT_CONTROLS_PROPERTIES);
    auto properties = info<VkPhysicalDeviceProperties2>(VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_PROPERTIES_2);
    properties.pNext = &floats;
    api.vkGetPhysicalDeviceProperties2(adapter.handle, &properties);

    khaoz_gpu_capabilities caps{};
    static_assert(sizeof(caps.device_name) == sizeof(adapter.properties.deviceName));
    std::memcpy(caps.device_name, adapter.properties.deviceName, sizeof(caps.device_name));
    caps.device_name[sizeof(caps.device_name) - 1] = '\0';
    caps.clip_space_y_inverted = 0;
    caps.depth_range_zero_to_one = 1;
    caps.sampler_anisotropy = adapter.features.samplerAnisotropy;
    caps.sampler_lod_bias = device.sampler_lod_bias() ? 1U : 0U;
    caps.supported_msaa_sample_counts = sample_counts(device);
    caps.max_msaa_sample_count = std::bit_floor(caps.supported_msaa_sample_counts);
    caps.supports_shadow_maps = (shadow.optimalTilingFeatures & shadow_flags) == shadow_flags;
    caps.supports_compute = 1;
    caps.max_storage_buffer_range_bytes = limits.maxStorageBufferRange;
    caps.max_render_texture_2d_dimension =
        std::min({limits.maxImageDimension2D, limits.maxFramebufferWidth, limits.maxFramebufferHeight});
    caps.supports_timestamps = adapter.queue.timestampValidBits >= 1 && adapter.queue.timestampValidBits <= 64 &&
                               std::isfinite(limits.timestampPeriod) && limits.timestampPeriod > 0.0F;
    caps.supports_shader_float_controls = floats.shaderSignedZeroInfNanPreserveFloat32;
    constexpr auto int_max = static_cast<uint32_t>(std::numeric_limits<int32_t>::max());
    caps.max_color_attachments =
        std::clamp(std::min(limits.maxColorAttachments, limits.maxFragmentOutputAttachments), 4U, int_max);
    caps.supports_completion_fences = 1;
    caps.max_fragment_storage_buffers = std::min(limits.maxPerStageDescriptorStorageBuffers, int_max);
    caps.max_dynamic_uniform_buffers = device.adapter.properties.limits.maxDescriptorSetUniformBuffersDynamic;
    caps.uniform_offset_alignment = device.adapter.properties.limits.minUniformBufferOffsetAlignment;
    return caps;
}

} // namespace khaoz::vulkan
