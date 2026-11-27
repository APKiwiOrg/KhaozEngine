#include "device.hpp"

#include <memory>
#include <mutex>

using namespace khaoz::vulkan;

namespace {

// Volk uses process-global pointers during table population. Calls on a live device use only its own tables.
std::mutex bootstrap_gate;

struct reset_volk {
    ~reset_volk() { volkFinalize(); }
};

khaoz_result create_logical_device(khaoz_gpu_device& device) {
    const auto& adapter = device.adapter;
    auto queue = info<VkDeviceQueueCreateInfo>(VK_STRUCTURE_TYPE_DEVICE_QUEUE_CREATE_INFO);
    constexpr float priority = 1.0F;
    queue.queueFamilyIndex = adapter.queue_family;
    queue.queueCount = 1;
    queue.pQueuePriorities = &priority;

    constexpr const char* portability_subset = "VK_KHR_portability_subset";
    std::vector<VkExtensionProperties> extensions;
    const auto enumerated = enumerate(extensions, [&device](auto* count, auto* properties) {
        return device.instance_api.vkEnumerateDeviceExtensionProperties(device.adapter.handle, nullptr, count,
                                                                        properties);
    });
    if (enumerated != VK_SUCCESS) {
        return check(enumerated, "Could not enumerate Vulkan device extensions");
    }

    auto features13 = info<VkPhysicalDeviceVulkan13Features>(VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_VULKAN_1_3_FEATURES);
    features13.dynamicRendering = VK_TRUE;
    features13.synchronization2 = VK_TRUE;
    auto features12 = info<VkPhysicalDeviceVulkan12Features>(VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_VULKAN_1_2_FEATURES);
    features12.pNext = &features13;
    features12.timelineSemaphore = VK_TRUE;
    VkPhysicalDeviceFeatures features{};
    features.samplerAnisotropy = adapter.features.samplerAnisotropy;

    auto desc = info<VkDeviceCreateInfo>(VK_STRUCTURE_TYPE_DEVICE_CREATE_INFO);
    desc.pNext = &features12;
    desc.queueCreateInfoCount = 1;
    desc.pQueueCreateInfos = &queue;
    desc.pEnabledFeatures = &features;
    device.portability_subset = has_extension(extensions, portability_subset);
    if (device.portability_subset) {
        desc.enabledExtensionCount = 1;
        desc.ppEnabledExtensionNames = &portability_subset;
    }
    const auto result = create_handle(device.handle, [&](auto* out) {
        return device.instance_api.vkCreateDevice(adapter.handle, &desc, nullptr, out);
    });
    if (result != VK_SUCCESS) {
        return check(result, "Could not create the Vulkan logical device");
    }

    volkLoadDeviceTable(&device.api, device.handle);
    device.api.vkGetDeviceQueue(device.handle, adapter.queue_family, 0, &device.queue);
    auto pool = info<VkCommandPoolCreateInfo>(VK_STRUCTURE_TYPE_COMMAND_POOL_CREATE_INFO);
    pool.queueFamilyIndex = adapter.queue_family;
    pool.flags = VK_COMMAND_POOL_CREATE_RESET_COMMAND_BUFFER_BIT;
    return check(
        create_handle(device.command_pool,
                      [&](auto* out) { return device.api.vkCreateCommandPool(device.handle, &pool, nullptr, out); }),
        "Could not create the Vulkan command pool");
}

struct startup_options {
    const char* loader_path{};
    const khaoz_gpu_device_selection* selection{};
    uint32_t* status{};
};

khaoz_result bootstrap(khaoz_gpu_device& device, const khaoz_gpu_device_desc& desc, const startup_options& options,
                       bool probe_only) {
    uint32_t ignored_status{};
    auto& status = options.status != nullptr ? *options.status : ignored_status;
    device.get_instance_proc = device.loader.open(options.loader_path);
    if (device.get_instance_proc == nullptr) {
        return error(KHAOZ_ERROR_VULKAN_UNAVAILABLE, "Could not load the Vulkan loader or its vkGetInstanceProcAddr");
    }

    const std::lock_guard lock(bootstrap_gate);
    volkInitializeCustom(device.get_instance_proc);
    const reset_volk reset;
    // A failed unlayered instance is a support refusal, regardless of the driver's result.
    // The instance keeps its diagnostic and any separate missing-driver status.
    auto result = create_instance(device, false, status);
    if (result != KHAOZ_OK) {
        return KHAOZ_ERROR_VULKAN_UNAVAILABLE;
    }

    const bool validation = !probe_only && desc.enable_validation != 0;
    uint32_t configured_status{};
    if (validation) {
        // Check the machine before asking for validation. Layers can fail or hide adapters.
        result = select_adapter(device, nullptr, nullptr, status);
        if (result != KHAOZ_OK) {
            return KHAOZ_ERROR_VULKAN_UNAVAILABLE;
        }
        device.close_instance();
        result = create_instance(device, true, configured_status);
        if (result != KHAOZ_OK) {
            return result == KHAOZ_ERROR_VULKAN_UNAVAILABLE ? KHAOZ_ERROR_INTERNAL : result;
        }
    }

    result =
        select_adapter(device, desc.device_name_contains, options.selection, validation ? configured_status : status);
    if (result == KHAOZ_OK) {
        return probe_only ? KHAOZ_OK : create_logical_device(device);
    }
    if (validation) {
        return result == KHAOZ_ERROR_VULKAN_UNAVAILABLE ? KHAOZ_ERROR_INTERNAL : result;
    }
    // An invalid selector result is a caller error. Other failures are from the support check.
    return result == KHAOZ_ERROR_INVALID_ARGUMENT ? result : KHAOZ_ERROR_VULKAN_UNAVAILABLE;
}

khaoz_result create(const khaoz_gpu_device_desc* desc, const startup_options& options, khaoz_gpu_device** out_device) {
    if (options.status != nullptr) {
        *options.status = KHAOZ_GPU_STARTUP_DEFAULT;
    }
    if (out_device == nullptr) {
        return error(KHAOZ_ERROR_INVALID_ARGUMENT, "An output device pointer is required");
    }
    *out_device = nullptr;
    if (desc == nullptr || (options.selection != nullptr && options.selection->select == nullptr)) {
        return error(KHAOZ_ERROR_INVALID_ARGUMENT, "A device description and a valid optional selector are required");
    }

    auto device = std::make_unique<khaoz_gpu_device>();
    auto result = bootstrap(*device, *desc, options, false);
    if (result != KHAOZ_OK) {
        return result;
    }
    result = create_allocator(*device);
    if (result != KHAOZ_OK) {
        return result;
    }
    device->capabilities = read_capabilities(*device);
    *out_device = device.release();
    return KHAOZ_OK;
}

} // namespace

khaoz_gpu_device::~khaoz_gpu_device() {
    if (instance != VK_NULL_HANDLE) {
        close();
    }
}

khaoz_result khaoz_gpu_device::require_usable() const noexcept {
    return queue_failure == KHAOZ_OK ? KHAOZ_OK
                                     : error(queue_failure, "The Vulkan device is unusable after a queue failure");
}

khaoz_result khaoz_gpu_device::wait_for_idle() noexcept {
    if (queue_failure == KHAOZ_ERROR_DEVICE_LOST) {
        return require_usable();
    }
    if (!work_pending) {
        return KHAOZ_OK;
    }

    const auto result = check(api.vkQueueWaitIdle(queue), "Could not drain the Vulkan queue");
    if (result != KHAOZ_OK) {
        queue_failure = result;
    }
    work_pending = result != KHAOZ_OK && result != KHAOZ_ERROR_DEVICE_LOST;
    return result;
}

khaoz_result khaoz_gpu_device::close() noexcept {
    const auto drained = wait_for_idle();
    if (work_pending) {
        return drained;
    }
    if (validation.messenger != VK_NULL_HANDLE) {
        for (auto* child = children; child != nullptr; child = child->next) {
            if (!child->retired) {
                validation.record_error("Vulkan device closed with live GPU resources");
                break;
            }
        }
    }
    while (children != nullptr) {
        delete children;
    }
    if (allocator != nullptr) {
        vmaDestroyAllocator(allocator);
        allocator = nullptr;
    }
    if (command_pool != VK_NULL_HANDLE) {
        api.vkDestroyCommandPool(handle, command_pool, nullptr);
        command_pool = VK_NULL_HANDLE;
    }
    if (handle != VK_NULL_HANDLE) {
        api.vkDestroyDevice(handle, nullptr);
        handle = VK_NULL_HANDLE;
        queue = VK_NULL_HANDLE;
    }
    close_instance();
    return drained;
}

device_child::device_child(khaoz_gpu_device& owner) noexcept : device(owner), next(owner.children) {
    if (next != nullptr) {
        next->previous = this;
    }
    device.children = this;
}

device_child::~device_child() {
    if (next != nullptr) {
        next->previous = previous;
    }
    if (previous == nullptr) {
        device.children = next;
    } else {
        previous->next = next;
    }
}

khaoz_result khaoz_gpu_device_create_headless(const khaoz_gpu_device_desc* desc, khaoz_gpu_device** out_device) {
    return khaoz::abi::guard([&] {
        const auto path = loader_environment();
        return create(desc, {.loader_path = path ? path->c_str() : nullptr}, out_device);
    });
}

khaoz_result khaoz_gpu_device_create_headless_with_loader(const khaoz_gpu_device_desc* desc, const char* loader_path,
                                                          khaoz_gpu_device** out_device) {
    return khaoz::abi::guard([&] { return create(desc, {.loader_path = loader_path}, out_device); });
}

khaoz_result khaoz_gpu_device_create_headless_with_selection(const khaoz_gpu_device_desc* desc, const char* loader_path,
                                                             const khaoz_gpu_device_selection* selection,
                                                             uint32_t* out_startup_status,
                                                             khaoz_gpu_device** out_device) {
    return khaoz::abi::guard([&] {
        return create(desc, {.loader_path = loader_path, .selection = selection, .status = out_startup_status},
                      out_device);
    });
}

khaoz_result khaoz_gpu_device_probe(const char* loader_path) {
    return khaoz::abi::guard([&] {
        const auto device = std::make_unique<khaoz_gpu_device>();
        return bootstrap(*device, {}, {.loader_path = loader_path}, true);
    });
}

khaoz_result khaoz_gpu_device_capabilities(const khaoz_gpu_device* device, khaoz_gpu_capabilities* out_caps) {
    return khaoz::abi::guard([&] {
        if (device == nullptr || out_caps == nullptr) {
            return error(KHAOZ_ERROR_INVALID_ARGUMENT, "A device and capability output are required");
        }
        *out_caps = device->capabilities;
        return KHAOZ_OK;
    });
}

uint32_t khaoz_gpu_device_validation_errors(const khaoz_gpu_device* device) {
    return device != nullptr ? device->validation.errors : 0;
}

khaoz_result khaoz_gpu_device_destroy(khaoz_gpu_device* device, uint32_t* out_validation_errors) {
    return khaoz::abi::guard([&] {
        const auto result = device != nullptr ? device->close() : KHAOZ_OK;
        if (out_validation_errors != nullptr) {
            *out_validation_errors = khaoz_gpu_device_validation_errors(device);
        }
        if (device != nullptr && !device->work_pending) {
            delete device;
        }
        return result;
    });
}

khaoz_result khaoz_gpu_device_wait_for_idle(khaoz_gpu_device* device) {
    return khaoz::abi::guard([&] {
        return device != nullptr ? device->wait_for_idle()
                                 : error(KHAOZ_ERROR_INVALID_ARGUMENT, "A device is required");
    });
}
