#include "device.hpp"

#include <charconv>
#include <span>
#include <string_view>

namespace khaoz::vulkan {
namespace {

char lower_ascii(char value) { return value >= 'A' && value <= 'Z' ? static_cast<char>(value + ('a' - 'A')) : value; }

bool contains(std::string_view text, std::string_view part) {
    const auto matching =
        std::ranges::search(text, part, [](char left, char right) { return lower_ascii(left) == lower_ascii(right); });
    return part.empty() || !matching.empty();
}

bool equals(std::string_view text, std::string_view expected) {
    return text.size() == expected.size() && contains(text, expected);
}

adapter inspect(const VolkInstanceTable& api, VkPhysicalDevice handle) {
    adapter read{};
    read.handle = handle;
    api.vkGetPhysicalDeviceProperties(handle, &read.properties);
    if (read.properties.apiVersion < api_version || read.properties.limits.maxDescriptorSetUniformBuffersDynamic < 8) {
        return read;
    }

    VkPhysicalDeviceDriverProperties driver{
        .sType = VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_DRIVER_PROPERTIES,
        .pNext = nullptr,
        .driverID = VK_DRIVER_ID_MAX_ENUM,
        .driverName = {},
        .driverInfo = {},
        .conformanceVersion = {},
    };
    auto properties = info<VkPhysicalDeviceProperties2>(VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_PROPERTIES_2);
    properties.pNext = &driver;
    api.vkGetPhysicalDeviceProperties2(handle, &properties);
    read.llvmpipe = driver.driverID == VK_DRIVER_ID_MESA_LLVMPIPE || contains(read.properties.deviceName, "llvmpipe");

    auto features13 = info<VkPhysicalDeviceVulkan13Features>(VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_VULKAN_1_3_FEATURES);
    auto features12 = info<VkPhysicalDeviceVulkan12Features>(VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_VULKAN_1_2_FEATURES);
    auto features = info<VkPhysicalDeviceFeatures2>(VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_FEATURES_2);
    features.pNext = &features12;
    features12.pNext = &features13;
    api.vkGetPhysicalDeviceFeatures2(handle, &features);
    read.features = features.features;
    if (!features13.dynamicRendering || !features13.synchronization2 || !features12.timelineSemaphore) {
        return read;
    }

    VkPhysicalDeviceMemoryProperties memory{};
    api.vkGetPhysicalDeviceMemoryProperties(handle, &memory);
    constexpr VkMemoryPropertyFlags required =
        VK_MEMORY_PROPERTY_HOST_VISIBLE_BIT | VK_MEMORY_PROPERTY_HOST_COHERENT_BIT;
    const auto types = std::span{memory.memoryTypes}.first(memory.memoryTypeCount);
    if (std::ranges::none_of(types, [](const auto& type) { return (type.propertyFlags & required) == required; })) {
        return read;
    }

    uint32_t count = 0;
    api.vkGetPhysicalDeviceQueueFamilyProperties(handle, &count, nullptr);
    std::vector<VkQueueFamilyProperties> queues(count);
    api.vkGetPhysicalDeviceQueueFamilyProperties(handle, &count, queues.data());
    queues.resize(count);
    const auto graphics = std::ranges::find_if(queues, [](const auto& queue) {
        return queue.queueCount > 0 && (queue.queueFlags & VK_QUEUE_GRAPHICS_BIT) != 0;
    });
    if (graphics != queues.end()) {
        read.eligible = true;
        read.queue = *graphics;
        read.queue_family = static_cast<uint32_t>(graphics - queues.begin());
    }
    return read;
}

std::string_view trim(std::string_view request) {
    constexpr std::string_view whitespace = " \t\r\n\f\v";
    const auto first = request.find_first_not_of(whitespace);
    if (first == std::string_view::npos) {
        return {};
    }
    return request.substr(first, request.find_last_not_of(whitespace) - first + 1);
}

bool requested(const adapter& candidate, size_t index, std::string_view request) {
    if (request.empty()) {
        return true;
    }
    if (equals(request, "llvmpipe")) {
        return candidate.llvmpipe;
    }
    if (equals(request, "discrete")) {
        return candidate.properties.deviceType == VK_PHYSICAL_DEVICE_TYPE_DISCRETE_GPU;
    }
    if (equals(request, "integrated")) {
        return candidate.properties.deviceType == VK_PHYSICAL_DEVICE_TYPE_INTEGRATED_GPU;
    }
    if (equals(request, "cpu")) {
        return candidate.properties.deviceType == VK_PHYSICAL_DEVICE_TYPE_CPU;
    }
    auto number = request;
    if (number.starts_with('+')) {
        number.remove_prefix(1);
    }
    int selected = 0;
    const auto parsed = std::from_chars(number.data(), number.data() + number.size(), selected);
    if (parsed.ec == std::errc{} && parsed.ptr == number.data() + number.size()) {
        return selected >= 0 && static_cast<size_t>(selected) == index;
    }
    return contains(candidate.properties.deviceName, request);
}

} // namespace

khaoz_result select_adapter(khaoz_gpu_device& device, const char* requested_name) {
    std::vector<VkPhysicalDevice> physical;
    const auto result = enumerate(physical, [&device](auto* count, auto* devices) {
        return device.instance_api.vkEnumeratePhysicalDevices(device.instance, count, devices);
    });
    if (result != VK_SUCCESS) {
        return check(result, "Could not enumerate Vulkan physical devices", KHAOZ_ERROR_VULKAN_UNAVAILABLE);
    }

    const auto request = requested_name == nullptr ? std::string_view{} : trim(requested_name);
    for (size_t index = 0; index < physical.size(); ++index) {
        const auto candidate = inspect(device.instance_api, physical.at(index));
        if (!candidate.eligible) {
            continue;
        }
        if (requested(candidate, index, request)) {
            device.adapter = candidate;
            return KHAOZ_OK;
        }
        if (!device.adapter.eligible) {
            device.adapter = candidate;
        }
    }
    return device.adapter.eligible
               ? KHAOZ_OK
               : error(KHAOZ_ERROR_VULKAN_UNAVAILABLE, "No physical device meets the Vulkan 1.3 backend requirements");
}

} // namespace khaoz::vulkan
