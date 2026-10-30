using System;
using KhaozEngine.Gpu;

namespace KhaozEngine.Tests.Gpu;

internal sealed partial class FrameOutputDevice : IGpuRingWaitSource
{
    internal IGpuRingWaitSource? WaitSource { get; set; }
    IGpuRingWaitSource? Source => WaitSource ?? inner as IGpuRingWaitSource;
    public Guid RingWaitDeviceIdentity => Source?.RingWaitDeviceIdentity ?? Guid.Empty;
    public GpuRingWaitSnapshotStatus TryReadRingWaitTotals(out GpuRingWaitTotals totals)
    {
        totals = default;
        return Source?.TryReadRingWaitTotals(out totals) ?? GpuRingWaitSnapshotStatus.Unavailable;
    }
}
