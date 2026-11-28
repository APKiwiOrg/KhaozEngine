#pragma once

#include "khaoz/khaoz_native.h"
#include "last_error.hpp"

#include <exception>
#include <new>
#include <utility>

namespace khaoz::abi {

template <class F> khaoz_result guard(F&& body) noexcept {
    try {
        return std::forward<F>(body)();
    } catch (const std::bad_alloc& error) {
        set_last_error(error.what());
        return KHAOZ_ERROR_OUT_OF_MEMORY;
    } catch (const std::exception& error) {
        set_last_error(error.what());
        return KHAOZ_ERROR_INTERNAL;
    } catch (...) {
        set_last_error("Unknown C++ exception");
        return KHAOZ_ERROR_INTERNAL;
    }
}

} // namespace khaoz::abi
