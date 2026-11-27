#include "last_error.hpp"

#include "khaoz/khaoz_native.h"

#include <algorithm>
#include <string_view>

#if defined(_WIN32)
#include <string>
#else
#include <pthread.h>
#include <string.h>

#include <atomic>
#include <cstdlib>
#include <memory>
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
    ThreadErrors() noexcept {
        message_key_valid_ = pthread_key_create(&message_key_, std::free) == 0;
        failure_key_valid_ = pthread_key_create(&failure_key_, nullptr) == 0;
    }

    ~ThreadErrors() {
        // Thread exit can race dlclose. Published slots stay registered so libc
        // can finish their cleanup without calling back into this library.
        if (published_.load(std::memory_order_relaxed)) {
            return;
        }
        if (message_key_valid_) {
            pthread_key_delete(message_key_);
        }
        if (failure_key_valid_) {
            pthread_key_delete(failure_key_);
        }
    }

    void record(const char* message) noexcept {
        if (!message_key_valid_ || !failure_key_valid_) {
            return;
        }
        ErrorMessage copy(::strdup(message), std::free);
        auto* previous = static_cast<char*>(pthread_getspecific(message_key_));
        if (pthread_setspecific(message_key_, copy.get()) != 0) {
            registration_failed_.store(true, std::memory_order_relaxed);
            return;
        }
        const ErrorMessage old_message(previous, std::free);
        if (copy.release() != nullptr || pthread_setspecific(failure_key_, &failure_marker_) == 0) {
            published_.store(true, std::memory_order_relaxed);
        } else {
            registration_failed_.store(true, std::memory_order_relaxed);
        }
    }

    [[nodiscard]] std::string_view message() const noexcept {
        if (!message_key_valid_ || !failure_key_valid_) {
            return storage_failure;
        }
        if (const auto* value = static_cast<const char*>(pthread_getspecific(message_key_))) {
            return value;
        }
        if (pthread_getspecific(failure_key_) != nullptr || registration_failed_.load(std::memory_order_relaxed)) {
            return storage_failure;
        }
        return {};
    }

  private:
    pthread_key_t message_key_{};
    pthread_key_t failure_key_{};
    bool message_key_valid_ = false;
    bool failure_key_valid_ = false;
    std::atomic<bool> published_{false};
    std::atomic<bool> registration_failed_{false};
    char failure_marker_{};
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
