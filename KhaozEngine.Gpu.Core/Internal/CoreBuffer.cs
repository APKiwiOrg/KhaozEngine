using KhaozEngine.Gpu.Core.Interop;

namespace KhaozEngine.Gpu.Core.Internal;

internal sealed unsafe class CoreBuffer(CoreGpuDevice owner, khaoz_gpu_buffer* handle, GpuBufferDescription desc)
    : CoreMappableResource(owner, handle), IGpuBuffer
{
    public uint SizeInBytes => desc.SizeInBytes;
    internal GpuBufferUsage Usage => desc.Usage;
    internal bool IsStaging => (desc.Usage & GpuBufferUsage.Staging) != 0;
    internal khaoz_gpu_buffer* Handle => (khaoz_gpu_buffer*)NativeHandle;

    protected override khaoz_result MapNative(void* value, uint mode, khaoz_gpu_mapped_data* data)
        => KhaozNative.khaoz_gpu_buffer_map((khaoz_gpu_buffer*)value, mode, data);

    protected override khaoz_result UnmapNative(void* value)
        => KhaozNative.khaoz_gpu_buffer_unmap((khaoz_gpu_buffer*)value);

    protected override khaoz_result Release(void* value) => KhaozNative.khaoz_gpu_buffer_destroy((khaoz_gpu_buffer*)value);
}
