#pragma once

#include "vulkan.hpp"

#include <optional>
#include <string>

namespace khaoz::vulkan {

[[nodiscard]] std::optional<std::string> loader_environment();

class loader {
  public:
    loader() = default;
    ~loader();
    loader(const loader&) = delete;
    loader& operator=(const loader&) = delete;

    PFN_vkGetInstanceProcAddr open(const char* override_path);

  private:
    void* module_{};
};

} // namespace khaoz::vulkan
