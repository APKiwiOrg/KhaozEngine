#include "device.hpp"

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

adapter inspect(const VolkInstanceTable& api, VkPhysicalDevice handle) {
    adapter read{};
    read.handle = handle;
    api.vkGetPhysicalDeviceProperties(handle, &read.properties);
    if (read.properties.apiVersion < api_version) {
        read.rejection_reason = "Vulkan 1.3 is required";
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
    read.llvmpipe = driver.driverID == VK_DRIVER_ID_MESA_LLVMPIPE;

    auto features13 = info<VkPhysicalDeviceVulkan13Features>(VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_VULKAN_1_3_FEATURES);
    auto features12 = info<VkPhysicalDeviceVulkan12Features>(VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_VULKAN_1_2_FEATURES);
    auto features = info<VkPhysicalDeviceFeatures2>(VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_FEATURES_2);
    features.pNext = &features12;
    features12.pNext = &features13;
    api.vkGetPhysicalDeviceFeatures2(handle, &features);
    read.features = features.features;
    if (!features13.dynamicRendering || !features13.synchronization2 || !features12.timelineSemaphore) {
        read.rejection_reason = "Dynamic rendering, synchronization2 and timeline semaphores are required";
        return read;
    }
    if (read.properties.limits.maxDescriptorSetUniformBuffersDynamic < 8) {
        read.rejection_reason = "At least eight dynamic uniform buffer descriptors are required";
        return read;
    }

    VkPhysicalDeviceMemoryProperties memory{};
    api.vkGetPhysicalDeviceMemoryProperties(handle, &memory);
    constexpr VkMemoryPropertyFlags required =
        VK_MEMORY_PROPERTY_HOST_VISIBLE_BIT | VK_MEMORY_PROPERTY_HOST_COHERENT_BIT;
    const auto types = std::span{memory.memoryTypes}.first(memory.memoryTypeCount);
    if (std::ranges::none_of(types, [](const auto& type) { return (type.propertyFlags & required) == required; })) {
        read.rejection_reason = "Host-visible coherent memory is required";
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
        read.queue = *graphics;
        read.queue_family = static_cast<uint32_t>(graphics - queues.begin());
    } else {
        read.rejection_reason = "A graphics queue family is required";
    }
    return read;
}

const adapter* choose_by_name(const std::vector<adapter>& candidates, const char* name) {
    const auto eligible = [](const adapter& entry) { return entry.rejection_reason == nullptr; };
    if (name != nullptr && *name != '\0') {
        const auto matching = std::ranges::find_if(candidates, [&](const adapter& entry) {
            return eligible(entry) && contains(entry.properties.deviceName, name);
        });
        if (matching != candidates.end()) {
            return &*matching;
        }
    }

    const auto first = std::ranges::find_if(candidates, eligible);
    return first != candidates.end() ? &*first : nullptr;
}

const adapter* choose_by_callback(const std::vector<adapter>& candidates, const khaoz_gpu_device_selection& selection) {
    std::vector<khaoz_gpu_adapter_info> infos;
    infos.reserve(candidates.size());
    for (const auto& entry : candidates) {
        khaoz_gpu_adapter_info info{};
        std::memcpy(info.device_name, entry.properties.deviceName, sizeof(info.device_name));
        info.device_name[sizeof(info.device_name) - 1] = '\0';
        info.device_type = static_cast<uint32_t>(entry.properties.deviceType);
        info.device_id = entry.properties.deviceID;
        info.mesa_llvmpipe = entry.llvmpipe ? 1U : 0U;
        info.rejection_reason = entry.rejection_reason;
        infos.push_back(info);
    }

    const auto selected = selection.select(infos.data(), static_cast<uint32_t>(infos.size()), selection.context);
    if (selected < 0 || static_cast<size_t>(selected) >= candidates.size()) {
        return nullptr;
    }
    return &candidates.at(static_cast<size_t>(selected));
}

} // namespace

khaoz_result select_adapter(khaoz_gpu_device& device, const char* requested_name,
                            const khaoz_gpu_device_selection* selection, uint32_t& startup_status) {
    std::vector<VkPhysicalDevice> physical;
    const auto result = enumerate(physical, [&device](auto* count, auto* devices) {
        return device.instance_api.vkEnumeratePhysicalDevices(device.instance, count, devices);
    });
    if (result != VK_SUCCESS) {
        return check(result, "Could not enumerate Vulkan physical devices");
    }
    if (physical.empty()) {
        startup_status = KHAOZ_GPU_STARTUP_NO_DRIVER;
        return error(KHAOZ_ERROR_VULKAN_UNAVAILABLE, "The Vulkan loader found no physical devices");
    }

    std::vector<adapter> candidates;
    candidates.reserve(physical.size());
    for (const auto handle : physical) {
        candidates.push_back(inspect(device.instance_api, handle));
    }
    if (std::ranges::none_of(candidates, [](const auto& entry) { return entry.rejection_reason == nullptr; })) {
        return error(KHAOZ_ERROR_VULKAN_UNAVAILABLE, "No physical device meets the Vulkan 1.3 backend requirements");
    }

    const auto* chosen =
        selection != nullptr ? choose_by_callback(candidates, *selection) : choose_by_name(candidates, requested_name);
    if (chosen == nullptr || chosen->rejection_reason != nullptr) {
        return error(KHAOZ_ERROR_INVALID_ARGUMENT, "The selector must return an eligible Vulkan adapter index");
    }
    device.adapter = *chosen;
    return KHAOZ_OK;
}

} // namespace khaoz::vulkan
