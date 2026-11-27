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

/* A row-major 3x4 affine transform followed by draw identifiers and an unsigned sort key. */
typedef struct khaoz_draw_record {
    float world[12];
    uint32_t mesh_id;
    uint32_t material_id;
    uint64_t sort_key;
} khaoz_draw_record;

typedef struct khaoz_frame khaoz_frame;

/* A frame owns fixed-capacity host storage. Serialize calls on each frame. */
KHAOZ_API khaoz_result khaoz_frame_create(uint32_t capacity, khaoz_frame** out_frame);

/* Stable-sorts a copy into the frame. Invalid input leaves the previous frame intact.
 * A null records pointer is valid for an empty submit.
 */
KHAOZ_API khaoz_result khaoz_frame_submit(khaoz_frame* frame, const khaoz_draw_record* records, uint32_t count);

/* Copies all sorted keys into a destination with room for every submitted record.
 * out_keys may be null for an empty frame. out_count is required and written on success.
 */
KHAOZ_API khaoz_result khaoz_frame_sorted_keys(const khaoz_frame* frame, uint64_t* out_keys, uint32_t capacity,
                                               uint32_t* out_count);
KHAOZ_API void khaoz_frame_destroy(khaoz_frame* frame);

#ifdef __cplusplus
}
#endif

#endif
