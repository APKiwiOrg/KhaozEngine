#include "last_error.hpp"

#include "khaoz/khaoz_native.h"

#include <algorithm>
#include <string>
#include <string_view>

namespace {

thread_local std::string last_error;
thread_local bool error_storage_failed = false;

} // namespace

void khaoz::abi::set_last_error(const char* message) noexcept {
    try {
        last_error.assign(message);
        error_storage_failed = false;
    } catch (...) {
        // Error reporting must still work when copying the diagnostic fails.
        error_storage_failed = true;
    }
}

size_t khaoz_last_error_message(char* buffer, size_t capacity) {
    constexpr std::string_view storage_failure = "Unable to store the exception message";
    const std::string_view message = error_storage_failed ? storage_failure : std::string_view{last_error};
    if (buffer != nullptr && capacity > 0) {
        const auto count = std::min(message.size(), capacity - 1);
        std::copy_n(message.begin(), count, buffer);
        buffer[count] = '\0';
    }
    return message.size();
}
