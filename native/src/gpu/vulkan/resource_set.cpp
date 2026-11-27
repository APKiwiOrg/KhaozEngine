#include "resource_set.hpp"
#include "buffer.hpp"
#include "sampler.hpp"
#include "texture.hpp"

#include <array>
#include <memory>

using namespace khaoz::vulkan;

namespace {

bool from_device(const device_child* resource, const khaoz_gpu_device& device) {
    return resource != nullptr && &resource->device == &device;
}

bool write_buffer(khaoz_gpu_resource_set& set, resource_set_shape& shape, const khaoz_gpu_resource_element& element,
                  const khaoz_gpu_resource_binding& resource, VkDescriptorBufferInfo& out) {
    const auto* buffer = resource.buffer;
    if (!from_device(buffer, set.device) || buffer->staging || resource.range == 0 ||
        !fits(buffer->size, resource.offset, resource.range))
        return false;

    const auto& limits = set.device.adapter.properties.limits;
    const bool uniform = element.kind == KHAOZ_GPU_RESOURCE_UNIFORM;
    constexpr auto storage = uint32_t{KHAOZ_GPU_BUFFER_STORAGE_READ_ONLY} | KHAOZ_GPU_BUFFER_STORAGE_READ_WRITE;
    const uint32_t usage = uniform ? uint32_t{KHAOZ_GPU_BUFFER_UNIFORM} : storage;
    const auto alignment = limits.minStorageBufferOffsetAlignment;
    const auto max_range = uniform ? limits.maxUniformBufferRange : limits.maxStorageBufferRange;
    if ((buffer->usage & usage) == 0 || resource.range > max_range ||
        (!uniform && alignment != 0 && resource.offset % alignment != 0))
        return false;

    set.resources.add(*buffer, &set);
    if (uniform) {
        shape.uniforms.push_back({resource.offset, resource.range, buffer->size, element.dynamic_offset != 0});
    }
    shape.writable_storage |= element.kind == KHAOZ_GPU_RESOURCE_STORAGE_READ_WRITE;
    out = {buffer->storage.handle, uniform ? 0U : resource.offset, resource.range};
    return true;
}

bool write_image(khaoz_gpu_resource_set& set, resource_set_shape& shape, const khaoz_gpu_resource_element& element,
                 const khaoz_gpu_resource_binding& resource, VkDescriptorImageInfo& out) {
    if (element.kind == KHAOZ_GPU_RESOURCE_SAMPLER) {
        if (!from_device(resource.sampler, set.device))
            return false;
        set.resources.add(*resource.sampler, &set);
        out.sampler = resource.sampler->handle;
        return true;
    }
    const auto* texture = resource.texture;
    if (!from_device(texture, set.device))
        return false;
    const bool sampled = element.kind == KHAOZ_GPU_RESOURCE_SAMPLED_IMAGE;
    const auto view = sampled ? texture->sampled : texture->storage;
    if (view == VK_NULL_HANDLE)
        return false;
    set.resources.add(*texture, &set);
    shape.images.push_back(texture->image);
    shape.writable_storage |= !sampled;
    // Task 4 establishes GENERAL as the resting layout for every image.
    out = {VK_NULL_HANDLE, view, VK_IMAGE_LAYOUT_GENERAL};
    return true;
}

khaoz_result allocate_set(khaoz_gpu_resource_set& set, resource_set_shape& shape, VkDescriptorSetLayout layout,
                          const std::vector<VkDescriptorPoolSize>& sizes) {
    auto& device = set.device;
    auto pool = info<VkDescriptorPoolCreateInfo>(VK_STRUCTURE_TYPE_DESCRIPTOR_POOL_CREATE_INFO);
    pool.maxSets = 1;
    pool.poolSizeCount = static_cast<uint32_t>(sizes.size());
    pool.pPoolSizes = sizes.data();
    const auto result =
        check(create_handle(set.pool,
                            [&](auto* pending) {
                                return device.api.vkCreateDescriptorPool(device.handle, &pool, nullptr, pending);
                            }),
              "Could not allocate a Vulkan descriptor pool");
    if (result != KHAOZ_OK)
        return result;

    auto allocate = info<VkDescriptorSetAllocateInfo>(VK_STRUCTURE_TYPE_DESCRIPTOR_SET_ALLOCATE_INFO);
    allocate.descriptorPool = set.pool;
    allocate.descriptorSetCount = 1;
    allocate.pSetLayouts = &layout;
    return check(create_handle(shape.handle,
                               [&](auto* pending) {
                                   return device.api.vkAllocateDescriptorSets(device.handle, &allocate, pending);
                               }),
                 "Could not allocate a Vulkan descriptor set");
}

khaoz_result build(khaoz_gpu_resource_set& set, const khaoz_gpu_resource_layout& layout,
                   const khaoz_gpu_resource_binding* resources, uint32_t count) {
    std::vector<VkDescriptorBufferInfo> buffers(count);
    std::vector<VkDescriptorImageInfo> images(count);
    std::vector<VkWriteDescriptorSet> writes(count);
    std::vector<VkDescriptorPoolSize> sizes;
    resource_set_shape shape;
    for (uint32_t i = 0; i < count; ++i) {
        const auto& element = layout.elements.at(i);
        const auto type = layout.bindings.at(i).descriptorType;
        auto& write = writes.at(i);
        write.sType = VK_STRUCTURE_TYPE_WRITE_DESCRIPTOR_SET;
        write.dstBinding = i;
        write.descriptorCount = 1;
        write.descriptorType = type;
        if (element.kind <= KHAOZ_GPU_RESOURCE_STORAGE_READ_WRITE) {
            if (!write_buffer(set, shape, element, resources[i], buffers.at(i)))
                return error(KHAOZ_ERROR_INVALID_ARGUMENT, "Invalid Vulkan buffer descriptor");
            write.pBufferInfo = &buffers.at(i);
        } else {
            if (!write_image(set, shape, element, resources[i], images.at(i)))
                return error(KHAOZ_ERROR_INVALID_ARGUMENT, "Invalid Vulkan image or sampler descriptor");
            write.pImageInfo = &images.at(i);
        }
        auto size = std::find_if(sizes.begin(), sizes.end(), [type](const auto& entry) { return entry.type == type; });
        if (size == sizes.end())
            sizes.push_back({type, 1});
        else
            ++size->descriptorCount;
    }

    const auto result = allocate_set(set, shape, layout.handle, sizes);
    if (result != KHAOZ_OK)
        return result;
    for (auto& write : writes)
        write.dstSet = shape.handle;
    shape.bindings = layout.bindings;
    auto published = std::make_shared<const resource_set_shape>(std::move(shape));
    if (count != 0)
        set.device.api.vkUpdateDescriptorSets(set.device.handle, count, writes.data(), 0, nullptr);
    set.shape = std::move(published);
    return KHAOZ_OK;
}

} // namespace

khaoz_gpu_resource_set::~khaoz_gpu_resource_set() {
    if (!device.closing)
        resources.release(this);
    if (pool != VK_NULL_HANDLE)
        device.api.vkDestroyDescriptorPool(device.handle, pool, nullptr);
}

khaoz_result khaoz_gpu_resource_set_create(khaoz_gpu_device* device, khaoz_gpu_resource_layout* layout,
                                           const khaoz_gpu_resource_binding* resources, uint32_t count,
                                           khaoz_gpu_resource_set** out_set) {
    return khaoz::abi::guard([&] {
        if (out_set == nullptr)
            return error(KHAOZ_ERROR_INVALID_ARGUMENT, "A resource set output is required");
        *out_set = nullptr;
        if (device == nullptr || !from_device(layout, *device) || count != layout->elements.size() ||
            (count != 0 && resources == nullptr))
            return error(KHAOZ_ERROR_INVALID_ARGUMENT, "A layout and one resource per binding are required");
        const auto usable = device->require_usable();
        if (usable != KHAOZ_OK)
            return usable;

        auto set = std::make_unique<khaoz_gpu_resource_set>(*device);
        const auto result = build(*set, *layout, resources, count);
        if (result == KHAOZ_OK)
            *out_set = set.release();
        return result;
    });
}

khaoz_result khaoz_gpu_resource_set_destroy(khaoz_gpu_resource_set* set) {
    return khaoz::abi::guard([&] { return destroy(set); });
}
