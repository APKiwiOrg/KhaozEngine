#define VMA_IMPLEMENTATION
#include "memory.hpp"

namespace khaoz::vulkan {

khaoz_result create_allocator(khaoz_gpu_device& device) {
    VmaVulkanFunctions functions{};
    functions.vkGetInstanceProcAddr = device.get_instance_proc;
    functions.vkGetDeviceProcAddr = device.instance_api.vkGetDeviceProcAddr;
    VmaAllocatorCreateInfo desc{};
    desc.instance = device.instance;
    desc.device = device.handle;
    desc.physicalDevice = device.adapter.handle;
    desc.vulkanApiVersion = api_version;
    desc.pVulkanFunctions = &functions;
    return check(vmaCreateAllocator(&desc, &device.allocator), "Could not create the Vulkan memory allocator");
}

allocated_buffer::~allocated_buffer() {
    if (mapped) {
        vmaUnmapMemory(allocator, memory);
    }
    if (handle != VK_NULL_HANDLE) {
        vmaDestroyBuffer(allocator, handle, memory);
    }
}

khaoz_result allocated_buffer::create(VmaAllocator owner, const buffer_allocation& desc) {
    allocator = owner;
    host_visible = desc.access != 0;
    auto buffer = info<VkBufferCreateInfo>(VK_STRUCTURE_TYPE_BUFFER_CREATE_INFO);
    buffer.size = desc.size;
    buffer.usage = desc.usage;
    VmaAllocationCreateInfo allocation{};
    allocation.usage = host_visible ? VMA_MEMORY_USAGE_AUTO_PREFER_HOST : VMA_MEMORY_USAGE_AUTO_PREFER_DEVICE;
    allocation.flags = desc.access;
    return check(vmaCreateBuffer(allocator, &buffer, &allocation, &handle, &memory, nullptr),
                 "Could not allocate a Vulkan buffer");
}

khaoz_result allocated_buffer::write(VkDeviceSize offset, const void* data, size_t size) const {
    if (mapped) {
        return error(KHAOZ_ERROR_INVALID_ARGUMENT, "The buffer already has an open mapping");
    }
    return check(vmaCopyMemoryToAllocation(allocator, data, memory, offset, size),
                 "Could not write Vulkan buffer memory");
}

khaoz_result allocated_buffer::map(const buffer_window& window, uint32_t access, khaoz_gpu_mapped_data& out) {
    if (mapped || access > KHAOZ_GPU_MAP_READ_WRITE) {
        return error(KHAOZ_ERROR_INVALID_ARGUMENT, "The map mode is invalid or a mapping is already open");
    }
    void* pointer = nullptr;
    auto result = check(vmaMapMemory(allocator, memory, &pointer), "Could not map Vulkan buffer memory");
    if (result != KHAOZ_OK) {
        return result;
    }
    if (access != KHAOZ_GPU_MAP_WRITE) {
        result = check(vmaInvalidateAllocation(allocator, memory, window.offset, window.size),
                       "Could not invalidate Vulkan buffer memory");
        if (result != KHAOZ_OK) {
            vmaUnmapMemory(allocator, memory);
            return result;
        }
    }
    mapped = true;
    mode = access;
    out = {static_cast<char*>(pointer) + window.offset, window.row_pitch, window.size};
    return KHAOZ_OK;
}

khaoz_result allocated_buffer::unmap() {
    if (!mapped) {
        return error(KHAOZ_ERROR_INVALID_ARGUMENT, "The buffer has no open mapping");
    }
    const auto result = mode != KHAOZ_GPU_MAP_READ ? check(vmaFlushAllocation(allocator, memory, 0, VK_WHOLE_SIZE),
                                                           "Could not flush Vulkan buffer memory")
                                                   : KHAOZ_OK;
    vmaUnmapMemory(allocator, memory);
    mapped = false;
    return result;
}

} // namespace khaoz::vulkan
