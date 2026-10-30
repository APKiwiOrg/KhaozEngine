using System;

namespace KhaozEngine.Gpu;

/// <summary>Private archive observation of one successful, still open recording.</summary>
public readonly record struct GpuRecordingWaitReceipt(Guid DeviceIdentity, Guid ListIdentity, ulong Attempt,
    ulong RecordingOrdinal, int Segment, int Depth, long Count, long Ticks, int WaitCallCount, long Frequency);

/// <summary>Coherent cumulative publications under the native submit lock.</summary>
public readonly record struct GpuRingWaitTotals(Guid DeviceIdentity, int Depth, long Frequency, long Count, long Ticks);

public enum GpuRingWaitSnapshotStatus { Unavailable, Available, Contended }

/// <summary>Optional list capability. Generation zero is a value, separate from availability.</summary>
public interface IGpuRecordingWaitSource
{
    Guid RecordingWaitListIdentity { get; }
    bool TryGetRecordingAttempt(out ulong attempt);
    bool TryGetRecordingWait(out GpuRecordingWaitReceipt receipt);
}

/// <summary>Optional device capability. Snapshot acquisition never waits for the publisher lock.</summary>
public interface IGpuRingWaitSource
{
    Guid RingWaitDeviceIdentity { get; }
    GpuRingWaitSnapshotStatus TryReadRingWaitTotals(out GpuRingWaitTotals totals);
}
