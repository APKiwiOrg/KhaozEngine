#pragma once

#include "vulkan.hpp"

namespace khaoz::vulkan {

struct validation {
    uint32_t errors{};
    VkDebugUtilsMessengerEXT messenger{};

    [[nodiscard]] VkDebugUtilsMessengerCreateInfoEXT create_info() noexcept;
    void record_error(const char* message) noexcept;
};

inline constexpr const char* validation_layer = "VK_LAYER_KHRONOS_validation";

} // namespace khaoz::vulkan
