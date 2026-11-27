#include "validation.hpp"

namespace khaoz::vulkan {
namespace {

VKAPI_ATTR VkBool32 VKAPI_CALL callback(VkDebugUtilsMessageSeverityFlagBitsEXT, VkDebugUtilsMessageTypeFlagsEXT,
                                        const VkDebugUtilsMessengerCallbackDataEXT* data, void* user) noexcept {
    static_cast<validation*>(user)->record_error(data->pMessage);
    return VK_FALSE;
}

} // namespace

VkDebugUtilsMessengerCreateInfoEXT validation::create_info() noexcept {
    auto desc = info<VkDebugUtilsMessengerCreateInfoEXT>(VK_STRUCTURE_TYPE_DEBUG_UTILS_MESSENGER_CREATE_INFO_EXT);
    desc.messageSeverity = VK_DEBUG_UTILS_MESSAGE_SEVERITY_ERROR_BIT_EXT;
    desc.messageType = VkDebugUtilsMessageTypeFlagsEXT{VK_DEBUG_UTILS_MESSAGE_TYPE_GENERAL_BIT_EXT} |
                       VK_DEBUG_UTILS_MESSAGE_TYPE_VALIDATION_BIT_EXT | VK_DEBUG_UTILS_MESSAGE_TYPE_PERFORMANCE_BIT_EXT;
    desc.pfnUserCallback = callback;
    desc.pUserData = this;
    return desc;
}

void validation::record_error(const char* message) noexcept {
    ++errors;
    abi::set_last_error(message != nullptr ? message : "Vulkan reported a validation error");
}

} // namespace khaoz::vulkan
