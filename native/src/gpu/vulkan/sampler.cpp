#include "sampler.hpp"

#include <array>
#include <memory>

using namespace khaoz::vulkan;

khaoz_gpu_sampler::~khaoz_gpu_sampler() {
    if (handle != VK_NULL_HANDLE)
        device.api.vkDestroySampler(device.handle, handle, nullptr);
}

namespace {

constexpr std::array address_modes{VK_SAMPLER_ADDRESS_MODE_REPEAT, VK_SAMPLER_ADDRESS_MODE_MIRRORED_REPEAT,
                                   VK_SAMPLER_ADDRESS_MODE_CLAMP_TO_EDGE, VK_SAMPLER_ADDRESS_MODE_CLAMP_TO_BORDER};

bool valid_address(uint32_t address) { return static_cast<size_t>(address) < address_modes.size(); }

VkSamplerCreateInfo sampler_info(const khaoz_gpu_device& device, const khaoz_gpu_sampler_desc& desc) {
    const auto& limits = device.adapter.properties.limits;
    const bool point = desc.filter == KHAOZ_GPU_FILTER_POINT;
    auto info = khaoz::vulkan::info<VkSamplerCreateInfo>(VK_STRUCTURE_TYPE_SAMPLER_CREATE_INFO);
    info.minFilter = point ? VK_FILTER_NEAREST : VK_FILTER_LINEAR;
    info.magFilter = info.minFilter;
    info.mipmapMode = point ? VK_SAMPLER_MIPMAP_MODE_NEAREST : VK_SAMPLER_MIPMAP_MODE_LINEAR;
    info.addressModeU = address_modes.at(static_cast<size_t>(desc.address_u));
    info.addressModeV = address_modes.at(static_cast<size_t>(desc.address_v));
    info.addressModeW = address_modes.at(static_cast<size_t>(desc.address_w));
    info.borderColor = VK_BORDER_COLOR_FLOAT_TRANSPARENT_BLACK;
    info.maxLod = VK_LOD_CLAMP_NONE;
    info.anisotropyEnable = desc.filter == KHAOZ_GPU_FILTER_ANISOTROPIC && device.capabilities.sampler_anisotropy;
    const auto anisotropy =
        desc.maximum_anisotropy == 0 ? limits.maxSamplerAnisotropy : static_cast<float>(desc.maximum_anisotropy);
    info.maxAnisotropy = info.anisotropyEnable ? std::clamp(anisotropy, 1.0F, limits.maxSamplerAnisotropy) : 1.0F;
    // Portability devices need a separate feature for nonzero bias. The spike enables the extension's baseline.
    if (device.sampler_lod_bias()) {
        info.mipLodBias =
            std::clamp(static_cast<float>(desc.mip_lod_bias), -limits.maxSamplerLodBias, limits.maxSamplerLodBias);
    }
    return info;
}

} // namespace

khaoz_result khaoz_gpu_sampler_create(khaoz_gpu_device* device, const khaoz_gpu_sampler_desc* desc,
                                      khaoz_gpu_sampler** out_sampler) {
    return khaoz::abi::guard([&] {
        if (out_sampler == nullptr) {
            return error(KHAOZ_ERROR_INVALID_ARGUMENT, "A sampler output is required");
        }
        *out_sampler = nullptr;
        if (device == nullptr || desc == nullptr || desc->filter > KHAOZ_GPU_FILTER_ANISOTROPIC ||
            !valid_address(desc->address_u) || !valid_address(desc->address_v) || !valid_address(desc->address_w)) {
            return error(KHAOZ_ERROR_INVALID_ARGUMENT, "A device and valid sampler description are required");
        }
        const auto usable = device->require_usable();
        if (usable != KHAOZ_OK) {
            return usable;
        }
        auto sampler = std::make_unique<khaoz_gpu_sampler>(*device);
        const auto vk_desc = sampler_info(*device, *desc);
        const auto result =
            check(create_handle(
                      sampler->handle,
                      [&](auto* out) { return device->api.vkCreateSampler(device->handle, &vk_desc, nullptr, out); }),
                  "Could not create a Vulkan sampler");
        if (result == KHAOZ_OK) {
            *out_sampler = sampler.release();
        }
        return result;
    });
}

khaoz_result khaoz_gpu_sampler_destroy(khaoz_gpu_sampler* sampler) {
    return khaoz::abi::guard([&] { return destroy(sampler); });
}
