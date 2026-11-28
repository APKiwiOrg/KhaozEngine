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

/* This version identifies the public export set. Bump it for any added, removed or changed export. */
#define KHAOZ_NATIVE_ABI_VERSION 3u

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
typedef struct khaoz_gpu_shader_set khaoz_gpu_shader_set;
typedef struct khaoz_gpu_resource_layout khaoz_gpu_resource_layout;
typedef struct khaoz_gpu_resource_set khaoz_gpu_resource_set;
typedef struct khaoz_gpu_pipeline khaoz_gpu_pipeline;
typedef struct khaoz_gpu_framebuffer khaoz_gpu_framebuffer;

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
    uint32_t max_dynamic_uniform_buffers;
    uint64_t uniform_offset_alignment;
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

/* SPIR-V consists of aligned 32-bit words. Both entry points are named main. */
KHAOZ_API khaoz_result khaoz_gpu_shader_set_create(khaoz_gpu_device* device, const uint32_t* vertex,
                                                   uint32_t vertex_words, const uint32_t* fragment,
                                                   uint32_t fragment_words, khaoz_gpu_shader_set** out_shaders);
KHAOZ_API khaoz_result khaoz_gpu_shader_set_destroy(khaoz_gpu_shader_set* shaders);

typedef enum khaoz_gpu_resource_kind {
    KHAOZ_GPU_RESOURCE_UNIFORM = 0,
    KHAOZ_GPU_RESOURCE_STORAGE_READ_ONLY = 1,
    KHAOZ_GPU_RESOURCE_STORAGE_READ_WRITE = 2,
    KHAOZ_GPU_RESOURCE_SAMPLED_IMAGE = 3,
    KHAOZ_GPU_RESOURCE_STORAGE_IMAGE = 4,
    KHAOZ_GPU_RESOURCE_SAMPLER = 5
} khaoz_gpu_resource_kind;

typedef struct khaoz_gpu_resource_element {
    uint32_t kind;   /* khaoz_gpu_resource_kind */
    uint32_t stages; /* GpuShaderStages flags */
    uint32_t dynamic_offset;
} khaoz_gpu_resource_element;

/* Element index is the binding number. */
KHAOZ_API khaoz_result khaoz_gpu_resource_layout_create(khaoz_gpu_device* device,
                                                        const khaoz_gpu_resource_element* elements, uint32_t count,
                                                        khaoz_gpu_resource_layout** out_layout);
KHAOZ_API khaoz_result khaoz_gpu_resource_layout_destroy(khaoz_gpu_resource_layout* layout);

typedef struct khaoz_gpu_resource_binding {
    khaoz_gpu_buffer* buffer;
    khaoz_gpu_texture* texture;
    khaoz_gpu_sampler* sampler;
    uint32_t offset;
    uint32_t range;
} khaoz_gpu_resource_binding;

KHAOZ_API khaoz_result khaoz_gpu_resource_set_create(khaoz_gpu_device* device, khaoz_gpu_resource_layout* layout,
                                                     const khaoz_gpu_resource_binding* resources, uint32_t count,
                                                     khaoz_gpu_resource_set** out_set);
KHAOZ_API khaoz_result khaoz_gpu_resource_set_destroy(khaoz_gpu_resource_set* set);

typedef struct khaoz_gpu_vertex_layout {
    const uint32_t* formats; /* GpuVertexElementFormat values */
    uint32_t count;
    uint32_t stride;
    uint32_t instance_step_rate;
} khaoz_gpu_vertex_layout;

typedef struct khaoz_gpu_blend_attachment {
    uint32_t enabled;
    uint32_t src_color;
    uint32_t dst_color;
    uint32_t color_op;
    uint32_t src_alpha;
    uint32_t dst_alpha;
    uint32_t alpha_op;
} khaoz_gpu_blend_attachment;

typedef struct khaoz_gpu_pipeline_desc {
    khaoz_gpu_shader_set* shaders;
    khaoz_gpu_resource_layout* const* layouts; /* Array index is the set number. */
    uint32_t layout_count;
    const khaoz_gpu_vertex_layout* vertex_layouts;
    uint32_t vertex_layout_count;
    const uint32_t* color_formats;
    const khaoz_gpu_blend_attachment* blends;
    uint32_t color_count;
    const uint32_t* depth_format; /* Null when there is no depth attachment. */
    uint32_t sample_count;
    float blend_constant[4];
    uint32_t depth_test;
    uint32_t depth_write;
    uint32_t depth_compare;
    uint32_t depth_clip;
    uint32_t cull;
    uint32_t fill;
    uint32_t front_face;
    uint32_t topology;
} khaoz_gpu_pipeline_desc;

KHAOZ_API khaoz_result khaoz_gpu_pipeline_create(khaoz_gpu_device* device, const khaoz_gpu_pipeline_desc* desc,
                                                 khaoz_gpu_pipeline** out_pipeline);
KHAOZ_API khaoz_result khaoz_gpu_pipeline_destroy(khaoz_gpu_pipeline* pipeline);

KHAOZ_API khaoz_result khaoz_gpu_framebuffer_create(khaoz_gpu_device* device, khaoz_gpu_texture* depth,
                                                    khaoz_gpu_texture* const* colors, uint32_t count,
                                                    khaoz_gpu_framebuffer** out_framebuffer);
KHAOZ_API khaoz_result khaoz_gpu_framebuffer_destroy(khaoz_gpu_framebuffer* framebuffer);

KHAOZ_API khaoz_result khaoz_gpu_command_list_set_framebuffer(khaoz_gpu_command_list* commands,
                                                              khaoz_gpu_framebuffer* framebuffer);
typedef struct khaoz_gpu_color {
    float r;
    float g;
    float b;
    float a;
} khaoz_gpu_color;

KHAOZ_API khaoz_result khaoz_gpu_command_list_clear_color(khaoz_gpu_command_list* commands, uint32_t index,
                                                          const khaoz_gpu_color* color);
KHAOZ_API khaoz_result khaoz_gpu_command_list_clear_depth(khaoz_gpu_command_list* commands, float depth);
KHAOZ_API khaoz_result khaoz_gpu_command_list_set_pipeline(khaoz_gpu_command_list* commands,
                                                           khaoz_gpu_pipeline* pipeline);
KHAOZ_API khaoz_result khaoz_gpu_command_list_set_resource_set(khaoz_gpu_command_list* commands, uint32_t slot,
                                                               khaoz_gpu_resource_set* set, uint32_t dynamic_offset);
KHAOZ_API khaoz_result khaoz_gpu_command_list_set_vertex_buffer(khaoz_gpu_command_list* commands, uint32_t slot,
                                                                khaoz_gpu_buffer* buffer, uint32_t offset);
KHAOZ_API khaoz_result khaoz_gpu_command_list_set_index_buffer(khaoz_gpu_command_list* commands,
                                                               khaoz_gpu_buffer* buffer, uint32_t format);
KHAOZ_API khaoz_result khaoz_gpu_command_list_set_scissor(khaoz_gpu_command_list* commands, int32_t x, int32_t y,
                                                          uint32_t width, uint32_t height);
KHAOZ_API khaoz_result khaoz_gpu_command_list_draw(khaoz_gpu_command_list* commands, uint32_t vertex_count,
                                                   uint32_t instance_count, uint32_t vertex_start,
                                                   uint32_t instance_start);
KHAOZ_API khaoz_result khaoz_gpu_command_list_draw_indexed(khaoz_gpu_command_list* commands, uint32_t index_count,
                                                           uint32_t instance_count, uint32_t index_start,
                                                           int32_t vertex_offset, uint32_t instance_start);
KHAOZ_API khaoz_result khaoz_gpu_command_list_update_buffer(khaoz_gpu_command_list* commands, khaoz_gpu_buffer* buffer,
                                                            uint32_t offset, const void* data, uint32_t size);

#ifdef __cplusplus
}
#endif

#endif
