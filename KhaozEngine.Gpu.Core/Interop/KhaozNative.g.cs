using System;
using System.CodeDom.Compiler;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace KhaozEngine.Gpu.Core.Interop;

[GeneratedCode("ClangSharp", "23.1.5.0")]
internal enum khaoz_result
{
    KHAOZ_OK = 0,
    KHAOZ_ERROR_INVALID_ARGUMENT = 1,
    KHAOZ_ERROR_VULKAN_UNAVAILABLE = 2,
    KHAOZ_ERROR_DEVICE_LOST = 3,
    KHAOZ_ERROR_OUT_OF_MEMORY = 4,
    KHAOZ_ERROR_UNSUPPORTED = 5,
    KHAOZ_ERROR_INTERNAL = 6,
}

[GeneratedCode("ClangSharp", "23.1.5.0")]
public partial struct khaoz_draw_record
{
    [NativeTypeName("float[12]")]
    public _world_e__FixedBuffer world;

    [NativeTypeName("uint32_t")]
    public uint mesh_id;

    [NativeTypeName("uint32_t")]
    public uint material_id;

    [NativeTypeName("uint64_t")]
    public ulong sort_key;

    [InlineArray(12)]
    public partial struct _world_e__FixedBuffer
    {
        public float e0;
    }
}

[GeneratedCode("ClangSharp", "23.1.5.0")]
internal partial struct khaoz_frame
{
}

[GeneratedCode("ClangSharp", "23.1.5.0")]
internal partial struct khaoz_gpu_device
{
}

[GeneratedCode("ClangSharp", "23.1.5.0")]
internal partial struct khaoz_gpu_buffer
{
}

[GeneratedCode("ClangSharp", "23.1.5.0")]
internal partial struct khaoz_gpu_texture
{
}

[GeneratedCode("ClangSharp", "23.1.5.0")]
internal partial struct khaoz_gpu_sampler
{
}

[GeneratedCode("ClangSharp", "23.1.5.0")]
internal partial struct khaoz_gpu_fence
{
}

[GeneratedCode("ClangSharp", "23.1.5.0")]
internal partial struct khaoz_gpu_command_list
{
}

[GeneratedCode("ClangSharp", "23.1.5.0")]
internal unsafe partial struct khaoz_gpu_device_desc
{
    [NativeTypeName("uint32_t")]
    public uint enable_validation;

    [NativeTypeName("const char *")]
    public sbyte* device_name_contains;
}

[GeneratedCode("ClangSharp", "23.1.5.0")]
internal unsafe partial struct khaoz_gpu_adapter_info
{
    [NativeTypeName("char[256]")]
    public _device_name_e__FixedBuffer device_name;

    [NativeTypeName("uint32_t")]
    public uint device_type;

    [NativeTypeName("uint32_t")]
    public uint device_id;

    [NativeTypeName("uint32_t")]
    public uint mesa_llvmpipe;

    [NativeTypeName("const char *")]
    public sbyte* rejection_reason;

    [InlineArray(256)]
    public partial struct _device_name_e__FixedBuffer
    {
        public sbyte e0;
    }
}

[GeneratedCode("ClangSharp", "23.1.5.0")]
internal unsafe partial struct khaoz_gpu_device_selection
{
    [NativeTypeName("khaoz_gpu_select_adapter")]
    public delegate* unmanaged[Cdecl]<khaoz_gpu_adapter_info*, uint, void*, int> select;

    public void* context;
}

[GeneratedCode("ClangSharp", "23.1.5.0")]
internal enum khaoz_gpu_startup_status
{
    KHAOZ_GPU_STARTUP_DEFAULT = 0,
    KHAOZ_GPU_STARTUP_NO_DRIVER = 1,
}

[GeneratedCode("ClangSharp", "23.1.5.0")]
internal partial struct khaoz_gpu_capabilities
{
    [NativeTypeName("uint32_t")]
    public uint clip_space_y_inverted;

    [NativeTypeName("uint32_t")]
    public uint depth_range_zero_to_one;

    [NativeTypeName("char[256]")]
    public _device_name_e__FixedBuffer device_name;

    [NativeTypeName("uint32_t")]
    public uint sampler_anisotropy;

    [NativeTypeName("uint32_t")]
    public uint sampler_lod_bias;

    [NativeTypeName("uint32_t")]
    public uint max_msaa_sample_count;

    [NativeTypeName("uint32_t")]
    public uint supported_msaa_sample_counts;

    [NativeTypeName("uint32_t")]
    public uint supports_shadow_maps;

    [NativeTypeName("uint32_t")]
    public uint supports_compute;

    [NativeTypeName("uint32_t")]
    public uint max_storage_buffer_range_bytes;

    [NativeTypeName("uint32_t")]
    public uint max_render_texture_2d_dimension;

    [NativeTypeName("uint32_t")]
    public uint supports_timestamps;

    [NativeTypeName("uint32_t")]
    public uint supports_shader_float_controls;

    [NativeTypeName("uint32_t")]
    public uint max_color_attachments;

    [NativeTypeName("uint32_t")]
    public uint supports_completion_fences;

    [NativeTypeName("uint32_t")]
    public uint max_fragment_storage_buffers;

    [InlineArray(256)]
    public partial struct _device_name_e__FixedBuffer
    {
        public sbyte e0;
    }
}

[GeneratedCode("ClangSharp", "23.1.5.0")]
internal enum khaoz_gpu_buffer_usage
{
    KHAOZ_GPU_BUFFER_VERTEX = ((int)(1U << 0)),
    KHAOZ_GPU_BUFFER_INDEX = ((int)(1U << 1)),
    KHAOZ_GPU_BUFFER_UNIFORM = ((int)(1U << 2)),
    KHAOZ_GPU_BUFFER_STORAGE_READ_ONLY = ((int)(1U << 3)),
    KHAOZ_GPU_BUFFER_STORAGE_READ_WRITE = ((int)(1U << 4)),
    KHAOZ_GPU_BUFFER_INDIRECT = ((int)(1U << 5)),
    KHAOZ_GPU_BUFFER_DYNAMIC = ((int)(1U << 6)),
    KHAOZ_GPU_BUFFER_STAGING = ((int)(1U << 7)),
}

[GeneratedCode("ClangSharp", "23.1.5.0")]
internal enum khaoz_gpu_texture_usage
{
    KHAOZ_GPU_TEXTURE_SAMPLED = ((int)(1U << 0)),
    KHAOZ_GPU_TEXTURE_RENDER_TARGET = ((int)(1U << 1)),
    KHAOZ_GPU_TEXTURE_DEPTH_STENCIL = ((int)(1U << 2)),
    KHAOZ_GPU_TEXTURE_STAGING = ((int)(1U << 3)),
    KHAOZ_GPU_TEXTURE_CUBEMAP = ((int)(1U << 4)),
    KHAOZ_GPU_TEXTURE_GENERATE_MIPMAPS = ((int)(1U << 5)),
    KHAOZ_GPU_TEXTURE_STORAGE = ((int)(1U << 6)),
}

[GeneratedCode("ClangSharp", "23.1.5.0")]
internal enum khaoz_gpu_pixel_format
{
    KHAOZ_GPU_RGBA8_UNORM = 0,
    KHAOZ_GPU_R32_FLOAT = 1,
    KHAOZ_GPU_D32_FLOAT_S8_UINT = 2,
    KHAOZ_GPU_D24_UNORM_S8_UINT = 3,
    KHAOZ_GPU_R8_UNORM = 4,
    KHAOZ_GPU_BGRA8_UNORM = 5,
    KHAOZ_GPU_RGBA16_FLOAT = 6,
    KHAOZ_GPU_RG16_FLOAT = 7,
    KHAOZ_GPU_RGBA32_FLOAT = 8,
}

[GeneratedCode("ClangSharp", "23.1.5.0")]
internal enum khaoz_gpu_map_mode
{
    KHAOZ_GPU_MAP_READ = 0,
    KHAOZ_GPU_MAP_WRITE = 1,
    KHAOZ_GPU_MAP_READ_WRITE = 2,
}

[GeneratedCode("ClangSharp", "23.1.5.0")]
internal enum khaoz_gpu_sampler_filter
{
    KHAOZ_GPU_FILTER_POINT = 0,
    KHAOZ_GPU_FILTER_LINEAR = 1,
    KHAOZ_GPU_FILTER_ANISOTROPIC = 2,
}

[GeneratedCode("ClangSharp", "23.1.5.0")]
internal enum khaoz_gpu_sampler_address
{
    KHAOZ_GPU_ADDRESS_REPEAT = 0,
    KHAOZ_GPU_ADDRESS_MIRROR = 1,
    KHAOZ_GPU_ADDRESS_CLAMP = 2,
    KHAOZ_GPU_ADDRESS_BORDER = 3,
}

[GeneratedCode("ClangSharp", "23.1.5.0")]
internal partial struct khaoz_gpu_texture_desc
{
    [NativeTypeName("uint32_t")]
    public uint width;

    [NativeTypeName("uint32_t")]
    public uint height;

    [NativeTypeName("uint32_t")]
    public uint mip_levels;

    [NativeTypeName("uint32_t")]
    public uint array_layers;

    [NativeTypeName("uint32_t")]
    public uint format;

    [NativeTypeName("uint32_t")]
    public uint usage;

    [NativeTypeName("uint32_t")]
    public uint sample_count;

    [NativeTypeName("uint32_t")]
    public uint is_array;
}

[GeneratedCode("ClangSharp", "23.1.5.0")]
internal partial struct khaoz_gpu_sampler_desc
{
    [NativeTypeName("uint32_t")]
    public uint filter;

    [NativeTypeName("uint32_t")]
    public uint address_u;

    [NativeTypeName("uint32_t")]
    public uint address_v;

    [NativeTypeName("uint32_t")]
    public uint address_w;

    [NativeTypeName("uint32_t")]
    public uint maximum_anisotropy;

    [NativeTypeName("int32_t")]
    public int mip_lod_bias;
}

[GeneratedCode("ClangSharp", "23.1.5.0")]
internal unsafe partial struct khaoz_gpu_mapped_data
{
    public void* data;

    [NativeTypeName("uint32_t")]
    public uint row_pitch;

    [NativeTypeName("uint32_t")]
    public uint size_in_bytes;
}

[GeneratedCode("ClangSharp", "23.1.5.0")]
internal partial struct khaoz_gpu_texture_region
{
    [NativeTypeName("uint32_t")]
    public uint x;

    [NativeTypeName("uint32_t")]
    public uint y;

    [NativeTypeName("uint32_t")]
    public uint width;

    [NativeTypeName("uint32_t")]
    public uint height;

    [NativeTypeName("uint32_t")]
    public uint mip_level;

    [NativeTypeName("uint32_t")]
    public uint array_layer;
}

[GeneratedCode("ClangSharp", "23.1.5.0")]
internal partial struct khaoz_gpu_texture_copy
{
    [NativeTypeName("uint32_t")]
    public uint src_mip_level;

    [NativeTypeName("uint32_t")]
    public uint src_array_layer;

    [NativeTypeName("uint32_t")]
    public uint dst_mip_level;

    [NativeTypeName("uint32_t")]
    public uint dst_array_layer;

    [NativeTypeName("uint32_t")]
    public uint width;

    [NativeTypeName("uint32_t")]
    public uint height;
}

[GeneratedCode("ClangSharp", "23.1.5.0")]
internal static unsafe partial class KhaozNative
{
    [DllImport("khaoz_native", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    [return: NativeTypeName("uint32_t")]
    public static extern uint khaoz_native_abi_version();

    [DllImport("khaoz_native", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    [return: NativeTypeName("size_t")]
    public static extern nuint khaoz_last_error_message([NativeTypeName("char *")] sbyte* buffer, [NativeTypeName("size_t")] nuint capacity);

    [DllImport("khaoz_native", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern khaoz_result khaoz_frame_create([NativeTypeName("uint32_t")] uint capacity, khaoz_frame** out_frame);

    [DllImport("khaoz_native", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern khaoz_result khaoz_frame_submit(khaoz_frame* frame, [NativeTypeName("const khaoz_draw_record *")] khaoz_draw_record* records, [NativeTypeName("uint32_t")] uint count);

    [DllImport("khaoz_native", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern khaoz_result khaoz_frame_sorted_keys([NativeTypeName("const khaoz_frame *")] khaoz_frame* frame, [NativeTypeName("uint64_t *")] ulong* out_keys, [NativeTypeName("uint32_t")] uint capacity, [NativeTypeName("uint32_t *")] uint* out_count);

    [DllImport("khaoz_native", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern void khaoz_frame_destroy(khaoz_frame* frame);

    [DllImport("khaoz_native", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern khaoz_result khaoz_gpu_device_create_headless([NativeTypeName("const khaoz_gpu_device_desc *")] khaoz_gpu_device_desc* desc, khaoz_gpu_device** out_device);

    [DllImport("khaoz_native", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern khaoz_result khaoz_gpu_device_create_headless_with_loader([NativeTypeName("const khaoz_gpu_device_desc *")] khaoz_gpu_device_desc* desc, [NativeTypeName("const char *")] sbyte* loader_path, khaoz_gpu_device** out_device);

    [DllImport("khaoz_native", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern khaoz_result khaoz_gpu_device_create_headless_with_selection([NativeTypeName("const khaoz_gpu_device_desc *")] khaoz_gpu_device_desc* desc, [NativeTypeName("const char *")] sbyte* loader_path, [NativeTypeName("const khaoz_gpu_device_selection *")] khaoz_gpu_device_selection* selection, [NativeTypeName("uint32_t *")] uint* out_startup_status, khaoz_gpu_device** out_device);

    [DllImport("khaoz_native", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern khaoz_result khaoz_gpu_device_probe([NativeTypeName("const char *")] sbyte* loader_path);

    [DllImport("khaoz_native", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern khaoz_result khaoz_gpu_device_capabilities([NativeTypeName("const khaoz_gpu_device *")] khaoz_gpu_device* device, khaoz_gpu_capabilities* out_caps);

    [DllImport("khaoz_native", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    [return: NativeTypeName("uint32_t")]
    public static extern uint khaoz_gpu_device_validation_errors([NativeTypeName("const khaoz_gpu_device *")] khaoz_gpu_device* device);

    [DllImport("khaoz_native", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern khaoz_result khaoz_gpu_device_destroy(khaoz_gpu_device* device, [NativeTypeName("uint32_t *")] uint* out_validation_errors);

    [DllImport("khaoz_native", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern khaoz_result khaoz_gpu_device_wait_for_idle(khaoz_gpu_device* device);

    [DllImport("khaoz_native", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern khaoz_result khaoz_gpu_device_submit(khaoz_gpu_device* device, khaoz_gpu_command_list* commands, khaoz_gpu_fence* completion);

    [DllImport("khaoz_native", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern khaoz_result khaoz_gpu_buffer_create(khaoz_gpu_device* device, [NativeTypeName("uint32_t")] uint size_in_bytes, [NativeTypeName("uint32_t")] uint usage, khaoz_gpu_buffer** out_buffer);

    [DllImport("khaoz_native", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern khaoz_result khaoz_gpu_buffer_destroy(khaoz_gpu_buffer* buffer);

    [DllImport("khaoz_native", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern khaoz_result khaoz_gpu_buffer_update(khaoz_gpu_buffer* buffer, [NativeTypeName("uint32_t")] uint offset, [NativeTypeName("const void *")] void* data, [NativeTypeName("uint32_t")] uint size);

    [DllImport("khaoz_native", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern khaoz_result khaoz_gpu_buffer_map(khaoz_gpu_buffer* buffer, [NativeTypeName("uint32_t")] uint mode, khaoz_gpu_mapped_data* out_data);

    [DllImport("khaoz_native", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern khaoz_result khaoz_gpu_buffer_unmap(khaoz_gpu_buffer* buffer);

    [DllImport("khaoz_native", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern khaoz_result khaoz_gpu_texture_create(khaoz_gpu_device* device, [NativeTypeName("const khaoz_gpu_texture_desc *")] khaoz_gpu_texture_desc* desc, khaoz_gpu_texture** out_texture);

    [DllImport("khaoz_native", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern khaoz_result khaoz_gpu_texture_destroy(khaoz_gpu_texture* texture);

    [DllImport("khaoz_native", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern khaoz_result khaoz_gpu_texture_update(khaoz_gpu_texture* texture, [NativeTypeName("const khaoz_gpu_texture_region *")] khaoz_gpu_texture_region* region, [NativeTypeName("const void *")] void* data, [NativeTypeName("uint32_t")] uint size);

    [DllImport("khaoz_native", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern khaoz_result khaoz_gpu_texture_map(khaoz_gpu_texture* texture, [NativeTypeName("uint32_t")] uint mode, khaoz_gpu_mapped_data* out_data);

    [DllImport("khaoz_native", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern khaoz_result khaoz_gpu_texture_unmap(khaoz_gpu_texture* texture);

    [DllImport("khaoz_native", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern khaoz_result khaoz_gpu_sampler_create(khaoz_gpu_device* device, [NativeTypeName("const khaoz_gpu_sampler_desc *")] khaoz_gpu_sampler_desc* desc, khaoz_gpu_sampler** out_sampler);

    [DllImport("khaoz_native", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern khaoz_result khaoz_gpu_sampler_destroy(khaoz_gpu_sampler* sampler);

    [DllImport("khaoz_native", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern khaoz_result khaoz_gpu_fence_create(khaoz_gpu_device* device, khaoz_gpu_fence** out_fence);

    [DllImport("khaoz_native", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern khaoz_result khaoz_gpu_fence_destroy(khaoz_gpu_fence* fence);

    [DllImport("khaoz_native", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern khaoz_result khaoz_gpu_fence_signaled(khaoz_gpu_fence* fence, [NativeTypeName("uint32_t *")] uint* out_signaled);

    [DllImport("khaoz_native", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern khaoz_result khaoz_gpu_fence_reset(khaoz_gpu_fence* fence);

    [DllImport("khaoz_native", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern khaoz_result khaoz_gpu_command_list_create(khaoz_gpu_device* device, khaoz_gpu_command_list** out_commands);

    [DllImport("khaoz_native", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern khaoz_result khaoz_gpu_command_list_destroy(khaoz_gpu_command_list* commands);

    [DllImport("khaoz_native", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern khaoz_result khaoz_gpu_command_list_begin(khaoz_gpu_command_list* commands);

    [DllImport("khaoz_native", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern khaoz_result khaoz_gpu_command_list_end(khaoz_gpu_command_list* commands);

    [DllImport("khaoz_native", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern khaoz_result khaoz_gpu_command_list_copy_buffer(khaoz_gpu_command_list* commands, khaoz_gpu_buffer* src, [NativeTypeName("uint32_t")] uint src_offset, khaoz_gpu_buffer* dst, [NativeTypeName("uint32_t")] uint dst_offset, [NativeTypeName("uint32_t")] uint size);

    [DllImport("khaoz_native", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern khaoz_result khaoz_gpu_command_list_copy_texture(khaoz_gpu_command_list* commands, khaoz_gpu_texture* src, khaoz_gpu_texture* dst, [NativeTypeName("const khaoz_gpu_texture_copy *")] khaoz_gpu_texture_copy* copy);

    [NativeTypeName("#define KHAOZ_NATIVE_ABI_VERSION 2u")]
    public const uint KHAOZ_NATIVE_ABI_VERSION = 2U;
}

/// <summary>Defines the type of a member as it was used in the native signature.</summary>
[AttributeUsage(AttributeTargets.Struct | AttributeTargets.Enum | AttributeTargets.Property | AttributeTargets.Field | AttributeTargets.Parameter | AttributeTargets.ReturnValue, AllowMultiple = false, Inherited = true)]
[Conditional("DEBUG")]
internal sealed partial class NativeTypeNameAttribute : Attribute
{
    private readonly string _name;

    /// <summary>Initializes a new instance of the <see cref="NativeTypeNameAttribute" /> class.</summary>
    /// <param name="name">The name of the type that was used in the native signature.</param>
    public NativeTypeNameAttribute(string name)
    {
        _name = name;
    }

    /// <summary>Gets the name of the type that was used in the native signature.</summary>
    public string Name => _name;
}

/// <summary>Defines the annotation found in a native declaration.</summary>
[AttributeUsage(AttributeTargets.Struct | AttributeTargets.Enum | AttributeTargets.Property | AttributeTargets.Field | AttributeTargets.Parameter | AttributeTargets.ReturnValue, AllowMultiple = true, Inherited = false)]
[Conditional("DEBUG")]
internal sealed partial class NativeAnnotationAttribute : Attribute
{
    private readonly string _annotation;

    /// <summary>Initializes a new instance of the <see cref="NativeAnnotationAttribute" /> class.</summary>
    /// <param name="annotation">The annotation that was used in the native declaration.</param>
    public NativeAnnotationAttribute(string annotation)
    {
        _annotation = annotation;
    }

    /// <summary>Gets the annotation that was used in the native declaration.</summary>
    public string Annotation => _annotation;
}
