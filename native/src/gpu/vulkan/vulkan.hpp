#pragma once

#include "abi/abi_guard.hpp"

#include <volk.h>

#include <algorithm>
#include <cstring>
#include <vector>

namespace khaoz::vulkan {

inline constexpr uint32_t api_version = VK_API_VERSION_1_3;

template <class T> T info(VkStructureType type) noexcept {
    T value{};
    value.sType = type;
    return value;
}

inline khaoz_result error(khaoz_result result, const char* message) noexcept {
    abi::set_last_error(message);
    return result;
}

inline khaoz_result check(VkResult result, const char* operation,
                          khaoz_result fallback = KHAOZ_ERROR_INTERNAL) noexcept {
    switch (result) {
    case VK_SUCCESS:
        return KHAOZ_OK;
    case VK_ERROR_OUT_OF_HOST_MEMORY:
    case VK_ERROR_OUT_OF_DEVICE_MEMORY:
        return error(KHAOZ_ERROR_OUT_OF_MEMORY, operation);
    case VK_ERROR_DEVICE_LOST:
        return error(KHAOZ_ERROR_DEVICE_LOST, operation);
    default:
        return error(fallback, operation);
    }
}

template <class T, class Query> VkResult enumerate(std::vector<T>& values, Query query) {
    VkResult result{};
    do {
        uint32_t count = 0;
        result = query(&count, static_cast<T*>(nullptr));
        if (result != VK_SUCCESS || count == 0) {
            values.clear();
            return result;
        }
        values.resize(count);
        result = query(&count, values.data());
        values.resize(count);
    } while (result == VK_INCOMPLETE);
    return result;
}

inline bool has_extension(const std::vector<VkExtensionProperties>& extensions, const char* name) {
    return std::ranges::any_of(
        extensions, [name](const auto& extension) { return std::strcmp(extension.extensionName, name) == 0; });
}

} // namespace khaoz::vulkan
