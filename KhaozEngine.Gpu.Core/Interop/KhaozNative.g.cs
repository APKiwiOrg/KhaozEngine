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

    [NativeTypeName("#define KHAOZ_NATIVE_ABI_VERSION 1u")]
    public const uint KHAOZ_NATIVE_ABI_VERSION = 1U;
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
