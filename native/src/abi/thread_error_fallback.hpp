#pragma once

#if defined(__APPLE__)
#include <libproc.h>
#include <pthread.h>
#include <unistd.h>

#include <algorithm>
#include <array>
#include <atomic>
#include <cerrno>
#include <cstdint>
#endif

namespace khaoz::abi {

#if defined(__linux__)

// The loader reserves this byte at dlopen, before a guard can run on any thread.
// Initial-exec avoids the lazy dynamic TLS allocation path in glibc.
class ThreadErrorFallback {
  public:
    void record() noexcept { active_ = true; }
    void clear() noexcept { active_ = false; }
    [[nodiscard]] bool active() const noexcept { return active_; }

  private:
    [[gnu::tls_model("initial-exec")]] static constinit thread_local bool active_;
};

#elif defined(__APPLE__)

// Darwin thread identifiers are issued for the lifetime of a thread. Store
// only the identifier here so even a rejected pthread write needs no allocation.
class EmergencyErrorOwners {
  public:
    void record() noexcept {
        const auto thread = current_thread();
        if (thread == 0 || contains(thread)) {
            return;
        }
        for (auto& owner : owners_) {
            std::uint64_t vacant = 0;
            if (owner.compare_exchange_strong(vacant, thread, std::memory_order_relaxed)) {
                return;
            }
        }
        for (auto& owner : owners_) {
            auto previous = owner.load(std::memory_order_relaxed);
            if (can_reuse(previous) && owner.compare_exchange_strong(previous, thread, std::memory_order_relaxed)) {
                return;
            }
        }
    }

    void clear() noexcept {
        const auto thread = current_thread();
        if (thread != 0) {
            for (auto& owner : owners_) {
                auto expected = thread;
                if (owner.compare_exchange_strong(expected, 0, std::memory_order_relaxed)) {
                    return;
                }
            }
        }
    }

    [[nodiscard]] bool active() const noexcept {
        const auto thread = current_thread();
        return thread != 0 && contains(thread);
    }

  private:
    static std::uint64_t current_thread() noexcept {
        std::uint64_t thread = 0;
        return pthread_threadid_np(nullptr, &thread) == 0 ? thread : 0;
    }

    static bool can_reuse(std::uint64_t thread) noexcept {
        if (thread == 0) {
            return true;
        }
        const int saved_errno = errno;
        proc_threadinfo info{};
        const auto result = proc_pidinfo(getpid(), PROC_PIDTHREADID64INFO, thread, &info, sizeof(info));
        const bool exited = result == 0 && errno == ESRCH;
        errno = saved_errno;
        return exited;
    }

    [[nodiscard]] bool contains(std::uint64_t thread) const noexcept {
        return std::any_of(owners_.begin(), owners_.end(),
                           [thread](const auto& owner) { return owner.load(std::memory_order_relaxed) == thread; });
    }

    std::array<std::atomic<std::uint64_t>, 1024> owners_{};
};

class ThreadErrorFallback {
  public:
    ThreadErrorFallback() noexcept : valid_(pthread_key_create(&key_, nullptr) == 0) {}

    ~ThreadErrorFallback() {
        if (valid_ && !published_.load(std::memory_order_relaxed)) {
            pthread_key_delete(key_);
        }
    }

    void record() noexcept {
        if (valid_ && pthread_setspecific(key_, &marker_) == 0) {
            published_.store(true, std::memory_order_relaxed);
            return;
        }
        emergency_.record();
    }

    void clear() noexcept {
        if (valid_ && pthread_getspecific(key_) != nullptr) {
            pthread_setspecific(key_, nullptr);
        }
        emergency_.clear();
    }

    [[nodiscard]] bool active() const noexcept {
        return (valid_ && pthread_getspecific(key_) != nullptr) || emergency_.active();
    }

  private:
    pthread_key_t key_{};
    bool valid_;
    std::atomic<bool> published_{false};
    char marker_{};
    EmergencyErrorOwners emergency_;
};

#endif

} // namespace khaoz::abi
