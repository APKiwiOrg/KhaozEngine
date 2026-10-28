using System;

namespace KhaozEngine.Gpu.D3D11.Internal;

// Plain native facts keep feature-level and adapter arithmetic independent of Windows interop.
internal readonly record struct D3D11BufferLimits(uint FeatureLevel, ulong DedicatedVideoMemoryBytes)
{
    // D3D11 guarantees this resource size. Oversized allocations may work, but cannot be admitted from these facts.
    internal ulong ResourceBytes => FeatureLevel < 0xb000 ? 0
        : Math.Min(Math.Max(128UL << 20, DedicatedVideoMemoryBytes / 4), 2048UL << 20);

    // The backend's storage buffers use full raw SRV/UAV views with four-byte elements, not structured views.
    internal uint StorageRangeBytes => (uint)Math.Min(ResourceBytes, (1UL << 27) * sizeof(uint));
}
