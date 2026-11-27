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

/* Calls on a device and its children must be serialized by the caller.
 * Keep referenced resources alive and unmapped from recording through GPU completion.
 * Child destruction consumes the handle. An inconclusive drain defers its Vulkan cleanup to the device.
 */
typedef struct khaoz_gpu_device khaoz_gpu_device;
typedef struct khaoz_gpu_buffer khaoz_gpu_buffer;
typedef struct khaoz_gpu_texture khaoz_gpu_texture;
typedef struct khaoz_gpu_sampler khaoz_gpu_sampler;
typedef struct khaoz_gpu_fence khaoz_gpu_fence;
typedef struct khaoz_gpu_command_list khaoz_gpu_command_list;

typedef struct khaoz_gpu_device_desc {
    uint32_t enable_validation;
    const char* device_name_contains; /* Standalone ASCII substring fallback when no selector is supplied. */
} khaoz_gpu_device_desc;

typedef struct khaoz_gpu_adapter_info {
    char device_name[256];
    uint32_t device_type;
    uint32_t device_id;
    uint32_t mesa_llvmpipe;
    const char* rejection_reason;
} khaoz_gpu_adapter_info;

/* The candidates are in Vulkan enumeration order and are valid during the callback.
 * Return the index of an eligible candidate, or -1 to decline. Do not re-enter the Vulkan ABI.
 */
typedef int32_t (*khaoz_gpu_select_adapter)(const khaoz_gpu_adapter_info* candidates, uint32_t count, void* context);

typedef struct khaoz_gpu_device_selection {
    khaoz_gpu_select_adapter select;
    void* context;
} khaoz_gpu_device_selection;

typedef enum khaoz_gpu_startup_status {
    KHAOZ_GPU_STARTUP_DEFAULT = 0,
    KHAOZ_GPU_STARTUP_NO_DRIVER = 1
} khaoz_gpu_startup_status;

typedef struct khaoz_gpu_capabilities {
    uint32_t clip_space_y_inverted;
    uint32_t depth_range_zero_to_one;
    char device_name[256];
    uint32_t sampler_anisotropy;
    uint32_t sampler_lod_bias;
    uint32_t max_msaa_sample_count;
    uint32_t supported_msaa_sample_counts;
    uint32_t supports_shadow_maps;
    uint32_t supports_compute;
    uint32_t max_storage_buffer_range_bytes;
    uint32_t max_render_texture_2d_dimension;
    uint32_t supports_timestamps;
    uint32_t supports_shader_float_controls;
    uint32_t max_color_attachments;
    uint32_t supports_completion_fences;
    uint32_t max_fragment_storage_buffers;
} khaoz_gpu_capabilities;

/* The default entry point reads KHAOZ_NATIVE_VULKAN_LOADER. The explicit entry point uses the
 * supplied path, or the platform loader when null. This also supports managed environment overrides.
 */
KHAOZ_API khaoz_result khaoz_gpu_device_create_headless(const khaoz_gpu_device_desc* desc,
                                                        khaoz_gpu_device** out_device);
KHAOZ_API khaoz_result khaoz_gpu_device_create_headless_with_loader(const khaoz_gpu_device_desc* desc,
                                                                    const char* loader_path,
                                                                    khaoz_gpu_device** out_device);
/* Selects from the same adapter enumeration used to create the logical device.
 * out_startup_status may be null. When supplied, it refines a Vulkan-unavailable result.
 */
KHAOZ_API khaoz_result khaoz_gpu_device_create_headless_with_selection(const khaoz_gpu_device_desc* desc,
                                                                       const char* loader_path,
                                                                       const khaoz_gpu_device_selection* selection,
                                                                       uint32_t* out_startup_status,
                                                                       khaoz_gpu_device** out_device);
KHAOZ_API khaoz_result khaoz_gpu_device_probe(const char* loader_path);
KHAOZ_API khaoz_result khaoz_gpu_device_capabilities(const khaoz_gpu_device* device, khaoz_gpu_capabilities* out_caps);
KHAOZ_API uint32_t khaoz_gpu_device_validation_errors(const khaoz_gpu_device* device);
/* Releases the device and its children. Live children count as a validation error when validation is enabled.
 * out_validation_errors may be null. The returned count includes errors issued during teardown.
 * If waiting fails without confirmed device loss, the device remains owned by the caller for a later destroy.
 * A return of KHAOZ_OK or KHAOZ_ERROR_DEVICE_LOST consumes the handle.
 */
KHAOZ_API khaoz_result khaoz_gpu_device_destroy(khaoz_gpu_device* device, uint32_t* out_validation_errors);
KHAOZ_API khaoz_result khaoz_gpu_device_wait_for_idle(khaoz_gpu_device* device);
KHAOZ_API khaoz_result khaoz_gpu_device_submit(khaoz_gpu_device* device, khaoz_gpu_command_list* commands,
                                               khaoz_gpu_fence* completion);

/* These ABI values mirror the corresponding managed descriptions. */
typedef enum khaoz_gpu_buffer_usage {
    KHAOZ_GPU_BUFFER_VERTEX = 1u << 0u,
    KHAOZ_GPU_BUFFER_INDEX = 1u << 1u,
    KHAOZ_GPU_BUFFER_UNIFORM = 1u << 2u,
    KHAOZ_GPU_BUFFER_STORAGE_READ_ONLY = 1u << 3u,
    KHAOZ_GPU_BUFFER_STORAGE_READ_WRITE = 1u << 4u,
    KHAOZ_GPU_BUFFER_INDIRECT = 1u << 5u,
    KHAOZ_GPU_BUFFER_DYNAMIC = 1u << 6u,
    KHAOZ_GPU_BUFFER_STAGING = 1u << 7u
} khaoz_gpu_buffer_usage;

typedef enum khaoz_gpu_texture_usage {
    KHAOZ_GPU_TEXTURE_SAMPLED = 1u << 0u,
    KHAOZ_GPU_TEXTURE_RENDER_TARGET = 1u << 1u,
    KHAOZ_GPU_TEXTURE_DEPTH_STENCIL = 1u << 2u,
    KHAOZ_GPU_TEXTURE_STAGING = 1u << 3u,
    KHAOZ_GPU_TEXTURE_CUBEMAP = 1u << 4u,
    KHAOZ_GPU_TEXTURE_GENERATE_MIPMAPS = 1u << 5u,
    KHAOZ_GPU_TEXTURE_STORAGE = 1u << 6u
} khaoz_gpu_texture_usage;

typedef enum khaoz_gpu_pixel_format {
    KHAOZ_GPU_RGBA8_UNORM = 0,
    KHAOZ_GPU_R32_FLOAT = 1,
    KHAOZ_GPU_D32_FLOAT_S8_UINT = 2,
    KHAOZ_GPU_D24_UNORM_S8_UINT = 3,
    KHAOZ_GPU_R8_UNORM = 4,
    KHAOZ_GPU_BGRA8_UNORM = 5,
    KHAOZ_GPU_RGBA16_FLOAT = 6,
    KHAOZ_GPU_RG16_FLOAT = 7,
    KHAOZ_GPU_RGBA32_FLOAT = 8
} khaoz_gpu_pixel_format;

typedef enum khaoz_gpu_map_mode {
    KHAOZ_GPU_MAP_READ = 0,
    KHAOZ_GPU_MAP_WRITE = 1,
    KHAOZ_GPU_MAP_READ_WRITE = 2
} khaoz_gpu_map_mode;

typedef enum khaoz_gpu_sampler_filter {
    KHAOZ_GPU_FILTER_POINT = 0,
    KHAOZ_GPU_FILTER_LINEAR = 1,
    KHAOZ_GPU_FILTER_ANISOTROPIC = 2
} khaoz_gpu_sampler_filter;

typedef enum khaoz_gpu_sampler_address {
    KHAOZ_GPU_ADDRESS_REPEAT = 0,
    KHAOZ_GPU_ADDRESS_MIRROR = 1,
    KHAOZ_GPU_ADDRESS_CLAMP = 2,
    KHAOZ_GPU_ADDRESS_BORDER = 3
} khaoz_gpu_sampler_address;

typedef struct khaoz_gpu_texture_desc {
    uint32_t width;
    uint32_t height;
    uint32_t mip_levels;
    uint32_t array_layers; /* Logical cubes when KHAOZ_GPU_TEXTURE_CUBEMAP is set. */
    uint32_t format;
    uint32_t usage;
    uint32_t sample_count;
    uint32_t is_array;
} khaoz_gpu_texture_desc;

typedef struct khaoz_gpu_sampler_desc {
    uint32_t filter;
    uint32_t address_u;
    uint32_t address_v;
    uint32_t address_w;
    uint32_t maximum_anisotropy;
    int32_t mip_lod_bias;
} khaoz_gpu_sampler_desc;

typedef struct khaoz_gpu_mapped_data {
    void* data;
    uint32_t row_pitch;
    uint32_t size_in_bytes;
} khaoz_gpu_mapped_data;

typedef struct khaoz_gpu_texture_region {
    uint32_t x;
    uint32_t y;
    uint32_t width;
    uint32_t height;
    uint32_t mip_level;
    uint32_t array_layer;
} khaoz_gpu_texture_region;

typedef struct khaoz_gpu_texture_copy {
    uint32_t src_mip_level;
    uint32_t src_array_layer;
    uint32_t dst_mip_level;
    uint32_t dst_array_layer;
    uint32_t width;
    uint32_t height;
} khaoz_gpu_texture_copy;

KHAOZ_API khaoz_result khaoz_gpu_buffer_create(khaoz_gpu_device* device, uint32_t size_in_bytes, uint32_t usage,
                                               khaoz_gpu_buffer** out_buffer);
KHAOZ_API khaoz_result khaoz_gpu_buffer_destroy(khaoz_gpu_buffer* buffer);
KHAOZ_API khaoz_result khaoz_gpu_buffer_update(khaoz_gpu_buffer* buffer, uint32_t offset, const void* data,
                                               uint32_t size);
KHAOZ_API khaoz_result khaoz_gpu_buffer_map(khaoz_gpu_buffer* buffer, uint32_t mode, khaoz_gpu_mapped_data* out_data);
KHAOZ_API khaoz_result khaoz_gpu_buffer_unmap(khaoz_gpu_buffer* buffer);

KHAOZ_API khaoz_result khaoz_gpu_texture_create(khaoz_gpu_device* device, const khaoz_gpu_texture_desc* desc,
                                                khaoz_gpu_texture** out_texture);
KHAOZ_API khaoz_result khaoz_gpu_texture_destroy(khaoz_gpu_texture* texture);
KHAOZ_API khaoz_result khaoz_gpu_texture_update(khaoz_gpu_texture* texture, const khaoz_gpu_texture_region* region,
                                                const void* data, uint32_t size);
/* Maps mip zero, array layer zero of a staging texture. */
KHAOZ_API khaoz_result khaoz_gpu_texture_map(khaoz_gpu_texture* texture, uint32_t mode,
                                             khaoz_gpu_mapped_data* out_data);
KHAOZ_API khaoz_result khaoz_gpu_texture_unmap(khaoz_gpu_texture* texture);

KHAOZ_API khaoz_result khaoz_gpu_sampler_create(khaoz_gpu_device* device, const khaoz_gpu_sampler_desc* desc,
                                                khaoz_gpu_sampler** out_sampler);
KHAOZ_API khaoz_result khaoz_gpu_sampler_destroy(khaoz_gpu_sampler* sampler);

KHAOZ_API khaoz_result khaoz_gpu_fence_create(khaoz_gpu_device* device, khaoz_gpu_fence** out_fence);
KHAOZ_API khaoz_result khaoz_gpu_fence_destroy(khaoz_gpu_fence* fence);
KHAOZ_API khaoz_result khaoz_gpu_fence_signaled(khaoz_gpu_fence* fence, uint32_t* out_signaled);
KHAOZ_API khaoz_result khaoz_gpu_fence_reset(khaoz_gpu_fence* fence);

KHAOZ_API khaoz_result khaoz_gpu_command_list_create(khaoz_gpu_device* device, khaoz_gpu_command_list** out_commands);
KHAOZ_API khaoz_result khaoz_gpu_command_list_destroy(khaoz_gpu_command_list* commands);
KHAOZ_API khaoz_result khaoz_gpu_command_list_begin(khaoz_gpu_command_list* commands);
KHAOZ_API khaoz_result khaoz_gpu_command_list_end(khaoz_gpu_command_list* commands);
KHAOZ_API khaoz_result khaoz_gpu_command_list_copy_buffer(khaoz_gpu_command_list* commands, khaoz_gpu_buffer* src,
                                                          uint32_t src_offset, khaoz_gpu_buffer* dst,
                                                          uint32_t dst_offset, uint32_t size);
/* A null copy descriptor copies all matching subresources. */
KHAOZ_API khaoz_result khaoz_gpu_command_list_copy_texture(khaoz_gpu_command_list* commands, khaoz_gpu_texture* src,
                                                           khaoz_gpu_texture* dst, const khaoz_gpu_texture_copy* copy);

#ifdef __cplusplus
}
#endif

#endif
