#pragma once

#include "loader.hpp"
#include "validation.hpp"

#include <vk_mem_alloc.h>

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
    bool work_pending{};
    khaoz_result queue_failure{KHAOZ_OK};

    [[nodiscard]] bool sampler_lod_bias() const noexcept { return !portability_subset; }

    ~khaoz_gpu_device();
    khaoz_result require_usable() const noexcept;
    khaoz_result wait_for_idle() noexcept;
    khaoz_result close() noexcept;
    void close_instance() noexcept;
};

namespace khaoz::vulkan {

struct device_child {
    khaoz_gpu_device& device;
    device_child* next{};
    device_child* previous{};
    bool retired{};

    explicit device_child(khaoz_gpu_device& owner) noexcept;
    virtual ~device_child();
    device_child(const device_child&) = delete;
    device_child& operator=(const device_child&) = delete;
};

// The child stays on the device's list and is no longer owned by the caller.
inline void retain_for_teardown(device_child* child) noexcept { child->retired = true; }

template <class T> khaoz_result destroy(T* child) noexcept {
    if (child == nullptr) {
        return KHAOZ_OK;
    }
    const khaoz_result result = child->device.wait_for_idle();
    if (child->device.work_pending) {
        retain_for_teardown(child);
    } else {
        delete child;
    }
    return result;
}

} // namespace khaoz::vulkan
