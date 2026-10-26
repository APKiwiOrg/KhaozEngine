using System;

namespace KhaozEngine.Gpu.Metal.Internal;

internal sealed partial class MetalGpuDevice : IGpuRingWaitSource
{
    public Guid RingWaitDeviceIdentity => _rings.RingWaitDeviceIdentity;
    public GpuRingWaitSnapshotStatus TryReadRingWaitTotals(out GpuRingWaitTotals totals)
    {
        totals = default;
        return _disposed ? GpuRingWaitSnapshotStatus.Unavailable : _rings.TryReadRingWaitTotals(out totals);
    }
}
