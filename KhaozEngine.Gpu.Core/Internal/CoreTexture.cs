using KhaozEngine.Gpu.Core.Interop;
using KhaozEngine.Gpu.Vulkan.Internal;

namespace KhaozEngine.Gpu.Core.Internal;

internal sealed unsafe class CoreTexture(CoreGpuDevice owner, khaoz_gpu_texture* handle, GpuTextureDescription desc,
    uint actualArrayLayers)
    : CoreMappableResource(owner, handle), IGpuTexture
{
    public uint Width => desc.Width;
    public uint Height => desc.Height;
    public uint MipLevels => desc.MipLevels;
    public uint SampleCount => desc.SampleCount;
    public GpuPixelFormat Format => desc.Format;
    internal uint ArrayLayers => actualArrayLayers;
    internal GpuTextureUsage Usage => desc.Usage;
    internal bool IsStaging => (desc.Usage & GpuTextureUsage.Staging) != 0;
    internal khaoz_gpu_texture* Handle => (khaoz_gpu_texture*)NativeHandle;

    internal bool HasCompatibleCopyFormat(CoreTexture destination)
        => SampleCount == destination.SampleCount
            && VulkanFormats.ToVkFormat(Format, (Usage & GpuTextureUsage.DepthStencil) != 0)
                == VulkanFormats.ToVkFormat(destination.Format, (destination.Usage & GpuTextureUsage.DepthStencil) != 0);

    protected override khaoz_result MapNative(void* value, uint mode, khaoz_gpu_mapped_data* data)
        => KhaozNative.khaoz_gpu_texture_map((khaoz_gpu_texture*)value, mode, data);

    protected override khaoz_result UnmapNative(void* value)
        => KhaozNative.khaoz_gpu_texture_unmap((khaoz_gpu_texture*)value);

    protected override khaoz_result Release(void* value) => KhaozNative.khaoz_gpu_texture_destroy((khaoz_gpu_texture*)value);
}
