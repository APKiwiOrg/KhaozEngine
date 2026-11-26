#define DOCTEST_CONFIG_IMPLEMENT_WITH_MAIN
#include <doctest/doctest.h>

#include "abi/abi_guard.hpp"
#include "khaoz/khaoz_native.h"

#include <array>
#include <cstring>
#include <exception>
#include <new>
#include <stdexcept>
#include <string>
#include <string_view>
#include <thread>

TEST_CASE("guard returns KHAOZ_OK when the body returns KHAOZ_OK") {
    CHECK(khaoz::abi::guard([] { return KHAOZ_OK; }) == KHAOZ_OK);
}

TEST_CASE("guard maps std::bad_alloc to KHAOZ_ERROR_OUT_OF_MEMORY") {
    CHECK(khaoz::abi::guard([]() -> khaoz_result { throw std::bad_alloc(); }) == KHAOZ_ERROR_OUT_OF_MEMORY);
}

TEST_CASE("guard maps std::out_of_range to KHAOZ_ERROR_INTERNAL and records a non-empty last error") {
    const std::out_of_range error("outside the range");
    CHECK(khaoz::abi::guard([&error]() -> khaoz_result { throw error; }) == KHAOZ_ERROR_INTERNAL);
    CHECK(khaoz_last_error_message(nullptr, 0) == std::strlen(error.what()));
}

TEST_CASE("guard maps a non-standard exception to KHAOZ_ERROR_INTERNAL") {
    const auto error = std::make_exception_ptr(1);
    CHECK(khaoz::abi::guard([&error]() -> khaoz_result { std::rethrow_exception(error); }) == KHAOZ_ERROR_INTERNAL);
}

TEST_CASE("guard passes through a body's own error code unchanged") {
    CHECK(khaoz::abi::guard([] { return KHAOZ_ERROR_UNSUPPORTED; }) == KHAOZ_ERROR_UNSUPPORTED);
}

TEST_CASE("khaoz_native_abi_version returns KHAOZ_NATIVE_ABI_VERSION") {
    CHECK(khaoz_native_abi_version() == KHAOZ_NATIVE_ABI_VERSION);
}

TEST_CASE("khaoz_last_error_message returns the full length when the buffer is too small and NUL-terminates") {
    const std::out_of_range error(std::string(2048, 'x'));
    REQUIRE(khaoz::abi::guard([&error]() -> khaoz_result { throw error; }) == KHAOZ_ERROR_INTERNAL);
    const auto length = std::strlen(error.what());
    REQUIRE(khaoz_last_error_message(nullptr, 0) == length);
    std::string full_message(length + 1, '#');
    REQUIRE(khaoz_last_error_message(full_message.data(), full_message.size()) == length);
    CHECK(full_message[length - 1] != '#');
    CHECK(full_message.back() == '\0');

    std::array<char, 4> buffer{'?', '?', '?', '?'};
    CHECK(khaoz_last_error_message(buffer.data(), 0) == length);
    CHECK(buffer.front() == '?');
    CHECK(khaoz_last_error_message(buffer.data(), 3) == length);
    CHECK(std::string_view(buffer.data(), 2) == std::string_view(full_message.data(), 2));
    CHECK(buffer[2] == '\0');
    CHECK(buffer[3] == '?');
}

TEST_CASE("khaoz_last_error_message keeps errors on their originating thread") {
    const std::out_of_range main_error("main");
    REQUIRE(khaoz::abi::guard([&main_error]() -> khaoz_result { throw main_error; }) == KHAOZ_ERROR_INTERNAL);
    std::size_t worker_initial_length = 0;
    std::size_t worker_error_length = 0;
    const std::out_of_range worker_error("worker");
    std::thread worker([&] {
        worker_initial_length = khaoz_last_error_message(nullptr, 0);
        khaoz::abi::guard([&worker_error]() -> khaoz_result { throw worker_error; });
        worker_error_length = khaoz_last_error_message(nullptr, 0);
    });
    worker.join();

    CHECK(worker_initial_length == 0);
    CHECK(worker_error_length == std::strlen(worker_error.what()));
    CHECK(khaoz_last_error_message(nullptr, 0) == std::strlen(main_error.what()));
}
