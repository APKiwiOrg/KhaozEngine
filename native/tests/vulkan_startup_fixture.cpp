#include "khaoz/khaoz_native.h"

#include <vulkan/vulkan.h>

#include <algorithm>
#include <cstring>

struct VkInstance_T {
    int poison;
};

namespace {

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
    static VkInstance_T failed_output{1};
    *instance = &failed_output;
    return info->enabledLayerCount > 0 ? VK_ERROR_LAYER_NOT_PRESENT : VK_ERROR_INCOMPATIBLE_DRIVER;
}

template <class Function> PFN_vkVoidFunction symbol(Function function) {
    // The Vulkan loader returns each named entry point through PFN_vkVoidFunction.
    // NOLINTNEXTLINE(cppcoreguidelines-pro-type-reinterpret-cast)
    return reinterpret_cast<PFN_vkVoidFunction>(function);
}

} // namespace

extern "C" KHAOZ_API VKAPI_ATTR PFN_vkVoidFunction VKAPI_CALL vkGetInstanceProcAddr(VkInstance, const char* name) {
    if (std::strcmp(name, "vkEnumerateInstanceVersion") == 0) {
        return symbol(&instance_version);
    }
    if (std::strcmp(name, "vkEnumerateInstanceExtensionProperties") == 0) {
        return symbol(&instance_extensions);
    }
    if (std::strcmp(name, "vkEnumerateInstanceLayerProperties") == 0) {
        return symbol(&instance_layers);
    }
    if (std::strcmp(name, "vkCreateInstance") == 0) {
        return symbol(&create_instance);
    }
    return nullptr;
}
