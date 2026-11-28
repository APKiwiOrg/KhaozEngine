#pragma once

#include "loader.hpp"
#include "validation.hpp"

#include <vk_mem_alloc.h>

#include <vector>

namespace khaoz::vulkan {
struct device_child;

struct adapter {
    VkPhysicalDevice handle{};
    VkPhysicalDeviceProperties properties{};
    VkPhysicalDeviceFeatures features{};
    VkQueueFamilyProperties queue{};
    uint32_t queue_family{};
    const char* rejection_reason{};
    bool llvmpipe{};
};

khaoz_result create_instance(khaoz_gpu_device& device, bool enable_validation, uint32_t& startup_status);
khaoz_result select_adapter(khaoz_gpu_device& device, const char* requested_name,
                            const khaoz_gpu_device_selection* selection, uint32_t& startup_status);
khaoz_gpu_capabilities read_capabilities(const khaoz_gpu_device& device);
khaoz_result create_allocator(khaoz_gpu_device& device);
} // namespace khaoz::vulkan

struct khaoz_gpu_device {
    khaoz::vulkan::loader loader;
    khaoz::vulkan::validation validation;
    PFN_vkGetInstanceProcAddr get_instance_proc{};
    VkInstance instance{};
    VolkInstanceTable instance_api{};
    khaoz::vulkan::adapter adapter{};
    VkDevice handle{};
    bool portability_subset{};
    VolkDeviceTable api{};
    VkQueue queue{};
    VkCommandPool command_pool{};
    VmaAllocator allocator{};
    khaoz_gpu_capabilities capabilities{};
    khaoz::vulkan::device_child* children{};
    uint32_t collectable{};
    bool work_pending{};
    bool closing{};
    khaoz_result queue_failure{KHAOZ_OK};

    [[nodiscard]] bool sampler_lod_bias() const noexcept { return !portability_subset; }

    ~khaoz_gpu_device();
    khaoz_result require_usable() const noexcept;
    khaoz_result wait_for_idle() noexcept;
    void collect() noexcept;
    khaoz_result close() noexcept;
    void close_instance() noexcept;
};

namespace khaoz::vulkan {

struct device_child {
    khaoz_gpu_device& device;
    device_child* next{};
    device_child* previous{};
    // Command lists, framebuffers and resource sets that still reference this child.
    // Use bookkeeping does not change the resource, so const references can take a use.
    mutable uint32_t uses{};
    // The holder that took the newest use, so repeated binds in one recording take one use.
    mutable const void* holder{};
    bool retired{};

    explicit device_child(khaoz_gpu_device& owner) noexcept;
    virtual ~device_child();
    device_child(const device_child&) = delete;
    device_child& operator=(const device_child&) = delete;

    // The queue drained, so no submitted work still uses anything this child references.
    virtual void drained() noexcept {}
};

// Uses one holder takes on other children of its device. Releasing them never deletes a child.
// The device collects retired children with no uses once no submitted work is pending.
struct held_uses {
    std::vector<const device_child*> children;

    void add(const device_child& child, const void* holder) {
        if (child.holder == holder)
            return;
        children.push_back(&child);
        ++child.uses;
        child.holder = holder;
    }

    void release(const void* holder) noexcept {
        for (const auto* child : children) {
            if (child->holder == holder)
                child->holder = nullptr;
            if (--child->uses == 0 && child->retired)
                ++child->device.collectable;
        }
        children.clear();
    }
};

// The child stays on the device's list and is no longer owned by the caller. The device deletes it once no
// submitted work is pending and nothing holds a use on it.
inline void retain_for_teardown(device_child* child) noexcept {
    child->retired = true;
    if (child->uses == 0)
        ++child->device.collectable;
}

template <class T> khaoz_result destroy(T* child) noexcept {
    if (child == nullptr) {
        return KHAOZ_OK;
    }
    auto& device = child->device;
    const khaoz_result result = device.wait_for_idle();
    // An inconclusive wait or a holder that still references the child defers its deletion.
    if (!device.work_pending && child->uses == 0) {
        delete child;
    } else {
        retain_for_teardown(child);
    }
    device.collect();
    return result;
}

} // namespace khaoz::vulkan
