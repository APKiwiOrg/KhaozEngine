using System;
using KhaozEngine.Gpu.Core.Interop;

namespace KhaozEngine.Gpu.Core.Internal;

internal abstract unsafe class CoreMappableResource(CoreGpuDevice owner, void* handle) : CoreResource(owner, handle)
{
    private GpuMapMode? _mapMode;

    internal MappedData Map(GpuMapMode mode)
    {
        var resource = NativeHandle;
        if (!Enum.IsDefined(mode))
            throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown GPU map mode.");
        if (_mapMode is { } existing)
            throw new GpuStagingMapStateException(GpuStagingMapRefusal.AlreadyMapped,
                "The staging resource already has an open mapping.")
            {
                Backend = Owner.Backend,
                ExistingMode = existing,
                RequestedMode = mode,
            };

        khaoz_gpu_mapped_data data;
        NativeResult.ThrowIfFailed(MapNative(resource, (uint)mode, &data), nameof(Map));
        _mapMode = mode;
        return new MappedData((nint)data.data, data.row_pitch, data.size_in_bytes);
    }

    internal void Unmap()
    {
        var resource = NativeHandle;
        if (_mapMode is null)
            throw new GpuStagingMapStateException(GpuStagingMapRefusal.NotMapped,
                "The staging resource has no open mapping.") { Backend = Owner.Backend };

        var result = UnmapNative(resource);
        _mapMode = null;
        NativeResult.ThrowIfFailed(result, nameof(Unmap));
    }

    protected abstract khaoz_result MapNative(void* value, uint mode, khaoz_gpu_mapped_data* data);
    protected abstract khaoz_result UnmapNative(void* value);
}
