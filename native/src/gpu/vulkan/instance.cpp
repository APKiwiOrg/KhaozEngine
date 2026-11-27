#include "device.hpp"

#include <array>

namespace khaoz::vulkan {

khaoz_result create_instance(khaoz_gpu_device& device, bool enable_validation) {
    if (volkGetInstanceVersion() < api_version || vkCreateInstance == nullptr ||
        vkEnumerateInstanceExtensionProperties == nullptr) {
        return error(KHAOZ_ERROR_VULKAN_UNAVAILABLE, "The Vulkan loader does not expose Vulkan 1.3");
    }

    std::vector<VkExtensionProperties> available;
    const VkResult enumerated = enumerate(available, [](auto* count, auto* extensions) {
        return vkEnumerateInstanceExtensionProperties(nullptr, count, extensions);
    });
    if (enumerated != VK_SUCCESS) {
        return check(enumerated, "Could not enumerate Vulkan instance extensions", KHAOZ_ERROR_VULKAN_UNAVAILABLE);
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
    const VkResult created = vkCreateInstance(&desc, nullptr, &device.instance);
    if (created != VK_SUCCESS) {
        return check(created, "Could not create a Vulkan 1.3 instance", KHAOZ_ERROR_VULKAN_UNAVAILABLE);
    }

    volkLoadInstanceTable(&device.instance_api, device.instance);
    if (enable_validation) {
        return check(device.instance_api.vkCreateDebugUtilsMessengerEXT(device.instance, &debug, nullptr,
                                                                        &device.validation.messenger),
                     "Could not create a Vulkan validation messenger");
    }
    return KHAOZ_OK;
}

} // namespace khaoz::vulkan
