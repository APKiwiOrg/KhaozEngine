#include "khaoz/khaoz_native.h"

#include <vulkan/vulkan.h>

#include <algorithm>
#include <cstdlib>
#include <cstring>
#include <utility>

struct VkInstance_T {};
struct VkPhysicalDevice_T {};

namespace {

enum class startup_mode : uint8_t {
    supported_with_unloadable_layer,
    missing_driver_and_layer,
    zero_devices,
    instance_initialization_failed,
    instance_host_memory_failed,
    device_enumeration_failed,
};
startup_mode mode{};
VkInstance_T unlayered_instance;
VkInstance_T failed_output;
VkPhysicalDevice_T eligible_device;

VKAPI_ATTR VkResult VKAPI_CALL instance_version(uint32_t* version) {
    *version = VK_API_VERSION_1_3;
    return VK_SUCCESS;
}

VKAPI_ATTR VkResult VKAPI_CALL instance_extensions(const char*, uint32_t* count, VkExtensionProperties* properties) {
    *count = 1;
    if (properties != nullptr) {
        *properties = {};
        constexpr char name[] = VK_EXT_DEBUG_UTILS_EXTENSION_NAME;
        std::copy_n(name, sizeof(name), properties->extensionName);
    }
    return VK_SUCCESS;
}

VKAPI_ATTR VkResult VKAPI_CALL instance_layers(uint32_t* count, VkLayerProperties* properties) {
    if (mode == startup_mode::missing_driver_and_layer) {
        *count = 0;
        return VK_SUCCESS;
    }

    *count = 1;
    if (properties != nullptr) {
        *properties = {};
        constexpr char name[] = "VK_LAYER_KHRONOS_validation";
        std::copy_n(name, sizeof(name), properties->layerName);
    }
    return VK_SUCCESS;
}

VKAPI_ATTR VkResult VKAPI_CALL create_instance(const VkInstanceCreateInfo* info, const VkAllocationCallbacks*,
                                               VkInstance* instance) {
    *instance = &failed_output;
    if (mode == startup_mode::missing_driver_and_layer) {
        return VK_ERROR_INCOMPATIBLE_DRIVER;
    }
    if (info->enabledLayerCount > 0) {
        return mode == startup_mode::supported_with_unloadable_layer ? VK_ERROR_LAYER_NOT_PRESENT
                                                                     : VK_ERROR_INCOMPATIBLE_DRIVER;
    }
    if (mode == startup_mode::instance_initialization_failed) {
        return VK_ERROR_INITIALIZATION_FAILED;
    }
    if (mode == startup_mode::instance_host_memory_failed) {
        return VK_ERROR_OUT_OF_HOST_MEMORY;
    }

    *instance = &unlayered_instance;
    return VK_SUCCESS;
}

VKAPI_ATTR void VKAPI_CALL destroy_instance(VkInstance instance, const VkAllocationCallbacks*) {
    if (instance != &unlayered_instance) {
        std::abort();
    }
}

VKAPI_ATTR VkResult VKAPI_CALL physical_devices(VkInstance, uint32_t* count, VkPhysicalDevice* devices) {
    if (mode == startup_mode::device_enumeration_failed) {
        return VK_ERROR_INITIALIZATION_FAILED;
    }
    if (mode == startup_mode::zero_devices) {
        *count = 0;
        return VK_SUCCESS;
    }

    *count = 1;
    if (devices != nullptr) {
        *devices = &eligible_device;
    }
    return VK_SUCCESS;
}

VKAPI_ATTR void VKAPI_CALL physical_properties(VkPhysicalDevice, VkPhysicalDeviceProperties* properties) {
    *properties = {};
    properties->apiVersion = VK_API_VERSION_1_3;
    properties->limits.maxDescriptorSetUniformBuffersDynamic = 8;
}

VKAPI_ATTR void VKAPI_CALL physical_properties2(VkPhysicalDevice device, VkPhysicalDeviceProperties2* properties) {
    physical_properties(device, &properties->properties);
}

VKAPI_ATTR void VKAPI_CALL physical_features2(VkPhysicalDevice, VkPhysicalDeviceFeatures2* features) {
    auto* features12 = static_cast<VkPhysicalDeviceVulkan12Features*>(features->pNext);
    auto* features13 = static_cast<VkPhysicalDeviceVulkan13Features*>(features12->pNext);
    features12->timelineSemaphore = VK_TRUE;
    features13->dynamicRendering = VK_TRUE;
    features13->synchronization2 = VK_TRUE;
}

VKAPI_ATTR void VKAPI_CALL physical_memory(VkPhysicalDevice, VkPhysicalDeviceMemoryProperties* memory) {
    *memory = {};
    memory->memoryTypeCount = 1;
    memory->memoryTypes[0].propertyFlags = VK_MEMORY_PROPERTY_HOST_VISIBLE_BIT | VK_MEMORY_PROPERTY_HOST_COHERENT_BIT;
}

VKAPI_ATTR void VKAPI_CALL physical_queues(VkPhysicalDevice, uint32_t* count, VkQueueFamilyProperties* queues) {
    *count = 1;
    if (queues != nullptr) {
        *queues = {};
        queues->queueCount = 1;
        queues->queueFlags = VK_QUEUE_GRAPHICS_BIT;
    }
}

template <class Function> PFN_vkVoidFunction symbol(Function function) noexcept {
    // The Vulkan loader returns each named entry point through PFN_vkVoidFunction.
    // NOLINTNEXTLINE(cppcoreguidelines-pro-type-reinterpret-cast)
    return reinterpret_cast<PFN_vkVoidFunction>(function);
}

} // namespace

extern "C" KHAOZ_API void khaoz_fixture_set_startup_mode(uint32_t value) { mode = static_cast<startup_mode>(value); }

extern "C" KHAOZ_API VKAPI_ATTR PFN_vkVoidFunction VKAPI_CALL vkGetInstanceProcAddr(VkInstance, const char* name) {
    static const std::pair<const char*, PFN_vkVoidFunction> exports[]{
        {"vkEnumerateInstanceVersion", symbol(&instance_version)},
        {"vkEnumerateInstanceExtensionProperties", symbol(&instance_extensions)},
        {"vkEnumerateInstanceLayerProperties", symbol(&instance_layers)},
        {"vkCreateInstance", symbol(&create_instance)},
        {"vkDestroyInstance", symbol(&destroy_instance)},
        {"vkEnumeratePhysicalDevices", symbol(&physical_devices)},
        {"vkGetPhysicalDeviceProperties", symbol(&physical_properties)},
        {"vkGetPhysicalDeviceProperties2", symbol(&physical_properties2)},
        {"vkGetPhysicalDeviceFeatures2", symbol(&physical_features2)},
        {"vkGetPhysicalDeviceMemoryProperties", symbol(&physical_memory)},
        {"vkGetPhysicalDeviceQueueFamilyProperties", symbol(&physical_queues)},
    };
    for (const auto& [export_name, address] : exports) {
        if (std::strcmp(name, export_name) == 0) {
            return address;
        }
    }
    return nullptr;
}
