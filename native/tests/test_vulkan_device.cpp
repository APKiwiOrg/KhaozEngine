#include <doctest/doctest.h>

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

} // namespace

TEST_CASE("headless device creates and destroys with zero validation errors" * doctest::skip(!gpu_tests_enabled())) {
    const khaoz_gpu_device_desc desc{1, nullptr};
    khaoz_gpu_device* device = nullptr;
    REQUIRE(khaoz_gpu_device_create_headless(&desc, &device) == KHAOZ_OK);
    REQUIRE(device != nullptr);
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
