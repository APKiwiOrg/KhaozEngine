#include "device.hpp"

#include <array>

namespace khaoz::vulkan {

khaoz_result create_instance(khaoz_gpu_device& device, bool enable_validation, uint32_t& startup_status) {
    if (vkCreateInstance == nullptr || vkEnumerateInstanceExtensionProperties == nullptr) {
        return error(KHAOZ_ERROR_VULKAN_UNAVAILABLE, "The Vulkan loader is missing required instance entry points");
    }
    uint32_t supported_version = VK_API_VERSION_1_0;
    if (vkEnumerateInstanceVersion != nullptr) {
        const auto version_result = vkEnumerateInstanceVersion(&supported_version);
        if (version_result != VK_SUCCESS) {
            return check(version_result, "Could not query the Vulkan loader version");
        }
    }
    if (supported_version < api_version) {
        return error(KHAOZ_ERROR_VULKAN_UNAVAILABLE, "The Vulkan loader does not expose Vulkan 1.3");
    }

    std::vector<VkExtensionProperties> available;
    const VkResult enumerated = enumerate(available, [](auto* count, auto* extensions) {
        return vkEnumerateInstanceExtensionProperties(nullptr, count, extensions);
    });
    if (enumerated != VK_SUCCESS) {
        return check(enumerated, "Could not enumerate Vulkan instance extensions");
    }

    std::array<const char*, 2> extensions{};
    uint32_t extension_count = 0;
    auto desc = info<VkInstanceCreateInfo>(VK_STRUCTURE_TYPE_INSTANCE_CREATE_INFO);
    if (has_extension(available, VK_KHR_PORTABILITY_ENUMERATION_EXTENSION_NAME)) {
        extensions.at(extension_count++) = VK_KHR_PORTABILITY_ENUMERATION_EXTENSION_NAME;
        desc.flags |= VK_INSTANCE_CREATE_ENUMERATE_PORTABILITY_BIT_KHR;
    }

    auto debug = device.validation.create_info();
    if (enable_validation) {
        if (vkEnumerateInstanceLayerProperties == nullptr) {
            return error(KHAOZ_ERROR_INTERNAL, "The Vulkan loader cannot enumerate validation layers");
        }
        std::vector<VkLayerProperties> layers;
        const VkResult layer_result = enumerate(layers, vkEnumerateInstanceLayerProperties);
        if (layer_result != VK_SUCCESS) {
            return check(layer_result, "Could not enumerate Vulkan validation layers");
        }
        const bool found = std::ranges::any_of(
            layers, [](const auto& layer) { return std::strcmp(layer.layerName, validation_layer) == 0; });
        if (!found || !has_extension(available, VK_EXT_DEBUG_UTILS_EXTENSION_NAME)) {
            return error(KHAOZ_ERROR_INTERNAL,
                         "Validation requires VK_LAYER_KHRONOS_validation and VK_EXT_debug_utils");
        }
        extensions.at(extension_count++) = VK_EXT_DEBUG_UTILS_EXTENSION_NAME;
        desc.enabledLayerCount = 1;
        desc.ppEnabledLayerNames = &validation_layer;
        desc.pNext = &debug;
    }

    auto application = info<VkApplicationInfo>(VK_STRUCTURE_TYPE_APPLICATION_INFO);
    application.pApplicationName = "KhaozEngine native core";
    application.apiVersion = api_version;
    desc.pApplicationInfo = &application;
    desc.enabledExtensionCount = extension_count;
    desc.ppEnabledExtensionNames = extensions.data();
    const VkResult created =
        create_handle(device.instance, [&](auto* out) { return vkCreateInstance(&desc, nullptr, out); });
    if (!enable_validation && created == VK_ERROR_INCOMPATIBLE_DRIVER) {
        startup_status = KHAOZ_GPU_STARTUP_NO_DRIVER;
        return error(KHAOZ_ERROR_VULKAN_UNAVAILABLE, "No compatible Vulkan driver is installed");
    }
    if (created != VK_SUCCESS) {
        return check(created, enable_validation ? "Could not create a validation-enabled Vulkan 1.3 instance"
                                                : "Could not create an unlayered Vulkan 1.3 instance");
    }

    volkLoadInstanceTable(&device.instance_api, device.instance);
    if (enable_validation) {
        return check(create_handle(device.validation.messenger,
                                   [&](auto* out) {
                                       return device.instance_api.vkCreateDebugUtilsMessengerEXT(device.instance,
                                                                                                 &debug, nullptr, out);
                                   }),
                     "Could not create a Vulkan validation messenger");
    }
    return KHAOZ_OK;
}

} // namespace khaoz::vulkan

void khaoz_gpu_device::close_instance() noexcept {
    if (validation.messenger != VK_NULL_HANDLE) {
        instance_api.vkDestroyDebugUtilsMessengerEXT(instance, validation.messenger, nullptr);
        validation.messenger = VK_NULL_HANDLE;
    }
    if (instance != VK_NULL_HANDLE) {
        instance_api.vkDestroyInstance(instance, nullptr);
        instance = VK_NULL_HANDLE;
    }
    adapter = {};
    instance_api = {};
}
