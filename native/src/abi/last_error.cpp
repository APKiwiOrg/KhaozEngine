#include "last_error.hpp"

#include "khaoz/khaoz_native.h"

#include <algorithm>
#include <string_view>

#if defined(_WIN32)
#include <string>
#else
#include "thread_error_fallback.hpp"

#include <pthread.h>
#include <string.h>

#include <atomic>
#include <cstdlib>
#include <memory>
#endif

#if defined(__linux__)
constinit thread_local bool khaoz::abi::ThreadErrorFallback::active_ = false;
#endif

namespace {

constexpr std::string_view storage_failure = "Unable to store the exception message";

#if defined(_WIN32)
thread_local std::string last_error;
thread_local bool error_storage_failed = false;
#else
using ErrorMessage = std::unique_ptr<char, decltype(&std::free)>;

// POSIX thread-specific slots report allocation failure without entering the loader's TLS resolver.
class ThreadErrors {
  public:
    ThreadErrors() noexcept : message_key_valid_(pthread_key_create(&message_key_, std::free) == 0) {}

    ~ThreadErrors() {
        // Thread exit can race dlclose. Published slots stay registered so libc
        // can finish their cleanup without calling back into this library.
        if (message_key_valid_ && !published_.load(std::memory_order_relaxed)) {
            pthread_key_delete(message_key_);
        }
    }

    void record(const char* message) noexcept {
        if (!message_key_valid_) {
            fallback_.record();
            return;
        }
        auto* previous = static_cast<char*>(pthread_getspecific(message_key_));
        auto* replacement = ::strdup(message);
        if (pthread_setspecific(message_key_, replacement) != 0) {
            const ErrorMessage uninstalled(replacement, std::free);
            fallback_.record();
            return;
        }
        const ErrorMessage old_message(previous, std::free);
        if (replacement == nullptr) {
            fallback_.record();
            return;
        }
        published_.store(true, std::memory_order_relaxed);
        fallback_.clear();
    }

    [[nodiscard]] std::string_view message() const noexcept {
        if (fallback_.active()) {
            return storage_failure;
        }
        if (message_key_valid_) {
            if (const auto* value = static_cast<const char*>(pthread_getspecific(message_key_))) {
                return value;
            }
        }
        return {};
    }

  private:
    khaoz::abi::ThreadErrorFallback fallback_;
    pthread_key_t message_key_{};
    bool message_key_valid_;
    std::atomic<bool> published_{false};
};

ThreadErrors last_error;
#endif

} // namespace

void khaoz::abi::set_last_error(const char* message) noexcept {
#if defined(_WIN32)
    try {
        last_error.assign(message);
        error_storage_failed = false;
    } catch (...) {
        // Error reporting must still work when copying the diagnostic fails.
        error_storage_failed = true;
    }
#else
    last_error.record(message);
#endif
}

size_t khaoz_last_error_message(char* buffer, size_t capacity) {
#if defined(_WIN32)
    const std::string_view message = error_storage_failed ? storage_failure : std::string_view{last_error};
#else
    const std::string_view message = last_error.message();
#endif
    if (buffer != nullptr && capacity > 0) {
        const auto count = std::min(message.size(), capacity - 1);
        if (count > 0) {
            std::copy_n(message.begin(), count, buffer);
        }
        buffer[count] = '\0';
    }
    return message.size();
}
