#include "resource_layout.hpp"

#include <array>
#include <memory>

using namespace khaoz::vulkan;

namespace {

constexpr std::array descriptor_types{
    VK_DESCRIPTOR_TYPE_UNIFORM_BUFFER_DYNAMIC, VK_DESCRIPTOR_TYPE_STORAGE_BUFFER, VK_DESCRIPTOR_TYPE_STORAGE_BUFFER,
    VK_DESCRIPTOR_TYPE_SAMPLED_IMAGE,          VK_DESCRIPTOR_TYPE_STORAGE_IMAGE,  VK_DESCRIPTOR_TYPE_SAMPLER,
};
static_assert(VK_SHADER_STAGE_VERTEX_BIT == 1 && VK_SHADER_STAGE_FRAGMENT_BIT == 16 &&
              VK_SHADER_STAGE_COMPUTE_BIT == 32);

khaoz_result build(khaoz_gpu_resource_layout& layout, const khaoz_gpu_resource_element* elements, uint32_t count) {
    for (uint32_t i = 0; i < count; ++i) {
        const auto& element = elements[i];
        const bool uniform = element.kind == KHAOZ_GPU_RESOURCE_UNIFORM;
        if (element.kind >= descriptor_types.size() || (element.stages & ~63U) != 0 ||
            (element.dynamic_offset != 0 && !uniform)) {
            return error(KHAOZ_ERROR_INVALID_ARGUMENT, "Invalid Vulkan descriptor kind, stages or dynamic offset");
        }
        layout.uniform_count += uniform ? 1U : 0U;
        layout.elements.push_back(element);
        // Each declared element is one Vulkan binding at its declaration index.
        layout.bindings.push_back({i, descriptor_types.at(element.kind), 1, element.stages, nullptr});
    }

    auto desc = info<VkDescriptorSetLayoutCreateInfo>(VK_STRUCTURE_TYPE_DESCRIPTOR_SET_LAYOUT_CREATE_INFO);
    desc.bindingCount = count;
    desc.pBindings = layout.bindings.data();
    auto& device = layout.device;
    return check(create_handle(layout.handle,
                               [&](auto* pending) {
                                   return device.api.vkCreateDescriptorSetLayout(device.handle, &desc, nullptr,
                                                                                 pending);
                               }),
                 "Could not create a Vulkan descriptor set layout");
}

} // namespace

khaoz_gpu_resource_layout::~khaoz_gpu_resource_layout() {
    if (handle != VK_NULL_HANDLE)
        device.api.vkDestroyDescriptorSetLayout(device.handle, handle, nullptr);
}

khaoz_result khaoz_gpu_resource_layout_create(khaoz_gpu_device* device, const khaoz_gpu_resource_element* elements,
                                              uint32_t count, khaoz_gpu_resource_layout** out_layout) {
    return khaoz::abi::guard([&] {
        if (out_layout == nullptr)
            return error(KHAOZ_ERROR_INVALID_ARGUMENT, "A resource layout output is required");
        *out_layout = nullptr;
        if (device == nullptr || (count != 0 && elements == nullptr))
            return error(KHAOZ_ERROR_INVALID_ARGUMENT, "A device and descriptor elements are required");
        const auto usable = device->require_usable();
        if (usable != KHAOZ_OK)
            return usable;

        auto layout = std::make_unique<khaoz_gpu_resource_layout>(*device);
        const auto result = build(*layout, elements, count);
        if (result == KHAOZ_OK)
            *out_layout = layout.release();
        return result;
    });
}

khaoz_result khaoz_gpu_resource_layout_destroy(khaoz_gpu_resource_layout* layout) {
    return khaoz::abi::guard([&] { return destroy(layout); });
}
