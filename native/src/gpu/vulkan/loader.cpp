#include "loader.hpp"

#include <bit>
#include <cstdlib>

#ifdef _WIN32
#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#else
#include <dlfcn.h>
#endif

namespace khaoz::vulkan {

#ifdef _WIN32
namespace {

HMODULE open_path(const char* path) {
    const int length = MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, path, -1, nullptr, 0);
    if (length == 0) {
        return nullptr;
    }
    std::wstring wide_path(static_cast<size_t>(length), L'\0');
    if (MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, path, -1, wide_path.data(), length) != length) {
        return nullptr;
    }
    return LoadLibraryW(wide_path.c_str());
}

} // namespace
#endif

std::optional<std::string> loader_environment() {
#ifdef _WIN32
    constexpr auto* variable = L"KHAOZ_NATIVE_VULKAN_LOADER";
    const DWORD capacity = GetEnvironmentVariableW(variable, nullptr, 0);
    if (capacity == 0) {
        return std::nullopt;
    }
    std::wstring wide_path(capacity, L'\0');
    const DWORD length = GetEnvironmentVariableW(variable, wide_path.data(), capacity);
    if (length >= capacity) {
        return std::string{};
    }
    const int utf8_size =
        WideCharToMultiByte(CP_UTF8, WC_ERR_INVALID_CHARS, wide_path.c_str(), -1, nullptr, 0, nullptr, nullptr);
    if (utf8_size == 0) {
        return std::string{};
    }
    std::string path(static_cast<size_t>(utf8_size), '\0');
    if (WideCharToMultiByte(CP_UTF8, WC_ERR_INVALID_CHARS, wide_path.c_str(), -1, path.data(), utf8_size, nullptr,
                            nullptr) != utf8_size) {
        return std::string{};
    }
    path.pop_back();
    return path;
#else
    const char* path = std::getenv("KHAOZ_NATIVE_VULKAN_LOADER");
    return path == nullptr ? std::nullopt : std::optional<std::string>(path);
#endif
}

PFN_vkGetInstanceProcAddr loader::open(const char* override_path) {
#ifdef _WIN32
    const HMODULE module = override_path != nullptr
                               ? open_path(override_path)
                               : LoadLibraryExW(L"vulkan-1.dll", nullptr, LOAD_LIBRARY_SEARCH_SYSTEM32);
    module_ = module;
    if (module == nullptr) {
        return nullptr;
    }
    // GetProcAddress returns FARPROC for every export, so recover the signature of this named symbol.
    // NOLINTNEXTLINE(bugprone-bitwise-pointer-cast)
    return std::bit_cast<PFN_vkGetInstanceProcAddr>(GetProcAddress(module, "vkGetInstanceProcAddr"));
#else
    constexpr int flags = RTLD_NOW | RTLD_LOCAL;
    if (override_path != nullptr) {
        module_ = dlopen(override_path, flags);
    } else {
#ifdef __APPLE__
        constexpr const char* defaults[]{
            "libvulkan.dylib",   "libvulkan.1.dylib",       "/usr/local/lib/libvulkan.dylib",
            "libMoltenVK.dylib", "vulkan.framework/vulkan", "MoltenVK.framework/MoltenVK"};
#else
        constexpr const char* defaults[]{"libvulkan.so.1", "libvulkan.so"};
#endif
        for (const char* name : defaults) {
            module_ = dlopen(name, flags);
            if (module_ != nullptr) {
                break;
            }
        }
    }
    if (module_ == nullptr) {
        return nullptr;
    }
    // POSIX permits converting the address returned by dlsym to its declared function pointer type.
    // NOLINTNEXTLINE(cppcoreguidelines-pro-type-reinterpret-cast)
    return reinterpret_cast<PFN_vkGetInstanceProcAddr>(dlsym(module_, "vkGetInstanceProcAddr"));
#endif
}

loader::~loader() {
    if (module_ != nullptr) {
#ifdef _WIN32
        FreeLibrary(static_cast<HMODULE>(module_));
#else
        dlclose(module_);
#endif
    }
}

} // namespace khaoz::vulkan
