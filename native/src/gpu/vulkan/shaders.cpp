#include "shaders.hpp"

#include <memory>

using namespace khaoz::vulkan;

namespace {

bool is_spirv(const uint32_t* words, uint32_t count) {
    return words != nullptr && count >= 5 && words[0] == 0x07230203;
}

khaoz_result create_module(const khaoz_gpu_device& device, const uint32_t* words, uint32_t count,
                           VkShaderModule& module) {
    auto desc = info<VkShaderModuleCreateInfo>(VK_STRUCTURE_TYPE_SHADER_MODULE_CREATE_INFO);
    desc.codeSize = size_t{count} * sizeof(uint32_t);
    desc.pCode = words;
    return check(create_handle(module,
                               [&](auto* pending) {
                                   return device.api.vkCreateShaderModule(device.handle, &desc, nullptr, pending);
                               }),
                 "Could not create a Vulkan shader module");
}

} // namespace

khaoz_gpu_shader_set::~khaoz_gpu_shader_set() {
    if (fragment != VK_NULL_HANDLE)
        device.api.vkDestroyShaderModule(device.handle, fragment, nullptr);
    if (vertex != VK_NULL_HANDLE)
        device.api.vkDestroyShaderModule(device.handle, vertex, nullptr);
}

khaoz_result khaoz_gpu_shader_set_create(khaoz_gpu_device* device, const uint32_t* vertex, uint32_t vertex_words,
                                         const uint32_t* fragment, uint32_t fragment_words,
                                         khaoz_gpu_shader_set** out_shaders) {
    return khaoz::abi::guard([&] {
        if (out_shaders == nullptr)
            return error(KHAOZ_ERROR_INVALID_ARGUMENT, "A shader set output is required");
        *out_shaders = nullptr;
        if (device == nullptr || !is_spirv(vertex, vertex_words) || !is_spirv(fragment, fragment_words))
            return error(KHAOZ_ERROR_INVALID_ARGUMENT, "A device and vertex and fragment SPIR-V are required");
        const auto usable = device->require_usable();
        if (usable != KHAOZ_OK)
            return usable;

        auto shaders = std::make_unique<khaoz_gpu_shader_set>(*device);
        auto result = create_module(*device, vertex, vertex_words, shaders->vertex);
        if (result == KHAOZ_OK)
            result = create_module(*device, fragment, fragment_words, shaders->fragment);
        if (result == KHAOZ_OK)
            *out_shaders = shaders.release();
        return result;
    });
}

khaoz_result khaoz_gpu_shader_set_destroy(khaoz_gpu_shader_set* shaders) {
    return khaoz::abi::guard([&] { return destroy(shaders); });
}
