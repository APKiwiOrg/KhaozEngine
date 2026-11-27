#pragma once

#include "resource_layout.hpp"

#include <memory>

namespace khaoz::vulkan {

struct uniform_window {
    uint32_t offset;
    uint32_t range;
    uint32_t capacity;
    bool applies_caller_offset;
};

// What a draw reads from a bound set. Recordings share this immutable value, the way the managed backend binds
// a set's plain data, so draw checks never read through a pointer to a destroyed set.
struct resource_set_shape {
    VkDescriptorSet handle{};
    std::vector<VkDescriptorSetLayoutBinding> bindings;
    std::vector<uniform_window> uniforms;
    std::vector<VkImage> images;
    bool writable_storage{};
};

} // namespace khaoz::vulkan

struct khaoz_gpu_resource_set final : khaoz::vulkan::device_child {
    VkDescriptorPool pool{};
    std::shared_ptr<const khaoz::vulkan::resource_set_shape> shape;
    // The buffers, textures and samplers its descriptors name stay alive while the set does.
    khaoz::vulkan::held_uses resources;

    explicit khaoz_gpu_resource_set(khaoz_gpu_device& owner) noexcept : device_child(owner) {}
    ~khaoz_gpu_resource_set() override;
};
