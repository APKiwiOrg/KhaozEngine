using System;
using KhaozEngine.Gpu;

namespace KhaozEngine.Tests.Gpu;

internal sealed partial class RecordingGpuCommandList : IGpuRecordingWaitSource
{
    internal IGpuRecordingWaitSource? WaitSource { get; set; }
    internal Action? BeforeProbeBegin { get; set; }
    internal Action? AfterProbeBegin { get; set; }
    internal Action? AfterProbeEnd { get; set; }
    IGpuRecordingWaitSource? Source => WaitSource ?? Inner as IGpuRecordingWaitSource;
    public Guid RecordingWaitListIdentity => Source?.RecordingWaitListIdentity ?? Guid.Empty;
    public bool TryGetRecordingAttempt(out ulong attempt)
    {
        attempt = 0;
        return Source?.TryGetRecordingAttempt(out attempt) ?? false;
    }
    public bool TryGetRecordingWait(out GpuRecordingWaitReceipt receipt)
    {
        receipt = default;
        return Source?.TryGetRecordingWait(out receipt) ?? false;
    }
}
