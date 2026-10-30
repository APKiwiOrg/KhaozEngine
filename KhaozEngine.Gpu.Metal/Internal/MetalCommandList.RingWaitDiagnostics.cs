using System;

namespace KhaozEngine.Gpu.Metal.Internal;

internal sealed partial class MetalCommandList : IGpuRecordingWaitSource
{
    GpuRecordingWaitReceipt _recordingWaitReceipt;
    bool _recordingWaitAvailable;
    public Guid RecordingWaitListIdentity { get; } = Guid.NewGuid();

    public bool TryGetRecordingAttempt(out ulong attempt)
    {
        attempt = _disposed ? 0 : _timestampRecordingId;
        return !_disposed;
    }

    public bool TryGetRecordingWait(out GpuRecordingWaitReceipt receipt)
    {
        bool available = !_disposed && _recording && _recordingWaitAvailable;
        receipt = available ? _recordingWaitReceipt : default;
        return available;
    }
}
