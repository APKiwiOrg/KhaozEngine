#ifndef KHAOZ_NATIVE_H
#define KHAOZ_NATIVE_H

#include <stddef.h>
#include <stdint.h>

#if defined(_WIN32)
#if defined(KHAOZ_NATIVE_BUILD)
#define KHAOZ_API __declspec(dllexport)
#else
#define KHAOZ_API
#endif
#else
#define KHAOZ_API __attribute__((visibility("default")))
#endif

#ifdef __cplusplus
extern "C" {
#endif

#define KHAOZ_NATIVE_ABI_VERSION 1u

typedef enum khaoz_result {
    KHAOZ_OK = 0,
    KHAOZ_ERROR_INVALID_ARGUMENT = 1,
    KHAOZ_ERROR_VULKAN_UNAVAILABLE = 2,
    KHAOZ_ERROR_DEVICE_LOST = 3,
    KHAOZ_ERROR_OUT_OF_MEMORY = 4,
    KHAOZ_ERROR_UNSUPPORTED = 5,
    KHAOZ_ERROR_INTERNAL = 6
} khaoz_result;

KHAOZ_API uint32_t khaoz_native_abi_version(void);

/* Returns the current thread's error length in bytes, excluding the NUL.
 * A non-null buffer with positive capacity receives a NUL-terminated prefix.
 */
KHAOZ_API size_t khaoz_last_error_message(char* buffer, size_t capacity);

#ifdef __cplusplus
}
#endif

#endif
