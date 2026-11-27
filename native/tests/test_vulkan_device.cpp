#include <doctest/doctest.h>

#include "gpu/vulkan/device.hpp"
#include "khaoz/khaoz_native.h"

#include <cstdlib>
#include <optional>
#include <string>
#include <string_view>

#ifdef _WIN32
#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#endif

namespace {

bool gpu_tests_enabled() noexcept {
#ifdef _WIN32
    char setting[2]{};
    return GetEnvironmentVariableA("KHAOZ_NATIVE_GPU_TESTS", setting, static_cast<DWORD>(sizeof(setting))) == 1 &&
           setting[0] == '1';
#else
    const auto* setting = std::getenv("KHAOZ_NATIVE_GPU_TESTS");
    return setting != nullptr && std::string_view(setting) == "1";
#endif
}

#ifdef _WIN32
struct restore_loader {
    static constexpr auto* variable = L"KHAOZ_NATIVE_VULKAN_LOADER";
    std::optional<std::wstring> original;

    restore_loader() {
        const auto capacity = GetEnvironmentVariableW(variable, nullptr, 0);
        if (capacity != 0) {
            original.emplace(capacity, L'\0');
            GetEnvironmentVariableW(variable, original->data(), capacity);
        }
    }
    ~restore_loader() { SetEnvironmentVariableW(variable, original ? original->c_str() : nullptr); }
};

bool set_missing_loader() {
    return SetEnvironmentVariableW(restore_loader::variable,
                                   L"khaoz-test-loader-directory-does-not-exist/missing-vulkan-loader") != 0;
}
#else
struct restore_loader {
    static constexpr auto* variable = "KHAOZ_NATIVE_VULKAN_LOADER";
    std::optional<std::string> original;

    restore_loader() {
        if (const char* path = std::getenv(variable)) {
            original = path;
        }
    }
    ~restore_loader() {
        if (original) {
            setenv(variable, original->c_str(), 1);
        } else {
            unsetenv(variable);
        }
    }
};

bool set_missing_loader() {
    return setenv(restore_loader::variable, "khaoz-test-loader-directory-does-not-exist/missing-vulkan-loader", 1) == 0;
}
#endif

struct wait_fault {
    static inline PFN_vkFreeCommandBuffers driver_free{};
    static inline bool awaiting_idle{};
    static inline bool freed_without_idle{};

    static VKAPI_ATTR VkResult VKAPI_CALL out_of_memory(VkQueue) noexcept { return VK_ERROR_OUT_OF_HOST_MEMORY; }

    static VKAPI_ATTR void VKAPI_CALL free_commands(VkDevice device, VkCommandPool pool, uint32_t count,
                                                    const VkCommandBuffer* commands) noexcept {
        freed_without_idle |= awaiting_idle;
        driver_free(device, pool, count, commands);
    }
};

} // namespace

TEST_CASE("headless device creates and destroys with zero validation errors" * doctest::skip(!gpu_tests_enabled())) {
    const khaoz_gpu_device_desc desc{1, nullptr};
    khaoz_gpu_device* device = nullptr;
    REQUIRE(khaoz_gpu_device_create_headless(&desc, &device) == KHAOZ_OK);
    REQUIRE(device != nullptr);
    std::vector<VkExtensionProperties> extensions;
    const auto enumerated = khaoz::vulkan::enumerate(extensions, [device](auto* count, auto* output) {
        return device->instance_api.vkEnumerateDeviceExtensionProperties(device->adapter.handle, nullptr, count,
                                                                         output);
    });
    CHECK(enumerated == VK_SUCCESS);
    khaoz_gpu_capabilities capabilities{};
    CHECK(khaoz_gpu_device_capabilities(device, &capabilities) == KHAOZ_OK);
    const bool has_portability = khaoz::vulkan::has_extension(extensions, "VK_KHR_portability_subset");
    CHECK((capabilities.sampler_lod_bias != 0) == !has_portability);
    khaoz_gpu_sampler* sampler = nullptr;
    const khaoz_gpu_sampler_desc sampler_desc{
        KHAOZ_GPU_FILTER_LINEAR, KHAOZ_GPU_ADDRESS_CLAMP, KHAOZ_GPU_ADDRESS_CLAMP, KHAOZ_GPU_ADDRESS_CLAMP, 0, 2};
    const auto sampler_status = khaoz_gpu_sampler_create(device, &sampler_desc, &sampler);
    CHECK(sampler_status == KHAOZ_OK);
    CHECK(khaoz_gpu_sampler_destroy(sampler) == KHAOZ_OK);
    CHECK(khaoz_gpu_device_validation_errors(device) == 0);
    uint32_t errors = 1;
    REQUIRE(khaoz_gpu_device_destroy(device, &errors) == KHAOZ_OK);
    CHECK(errors == 0);
}

TEST_CASE("a missing loader path yields KHAOZ_ERROR_VULKAN_UNAVAILABLE" * doctest::skip(!gpu_tests_enabled())) {
    const restore_loader restore;
    REQUIRE(set_missing_loader());
    const khaoz_gpu_device_desc desc{};
    khaoz_gpu_device* device = nullptr;
    CHECK(khaoz_gpu_device_create_headless(&desc, &device) == KHAOZ_ERROR_VULKAN_UNAVAILABLE);
    CHECK(device == nullptr);
}

TEST_CASE("device teardown counts live GPU resources" * doctest::skip(!gpu_tests_enabled())) {
    const khaoz_gpu_device_desc desc{1, nullptr};
    khaoz_gpu_device* device = nullptr;
    REQUIRE(khaoz_gpu_device_create_headless(&desc, &device) == KHAOZ_OK);
    khaoz_gpu_sampler* sampler = nullptr;
    const khaoz_gpu_sampler_desc sampler_desc{
        KHAOZ_GPU_FILTER_LINEAR, KHAOZ_GPU_ADDRESS_CLAMP, KHAOZ_GPU_ADDRESS_CLAMP, KHAOZ_GPU_ADDRESS_CLAMP, 0, 0};
    const auto created = khaoz_gpu_sampler_create(device, &sampler_desc, &sampler);
    uint32_t errors = 0;
    REQUIRE(khaoz_gpu_device_destroy(device, &errors) == KHAOZ_OK);
    CHECK(created == KHAOZ_OK);
    CHECK(errors > 0);
}

TEST_CASE("failed Vulkan instance creation does not acquire the poisoned output handle") {
    const khaoz_gpu_device_desc desc{1, nullptr};
    khaoz_gpu_device* device = nullptr;
    CHECK(khaoz_gpu_device_create_headless_with_loader(&desc, KHAOZ_STARTUP_FIXTURE_PATH, &device) ==
          KHAOZ_ERROR_INTERNAL);
    CHECK(device == nullptr);
}

TEST_CASE("failed queue wait retains transfer and device until a later verified drain" *
          doctest::skip(!gpu_tests_enabled())) {
    const khaoz_gpu_device_desc desc{1, nullptr};
    khaoz_gpu_device* device = nullptr;
    REQUIRE(khaoz_gpu_device_create_headless(&desc, &device) == KHAOZ_OK);
    khaoz_gpu_buffer* destination = nullptr;
    constexpr uint32_t word = 0xA1B2C3D4U;
    REQUIRE(khaoz_gpu_buffer_create(device, sizeof(word), KHAOZ_GPU_BUFFER_VERTEX, &destination) == KHAOZ_OK);

    const auto driver_wait = device->api.vkQueueWaitIdle;
    wait_fault::driver_free = device->api.vkFreeCommandBuffers;
    wait_fault::awaiting_idle = true;
    wait_fault::freed_without_idle = false;
    device->api.vkQueueWaitIdle = &wait_fault::out_of_memory;
    device->api.vkFreeCommandBuffers = &wait_fault::free_commands;

    CHECK(khaoz_gpu_buffer_update(destination, 0, &word, sizeof(word)) == KHAOZ_ERROR_OUT_OF_MEMORY);
    CHECK_FALSE(wait_fault::freed_without_idle);
    khaoz_gpu_buffer* rejected = nullptr;
    const auto create_result = khaoz_gpu_buffer_create(device, sizeof(word), KHAOZ_GPU_BUFFER_STAGING, &rejected);
    CHECK(create_result == KHAOZ_ERROR_OUT_OF_MEMORY);
    if (rejected != nullptr) {
        static_cast<void>(khaoz_gpu_buffer_destroy(rejected));
    }
    CHECK(khaoz_gpu_buffer_destroy(destination) == KHAOZ_ERROR_OUT_OF_MEMORY);

    uint32_t errors = 0;
    CHECK(khaoz_gpu_device_destroy(device, &errors) == KHAOZ_ERROR_OUT_OF_MEMORY);
    REQUIRE_FALSE(wait_fault::freed_without_idle);
    device->api.vkQueueWaitIdle = driver_wait;
    REQUIRE(khaoz_gpu_device_wait_for_idle(device) == KHAOZ_OK);
    wait_fault::awaiting_idle = false;
    CHECK(khaoz_gpu_device_destroy(device, &errors) == KHAOZ_OK);
    CHECK(errors == 0);
}
